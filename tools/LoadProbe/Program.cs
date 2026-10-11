using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Planet.Protocol;
using Planet.Server;

// The server uses its own process, port and disposable data directory. Never touches Play.cmd data.
if(args.Length>0&&args[0]=="--host") {
    string dir=args[1];int limit=int.Parse(args[2]);
    using var cert=GameHost.DevelopmentCertificate(Path.Combine(dir,"tls"));
    var host=new GameHost(new World(new FileStore(Path.Combine(dir,"accounts")),channelCapacity:limit),cert,0,limit);
    host.Start();await File.WriteAllTextAsync(Path.Combine(dir,"ready.json"),JsonSerializer.Serialize(new{port=host.Port,pin=cert.GetCertHashString(HashAlgorithmName.SHA256)}));
    var run=host.Run();while(!File.Exists(Path.Combine(dir,"stop"))&&!run.IsCompleted)await Task.Delay(100);
    await host.DisposeAsync();await run;return;
}
int count=args.Length>0?int.Parse(args[0]):50;
int seconds=args.Length>1?int.Parse(args[1]):20;
string output=Path.GetFullPath(args.Length>2?args[2]:"load-result.json");
string scenario=args.Length>3?args[3]:"spread";
if(count is <1 or >1000||seconds is <2 or >120||scenario is not ("spread" or "crowded"))throw new ArgumentException("LoadProbe <1..1000 clients> <2..120 seconds> <result.json> <spread|crowded>");
string directory=Path.Combine(Path.GetDirectoryName(output)!,"run-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
var store=new FileStore(Path.Combine(directory,"accounts"));
Console.WriteLine($"Preparing {count} isolated accounts; scenario={scenario}");
for(int i=0;i<count;i++)await store.Save(Account.Create("probe"+i,"local-probe-password"),null);
var launch=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
if(Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet",StringComparison.OrdinalIgnoreCase))launch.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
launch.ArgumentList.Add("--host");launch.ArgumentList.Add(directory);launch.ArgumentList.Add(count.ToString());
using var server=Process.Start(launch)!;
var stdout=server.StandardOutput.ReadToEndAsync();var stderr=server.StandardError.ReadToEndAsync();
using var stop=new CancellationTokenSource();
var errors=new ConcurrentDictionary<string,int>();var samples=new ConcurrentBag<double>();
int stage=0,connected=0,active=0,attempts=0;long received=0;var tasks=new List<Task>();
void Error(string name)=>errors.AddOrUpdate(name,1,(_,n)=>n+1);
async Task<Update> Request(SslStream tls,Command command){using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(8000);await Framing.WriteAsync(tls,command.ToByteArray(),timeout.Token);var bytes=await Framing.ReadAsync(tls,timeout.Token);if(Volatile.Read(ref stage)==1)Interlocked.Add(ref received,bytes.Length);var result=Update.Parser.ParseFrom(bytes);if(result.Sequence!=command.Sequence)throw new IOException("Sequence mismatch");return result;}
try {
    var readyPath=Path.Combine(directory,"ready.json");var deadline=Stopwatch.StartNew();
    while(!File.Exists(readyPath)){if(server.HasExited||deadline.Elapsed.TotalSeconds>15)throw new IOException("Test server startup failed");await Task.Delay(100);}
    using var ready=JsonDocument.Parse(await File.ReadAllTextAsync(readyPath));int port=ready.RootElement.GetProperty("port").GetInt32();string pin=ready.RootElement.GetProperty("pin").GetString()!;
    using var setupGate=new SemaphoreSlim(8);using var setupDone=new CountdownEvent(count);
    for(int i=0;i<count;i++){int id=i;tasks.Add(Task.Run(async()=>{
        bool signalled=false,admitted=false;string phase="setup";
        using var tcp=new TcpClient{NoDelay=true};SslStream? tls=null;
        try {
            await setupGate.WaitAsync(stop.Token);
            try {
                await tcp.ConnectAsync("127.0.0.1",port,stop.Token);
                tls=new SslStream(tcp.GetStream(),false,(_,cert,_,_)=>cert!=null&&Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()))==pin);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost="localhost"},stop.Token);
                var login=await Request(tls,new Command{Sequence=1,Kind="login",Name="probe"+id,Password="local-probe-password"});if(login.Code!="ok")throw new IOException("login:"+login.Code);
                var enter=await Request(tls,new Command{Sequence=2,Kind="switch_channel",ChannelId=scenario=="crowded"?1:id%30+1});if(enter.Code!="ok")throw new IOException("channel:"+enter.Code);
                Interlocked.Increment(ref connected);Interlocked.Increment(ref active);admitted=true;
            }finally{setupGate.Release();}
            setupDone.Signal();signalled=true;ulong sequence=2;int step=0;
            while(!stop.IsCancellationRequested){
                bool measuring=Volatile.Read(ref stage)==1;phase=measuring?"measured":"warmup";
                var watch=Stopwatch.StartNew();if(measuring)Interlocked.Increment(ref attempts);
                var reply=await Request(tls!,new Command{Sequence=++sequence,Kind=step%2==0?"move":"snapshot",Dx=step%4==0?1:-1});
                if(measuring){samples.Add(watch.Elapsed.TotalMilliseconds);if(reply.Code!="ok")Error("measured:"+reply.Code);}
                else if(reply.Code!="ok")Error("warmup:"+reply.Code);
                step++;await Task.Delay(100,stop.Token);
            }
        }catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        catch(Exception e){Error(phase+":"+e.GetType().Name+":"+e.Message);}
        finally{if(!signalled)setupDone.Signal();if(admitted)Interlocked.Decrement(ref active);tls?.Dispose();}
    }));}
    while(!setupDone.IsSet){if(deadline.Elapsed.TotalSeconds>180)throw new TimeoutException("Setup timed out");await Task.Delay(200);}
    Console.WriteLine($"Measurement: admitted={connected}, active={active}, duration={seconds}s");
    int activeAtStart=active;var generator=Process.GetCurrentProcess();server.Refresh();var serverCpu=server.TotalProcessorTime;var generatorCpu=generator.TotalProcessorTime;
    long peakServer=server.WorkingSet64,peakGenerator=generator.WorkingSet64;Volatile.Write(ref stage,1);var elapsed=Stopwatch.StartNew();
    while(elapsed.Elapsed.TotalSeconds<seconds){await Task.Delay(250);server.Refresh();generator.Refresh();peakServer=Math.Max(peakServer,server.WorkingSet64);peakGenerator=Math.Max(peakGenerator,generator.WorkingSet64);}
    Volatile.Write(ref stage,2);elapsed.Stop();server.Refresh();generator.Refresh();int activeAtEnd=active;
    double serverCpuSeconds=(server.TotalProcessorTime-serverCpu).TotalSeconds,generatorCpuSeconds=(generator.TotalProcessorTime-generatorCpu).TotalSeconds;
    stop.Cancel();await Task.WhenAll(tasks);var sorted=samples.Order().ToArray();
    double? P(double p)=>sorted.Length==0?null:sorted[(int)Math.Ceiling((sorted.Length-1)*p)];
    var report=new{requestedClients=count,admitted=connected,activeAtStart,activeAtEnd,scenario,channels=scenario=="crowded"?1:Math.Min(count,30),seconds=elapsed.Elapsed.TotalSeconds,attempts,replies=sorted.Length,requestsPerSecond=sorted.Length/elapsed.Elapsed.TotalSeconds,p50Ms=P(.5),p95Ms=P(.95),p99Ms=P(.99),errors,serverCpuSeconds,generatorCpuSeconds,serverCpuMachinePercent=serverCpuSeconds/elapsed.Elapsed.TotalSeconds/Environment.ProcessorCount*100,generatorCpuMachinePercent=generatorCpuSeconds/elapsed.Elapsed.TotalSeconds/Environment.ProcessorCount*100,logicalProcessors=Environment.ProcessorCount,peakServerWorkingSet=peakServer,peakGeneratorWorkingSet=peakGenerator,receivedBytes=received,scope="Separate server and generator processes, SAME computer; loopback TCP/TLS/Protobuf, file store, move/snapshot only. Closed-loop: 100ms delay AFTER response; slower server reduces offered load. Setup, disconnect saves and PostgreSQL excluded. NOT a production capacity certification.",dataDirectory=directory};
    await File.WriteAllTextAsync(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(report));if(errors.Count>0||activeAtEnd<count)Environment.ExitCode=1;
}finally{
    stop.Cancel();await Task.WhenAll(tasks);await File.WriteAllTextAsync(Path.Combine(directory,"stop"),"stop");
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));try{await server.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){server.Kill(true);await server.WaitForExitAsync();}
    await File.WriteAllTextAsync(Path.Combine(directory,"server.log"),await stdout);await File.WriteAllTextAsync(Path.Combine(directory,"server-error.log"),await stderr);
    if(server.ExitCode!=0){Console.Error.WriteLine("Test server exited abnormally; inspect server-error.log");Environment.ExitCode=1;}
}
