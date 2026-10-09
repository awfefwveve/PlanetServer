using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using Google.Protobuf;
using Planet.Protocol;
using Planet.Server;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name); passed++;
}
var dir = Path.Combine(Environment.GetEnvironmentVariable("PLANET_TEST_ROOT") ?? Path.GetTempPath(), "planet-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
var storage = new FaultStore(new FileStore(dir));
double time = 100;
var world = new World(storage, () => time);
var alice = new Session(); var bob = new Session();
ulong sequence = 0;
async Task<Update> Send(Session s, string kind, float dx = 0, float dz = 0, string name = "alice", string password = "test-pass-123")
    => await world.Handle(s, new Command { Sequence = ++sequence, Kind = kind, Dx = dx, Dz = dz, Name = name, Password = password });
Check((await Send(alice, "attack")).Code == "unauthorized", "unauthenticated actions rejected");
Check((await Send(alice, "login")).Code == "ok", "account creation and login");
Check((await Send(new Session(), "login", password: "wrong-password")).Code == "invalid_login", "wrong password rejected");
Check((await Send(new Session(), "login")).Code == "duplicate_login", "duplicate login rejected");
Check((await Send(bob, "login", name: "bob")).Players.Count == 2, "two players share the same world");
Check((await Send(alice, "move", float.NaN)).Code == "invalid", "NaN position input rejected");
Check((await Send(alice, "move", 100)).Code == "invalid", "oversized movement rejected");
Check((await Send(alice, "claim")).Code == "not_ready", "quest reward prerequisites enforced");
Check((await Send(alice, "attack")).Code == "out_of_range", "server checks attack distance");
time += 1;
await Send(alice, "move", 1);
Check((await Send(alice, "move", 1)).Code == "rate_limited", "movement spam rejected");
for (var i = 0; i < 9; i++) { time += .1; await Send(alice, "move", 1); }
Check((await Send(alice, "snapshot")).Players.First(p => p.Name == "alice").X < 3, "movement uses bounded server steps");
var duplicate = new Command { Sequence = sequence, Kind = "attack" };
Check((await world.Handle(alice, duplicate)).Code == "replay", "duplicate sequence rejected");
time += 1;
await Send(alice, "attack");
Check((await Send(alice, "attack")).Code == "cooldown", "attack cooldown enforced");
time += .5; await Send(alice, "attack");
time += .5; storage.Fail = true;
bool failed = false;
try { await Send(alice, "attack"); } catch (IOException) { failed = true; }
Check(failed, "injected storage failure surfaces");
var state = await Send(alice, "snapshot");
Check(state.Monster.Hp == 1 && state.Players.First(p => p.Name == "alice").Kills == 0, "failed save does not grant reward or kill monster");
storage.Fail = false;
time += .5;
state = await Send(alice, "attack");
Check(state.Monster.Hp == 0 && state.Players.First(p => p.Name == "alice").Shards == 1, "kill reward persisted once");
for (var kill = 0; kill < 2; kill++)
{
    time += 4;
    for (var hit = 0; hit < 3; hit++) { time += .5; await Send(alice, "attack"); }
}
var claims = await Task.WhenAll(Send(alice, "claim"), Send(alice, "claim"));
Check(claims.Count(c => c.Code == "ok") == 1 && claims.Count(c => c.Code == "already_claimed") == 1,
    "concurrent quest claims grant reward once");
await world.Disconnect(alice);
await world.Disconnect(bob);
world = new World(new FileStore(dir), () => time);
alice = new Session();
state = await Send(alice, "login");
var saved = state.Players.Single();
Check(saved.Kills == 3 && saved.Shards == 3 && saved.Gold == 100 && saved.QuestClaimed, "progress survives a new world instance");
Check((await Send(alice, "claim")).Code == "already_claimed", "reward cannot be reclaimed after restart");

// Real loopback TCP + TLS + generated Protobuf integration.
using var certificate = GameHost.DevelopmentCertificate(Path.Combine(dir, "tls"));
var pin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
await using (var host = new GameHost(new World(new FileStore(Path.Combine(dir, "network"))), certificate, 0))
{
    host.Start(); var running = host.Run();
    using var client = new TcpClient();
    await client.ConnectAsync("127.0.0.1", host.Port);
    using var tls = new SslStream(client.GetStream(), false, (_, cert, _, _) => cert != null &&
        Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())) == pin);
    await tls.AuthenticateAsClientAsync("localhost");
    var command = new Command { Sequence = 1, Kind = "login", Name = "network", Password = "test-pass-123" };
    await Framing.WriteAsync(tls, command.ToByteArray(), CancellationToken.None);
    var response = Update.Parser.ParseFrom(await Framing.ReadAsync(tls, CancellationToken.None));
    Check(response.Code == "ok" && response.Players.Single().Name == "network", "TCP/TLS/Protobuf end-to-end login");
    using var second = new TcpClient(); await second.ConnectAsync("127.0.0.1", host.Port);
    using var tls2 = new SslStream(second.GetStream(), false, (_, cert, _, _) => cert != null &&
        Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())) == pin);
    await tls2.AuthenticateAsClientAsync("localhost");
    command.Name = "second";
    await Framing.WriteAsync(tls2, command.ToByteArray(), CancellationToken.None);
    response = Update.Parser.ParseFrom(await Framing.ReadAsync(tls2, CancellationToken.None));
    Check(response.Players.Count == 2, "two TLS clients observe both characters");
    command.Sequence = 2; command.Kind = "snapshot";
    using var combined = new MemoryStream();
    await Framing.WriteAsync(combined, command.ToByteArray(), CancellationToken.None);
    command.Sequence = 3;
    await Framing.WriteAsync(combined, command.ToByteArray(), CancellationToken.None);
    var bytes = combined.ToArray();
    await tls.WriteAsync(bytes.AsMemory(0, 2));
    await tls.WriteAsync(bytes.AsMemory(2));
    var r1 = Update.Parser.ParseFrom(await Framing.ReadAsync(tls, CancellationToken.None));
    var r2 = Update.Parser.ParseFrom(await Framing.ReadAsync(tls, CancellationToken.None));
    Check(r1.Sequence == 2 && r2.Sequence == 3, "fragmented header and coalesced frames parsed correctly");
    using var badClient = new TcpClient(); await badClient.ConnectAsync("127.0.0.1", host.Port);
    using var badTls = new SslStream(badClient.GetStream(), false, (_, cert, _, _) => cert != null &&
        Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())) == "WRONG_PIN");
    bool rejected = false;
    try { await badTls.AuthenticateAsClientAsync("localhost"); } catch (System.Security.Authentication.AuthenticationException) { rejected = true; }
    Check(rejected, "wrong TLS certificate pin rejected");
}
using (var invalid = new MemoryStream(new byte[] { 0x7f, 0xff, 0xff, 0xff }))
{
    bool rejected = false;
    try { await Framing.ReadAsync(invalid, CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "oversized frames rejected before allocation");
}
using (var partial = new MemoryStream(new byte[] { 0, 0, 0, 4, 1 }))
{
    bool rejected = false;
    try { await Framing.ReadAsync(partial, CancellationToken.None); } catch (EndOfStreamException) { rejected = true; }
    Check(rejected, "truncated frames rejected");
}
var db = Environment.GetEnvironmentVariable("PLANET_TEST_DB");
if (!string.IsNullOrEmpty(db))
{
    await using var postgres = new PostgresStore(db);
    await postgres.Initialize();
    var name = "test_" + Guid.NewGuid().ToString("N")[..10];
    var account = Account.Create(name, "test-pass-123");
    await postgres.Save(account, null);
    await postgres.Save(account with { Gold = 100, Revision = 1 }, 0);
    Check((await postgres.Load(name))?.Gold == 100, "PostgreSQL save/load");
    bool conflict = false;
    try { await postgres.Save(account with { Gold = 200, Revision = 1 }, 0); } catch (InvalidOperationException) { conflict = true; }
    Check(conflict && (await postgres.Load(name))?.Gold == 100, "PostgreSQL stale revision cannot overwrite committed reward");
    await postgres.Save((await postgres.Load(name))! with { Kills = 3, Gold = 0, Revision = 2 }, 1);
    var pgWorld = new World(postgres);
    var pgSession = new Session();
    await pgWorld.Handle(pgSession, new Command { Sequence = 1, Kind = "login", Name = name, Password = "test-pass-123" });
    var pgClaims = await Task.WhenAll(
        pgWorld.Handle(pgSession, new Command { Sequence = 2, Kind = "claim" }),
        pgWorld.Handle(pgSession, new Command { Sequence = 3, Kind = "claim" }));
    Check(pgClaims.Count(c => c.Code == "ok") == 1, "PostgreSQL world grants concurrent quest reward once");
    await pgWorld.Disconnect(pgSession);
    pgWorld = new World(postgres);
    var restored = await pgWorld.Handle(new Session(), new Command { Sequence = 1, Kind = "login", Name = name, Password = "test-pass-123" });
    Check(restored.Players.Single().Gold == 100 && restored.Players.Single().QuestClaimed,
        "PostgreSQL world restores completed quest after restart");
}
else Console.WriteLine("SKIP: PostgreSQL integration (PLANET_TEST_DB not set)");
Console.WriteLine($"RESULT: {passed} assertions passed. Temporary test data: {dir}");

sealed class FaultStore(IStore inner) : IStore
{
    public bool Fail;
    public Task<Account?> Load(string name) => inner.Load(name);
    public Task Save(Account account, long? expectedRevision) => Fail
        ? Task.FromException(new IOException("Injected storage failure")) : inner.Save(account, expectedRevision);
}
