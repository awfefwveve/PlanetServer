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

// Room chat: authoritative sender, scoped audience, bounded history and incremental delivery.
var chatStore=new FileStore(Path.Combine(dir,"chat"));
var chatWorld=new World(chatStore,()=>time);
var ca=new Session();var cb=new Session();ulong cs=0;
async Task<Update> ChatSend(Session session,string kind,string text="",long after=0,int channel=0)=>await chatWorld.Handle(session,new Command{Sequence=++cs,Kind=kind,Name=session==ca?"chatalice":"chatbob",Password="test-pass-123",ChatText=text,ChatAfter=after,ChannelId=channel});
Check((await ChatSend(ca,"chat","hello")).Code=="unauthorized","chat requires authentication");
await ChatSend(ca,"login");await ChatSend(cb,"login");
Check((await ChatSend(ca,"chat","안녕하세요 <b>문자</b>")).Code=="ok","Korean chat accepted");
var heard=await ChatSend(cb,"snapshot");
Check(heard.Chat.Count==1&&heard.Chat[0].Sender=="chatalice"&&heard.Chat[0].Text.Contains("<b>"),"server sender and plain-text body delivered to peer");
Check((await ChatSend(cb,"snapshot",after:heard.Chat[0].Id)).Chat.Count==0,"chat cursor excludes acknowledged messages");
await chatStore.Save(Account.Create("forestpeer","test-pass-123") with {RegionId="forest"},null);
var forestPeer=new Session();var forestChat=await chatWorld.Handle(forestPeer,new Command{Sequence=1,Kind="login",Name="forestpeer",Password="test-pass-123"});
Check(forestChat.Chat.Count==0,"other regions in same channel cannot read chat");
await chatWorld.Disconnect(forestPeer);
var lobbyChatSession=new Session();await chatWorld.Handle(lobbyChatSession,new Command{Sequence=1,Kind="login",Name="lobbypeer",Password="test-pass-123",LobbyLogin=true});
Check((await chatWorld.Handle(lobbyChatSession,new Command{Sequence=2,Kind="chat",ChatText="lobby message"})).Code=="not_in_world","lobby cannot send field chat");
await chatWorld.Disconnect(lobbyChatSession);
Check((await ChatSend(ca,"chat","spam")).Code=="chat_rate_limited","chat flood limited");
foreach(var bad in new[]{"",new string('x',101),"fake\n[Admin]","a\u202eb"})Check((await ChatSend(ca,"chat",bad)).Code=="invalid_chat","invalid chat rejected");
time+=2;await ChatSend(cb,"switch_channel",channel:2);
Check((await ChatSend(cb,"snapshot")).Chat.Count==0,"other channels cannot read room chat");
for(int i=0;i<55;i++){time+=1.01;await ChatSend(ca,"chat","line "+i);}
Check((await ChatSend(ca,"snapshot")).Chat.Count==50,"room chat history is bounded");
await chatWorld.Disconnect(ca);await chatWorld.Disconnect(cb);

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
    await Framing.WriteAsync(tls,new Command{Sequence=4,Kind="chat",ChatText="실제 TLS 채팅",DisplayName="forged"}.ToByteArray(),CancellationToken.None);
    var sentChat=Update.Parser.ParseFrom(await Framing.ReadAsync(tls,CancellationToken.None));
    await Framing.WriteAsync(tls2,new Command{Sequence=2,Kind="snapshot"}.ToByteArray(),CancellationToken.None);
    var peerChat=Update.Parser.ParseFrom(await Framing.ReadAsync(tls2,CancellationToken.None));
    Check(sentChat.Code=="ok"&&peerChat.Chat.Single().Sender=="network"&&peerChat.Chat[0].Text=="실제 TLS 채팅","chat traverses TLS protobuf between two clients without spoofing");
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
    var pgOwner=Account.Create("pc_"+Guid.NewGuid().ToString("N")[..10],"test-pass-123");
    var pgOther=Account.Create("pc_"+Guid.NewGuid().ToString("N")[..10],"test-pass-123");
    await postgres.Save(pgOwner,null);await postgres.Save(pgOther,null);
    string uniqueName="C"+Guid.NewGuid().ToString("N")[..10];
    var made=await new CharacterCatalog(postgres).Create(pgOwner,new Command{DisplayName=uniqueName,Appearance=new Appearance{Archetype="나린",HairColor=2}});
    Check(made.Code=="ok"&&(await postgres.Load(pgOwner.Name))!.Characters![0].Look.HairColor==2,"PostgreSQL character and appearance persistence");
    var regionSaved=made.Account with {Revision=made.Account.Revision+1,Characters=[made.Account.Characters![0] with {RegionId="coast",X=-15.5f}]};
    await postgres.Save(regionSaved,made.Account.Revision);
    Check((await postgres.Load(pgOwner.Name))!.Characters![0].RegionId=="coast","PostgreSQL destination region persistence");
    Check(await postgres.CharacterNameExists(CharacterCatalog.NameKey(uniqueName.ToLowerInvariant())),"PostgreSQL normalized name index");
    bool duplicateName=false;
    try {await postgres.Save(pgOther with {Revision=1,Characters=[made.Account.Characters![0] with {Id=Guid.NewGuid().ToString("N")}]},0);}
    catch(Npgsql.PostgresException e) when(e.SqlState=="23505"){duplicateName=true;}
    var unchanged=await postgres.Load(pgOther.Name);
    Check(duplicateName&&unchanged!.Revision==0&&unchanged.Characters!.Length==0,"PostgreSQL unique-name failure rolls back account payload and revision");
}
else Console.WriteLine("SKIP: PostgreSQL integration (PLANET_TEST_DB not set)");
// Movement policy and actual world regression checks for the shared meadow map.
var diagonal=MovementRules.Evaluate(0,0,(float)(1/Math.Sqrt(2)),(float)(1/Math.Sqrt(2)),.1);
Check(diagonal.Code=="ok" && Math.Abs(Math.Sqrt(diagonal.X*diagonal.X+diagonal.Z*diagonal.Z)-.24)<.00001,"diagonal input does not increase speed");
Check(MovementRules.Evaluate(0,0,1,1,.1).Code=="invalid","unnormalized diagonal cheat rejected");
Check(MovementRules.Evaluate(17.96f,0,1,0,.1).Code=="boundary","walk boundary rejects crossing");
Check(MovementRules.Evaluate(-3.1f,2,1,0,.1).Code=="blocked","rock includes player collision radius");
Check(MovementRules.Evaluate(-3.1f,2,-1,0,.1).Code=="ok","player can move away from rock");
Check(MovementRules.Evaluate(0,0,float.PositiveInfinity,0,.1).Code=="invalid","infinite input rejected");
Check(Math.Abs(MovementRules.Evaluate(0,0,1,0,1000).X-.24f)<.00001,"idle time does not accumulate speed credit");
var moveStore=new FileStore(Path.Combine(dir,"movement"));
var moveWorld=new World(moveStore,()=>time);var mover=new Session();
await moveWorld.Handle(mover,new Command{Sequence=1,Kind="login",Name="walker",Password="test-pass-123"});
Update moveState=null!;
for(ulong i=2;i<100;i++){time+=.1;moveState=await moveWorld.Handle(mover,new Command{Sequence=i,Kind="move",Dx=1});}
Check(moveState.Code=="boundary" && moveState.Players.Single().X<=18,"world enforces boundary under repeated requests");
float lastX=moveState.Players.Single().X;
await moveWorld.Disconnect(mover);
var reloaded=await new World(moveStore,()=>time).Handle(new Session(),new Command{Sequence=1,Kind="login",Name="walker",Password="test-pass-123"});
Check(Math.Abs(reloaded.Players.Single().X-lastX)<.00001,"accepted movement survives disconnect and reload");
// Real server-owned lobby, independent characters, persistence and ownership.
var lobbyStore=new FaultStore(new FileStore(Path.Combine(dir,"lobby")));
var lobbyWorld=new World(lobbyStore,()=>time);
var owner=new Session();var otherOwner=new Session();ulong lobbySeq=0;
async Task<Update> Lobby(Session who,string kind,string display="",string id="",Appearance? look=null)
    =>await lobbyWorld.Handle(who,new Command{Sequence=++lobbySeq,Kind=kind,Name=who==owner?"owner":"other",Password="test-pass-123",LobbyLogin=true,DisplayName=display,CharacterId=id,RealmId="meadow",Appearance=look});
var look=new Appearance{Archetype="나린",HairColor=2,Skin=1,Hair=2,Face=1,Gender=1};
var roster=await Lobby(owner,"login");
Check(!roster.InWorld&&roster.Characters.Count==0,"new account enters empty lobby, not world");
Check((await Lobby(owner,"move")).Code=="not_in_world","lobby cannot move before selecting character");
Check((await Lobby(owner,"check_name","../x")).Code=="invalid_name","invalid character name rejected by server");
Check((await Lobby(owner,"create_character","첫여행자",look:new Appearance{Archetype="unknown"})).Code=="invalid_appearance","forged appearance rejected");
roster=await Lobby(owner,"create_character","첫여행자",look:look);
var firstId=roster.Characters.Single().Id;
Check(roster.Code=="ok"&&roster.Characters[0].Appearance.HairColor==2,"character identity and appearance saved");
await Lobby(otherOwner,"login");
Check((await Lobby(otherOwner,"check_name","첫여행자")).Code=="name_taken","names are unique across accounts");
Check((await Lobby(otherOwner,"enter_world",id:firstId)).Code=="character_not_owned","cannot enter using another account character");
var duplicateCreates=await Task.WhenAll(Lobby(owner,"create_character","동시생성",look:look),Lobby(otherOwner,"create_character","동시생성",look:look));
Check(duplicateCreates.Count(r=>r.Code=="ok")==1&&duplicateCreates.Count(r=>r.Code=="name_taken")==1,"concurrent duplicate create grants name once");
lobbyStore.Fail=true;
Check((await Lobby(owner,"create_character","저장실패",look:look)).Code=="save_failed","creation storage failure returned to UI");
lobbyStore.Fail=false;
Check((await Lobby(owner,"characters")).Characters.All(c=>c.Name!="저장실패"),"failed creation not applied to roster");
var entered=await Lobby(owner,"enter_world",id:firstId);
Check(entered.InWorld&&entered.Players.Single().DisplayName=="첫여행자","owned selection enters correct character");
Check(entered.SelfStats is {Level:1,Hp:275,MaxHp:275,Mp:60,MaxMp:60,Attack:1},"server supplies initial character stats");
Check(entered.Channels.All(c=>c.Capacity==1000),"default channel capacity is 1000");
time+=1;
await lobbyWorld.Handle(owner,new Command{Sequence=++lobbySeq,Kind="move",Dx=1});
await Lobby(owner,"leave_world");
roster=await Lobby(owner,"characters");var secondId=roster.Characters.First(c=>c.Id!=firstId).Id;
entered=await Lobby(owner,"enter_world",id:secondId);
Check(Math.Abs(entered.Players.Single().X+6.2f)<.00001,"second character has independent position");
await lobbyWorld.Disconnect(owner);await lobbyWorld.Disconnect(otherOwner);
lobbyWorld=new World(new FileStore(Path.Combine(dir,"lobby")),()=>time);owner=new Session();
roster=await Lobby(owner,"login");
Check(roster.Characters.Count==2&&roster.Characters.Any(c=>c.Id==firstId&&c.Appearance.Skin==1),"roster and appearance survive fresh server instance");
entered=await Lobby(owner,"enter_world",id:firstId);
Check(Math.Abs(entered.Players.Single().X-(-6.2f+.24f))<.00001,"selected character position survives fresh server instance");
await Lobby(owner,"leave_world");
for(int i=0;i<10;i++)await Lobby(owner,"create_character","추가"+i,look:look);
Check((await Lobby(owner,"create_character","초과생성",look:look)).Code=="slots_full","server enforces twelve character slots");
var legacyAccount=Account.Create("oldaccount","test-pass-123") with {Characters=null,X=1,Gold=200,Kills=3,QuestClaimed=true};
await lobbyStore.Save(legacyAccount,null);
var imported=await lobbyWorld.Handle(new Session(),new Command{Sequence=1,Kind="login",Name="oldaccount",Password="test-pass-123",LobbyLogin=true});
Check(imported.Characters.Count==1,"old account progress migrated into character roster");
var oldStored=await lobbyStore.Load("oldaccount");
Check(oldStored!.Characters![0].Gold==200&&oldStored.Characters[0].QuestClaimed,"legacy migration preserves rewards");
// Region transitions are server-authoritative and scoped by channel.
var regionStore=new FaultStore(new FileStore(Path.Combine(dir,"regions")));
await regionStore.Save(Account.Create("portaluser","test-pass-123") with {X=16.5f},null);
var regionWorld=new World(regionStore,()=>time);var traveler=new Session();var neighbor=new Session();ulong rs=0;
async Task<Update> RegionSend(Session session,string kind,string portal="",int channel=0,float dx=0)=>await regionWorld.Handle(session,new Command{Sequence=++rs,Kind=kind,Name=session==traveler?"portaluser":"neighbor",Password="test-pass-123",PortalId=portal,ChannelId=channel,Dx=dx});
await RegionSend(traveler,"login");await RegionSend(neighbor,"login");
Check((await RegionSend(neighbor,"portal","east")).Code=="portal_denied","remote portal activation rejected");
regionStore.Fail=true;
var failedTransfer=await RegionSend(traveler,"portal","east");
Check(failedTransfer.Code=="save_failed"&&failedTransfer.RegionId=="meadow"&&failedTransfer.Players.Count==2,"failed portal save leaves player in source region");
regionStore.Fail=false;var moved=await RegionSend(traveler,"portal","east");
Check(moved.Code=="ok"&&moved.RegionId=="coast"&&moved.Players.Single().X==-15.5f,"server chooses linked region and arrival position");
Check((await RegionSend(neighbor,"snapshot")).Players.Count==1,"source region no longer includes transferred player");
Check((await RegionSend(traveler,"portal","north")).Code=="portal_denied","closed exit rejected");
Check((await RegionSend(traveler,"portal","west")).Code=="transfer_cooldown","portal bounce cooldown enforced");
await regionWorld.Disconnect(traveler);traveler=new Session();
Check((await RegionSend(traveler,"login")).RegionId=="coast","region and destination survive reconnect");
time+=2;await RegionSend(traveler,"portal","west");
Check((await RegionSend(neighbor,"snapshot")).Players.Count==2,"linked return portal rejoins original region");
for(int i=0;i<10;i++){time+=.1;await RegionSend(neighbor,"move",dx:1);}
time+=1;await RegionSend(neighbor,"attack");
Check((await RegionSend(traveler,"snapshot")).Monster.Hp==2,"same region and channel share monster state");
time+=2;var differentChannel=await RegionSend(traveler,"switch_channel",channel:30);
Check(differentChannel.ChannelId==30&&differentChannel.Players.Count==1&&differentChannel.Monster.Hp==3,"channel thirty isolates players and monsters");
Check((await RegionSend(traveler,"switch_channel",channel:31)).Code=="invalid_channel","out of range channel rejected");
Check(Regions.BlockReason("meadow",18.2f,0)=="boundary"&&Regions.BlockReason("coast",0,22.2f)=="boundary","unopened region edges cannot be walked through");
// A deliberately stalled durable write must not stop an unrelated channel.
var delayed=new DelayedStore(new FileStore(Path.Combine(dir,"parallel")));
string fastName="fastuser";
while(((uint)StringComparer.Ordinal.GetHashCode(fastName)%256)==((uint)StringComparer.Ordinal.GetHashCode("slowuser")%256))fastName+="x";
await delayed.Save(Account.Create("slowuser","test-pass-123") with {Kills=3},null);
double parallelTime=100;var parallelWorld=new World(delayed,()=>parallelTime);var slowSession=new Session();var fastSession=new Session();
await parallelWorld.Handle(slowSession,new Command{Sequence=1,Kind="login",Name="slowuser",Password="test-pass-123"});
await parallelWorld.Handle(fastSession,new Command{Sequence=1,Kind="login",Name=fastName,Password="test-pass-123"});
await parallelWorld.Handle(fastSession,new Command{Sequence=2,Kind="switch_channel",ChannelId=30});
delayed.Block=true;
var stalled=parallelWorld.Handle(slowSession,new Command{Sequence=2,Kind="claim"});
await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
try {
    var independent=await parallelWorld.Handle(fastSession,new Command{Sequence=3,Kind="move",Dx=1}).WaitAsync(TimeSpan.FromSeconds(2));
    Check(independent.Code=="ok"&&!stalled.IsCompleted,"other channel moves while reward persistence is stalled");
} finally {delayed.Release.TrySetResult();}
Check((await stalled).Players.Single().Gold==100,"stalled reward commits once after storage resumes");
parallelTime+=2;var swapA=parallelWorld.Handle(slowSession,new Command{Sequence=3,Kind="switch_channel",ChannelId=30});
var swapB=parallelWorld.Handle(fastSession,new Command{Sequence=4,Kind="switch_channel",ChannelId=1});
await Task.WhenAll(swapA,swapB).WaitAsync(TimeSpan.FromSeconds(3));
Check(swapA.Result.Code=="ok" && swapB.Result.Code=="ok","opposing channel requests finish without deadlock");
// Admission transactions: a pending seat counts against capacity until commit/dispose.
var directory=new ChannelDirectory(1);
using(var seat=directory.Reserve("pending",1)!) {
    Check(directory.Reserve("other",1)==null,"pending reservation prevents overbooking");
    Check(directory.Snapshot()[0].Online==0&&directory.Snapshot()[0].Reserved==1,"pending and active seats reported separately");
}
Check(directory.Snapshot()[0].Reserved==0,"abandoned request releases reservation");
var raceSeats=await Task.WhenAll(Enumerable.Range(0,40).Select(i=>Task.Run(()=>directory.Reserve("race"+i,1))));
Check(raceSeats.Count(x=>x!=null)==1,"forty simultaneous reservations admit one into last slot");
foreach(var seat in raceSeats)seat?.Dispose();
for(int i=1;i<=Regions.ChannelCount;i++){using var seat=directory.Reserve("filled"+i,0)!;Check(seat.Channel==i,"automatic assignment selects least occupied channel "+i);seat.Commit();}
Check(directory.Reserve("overflow",0)==null,"automatic admission refuses full world");
directory.Remove("filled7");directory.Remove("filled7");
using(var seat=directory.Reserve("replacement",0)!)Check(seat.Channel==7,"released seat becomes available and cleanup is idempotent");
var admissionStore=new FaultStore(new FileStore(Path.Combine(dir,"admission")));
for(int i=0;i<3;i++)await admissionStore.Save(Account.Create("admit"+i,"test-pass-123") with {Characters=[new CharacterData("char"+i,"입장"+i,new())]},null);
var admissionWorld=new World(admissionStore,channelCapacity:1);var entrants=Enumerable.Range(0,3).Select(_=>new Session()).ToArray();ulong admissionSequence=0;
Task<Update> Admit(int i,string kind,int channel=0)=>admissionWorld.Handle(entrants[i],new Command{Sequence=++admissionSequence,Kind=kind,Name="admit"+i,Password="test-pass-123",LobbyLogin=true,CharacterId="char"+i,RealmId="meadow",ChannelId=channel});
for(int i=0;i<3;i++)await Admit(i,"login");
Check((await Admit(0,"snapshot")).Channels.All(c=>c.Online==0),"lobby connections do not consume channel seats");
var entrantsRace=await Task.WhenAll(Admit(0,"enter_world",1),Admit(1,"enter_world",1));
Check(entrantsRace.Count(x=>x.Code=="ok")==1&&entrantsRace.Count(x=>x.Code=="channel_full")==1,"simultaneous world entries cannot exceed capacity");
int winner=entrantsRace[0].Code=="ok"?0:1,loser=1-winner;
var autoEntry=await Admit(loser,"enter_world");
Check(autoEntry.Code=="ok"&&autoEntry.ChannelId==2,"world entry automatically selects free channel");
var deniedSwitch=await Admit(loser,"switch_channel",1);
Check(deniedSwitch.Code=="channel_full"&&deniedSwitch.ChannelId==2&&deniedSwitch.InWorld,"full channel transfer preserves source membership");
Check((await Admit(loser,"snapshot")).Channels.Single(c=>c.Id==2).Online==1,"failed transfer does not release original seat");
admissionStore.Fail=true;
Check((await Admit(winner,"leave_world")).Code=="save_failed","failed departure save keeps player in world");
Check((await Admit(2,"enter_world",1)).Code=="channel_full","failed departure retains occupied seat");
admissionStore.Fail=false;await Admit(winner,"leave_world");
Check((await Admit(2,"enter_world",1)).Code=="ok","successful departure releases seat for next player");
admissionStore.Fail=true;
try{await admissionWorld.Disconnect(entrants[2]);}catch(IOException){}
admissionStore.Fail=false;
Check((await Admit(winner,"enter_world",1)).Code=="ok","disconnect save failure still releases admission seat");
Check((await Admit(winner,"snapshot")).Channels.All(c=>c.Reserved==0&&c.Online<=c.Capacity),"completed requests leave no leaked reservations");
Check(Regions.BlockReason("meadow",4,6.4f)=="blocked","cliff face cannot be walked through");
Check(Regions.BlockReason("meadow",0,6.4f)=="ok","central stairs remain traversable");
Check(RegionTerrain.Height("meadow",0,8)==1.6f,"staircase reaches plateau height");
Check(Regions.BlockReason("coast",4,6.4f)=="ok","meadow cliff does not affect other regions");
// Multi-monster combat: explicit targets, scoped ownership, save-before-death,
// concurrent lethal hits, and incarnation IDs that reject delayed old attacks.
var monsterStore=new FaultStore(new FileStore(Path.Combine(dir,"monsters")));
foreach(var name in new[]{"hunterone","huntertwo"})await monsterStore.Save(Account.Create(name,"test-pass-123") with {X=7,Z=-4},null);
await monsterStore.Save(Account.Create("foresthunter","test-pass-123") with {X=3,Z=0,RegionId="forest"},null);
double combatTime=1000;var combatWorld=new World(monsterStore,()=>combatTime);
var hunterOne=new Session();var hunterTwo=new Session();var forestHunter=new Session();ulong combatSequence=0;
Task<Update> CombatSend(Session session,string kind,string target="",string name="hunterone",int channel=0)=>combatWorld.Handle(session,new Command{Sequence=++combatSequence,Kind=kind,TargetId=target,Name=name,Password="test-pass-123",ChannelId=channel});
var populated=await CombatSend(hunterOne,"login");await CombatSend(hunterTwo,"login",name:"huntertwo");
var forestPopulation=await CombatSend(forestHunter,"login",name:"foresthunter");
Check(populated.Monsters.Count==5&&populated.Monsters.Select(m=>m.Id).Distinct().Count()==5,"meadow publishes five unique monster IDs");
Check(populated.Monsters.All(m=>Regions.BlockReason("meadow",m.X,m.Z)=="ok"),"all spawn slots are walkable");
Check(Update.Parser.ParseFrom(populated.ToByteArray()).Monsters.Count==5,"monster list survives Protobuf round trip");
var targetMonster=populated.Monsters.Single(m=>m.X==7&&m.Z==-4);string targetId=targetMonster.Id;
Check((await CombatSend(hunterOne,"attack","invented-id")).Code=="no_target","unknown target ID rejected");
combatTime+=1;
Check((await CombatSend(hunterOne,"attack",forestPopulation.Monsters[0].Id)).Code=="no_target","another region's monster ID rejected");
combatTime+=1;
Check((await CombatSend(hunterOne,"attack",populated.Monsters.Single(m=>m.X==11).Id)).Code=="out_of_range","explicit distant target rejected");
combatTime+=1;
var firstHit=await CombatSend(hunterOne,"attack",targetId);
Check(firstHit.Monsters.Single(m=>m.Id==targetId).Hp==2&&firstHit.Monsters.Where(m=>m.Id!=targetId).All(m=>m.Hp==3),"targeted damage leaves other monsters unchanged");
combatTime+=1;await CombatSend(hunterOne,"attack",targetId);
monsterStore.Fail=true;combatTime+=1;bool monsterSaveFailed=false;
try{await CombatSend(hunterOne,"attack",targetId);}catch(IOException){monsterSaveFailed=true;}
var uncommitted=await CombatSend(hunterTwo,"snapshot");
Check(monsterSaveFailed&&uncommitted.Monsters.Single(m=>m.Id==targetId).Hp==1&&uncommitted.Players.Sum(p=>p.Shards)==0,"failed lethal save keeps monster alive and awards nothing");
monsterStore.Fail=false;combatTime+=1;
var lethalRace=await Task.WhenAll(CombatSend(hunterOne,"attack",targetId),CombatSend(hunterTwo,"attack",targetId));
Check(lethalRace.Count(r=>r.Code=="ok")==1&&lethalRace.Count(r=>r.Code=="no_target")==1,"simultaneous lethal requests commit one death");
var afterKill=await CombatSend(hunterTwo,"snapshot");
Check(afterKill.Players.Sum(p=>p.Kills)==1&&afterKill.Players.Sum(p=>p.Shards)==1,"one monster grants exactly one kill and shard across players");
Check(afterKill.Monsters.Single(m=>m.Id==targetId).Hp==0,"peer sees the same dead monster");
combatTime+=2.9;
Check((await CombatSend(hunterTwo,"snapshot")).Monsters.Single(m=>m.Id==targetId).Hp==0,"monster does not respawn before its deadline");
combatTime+=.2;var respawned=await CombatSend(hunterOne,"snapshot");
var newLife=respawned.Monsters.Single(m=>m.X==7&&m.Z==-4);
Check(newLife.Id!=targetId&&newLife.Hp==3&&respawned.Monsters.Count==5,"respawn restores only its slot with a fresh incarnation ID");
Check((await CombatSend(hunterOne,"attack",targetId)).Code=="no_target","delayed attack cannot damage a new incarnation");
combatTime+=1;await CombatSend(hunterTwo,"switch_channel",channel:2);
Check((await CombatSend(hunterTwo,"attack",newLife.Id)).Code=="no_target","another channel's monster ID rejected");
await combatWorld.Disconnect(hunterOne);await combatWorld.Disconnect(hunterTwo);await combatWorld.Disconnect(forestHunter);
var storedHunters=await Task.WhenAll(monsterStore.Load("hunterone"),monsterStore.Load("huntertwo"));
Check(storedHunters.Sum(a=>a!.Shards)==1&&storedHunters.Sum(a=>a!.Kills)==1,"single lethal reward persists after both hunters disconnect");
var statsStore=new FileStore(Path.Combine(dir,"stats"));
var statsAccount=Account.Create("statsuser","test-pass-123") with {Progress=new CharacterProgress(3,100,287,61)};
await statsStore.Save(statsAccount,null);
var statsWorld=new World(statsStore);var statsSession=new Session();
var statsReply=await statsWorld.Handle(statsSession,new Command{Kind="login",Sequence=1,Name="statsuser",Password="test-pass-123"});
Check(statsReply.SelfStats is {Level:3,Experience:100,Hp:287,MaxHp:315,Mp:61,MaxMp:70},"persisted progress derives server stat bounds");
await statsWorld.Disconnect(statsSession);
var reloadedStats=await statsStore.Load("statsuser");
Check(reloadedStats!.Progress==statsAccount.Progress,"disconnect preserves progression");
Check(CharacterStatsRules.View(null).Hp==275,"old saves without stats retain defaults");

var authStore=new FileStore(Path.Combine(dir,"authentication"));var authWorld=new World(authStore);
Command Auth(string kind,string password="test-pass-123")=>new Command{Sequence=1,Kind=kind,Name="authuser",Password=password,LobbyLogin=true};
Check((await authWorld.Handle(new Session(),Auth("authenticate"))).Code=="invalid_login"&&await authStore.Load("authuser")==null,"unknown login does not create account");
var authOwner=new Session();Check((await authWorld.Handle(authOwner,Auth("register"))).Code=="ok","explicit registration authenticates");
Check((await authWorld.Handle(new Session(),Auth("register"))).Code=="account_exists","duplicate registration rejected");
Check((await authWorld.Handle(new Session(),Auth("authenticate","wrong-pass-123"))).Code=="invalid_login","wrong password rejected");
Check((await authWorld.Handle(new Session(),Auth("authenticate"))).Code=="duplicate_login","duplicate authenticated connection rejected");
Check((await authWorld.Handle(new Session(),new Command{Sequence=1,Kind="characters"})).Code=="unauthorized","roster requires authentication");
await authWorld.Disconnect(authOwner);
var authReturning=new Session();Check((await authWorld.Handle(authReturning,Auth("authenticate"))).Code=="ok","login succeeds after logout");
await authWorld.Disconnect(authReturning);

var travelStorage=new FaultStore(new FileStore(Path.Combine(dir,"transport")));
await travelStorage.Save(Account.Create("transportTester","test-pass-123") with {X=-6.2f,Z=-2.2f},null);
double travelTime=100;var travelWorld=new World(travelStorage,()=>travelTime);var transportTester=new Session();ulong travelSeq=0;
Task<Update> TravelCommand(string kind,string target="")=>travelWorld.Handle(transportTester,new Command{Sequence=++travelSeq,Kind=kind,TargetId=target,Name="transportTester",Password="test-pass-123"});
await TravelCommand("login");
Check((await TravelCommand("travel_begin","invented")).Code=="travel_denied","unknown transport route rejected");
var departure=await TravelCommand("travel_begin","meadow_coast");string journeyTicket=departure.Message;
Check(departure.Code=="ok"&&journeyTicket.Length==32,"station grants travel ticket");
Check((await TravelCommand("move")).Code=="in_transit","travel locks gameplay requests");
Check((await TravelCommand("travel_commit","wrong-ticket")).Code=="travel_invalid","travel ticket ownership enforced");
Check((await TravelCommand("travel_commit",journeyTicket)).Code=="travel_too_early","departure cannot be bypassed");
travelTime+=13;travelStorage.Fail=true;
Check((await TravelCommand("travel_commit",journeyTicket)).Code=="save_failed","failed arrival save rejected");travelStorage.Fail=false;
Check((await TravelCommand("snapshot")).RegionId=="meadow","save failure retains source region");
var landed=await TravelCommand("travel_commit",journeyTicket);
Check(landed.Code=="ok"&&landed.RegionId=="coast"&&landed.ChannelId==1,"airship changes region while preserving channel");
var revision=(await travelStorage.Load("transportTester"))!.Revision;
Check((await TravelCommand("travel_commit",journeyTicket)).Code=="ok"&&(await travelStorage.Load("transportTester"))!.Revision==revision,"repeated commit does not save twice");
Check((await TravelCommand("travel_finish",journeyTicket)).Code=="ok","landing unlocks player");
Check((await TravelCommand("travel_finish",journeyTicket)).Code=="ok","landing retry idempotent");
travelTime+=2;var returning=await TravelCommand("travel_begin","coast_meadow");await travelWorld.Disconnect(transportTester);
Check((await travelStorage.Load("transportTester"))!.RegionId=="coast","disconnect before commit restores departure region");
transportTester=new Session();travelSeq=0;await TravelCommand("login");returning=await TravelCommand("travel_begin","coast_meadow");travelTime+=13;await TravelCommand("travel_commit",returning.Message);await travelWorld.Disconnect(transportTester);
Check((await travelStorage.Load("transportTester"))!.RegionId=="meadow","disconnect after commit restores destination");
Console.WriteLine($"RESULT: {passed} assertions passed. Temporary test data: {dir}");

sealed class FaultStore(IStore inner) : IStore
{
    public bool Fail;
    public Task<Account?> Load(string name) => inner.Load(name);
    public Task<bool> CharacterNameExists(string name) => inner.CharacterNameExists(name);
    public Task Save(Account account, long? expectedRevision) => Fail
        ? Task.FromException(new IOException("Injected storage failure")) : inner.Save(account, expectedRevision);
}

sealed class DelayedStore(IStore inner):IStore {
    public bool Block;
    public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<Account?> Load(string name)=>inner.Load(name);
    public Task<bool> CharacterNameExists(string name)=>inner.CharacterNameExists(name);
    public async Task Save(Account account,long? revision) {if(Block&&account.Name=="slowuser"){Entered.TrySetResult();await Release.Task;}await inner.Save(account,revision);}
}
