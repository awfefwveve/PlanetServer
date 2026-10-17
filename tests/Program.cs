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

    // Relational layout: bag slots, worn items and skills come back from their own tables.
    var hero=made.Account.Characters![0];
    var gear=InventoryRules.Add(InventoryData.Empty(),[new ItemStack("potion_red",7),new ItemStack("twig_sword",1)]).Inventory!;
    gear=InventoryRules.Equip(gear,1,1).Inventory;
    var geared=regionSaved with {Revision=regionSaved.Revision+1,Characters=[hero with {Inventory=gear,Skills=new SkillData(new(){{"pebble_strike",2},{"sprout_heal",1}},3),Progress=new CharacterProgress(4,250,300,50)}]};
    await postgres.Save(geared,regionSaved.Revision);
    var back=(await postgres.Load(pgOwner.Name))!.Characters![0];
    Check(back.Inventory!.Slots[0]==new ItemStack("potion_red",7)&&back.Inventory.Slots.Count(s=>s is not null)==1,"PostgreSQL bag slots round-trip as rows");
    Check(back.Inventory.Equipped.SequenceEqual(gear.Equipped),"PostgreSQL equipment round-trips as rows");
    Check(back.Skills!.Points==3&&back.Skills.Levels["pebble_strike"]==2&&back.Skills.Levels.Count==2,"PostgreSQL skills round-trip as rows");
    Check(back.Progress==new CharacterProgress(4,250,300,50),"PostgreSQL level and experience columns");
    async Task<long> Count(string sql){await using var c=new Npgsql.NpgsqlConnection(db);await c.OpenAsync();await using var q=new Npgsql.NpgsqlCommand(sql,c);q.Parameters.AddWithValue(pgOwner.Name);return (long)(await q.ExecuteScalarAsync())!;}
    Check(await Count("SELECT count(*) FROM inventory_items WHERE account_key=$1")==1&&await Count("SELECT count(*) FROM equipment WHERE account_key=$1")==1,"PostgreSQL stores one row per occupied slot");
    var emptied=geared with {Revision=geared.Revision+1,Characters=[back with {Inventory=InventoryData.Empty()}]};
    await postgres.Save(emptied,geared.Revision);
    Check(await Count("SELECT count(*) FROM inventory_items WHERE account_key=$1")==0,"PostgreSQL removes rows of items that left the bag");

    // Trade: two accounts in one transaction. A stale second account rolls back the first.
    var giver=(await postgres.Load(pgOwner.Name))!;var taker=(await postgres.Load(pgOther.Name))!;
    bool tradeConflict=false;
    try {await postgres.SaveMany([(giver with {Revision=giver.Revision+1,Characters=[giver.Characters![0] with {Gold=0}]},giver.Revision),(taker with {Revision=taker.Revision+1},taker.Revision-1)]);}
    catch(InvalidOperationException){tradeConflict=true;}
    Check(tradeConflict&&(await postgres.Load(pgOwner.Name))!.Revision==giver.Revision,"PostgreSQL trade conflict leaves both accounts unchanged");

    // Pre-lobby accounts keep their play state on the account (legacy row) and no roster.
    var pgVeteran=Account.Create("pv_"+Guid.NewGuid().ToString("N")[..10],"test-pass-123") with {Characters=null,Gold=40,X=1.5f,RegionId="coast",Progress=new CharacterProgress(2,20,280,61)};
    await postgres.Save(pgVeteran,null);
    var pgVeteranBack=(await postgres.Load(pgVeteran.Name))!;
    Check(pgVeteranBack.Characters is null&&pgVeteranBack.Gold==40&&pgVeteranBack.X==1.5f&&pgVeteranBack.RegionId=="coast"&&pgVeteranBack.Progress==pgVeteran.Progress,"PostgreSQL legacy account state round-trip");
    Check(await postgres.CharacterNameExists(CharacterCatalog.NameKey(pgVeteran.Name)),"PostgreSQL legacy account name stays reserved");
    Check((await postgres.Load(pgVeteran.Name.ToUpperInvariant()))?.Gold==40,"PostgreSQL account lookup ignores letter case");

    // Accounts saved by the old one-document layout are copied into the tables on startup.
    var oldName="po_"+Guid.NewGuid().ToString("N")[..10];
    var oldAccount=Account.Create(oldName,"test-pass-123") with {Revision=5,Characters=[hero with {Id=Guid.NewGuid().ToString("N"),Name="O"+Guid.NewGuid().ToString("N")[..10],Gold=77,Inventory=gear}]};
    await using(var c=new Npgsql.NpgsqlConnection(db)) {
        await c.OpenAsync();
        await using(var create=new Npgsql.NpgsqlCommand("CREATE TABLE IF NOT EXISTS planet_accounts(name text PRIMARY KEY, revision bigint NOT NULL, payload jsonb NOT NULL, updated_at timestamptz NOT NULL DEFAULT now())",c))await create.ExecuteNonQueryAsync();
        await using var q=new Npgsql.NpgsqlCommand("INSERT INTO planet_accounts(name,revision,payload) VALUES($1,5,$2::jsonb)",c);
        q.Parameters.AddWithValue(oldName);q.Parameters.AddWithValue(System.Text.Json.JsonSerializer.Serialize(oldAccount));await q.ExecuteNonQueryAsync();
    }
    await using(var restarted=new PostgresStore(db)) {
        await restarted.Initialize();
        var migrated=await restarted.Load(oldName);
        Check(migrated?.Revision==5&&migrated.Characters![0].Gold==77&&migrated.Characters[0].Inventory!.Equipped.Count==1&&migrated.Verify("test-pass-123"),"PostgreSQL migrates old jsonb accounts into tables");
        await restarted.Initialize();
        Check((await restarted.Load(oldName))?.Revision==5,"PostgreSQL migration is safe to run twice");
    }
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
// Server tick + push: state reaches subscribed clients without a request.
var pushStore=new FileStore(Path.Combine(dir,"push"));
await pushStore.Save(Account.Create("pushhunter","test-pass-123") with {X=2},null);
double pushTime=500;var pushWorld=new World(pushStore,()=>pushTime);
var watcher=new Session();var pushWalker=new Session();var pushHunter=new Session();var oldStyle=new Session();var stuck=new Session();
var watcherFrames=new List<Update>();var oldFrames=new List<Update>();
watcher.Deliver=frame=>{watcherFrames.Add(Update.Parser.ParseFrom(frame));return true;};
oldStyle.Deliver=frame=>{oldFrames.Add(Update.Parser.ParseFrom(frame));return true;};
stuck.Deliver=_=>false;pushHunter.Deliver=_=>true;
ulong pushSeq=0;
Task<Update> PushSend(Session s,string kind,string name,bool subscribe=false,float dx=0,string text="",int channel=0)=>pushWorld.Handle(s,new Command{Sequence=++pushSeq,Kind=kind,Name=name,Password="test-pass-123",SubscribeUpdates=subscribe,Dx=dx,ChatText=text,ChannelId=channel});
await PushSend(watcher,"login","pushwatcher",subscribe:true);
await PushSend(pushWalker,"login","pushwalker");
await PushSend(oldStyle,"login","pushold");
await PushSend(stuck,"login","pushstuck",subscribe:true);
await PushSend(pushHunter,"login","pushhunter",subscribe:true);
pushTime+=1;await PushSend(pushWalker,"move","pushwalker",dx:1);
await pushWorld.TickOnce();
var pushed=watcherFrames.Single();
Check(pushed.Push&&pushed.Sequence==0&&pushed.Code=="tick","subscribed client receives a server push without sending a request");
Check(Math.Abs(pushed.Players.Single(p=>p.Name=="pushwalker").X-.24f)<.00001,"push carries another player's accepted movement");
Check(oldFrames.Count==0,"clients that did not opt in never receive unsolicited frames");
Check(pushed.InWorld&&pushed.ChannelId==1&&pushed.SelfStats!=null&&pushed.Channels.Count==Regions.ChannelCount,"shared room bytes and personal bytes merge into one Update");
var pushReply=await PushSend(watcher,"snapshot","pushwatcher");
Check(pushReply.Players.SequenceEqual(pushed.Players)&&pushReply.Monsters.SequenceEqual(pushed.Monsters),"push and reply describe the same room state");
Check(pushReply.Tick>pushed.Tick,"tick numbers increase across pushes and replies");
for(int hit=0;hit<3;hit++){pushTime+=.5;await PushSend(pushHunter,"attack","pushhunter");}
watcherFrames.Clear();pushTime+=3.1;await pushWorld.TickOnce();
Check(watcherFrames.Single().Monster.Hp==3,"monster respawns on the server tick without any client request");
pushTime+=2;await PushSend(pushWalker,"chat","pushwalker",text:"푸시 채팅");
watcherFrames.Clear();await pushWorld.TickOnce();await pushWorld.TickOnce();
Check(watcherFrames.Count==2&&watcherFrames[0].Chat.Count(c=>c.Text=="푸시 채팅")==1&&watcherFrames[1].Chat.Count==0,"room chat is pushed exactly once");
pushTime+=2;await PushSend(pushWalker,"switch_channel","pushwalker",channel:2);
watcherFrames.Clear();await pushWorld.TickOnce();
Check(watcherFrames.Single().Players.All(p=>p.Name!="pushwalker"),"push is scoped to channel");
await pushWorld.Disconnect(watcher);watcherFrames.Clear();await pushWorld.TickOnce();
Check(watcherFrames.Count==0,"disconnected session receives no further pushes");
await pushWorld.Disconnect(pushWalker);await pushWorld.Disconnect(oldStyle);await pushWorld.Disconnect(stuck);await pushWorld.Disconnect(pushHunter);
// Real ticker over TLS: frames arrive while the client stays silent, and replies are still matched.
using (var pushCert = GameHost.DevelopmentCertificate(Path.Combine(dir, "push-tls")))
await using (var pushHost = new GameHost(new World(new FileStore(Path.Combine(dir, "push-network"))), pushCert, 0))
{
    pushHost.Start(); var pushRunning = pushHost.Run();
    var pushPin = pushCert.GetCertHashString(HashAlgorithmName.SHA256);
    using var pushClient = new TcpClient(); await pushClient.ConnectAsync("127.0.0.1", pushHost.Port);
    using var pushTls = new SslStream(pushClient.GetStream(), false, (_, cert, _, _) => cert != null &&
        Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())) == pushPin);
    await pushTls.AuthenticateAsClientAsync("localhost");
    using var pushTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await Framing.WriteAsync(pushTls, new Command { Sequence = 1, Kind = "login", Name = "livepush", Password = "test-pass-123", SubscribeUpdates = true }.ToByteArray(), pushTimeout.Token);
    int pushes = 0; bool loginReply = false; var listen = System.Diagnostics.Stopwatch.StartNew();
    while (listen.ElapsedMilliseconds < 1000)
    {
        var frame = Update.Parser.ParseFrom(await Framing.ReadAsync(pushTls, pushTimeout.Token));
        if (frame.Push) pushes++; else loginReply |= frame.Sequence == 1 && frame.Code == "ok";
    }
    Check(loginReply && pushes >= 5, $"live ticker pushes about every 100 ms over TLS ({pushes} in 1 s)");
    await Framing.WriteAsync(pushTls, new Command { Sequence = 2, Kind = "snapshot" }.ToByteArray(), pushTimeout.Token);
    Update matched;
    do matched = Update.Parser.ParseFrom(await Framing.ReadAsync(pushTls, pushTimeout.Token)); while (matched.Push);
    Check(matched.Sequence == 2, "reply is found among interleaved pushes by sequence");
}
// Monster AI, player damage, death penalty, revive, experience and level-up (server tick driven).
var (fourLevels,gainedLevels)=CharacterStatsRules.GainExperience(new CharacterProgress(),500+1000+1500+5);
Check(fourLevels.Level==4&&fourLevels.Experience==5&&gainedLevels==3&&fourLevels.Hp==CharacterStatsRules.MaxHp(4),"experience can cross several levels and level-up restores HP");
Check(CharacterStatsRules.GainExperience(new CharacterProgress(100,0),long.MaxValue/2).Progress.Level==100,"level is capped");
var fightStore=new FaultStore(new FileStore(Path.Combine(dir,"fight")));
await fightStore.Save(Account.Create("fighter","test-pass-123") with {X=2},null);
await fightStore.Save(Account.Create("fragile","test-pass-123") with {X=2,Progress=new CharacterProgress(1,100,10,60)},null);
await fightStore.Save(Account.Create("almost","test-pass-123") with {X=2,Progress=new CharacterProgress(1,490,200,60)},null);
await fightStore.Save(Account.Create("veteran","test-pass-123") with {X=2,Progress=new CharacterProgress(4,0,335,75)},null);
double fightTime=2000;ulong fightSeq=0;
var fightFrames=new List<Update>();
Task<Update> Fight(World w,Session s,string name,string kind,string target="",float dx=0)=>w.Handle(s,new Command{Sequence=++fightSeq,Kind=kind,Name=name,Password="test-pass-123",TargetId=target,Dx=dx,SubscribeUpdates=true});
async Task Ticks(World w,double seconds){for(double t=0;t<seconds-1e-9;t+=.1){fightTime+=.1;await w.TickOnce();}}
var fightWorld=new World(fightStore,()=>fightTime);var fighter=new Session{Deliver=f=>{fightFrames.Add(Update.Parser.ParseFrom(f));return true;}};
var fightStart=await Fight(fightWorld,fighter,"fighter","login");
var slime=fightStart.Monsters.Single(m=>m.X==3&&m.Z==0);
await Ticks(fightWorld,1);
Check((await Fight(fightWorld,fighter,"fighter","snapshot")).Monsters.Single(m=>m.Id==slime.Id).State=="idle","passive monster ignores a nearby player");
fightTime+=1;await Fight(fightWorld,fighter,"fighter","attack",slime.Id);
var provoked=(await Fight(fightWorld,fighter,"fighter","snapshot")).Monsters.Single(m=>m.Id==slime.Id);
Check(provoked.State=="chase"&&provoked.Target=="fighter"&&provoked.Hp==2,"being hit makes the monster fight back");
fightFrames.Clear();await Ticks(fightWorld,2);
var hurt=await Fight(fightWorld,fighter,"fighter","snapshot");
Check(hurt.SelfStats.Hp==275-12,"monster attack lands once per interval after a wind-up");
Check(fightFrames.SelectMany(f=>f.Events).Count(e=>e.Kind=="player_hit"&&e.Target=="fighter"&&e.Amount==12)==1,"damage event pushed once to the room");
Check(hurt.Players.Single().Hp==263&&hurt.Players.Single().MaxHp==275,"other players can see HP through PlayerView");
for(int step=0;step<60;step++){fightTime+=.1;await Fight(fightWorld,fighter,"fighter","move",dx:-1);await fightWorld.TickOnce();}
var escaped=await Fight(fightWorld,fighter,"fighter","snapshot");
var chaser=escaped.Monsters.Single(m=>m.Id==slime.Id);
Check(chaser.State!="chase"&&chaser.Target=="","monster gives up beyond its leash");
int beforeRegen=escaped.SelfStats.Hp;
await Ticks(fightWorld,10);
var settled=await Fight(fightWorld,fighter,"fighter","snapshot");
var home=settled.Monsters.Single(m=>m.Id==slime.Id);
Check(home.State=="idle"&&home.X==3&&home.Z==0&&home.Hp==3,"returning monster resets at home with full HP");
Check(settled.SelfStats.Hp>beforeRegen&&settled.SelfStats.Hp<=275,"HP regenerates on the tick after combat ends");
await fightWorld.Disconnect(fighter);
var fragile=new Session();var fragileStart=await Fight(fightWorld,fragile,"fragile","login");
var fragileSlime=fragileStart.Monsters.Single(m=>m.X==3&&m.Z==0);
fightTime+=1;await Fight(fightWorld,fragile,"fragile","attack",fragileSlime.Id);
await Ticks(fightWorld,1);
var fallen=await Fight(fightWorld,fragile,"fragile","snapshot");
Check(fallen.SelfStats.Hp==0&&fallen.Players.Single().Dead,"lethal monster damage kills the player");
Check(fallen.SelfStats.Experience==100-25,"death costs five percent of the level requirement");
Check(fallen.Monsters.Single(m=>m.Id==fragileSlime.Id).State!="chase","monsters stop chasing a dead player");
fightTime+=1;
Check((await Fight(fightWorld,fragile,"fragile","move",dx:1)).Code=="dead"&&(await Fight(fightWorld,fragile,"fragile","attack",fragileSlime.Id)).Code=="dead","dead players cannot move or attack");
fightStore.Fail=true;
Check((await Fight(fightWorld,fragile,"fragile","revive")).Code=="save_failed","failed revive save keeps the player dead");
fightStore.Fail=false;
var revived=await Fight(fightWorld,fragile,"fragile","revive");
Check(revived.Code=="ok"&&revived.SelfStats.Hp==137&&revived.Players.Single().X==0,"revive restores half HP at the safe point");
Check((await fightStore.Load("fragile"))!.Progress!.Hp==137,"revive is persisted");
Check((await Fight(fightWorld,fragile,"fragile","revive")).Code=="not_dead","revive requires being dead");
await Ticks(fightWorld,5); // let the provoked slime walk home and reset
await fightWorld.Disconnect(fragile);
var almost=new Session();var almostStart=await Fight(fightWorld,almost,"almost","login");
var almostSlime=almostStart.Monsters.Single(m=>m.SpawnZone=="practice");Update levelled=null!;
for(int hit=0;hit<3;hit++){fightTime+=.5;levelled=await Fight(fightWorld,almost,"almost","attack",almostSlime.Id);}
Check(levelled.SelfStats.Level==2&&levelled.SelfStats.Experience==30&&levelled.SelfStats.Hp==levelled.SelfStats.MaxHp,"kill experience levels up and refills HP");
Check((await fightStore.Load("almost"))!.Progress!.Level==2,"level-up is saved together with the kill");
await fightWorld.Disconnect(almost);
fightTime+=4; // practice slime respawns after the previous kill
var veteran=new Session();var veteranStart=await Fight(fightWorld,veteran,"veteran","login");
Check(veteranStart.SelfStats.Attack==2,"attack grows with level");
var veteranSlime=veteranStart.Monsters.Single(m=>m.SpawnZone=="practice");
fightTime+=1;var vHit=await Fight(fightWorld,veteran,"veteran","attack",veteranSlime.Id);
fightTime+=1;var vKill=await Fight(fightWorld,veteran,"veteran","attack",veteranSlime.Id);
Check(vHit.Monsters.Single(m=>m.Id==veteranSlime.Id).Hp==1&&vKill.Monsters.Single(m=>m.Id==veteranSlime.Id).Hp==0,"higher attack kills in fewer hits");
await fightWorld.Disconnect(veteran);
// Items, inventory, equipment and skills.
Check(InventoryRules.Add(InventoryData.Empty(),[new ItemStack("potion_red",150)]).Inventory!.Slots.Count(s=>s!=null)==2,"stacks split at the item's maximum");
Check(InventoryRules.Add(InventoryData.Empty(),[new ItemStack("no_such_item",1)]).Code=="unknown_item","unknown items are rejected");
var fullBag=InventoryData.Empty() with {Slots=Enumerable.Repeat<ItemStack?>(new ItemStack("leaf_cap",1),ItemCatalog.BagCapacity).ToArray()};
Check(InventoryRules.Add(fullBag,[new ItemStack("potion_red",1)]).Code=="bag_full","full bag refuses new items without partial changes");
var itemStore=new FaultStore(new FileStore(Path.Combine(dir,"items")));
double itemTime=5000;double nextRoll=.99;string itemCharId="";
var itemWorld=new World(itemStore,()=>itemTime,roll:()=>nextRoll);
var owner2=new Session();ulong itemSeq=0;
Task<Update> Bag(string kind,int slot=0,int to=0,string skill="",string equip="")=>itemWorld.Handle(owner2,new Command{Sequence=++itemSeq,Kind=kind,Name="bagowner",Password="test-pass-123",LobbyLogin=true,Slot=slot,TargetSlot=to,SkillId=skill,EquipSlot=equip,DisplayName="가방주인",CharacterId=itemCharId,RealmId="meadow",Appearance=look});
await Bag("login");itemCharId=(await Bag("create_character")).Characters.Single().Id;
var kit=await Bag("enter_world");
Check(kit.Inventory.Capacity==24&&kit.Inventory.Items.Any(i=>i.ItemId=="potion_red"&&i.Count==5)&&kit.Inventory.Items.Any(i=>i.ItemId=="twig_sword"),"new characters start with a server-granted kit");
Check(kit.Skills.Single().Id=="pebble_strike"&&kit.Skills[0].Level==1&&kit.SkillPoints==0,"new characters know their first skill");
int swordSlot=kit.Inventory.Items.Single(i=>i.ItemId=="twig_sword").Slot,potionSlot=kit.Inventory.Items.Single(i=>i.ItemId=="potion_red").Slot;
var armed=await Bag("equip",swordSlot);
Check(armed.Code=="ok"&&armed.SelfStats.Attack==2&&armed.Inventory.Equipped.Single().ItemId=="twig_sword"&&armed.Inventory.Items.All(i=>i.ItemId!="twig_sword"),"equipping moves the item out of the bag and raises attack");
Check((await itemStore.Load("bagowner"))!.Characters![0].Inventory!.Equipped["weapon"]=="twig_sword","equipment is saved immediately");
Check((await Bag("equip",potionSlot)).Code=="not_equipment","consumables cannot be equipped");
Check((await Bag("use_item",swordSlot)).Code=="empty_slot","empty slots cannot be used");
var movedBag=await Bag("move_item",potionSlot,10);
Check(movedBag.Code=="ok"&&movedBag.Inventory.Items.Single(i=>i.ItemId=="potion_red").Slot==10,"items move between bag slots");
Check((await Bag("move_item",10,99)).Code=="invalid_slot","moving outside the bag is rejected");
var bare=await Bag("unequip",equip:"weapon");
Check(bare.Code=="ok"&&bare.SelfStats.Attack==1&&bare.Inventory.Items.Any(i=>i.ItemId=="twig_sword"),"unequipping returns the item to the bag");
Check((await Bag("unequip",equip:"weapon")).Code=="not_equipped","nothing to unequip is rejected");
itemStore.Fail=true;
Check((await Bag("discard_item",10)).Code=="save_failed","failed save keeps the item");
itemStore.Fail=false;
Check((await Bag("snapshot")).Inventory.Items.Single(i=>i.ItemId=="potion_red").Count==5,"inventory unchanged after a failed save");
Check((await Bag("learn_skill",skill:"sprout_heal")).Code=="level_too_low","skills have level requirements");
await itemWorld.Disconnect(owner2);
itemWorld=new World(new FileStore(Path.Combine(dir,"items")),()=>itemTime,roll:()=>nextRoll);owner2=new Session();
await Bag("login");var restored2=await Bag("enter_world");
Check(restored2.Inventory.Items.Single(i=>i.ItemId=="potion_red").Slot==10&&restored2.Inventory.Items.Any(i=>i.ItemId=="twig_sword"),"inventory survives a fresh server instance");
await itemWorld.Disconnect(owner2);

var lootStore=new FaultStore(new FileStore(Path.Combine(dir,"loot")));
await lootStore.Save(Account.Create("looter","test-pass-123") with {X=2,Progress=new CharacterProgress(1,0,200,60)},null);
double lootTime=7000;double lootRoll=0;
var lootWorld=new World(lootStore,()=>lootTime,roll:()=>lootRoll);var looter=new Session();ulong lootSeq=0;
Task<Update> Loot(string kind,string target="",int slot=0)=>lootWorld.Handle(looter,new Command{Sequence=++lootSeq,Kind=kind,Name="looter",Password="test-pass-123",TargetId=target,Slot=slot});
async Task<Update> KillPractice(){
    var state=await Loot("snapshot");var id=state.Monsters.Single(m=>m.SpawnZone=="practice").Id;Update last=state;
    for(int hit=0;hit<5&&last.Monsters.Single(m=>m.SpawnZone=="practice").Hp>0;hit++){lootTime+=.5;last=await Loot("attack",id);}
    lootTime+=3.5;return last;
}
await Loot("login");
lootStore.Fail=true;bool lootFailed=false;
try{await KillPractice();}catch(IOException){lootFailed=true;}
lootStore.Fail=false;
Check(lootFailed&&(await Loot("snapshot")).Inventory.Items.Count==0,"failed kill save grants no drops");
lootTime+=1;var looted=await KillPractice();
Check(looted.Inventory.Items.Count==7&&looted.Inventory.Items.Any(i=>i.ItemId=="star_blade"),"every winning drop roll lands in the bag");
Check((await lootStore.Load("looter"))!.Inventory!.Slots.Count(s=>s!=null)==7,"drops are saved in the same write as the kill");
int bladeSlot=looted.Inventory.Items.Single(i=>i.ItemId=="star_blade").Slot;
Check((await Loot("equip",slot:bladeSlot)).Code=="level_too_low","equipment level requirement enforced");
var capped=await Loot("equip",slot:looted.Inventory.Items.Single(i=>i.ItemId=="leaf_cap").Slot);
Check(capped.SelfStats.Defense==1,"armor raises defense");
var redSlot=capped.Inventory.Items.Single(i=>i.ItemId=="potion_red").Slot;
var drank=await Loot("use_item",slot:redSlot);
Check(drank.Code=="ok"&&drank.SelfStats.Hp==275&&drank.Inventory.Items.All(i=>i.ItemId!="potion_red"),"potion heals and is consumed");
Check((await Loot("use_item",slot:capped.Inventory.Items.Single(i=>i.ItemId=="potion_blue").Slot)).Code=="item_cooldown","item use has a short cooldown");
lootTime+=1;var practice=(await Loot("snapshot")).Monsters.Single(m=>m.SpawnZone=="practice");
await Loot("attack",practice.Id);
for(int t=0;t<20;t++){lootTime+=.1;await lootWorld.TickOnce();}
Check((await Loot("snapshot")).SelfStats.Hp==275-11,"equipped defense reduces monster damage");
lootTime+=10;
for(int kill=0;kill<5;kill++){lootTime+=1;await KillPractice();}
var overflowState=await Loot("snapshot");
Check(overflowState.Inventory.Items.Count==24,"bag fills up to its capacity");
lootTime+=1;var overflowKill=await KillPractice();
Check(overflowKill.Message.Contains("Bag full")&&overflowKill.Inventory.Items.Count==24,"drops that do not fit are reported and lost, never over-filled");
await lootWorld.Disconnect(looter);

var castStore=new FileStore(Path.Combine(dir,"cast"));
await castStore.Save(Account.Create("caster","test-pass-123") with {X=2,Progress=new CharacterProgress(3,0,200,60),Skills=new SkillData(new(){{"pebble_strike",1}},2)},null);
await castStore.Save(Account.Create("climber","test-pass-123") with {X=2,Progress=new CharacterProgress(1,490,275,60)},null);
double castTime=9000;var castWorld=new World(castStore,()=>castTime,roll:()=>.99);var caster=new Session();ulong castSeq=0;
Task<Update> Cast(string kind,string skill="",string target="",Session? who=null,string name="caster")=>castWorld.Handle(who??caster,new Command{Sequence=++castSeq,Kind=kind,Name=name,Password="test-pass-123",SkillId=skill,TargetId=target});
var castStart=await Cast("login");var castSlime=castStart.Monsters.Single(m=>m.SpawnZone=="practice");
Check((await Cast("skill","no_such_skill",castSlime.Id)).Code=="unknown_skill","unknown skill rejected");
Check((await Cast("skill","petal_whirl")).Code=="not_learned","unlearned skill rejected");
castTime+=1;var struck=await Cast("skill","pebble_strike",castSlime.Id);
Check(struck.Code=="ok"&&struck.Monsters.Single(m=>m.Id==castSlime.Id).Hp==1&&struck.SelfStats.Mp==52,"single-target skill multiplies attack and spends MP");
castTime+=1;
Check((await Cast("skill","pebble_strike",castSlime.Id)).Code=="skill_cooldown","skill cooldown enforced");
Check(struck.Skills.Single(s=>s.Id=="pebble_strike").CooldownRemaining>2,"cooldown remaining is sent to the client");
var learnedWhirl=await Cast("learn_skill","petal_whirl");
Check(learnedWhirl.Code=="ok"&&learnedWhirl.SkillPoints==1&&learnedWhirl.Skills.Any(s=>s.Id=="petal_whirl"),"skill points buy new skills");
Check((await Cast("learn_skill","sprout_heal")).Code=="ok"&&(await Cast("learn_skill","sprout_heal")).Code=="no_skill_points","learning needs a point each time");
Check((await castStore.Load("caster"))!.Skills!.Levels["sprout_heal"]==1,"learned skills are saved");
castTime+=1;var whirled=await Cast("skill","petal_whirl");
Check(whirled.Code=="ok"&&whirled.Players.Single().Kills==1&&whirled.Monsters.Single(m=>m.SpawnZone=="practice").Hp==0,"area skill can finish a monster and reward the kill");
castTime+=1;var healedCast=await Cast("skill","sprout_heal");
Check(healedCast.Code=="ok"&&healedCast.SelfStats.Hp==Math.Min(315,200+315*24/100),"self skill heals a share of max HP");
int mpBefore=healedCast.SelfStats.Mp;
for(int t=0;t<30;t++){castTime+=.1;await castWorld.TickOnce();}
Check((await Cast("snapshot")).SelfStats.Mp>mpBefore,"MP regenerates on the tick");
var climber=new Session();await Cast("login",who:climber,name:"climber");
var climbSlime=(await Cast("snapshot",who:climber,name:"climber")).Monsters.Single(m=>m.SpawnZone=="practice");
castTime+=4;climbSlime=(await Cast("snapshot",who:climber,name:"climber")).Monsters.Single(m=>m.SpawnZone=="practice");
Update climbed=null!;for(int hit=0;hit<3;hit++){castTime+=.5;climbed=await Cast("attack",target:climbSlime.Id,who:climber,name:"climber");}
Check(climbed.SelfStats.Level==2&&climbed.SkillPoints==1,"each level gained grants a skill point");
await castWorld.Disconnect(caster);await castWorld.Disconnect(climber);
// NPC shop and player-to-player trade (two accounts saved in one write).
var shopStore=new FaultStore(new FileStore(Path.Combine(dir,"shop")));
await shopStore.Save(Account.Create("buyer","test-pass-123") with {Gold=100,Inventory=InventoryData.Empty()},null);
double shopTime=11000;var shopWorld=new World(shopStore,()=>shopTime);var buyer=new Session();ulong shopSeq=0;
Task<Update> Shop(string kind,string item="",int slot=0,int count=1)=>shopWorld.Handle(buyer,new Command{Sequence=++shopSeq,Kind=kind,Name="buyer",Password="test-pass-123",TargetId=item,Slot=slot,Count=count});
await Shop("login");
var bought=await Shop("shop_buy","potion_red",count:3);
Check(bought.Code=="ok"&&bought.Players.Single().Gold==40&&bought.Inventory.Items.Single().Count==3,"shop sells for gold and fills the bag");
Check((await Shop("shop_buy","twig_sword")).Code=="not_enough_gold","shop refuses when gold is short");
Check((await Shop("shop_buy","slime_jelly")).Code=="not_sold","items not in stock cannot be bought");
Check((await Shop("shop_buy","potion_red",count:0)).Code=="invalid_count","invalid count rejected");
shopStore.Fail=true;
Check((await Shop("shop_buy","potion_red")).Code=="save_failed"&&(await Shop("snapshot")).Players.Single().Gold==40,"failed purchase save keeps gold");
shopStore.Fail=false;
var sold=await Shop("shop_sell",slot:0,count:2);
Check(sold.Code=="ok"&&sold.Players.Single().Gold==50&&sold.Inventory.Items.Single().Count==1,"selling returns the sell price");
Check((await Shop("shop_sell",slot:0,count:5)).Code=="invalid_count","cannot sell more than owned");
Check((await shopStore.Load("buyer"))!.Gold==50,"shop results are saved");
await shopWorld.Disconnect(buyer);

var tradeStore=new FaultStore(new FileStore(Path.Combine(dir,"trade")));
var sellerBag=InventoryRules.Add(InventoryData.Empty(),[new ItemStack("star_blade",1),new ItemStack("potion_red",10)]).Inventory!;
await tradeStore.Save(Account.Create("tradera","test-pass-123") with {X=0,Gold=500,Inventory=InventoryData.Empty()},null);
await tradeStore.Save(Account.Create("traderb","test-pass-123") with {X=1,Gold=20,Inventory=sellerBag},null);
await tradeStore.Save(Account.Create("farfriend","test-pass-123") with {X=12,Z=-4},null);
double tradeTime=12000;var tradeWorld=new World(tradeStore,()=>tradeTime);
var ta=new Session();var tb=new Session();var tc=new Session();ulong tradeSeq=0;
Task<Update> T(Session s,string name,string kind,string target="",int slot=-1,int count=0,long gold=0)=>tradeWorld.Handle(s,new Command{Sequence=++tradeSeq,Kind=kind,Name=name,Password="test-pass-123",TargetId=target,Slot=slot,Count=count,Gold=gold});
await T(ta,"tradera","login");await T(tb,"traderb","login");await T(tc,"farfriend","login");
Check((await T(ta,"tradera","trade_request","farfriend")).Code=="too_far","trading needs the partner nearby");
Check((await T(ta,"tradera","trade_request","tradera")).Code=="no_partner","cannot trade with yourself");
await T(ta,"tradera","trade_request","traderb");
var invited=await T(tb,"traderb","snapshot");
Check(invited.Trade?.State=="incoming"&&invited.Trade.Partner=="tradera","invitee sees the request");
Check((await T(tc,"farfriend","trade_accept","tradera")).Code=="no_invite","only the invited player can accept");
var opened=await T(tb,"traderb","trade_accept","tradera");
Check(opened.Code=="ok"&&opened.Trade.State=="open","accepting opens the trade for both");
Check((await T(tb,"traderb","discard_item",slot:0)).Code=="trading","bag is locked while trading");
await T(tb,"traderb","trade_offer",slot:0,count:1);
await T(tb,"traderb","trade_offer",slot:1,count:4);
await T(ta,"tradera","trade_offer",gold:300);
Check((await T(ta,"tradera","trade_offer",gold:999)).Code=="not_enough_gold","cannot offer more gold than owned");
await T(ta,"tradera","trade_offer",gold:300);
var view=await T(ta,"tradera","snapshot");
Check(view.Trade.Theirs.Count==2&&view.Trade.MyGold==300,"both sides see the offers");
await T(ta,"tradera","trade_confirm");
await T(tb,"traderb","trade_offer",slot:1,count:5);
Check(!(await T(ta,"tradera","snapshot")).Trade.MyConfirmed,"changing an offer clears confirmations");
await T(ta,"tradera","trade_confirm");
tradeStore.Fail=true;
var failedTrade=await T(tb,"traderb","trade_confirm");
tradeStore.Fail=false;
Check(failedTrade.Code=="save_failed","storage failure is reported");
var diskA=await tradeStore.Load("tradera");var diskB=await tradeStore.Load("traderb");
Check(diskA!.Gold==500&&diskB!.Gold==20&&diskB.Inventory!.Slots.Count(s=>s!=null)==2,"failed trade changes neither account");
Check((await T(ta,"tradera","snapshot")).Inventory.Items.Count==0,"failed trade leaves memory untouched");
await T(ta,"tradera","trade_confirm");
var done=await T(tb,"traderb","trade_confirm");
Check(done.Code=="ok"&&done.Trade==null,"trade completes when both confirm");
var afterA=await T(ta,"tradera","snapshot");
Check(afterA.Inventory.Items.Any(i=>i.ItemId=="star_blade")&&afterA.Inventory.Items.Single(i=>i.ItemId=="potion_red").Count==5&&afterA.Players.Single(p=>p.Name=="tradera").Gold==200,"buyer receives items and pays gold");
Check(done.Inventory.Items.Single(i=>i.ItemId=="potion_red").Count==5&&done.Players.Single(p=>p.Name=="traderb").Gold==320,"seller receives gold and keeps the rest");
diskA=await tradeStore.Load("tradera");diskB=await tradeStore.Load("traderb");
Check(diskA!.Gold==200&&diskB!.Gold==320&&diskA.Inventory!.Slots.Any(s=>s?.ItemId=="star_blade")&&!diskB.Inventory!.Slots.Any(s=>s?.ItemId=="star_blade"),"both accounts saved together");
Check(diskA.Inventory!.Slots.Sum(s=>s?.ItemId=="potion_red"?s.Count:0)+diskB.Inventory!.Slots.Sum(s=>s?.ItemId=="potion_red"?s.Count:0)==10,"no item is duplicated or lost");
await T(ta,"tradera","trade_request","traderb");await T(tb,"traderb","trade_accept","tradera");
await tradeWorld.Disconnect(tb);
Check((await T(ta,"tradera","snapshot")).Trade==null,"partner disconnect closes the trade");
Check((await T(ta,"tradera","discard_item",slot:0)).Code!="trading","bag unlocks after the trade closes");
await tradeWorld.Disconnect(ta);await tradeWorld.Disconnect(tc);
// Parties: invitations, shared experience saved atomically, party and whisper chat.
var partyStore=new FaultStore(new FileStore(Path.Combine(dir,"party")));
foreach(var (who,x,region) in new[]{("pleader",2f,"meadow"),("pmember",2.5f,"meadow"),("pfar",0f,"forest"),("poutsider",1f,"meadow"),("pfour",1f,"meadow"),("pfive",1f,"meadow")})
    await partyStore.Save(Account.Create(who,"test-pass-123") with {X=x,RegionId=region},null);
double partyTime=13000;var partyWorld=new World(partyStore,()=>partyTime,roll:()=>.99);ulong partySeq=0;
var ps=new Dictionary<string,Session>();
Task<Update> P(string who,string kind,string target="",string text="",string scope="")=>partyWorld.Handle(ps[who],new Command{Sequence=++partySeq,Kind=kind,Name=who,Password="test-pass-123",TargetId=target,ChatText=text,ChatScope=scope});
foreach(var who in new[]{"pleader","pmember","pfar","poutsider","pfour","pfive"}){ps[who]=new Session();await P(who,"login");}
Check((await P("pleader","party_invite","nobody")).Code=="no_such_player","inviting an unknown player is rejected");
await P("pleader","party_invite","pmember");
var partyInvite=await P("pmember","snapshot");
Check(partyInvite.Party?.InvitedBy=="pleader"&&partyInvite.Chat.Any(c=>c.Scope=="system"),"invitee sees the invitation");
Check((await P("poutsider","party_accept","pleader")).Code=="no_invite","only the invited player can join");
var joined=await P("pmember","party_accept","pleader");
Check(joined.Code=="ok"&&joined.Party.Members.Count==2&&joined.Party.Members.Single(m=>m.Leader).Name=="pleader","accepting forms a party with the inviter as leader");
Check((await P("pmember","party_invite","poutsider")).Code=="not_leader","only the leader invites");
await P("pleader","party_invite","pfar");await P("pfar","party_accept","pleader");
var slimeId=(await P("pleader","snapshot")).Monsters.Single(m=>m.SpawnZone=="practice").Id;
for(int hit=0;hit<2;hit++){partyTime+=.5;await P("pleader","attack",slimeId);}
partyStore.Fail=true;bool partyFailed=false;partyTime+=.5;
try{await P("pleader","attack",slimeId);}catch(IOException){partyFailed=true;}
partyStore.Fail=false;
var afterFail=await P("pmember","snapshot");
Check(partyFailed&&afterFail.SelfStats.Experience==0&&afterFail.Monsters.Single(m=>m.Id==slimeId).Hp==1,"failed shared save gives nobody experience");
partyTime+=.5;await P("pleader","attack",slimeId);
var leaderAfter=await P("pleader","snapshot");var memberAfter=await P("pmember","snapshot");var farAfter=await P("pfar","snapshot");
Check(leaderAfter.SelfStats.Experience==22&&memberAfter.SelfStats.Experience==22,"nearby party members split experience with a bonus");
Check(farAfter.SelfStats.Experience==0,"members in another region get nothing");
Check((await partyStore.Load("pmember"))!.Progress!.Experience==22&&(await partyStore.Load("pleader"))!.Progress!.Experience==22,"shared experience is saved for every member");
partyTime+=2;await P("pleader","chat",text:"파티 안녕",scope:"party");
Check((await P("pfar","snapshot")).Chat.Any(c=>c.Scope=="party"&&c.Text=="파티 안녕"),"party chat reaches members in other regions");
Check(!(await P("poutsider","snapshot")).Chat.Any(c=>c.Scope=="party"),"outsiders do not receive party chat");
partyTime+=2;var whisperReply=await P("pmember","chat","pfar","귓속말이야",scope:"whisper");
var whispered=await P("pfar","snapshot");
Check(whispered.Chat.Single(c=>c.Scope=="whisper").Sender=="pmember","whisper reaches the named player");
Check(whisperReply.Chat.Any(c=>c.Scope=="whisper"&&c.To=="pfar"),"sender sees their own whisper");
Check(!(await P("pleader","snapshot")).Chat.Any(c=>c.Scope=="whisper"),"whispers stay private");
Check((await P("pfar","snapshot")).Chat.All(c=>c.Scope!="whisper"),"each private message is delivered once");
partyTime+=2;Check((await P("pmember","chat","ghost","hi",scope:"whisper")).Code=="no_such_player","whisper to an unknown name fails");
await P("pleader","party_invite","pfour");await P("pfour","party_accept","pleader");
Check((await P("pleader","party_invite","pfive")).Code=="party_full","parties hold four players");
Check((await P("pmember","party_kick","pfour")).Code=="not_leader","only the leader kicks");
await P("pleader","party_kick","pfour");
Check((await P("pfour","snapshot")).Party==null,"kicked player leaves the party");
await P("pleader","party_leave");
var newLead=await P("pmember","snapshot");
Check(newLead.Party.Members.Count==2&&newLead.Party.Members.Single(m=>m.Leader).Name=="pmember","leadership passes on when the leader leaves");
await partyWorld.Disconnect(ps["pfar"]);
Check((await P("pmember","snapshot")).Party==null,"a party of one disbands");
foreach(var who in new[]{"pleader","pmember","poutsider","pfour","pfive"})await partyWorld.Disconnect(ps[who]);
Console.WriteLine($"RESULT: {passed} assertions passed. Temporary test data: {dir}");

sealed class FaultStore(IStore inner) : IStore
{
    public bool Fail;
    public Task<Account?> Load(string name) => inner.Load(name);
    public Task<bool> CharacterNameExists(string name) => inner.CharacterNameExists(name);
    public Task Save(Account account, long? expectedRevision) => Fail
        ? Task.FromException(new IOException("Injected storage failure")) : inner.Save(account, expectedRevision);
    public Task SaveMany(IReadOnlyList<(Account Account, long? ExpectedRevision)> changes) => Fail
        ? Task.FromException(new IOException("Injected storage failure")) : inner.SaveMany(changes);
}

sealed class DelayedStore(IStore inner):IStore {
    public bool Block;
    public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<Account?> Load(string name)=>inner.Load(name);
    public Task<bool> CharacterNameExists(string name)=>inner.CharacterNameExists(name);
    public async Task Save(Account account,long? revision) {if(Block&&account.Name=="slowuser"){Entered.TrySetResult();await Release.Task;}await inner.Save(account,revision);}
    public Task SaveMany(IReadOnlyList<(Account Account,long? ExpectedRevision)> changes)=>inner.SaveMany(changes);
}
