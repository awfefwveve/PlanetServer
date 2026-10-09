using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Planet.Protocol;
using Planet.Server;

int count=args.Length>0?int.Parse(args[0]):64;
int seconds=args.Length>1?int.Parse(args[1]):10;
string output=args.Length>2?Path.GetFullPath(args[2]):Path.GetFullPath("load-result.json");
if(count is <1 or >128 || seconds is <2 or >120)throw new ArgumentOutOfRangeException("Use 1..128 clients and 2..120 seconds.");
string directory=Path.Combine(Path.GetTempPath(),"planet-probe-"+Guid.NewGuid().ToString("N"));
var store=new FileStore(directory);
for(int i=0;i<count;i++)await store.Save(Account.Create("probe"+i,"local-probe-password"),null);
using var certificate=GameHost.DevelopmentCertificate(Path.Combine(directory,"tls"));
string pin=certificate.GetCertHashString(HashAlgorithmName.SHA256);
await using var host=new GameHost(new World(store),certificate,0,128);
host.Start();var serving=host.Run();
var clients=new List<(TcpClient Tcp,SslStream Tls,ulong Sequence)>();
async Task<Update> Request(SslStream tls,Command command) {
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));
    await Framing.WriteAsync(tls,command.ToByteArray(),timeout.Token);
    var reply=Update.Parser.ParseFrom(await Framing.ReadAsync(tls,timeout.Token));
    if(reply.Sequence!=command.Sequence)throw new IOException("Mismatched response sequence");
    return reply;
}
try {
    // Setup is excluded from gameplay latency. This is not a login-storm benchmark.
    for(int i=0;i<count;i++) {
        var tcp=new TcpClient{NoDelay=true};await tcp.ConnectAsync("127.0.0.1",host.Port);
        var tls=new SslStream(tcp.GetStream(),false,(_,cert,_,_)=>cert!=null&&Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()))==pin);
        await tls.AuthenticateAsClientAsync("localhost");clients.Add((tcp,tls,2));
        if((await Request(tls,new Command{Kind="login",Sequence=1,Name="probe"+i,Password="local-probe-password"})).Code!="ok")throw new IOException("Login failed");
        if((await Request(tls,new Command{Kind="switch_channel",Sequence=2,ChannelId=i%30+1})).Code!="ok")throw new IOException("Channel entry failed");
    }
    var samples=new ConcurrentBag<double>();var failures=new ConcurrentBag<string>();
    var process=Process.GetCurrentProcess();var cpuBefore=process.TotalProcessorTime;long memoryBefore=process.WorkingSet64;
    var elapsed=Stopwatch.StartNew();
    await Task.WhenAll(clients.Select(async client=> {
        ulong sequence=client.Sequence;int step=0;
        while(elapsed.Elapsed.TotalSeconds<seconds) {
            var start=Stopwatch.GetTimestamp();
            try {
                var reply=await Request(client.Tls,new Command{Sequence=++sequence,Kind=step%2==0?"move":"snapshot",Dx=step%4==0?1:-1});
                if(reply.Code!="ok")failures.Add(reply.Code);
                samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            } catch(Exception e){failures.Add(e.GetType().Name);break;}
            step++;await Task.Delay(100); // At most ten requests per second per client.
        }
    }));
    elapsed.Stop();process.Refresh();var sorted=samples.Order().ToArray();
    double Percentile(double p)=>sorted.Length==0?0:sorted[(int)Math.Ceiling((sorted.Length-1)*p)];
    var report=new {clients=count,channelCount=Math.Min(count,30),seconds=elapsed.Elapsed.TotalSeconds,requests=samples.Count,errors=failures.Count,
        requestsPerSecond=samples.Count/elapsed.Elapsed.TotalSeconds,p50Ms=Percentile(.50),p95Ms=Percentile(.95),p99Ms=Percentile(.99),
        processCpuSeconds=(process.TotalProcessorTime-cpuBefore).TotalSeconds,workingSetBefore=memoryBefore,workingSetAfter=process.WorkingSet64,
        scope="Local loopback TLS, file storage, mixed movement/snapshot; client and server share process. Setup and disconnect persistence excluded. Not production capacity or PostgreSQL load.",
        errorKinds=failures.GroupBy(x=>x).ToDictionary(g=>g.Key,g=>g.Count())};
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);await File.WriteAllTextAsync(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine(JsonSerializer.Serialize(report));if(failures.Count>0)Environment.ExitCode=1;
} finally {foreach(var client in clients){client.Tls.Dispose();client.Tcp.Dispose();}}
