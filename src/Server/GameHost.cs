using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;
using Google.Protobuf;
using Planet.Protocol;

namespace Planet.Server;

public sealed class GameHost(World world, X509Certificate2 certificate, int port, int maxConnections=1000) : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, port);
    private readonly CancellationTokenSource shutdown = new();
    private readonly List<Task> clients = [];
    private readonly SemaphoreSlim slots = new(ValidateLimit(maxConnections), ValidateLimit(maxConnections));
    private static int ValidateLimit(int value)=>value is >=1 and <=4096?value:throw new ArgumentOutOfRangeException(nameof(value));
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public void Start() => listener.Start();
    // Fixed simulation/broadcast period. 100 ms matches the old client polling rate.
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
    // Frames waiting for one client. A client that falls this far behind is disconnected
    // rather than letting server memory grow without bound.
    public const int OutboxCapacity = 64;
    public async Task Run()
    {
        var ticking = world.RunTicker(TickInterval, shutdown.Token);
        while (!shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(shutdown.Token); }
            catch (OperationCanceledException) { break; }
            if (!await slots.WaitAsync(0)) { client.Dispose(); continue; }
            clients.RemoveAll(t => t.IsCompleted);
            clients.Add(Serve(client));
        }
        await ticking;
    }
    // One connection = one reader loop (this method) + one writer loop.
    // Only the writer touches SslStream.Write, so replies and tick pushes never interleave bytes.
    private async Task Serve(TcpClient client)
    {
        var session = new Session();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        var outbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(OutboxCapacity) { SingleReader = true });
        bool closing = false;
        session.Deliver = frame =>
        {
            if (Volatile.Read(ref closing)) return false;
            if (outbox.Writer.TryWrite(frame)) return true;
            Log("slow_consumer", session.Name, $"outbox>{OutboxCapacity}");
            try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
            return false;
        };
        Task writer = Task.CompletedTask;
        using (client)
        await using (var tls = new SslStream(client.GetStream(), false))
        {
            try
            {
                client.NoDelay = true;
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                handshake.CancelAfter(TimeSpan.FromSeconds(5));
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                    ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, handshake.Token);
                writer = WriteLoop(tls, outbox.Reader, lifetime);
                long window = Environment.TickCount64;
                int count = 0;
                while (!lifetime.IsCancellationRequested)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    var bytes = await Framing.ReadAsync(tls, deadline.Token);
                    var time = Environment.TickCount64;
                    if (time - window >= 1000) { window = time; count = 0; }
                    if (++count > 25) throw new InvalidDataException("Request rate exceeded");
                    var command = Command.Parser.ParseFrom(bytes);
                    var result = await world.Handle(session, command);
                    if (!session.Deliver(result.ToByteArray())) break;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log("session_closed", session.Name, ex.GetType().Name);
            }
            finally
            {
                Volatile.Write(ref closing, true);
                outbox.Writer.TryComplete();
                try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
                try { await writer; } catch (Exception) { }
                try { await world.Disconnect(session); }
                catch (Exception ex) { Log("disconnect_save_failed", session.Name, ex.GetType().Name); }
                slots.Release();
            }
        }
    }
    private static async Task WriteLoop(SslStream tls, ChannelReader<byte[]> frames, CancellationTokenSource lifetime)
    {
        try
        {
            await foreach (var frame in frames.ReadAllAsync(lifetime.Token))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                await Framing.WriteAsync(tls, frame, deadline.Token);
            }
        }
        finally
        {
            // A failed or stalled write ends the connection; the reader observes the cancellation.
            try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
    public static void Log(string kind, string? character, string? detail = null) =>
        Console.WriteLine(JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, kind, character, detail }));
    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel(); listener.Stop();
        await Task.WhenAll(clients);
        shutdown.Dispose(); slots.Dispose();
    }
    public static X509Certificate2 DevelopmentCertificate(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "localhost.pfx");
        if (File.Exists(path)) return X509CertificateLoader.LoadPkcs12FromFile(path, null);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
        return X509CertificateLoader.LoadPkcs12FromFile(path, null);
    }
}
