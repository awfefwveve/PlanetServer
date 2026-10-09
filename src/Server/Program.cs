using System.Security.Cryptography;
using Planet.Server;

var directory = Path.GetFullPath(Environment.GetEnvironmentVariable("PLANET_DATA") ?? "data");
Directory.CreateDirectory(directory);
// Keep local JSON mode single-process; also prevent accidental overlapping worlds on this data folder.
using var ownership = new FileStream(Path.Combine(directory, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
using var certificate = GameHost.DevelopmentCertificate(directory);
File.WriteAllText(Path.Combine(directory, "server-pin.txt"), certificate.GetCertHashString(HashAlgorithmName.SHA256));
var connection = Environment.GetEnvironmentVariable("PLANET_DB");
IStore store;
if (string.IsNullOrWhiteSpace(connection)) store = new FileStore(Path.Combine(directory, "accounts"));
else { var postgres = new PostgresStore(connection); await postgres.Initialize(); store = postgres; }
var host = new GameHost(new World(store), certificate, 7777);
host.Start();
GameHost.Log("listening", null, $"127.0.0.1:{host.Port}; storage={(connection is null ? "local-development" : "postgresql")}");
Console.WriteLine("Planet server running. Press Ctrl+C to save and stop.");
var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
var running = host.Run();
var stopFile = Path.Combine(directory, "stop.request");
async Task WatchStopFile()
{
    while (!File.Exists(stopFile) && !stop.Task.IsCompleted) await Task.Delay(250);
}
await Task.WhenAny(running, stop.Task, WatchStopFile());
await host.DisposeAsync();
await running;
if (store is IAsyncDisposable disposable)
{
    await disposable.DisposeAsync();
}
