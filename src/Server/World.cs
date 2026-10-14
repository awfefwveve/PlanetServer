using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Google.Protobuf;
using Planet.Protocol;

namespace Planet.Server;

public sealed class Session
{
    public string? Name;
    public TransportRoute? Travel;public string TravelTicket="",CompletedTravel="";public double TravelStarted;public bool TravelCommitted;
    public bool LobbyLogin;
    public int Channel=1;
    public double LastTransfer=double.NegativeInfinity;
    public ulong Sequence;
    public double LastMove = double.NegativeInfinity;
    public double LastAttack = double.NegativeInfinity;
    public int LoginAttempts;
    public double LastChat = double.NegativeInfinity;
    // Combat bookkeeping used by the tick: regeneration waits after the last hit.
    public double LastHurt = double.NegativeInfinity;
    public double RegenCarry;
    public long ChatAfter;
    // Set at login from Command.subscribe_updates. Only subscribed sessions receive tick pushes.
    public bool Subscribed;
    // Supplied by the transport (GameHost). Must not block: it enqueues a serialized Update
    // and returns false when the connection cannot keep up or is closing.
    public Func<byte[], bool>? Deliver;
}

public sealed class World(IStore store, Func<double>? clock = null, int channelCapacity=1000)
{
    // A bounded account stripe protects duplicate login across sessions. Channel ownership
    // serializes shared gameplay only within a channel; unrelated channels can progress.
    private readonly SemaphoreSlim[] accountGates=Enumerable.Range(0,256).Select(_=>new SemaphoreSlim(1,1)).ToArray();
    private readonly SemaphoreSlim[] channelGates=Enumerable.Range(0,31).Select(_=>new SemaphoreSlim(1,1)).ToArray();
    private readonly ChannelDirectory admission=new(channelCapacity);
    private readonly SemaphoreSlim catalogGate=new(1,1);
    private SemaphoreSlim AccountGate(string name)=>accountGates[(uint)StringComparer.Ordinal.GetHashCode(name)%256];
    private readonly ConcurrentDictionary<string, Account> accounts = new();
    private readonly ConcurrentDictionary<string, CharacterData> players = new();
    private readonly CharacterCatalog catalog=new(store);
    private readonly Func<double> now = clock ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
    private sealed class Room(int channel,string region) {
        public readonly MonsterPopulation Monsters=new(channel,region);public readonly RoomChat Chat=new();
        // Combat events since the last push. Bounded so a room nobody watches cannot grow it.
        private readonly List<CombatEvent> events=[];
        public double LastTick=double.NaN;
        public void Event(CombatEvent e){events.Add(e);if(events.Count>64)events.RemoveAt(0);}
        public CombatEvent[] TakeEvents(){var taken=events.ToArray();events.Clear();return taken;}
    }
    private readonly ConcurrentDictionary<string,Room> rooms=new();
    private readonly ConcurrentDictionary<string,int> channels=new();
    private Room GetRoom(int channel,string region){string key=channel+":"+region;if(!rooms.TryGetValue(key,out var room)){room=rooms.GetOrAdd(key,_=>new Room(channel,region));}room.Monsters.Refresh(now());return room;}
    private long tick;
    private long chatId;
    // Authenticated connections by account name. Written only under that account's gate.
    private readonly ConcurrentDictionary<string,Session> online=new();
    // 1 while a channel's tick is still running, so a stalled channel is skipped instead of queued.
    private readonly int[] tickRunning=new int[Regions.ChannelCount+1];
    private long oversizedPushes;
    public long OversizedPushes=>Interlocked.Read(ref oversizedPushes);

    public async Task<Update> Handle(Session session, Command command)
    {
        var accountGate=AccountGate(session.Name??command.Name.ToLowerInvariant());
        await accountGate.WaitAsync();
        using var reservation=PrepareAdmission(session,command);
        int requested=reservation?.Channel??command.ChannelId;
        int first=session.Channel,second=(command.Kind=="switch_channel"||command.Kind=="enter_world")&&requested>=1&&requested<=30?requested:first;
        if(first>second)(first,second)=(second,first);
        await channelGates[first].WaitAsync();
        if(second!=first)await channelGates[second].WaitAsync();
        try {
            var time=now();
            if(command.Sequence==0||command.Sequence<=session.Sequence)return Snapshot(session,command.Sequence,"replay");
            session.Sequence=command.Sequence;
            session.ChatAfter=Math.Max(0,command.ChatAfter);
            if(command.Kind is "login" or "authenticate" or "register")return await Login(session,command);
            if(session.Name is null)return Snapshot(session,command.Sequence,"unauthorized");
            string name=session.Name,code="ok",message="";
            if(session.Travel!=null&&time-session.TravelStarted>90){session.Travel=null;session.TravelTicket="";session.TravelCommitted=false;}
            if(session.Travel!=null&&command.Kind!="snapshot"&&!command.Kind.StartsWith("travel_"))return Snapshot(session,command.Sequence,"in_transit");
            if(command.Kind is "characters" or "check_name" or "create_character" or "enter_world" or "leave_world") {
                try {await catalogGate.WaitAsync();try {code=await Lobby(session,command,reservation);}finally {catalogGate.Release();}}
                catch(Npgsql.PostgresException e) when(e.SqlState=="23505"){code="name_taken";}
                catch(Exception e) when(e is IOException or InvalidOperationException or Npgsql.NpgsqlException) {
                    GameHost.Log("character_save_failed",name,e.GetType().Name);code="save_failed";
                }
                return Snapshot(session,command.Sequence,code);
            }
            if(command.Kind=="snapshot")return Snapshot(session,command.Sequence,"ok");
            if(!players.TryGetValue(name,out var current))return Snapshot(session,command.Sequence,"not_in_world");
            var room=GetRoom(session.Channel,current.RegionId);
            // A dead character can talk, look, leave or revive; nothing that acts on the world.
            if(CharacterStatsRules.IsDead(current.Progress)&&command.Kind is "move" or "attack" or "portal" or "travel_begin" or "switch_channel" or "claim")
                return Snapshot(session,command.Sequence,"dead");
            switch(command.Kind) {
                case "chat":
                    if(!RoomChat.Valid(command.ChatText)){code="invalid_chat";break;}
                    if(time-session.LastChat<1){code="chat_rate_limited";break;}
                    session.LastChat=time;
                    room.Chat.Add(new ChatMessage{Id=Interlocked.Increment(ref chatId),Sender=current.Name,Text=command.ChatText.Trim(),ChannelId=session.Channel,RegionId=current.RegionId});
                    break;
                case "move":
                    var move=MovementRules.Evaluate(current.X,current.Z,command.Dx,command.Dz,time-session.LastMove,current.RegionId);
                    code=move.Code;
                    if(code!="invalid"&&code!="rate_limited")session.LastMove=time;
                    if(code=="ok")players[name]=current with {X=move.X,Z=move.Z};
                    break;
                case "travel_begin":
                    if(session.Travel!=null){code="in_transit";break;}
                    var route=TransportRoutes.Find(current.RegionId,command.TargetId);
                    if(route==null||!TransportRoutes.Near(route.Vehicle,current.X,current.Z)){code="travel_denied";break;}
                    if(time-session.LastTransfer<1){code="transfer_cooldown";break;}
                    session.Travel=route;session.TravelTicket=Guid.NewGuid().ToString("N");session.TravelStarted=time;session.TravelCommitted=false;message=session.TravelTicket;
                    break;
                case "travel_commit":
                    if(command.TargetId==session.CompletedTravel&&command.TargetId!=""){break;}
                    if(session.Travel==null||command.TargetId!=session.TravelTicket){code="travel_invalid";break;}
                    if(session.TravelCommitted)break;
                    if(time-session.TravelStarted<8){code="travel_too_early";break;}
                    var arrival=current with {RegionId=session.Travel.Target,X=TransportRoutes.StationX(session.Travel.Vehicle),Z=TransportRoutes.StationZ};
                    try {await SaveProgress(name,arrival);}
                    catch(Exception e) when(e is IOException or InvalidOperationException or Npgsql.NpgsqlException){code="save_failed";break;}
                    players[name]=arrival;session.TravelCommitted=true;session.LastTransfer=time;session.LastMove=time;
                    break;
                case "travel_finish":
                    if(command.TargetId==session.CompletedTravel&&command.TargetId!="")break;
                    if(session.Travel==null||command.TargetId!=session.TravelTicket||!session.TravelCommitted){code="travel_invalid";break;}
                    session.CompletedTravel=session.TravelTicket;session.Travel=null;session.TravelTicket="";session.TravelCommitted=false;break;
                case "travel_cancel":
                    if(session.Travel!=null&&command.TargetId==session.TravelTicket&&!session.TravelCommitted){session.Travel=null;session.TravelTicket="";}else code="travel_invalid";
                    break;
                case "portal":
                    var portal=Regions.Nearby(current.RegionId,current.X,current.Z);
                    if(portal is null||portal.Id!=command.PortalId){code="portal_denied";break;}
                    if(time-session.LastTransfer<1){code="transfer_cooldown";break;}
                    var destination=current with {RegionId=portal.Target,X=portal.SpawnX,Z=portal.SpawnZ};
                    try {await SaveProgress(name,destination);}
                    catch(Exception e) when(e is IOException or InvalidOperationException or Npgsql.NpgsqlException){code="save_failed";break;}
                    players[name]=destination;session.LastTransfer=time;session.LastMove=time;
                    break;
                case "switch_channel":
                    if(command.ChannelId<1||command.ChannelId>Regions.ChannelCount){code="invalid_channel";break;}
                    if(time-session.LastTransfer<1){code="transfer_cooldown";break;}
                    if(reservation is null){code="channel_full";break;}
                    session.Channel=reservation.Channel;channels[name]=session.Channel;reservation.Commit();session.LastTransfer=time;
                    break;
                case "attack":
                    if(time-session.LastAttack<.4){code="cooldown";break;}
                    session.LastAttack=time;
                    // Empty target is retained only for the original demo client.
                    var target=string.IsNullOrEmpty(command.TargetId)?room.Monsters.Primary:room.Monsters.Find(command.TargetId);
                    if(target is null||target.Hp==0){code="no_target";break;}
                    if((current.X-target.X)*(current.X-target.X)+(current.Z-target.Z)*(current.Z-target.Z)>2.25f){code="out_of_range";break;}
                    int damage=CharacterStatsRules.Attack(CharacterStatsRules.Normalize(current.Progress).Level);
                    var hit=new CombatEvent{Kind="monster_hit",Source=name,Target=target.Id,Amount=Math.Min(damage,target.Hp),X=target.X,Z=target.Z};
                    if(!target.WouldDie(damage)){target.Damage(damage,name,time);room.Event(hit);break;}
                    // Lethal: persist kill, shard and experience first; only then is the monster dead.
                    var (progress,levels)=CharacterStatsRules.GainExperience(current.Progress,target.Kind.Experience);
                    var reward=current with {Kills=current.Kills+1,Shards=current.Shards+1,Progress=progress};
                    await SaveProgress(name,reward);
                    players[name]=reward;target.CommitDeath(now());room.Event(hit);
                    message=$"Slime defeated. +1 shard. +{target.Kind.Experience} EXP. Progress saved.";
                    if(levels>0)room.Event(new CombatEvent{Kind="level_up",Source=name,Target=name,Amount=progress.Level,X=current.X,Z=current.Z});
                    break;
                case "revive":
                    if(!CharacterStatsRules.IsDead(current.Progress)){code="not_dead";break;}
                    // Back to the region's safe point with half HP. Saved so a reconnect cannot skip it.
                    var revived=current with {X=0,Z=0,Progress=CharacterStatsRules.Revive(current.Progress)};
                    try {await SaveProgress(name,revived);}
                    catch(Exception e) when(e is IOException or InvalidOperationException or Npgsql.NpgsqlException){code="save_failed";break;}
                    players[name]=revived;session.LastHurt=time;session.LastMove=time;
                    room.Event(new CombatEvent{Kind="revived",Source=name,Target=name,X=0,Z=0});
                    break;
                case "claim":
                    if(current.QuestClaimed){code="already_claimed";break;}
                    if(current.Kills<3){code="not_ready";break;}
                    var claimed=current with {Gold=current.Gold+100,QuestClaimed=true};
                    await SaveProgress(name,claimed);players[name]=claimed;message="Quest complete. +100 gold. Progress saved.";
                    break;
                default:code="unknown_command";break;
            }
            return Snapshot(session,command.Sequence,code,message);
        } finally {if(second!=first)channelGates[second].Release();channelGates[first].Release();accountGate.Release();}
    }
    private ChannelDirectory.Reservation? PrepareAdmission(Session session,Command command)
    {
        if(session.Name is null || command.Sequence<=session.Sequence)return null;
        if(command.Kind=="switch_channel" && players.ContainsKey(session.Name) && command.ChannelId>=1 && command.ChannelId<=Regions.ChannelCount)
            return admission.Reserve(session.Name,command.ChannelId);
        if(command.Kind=="enter_world" && session.LobbyLogin && !players.ContainsKey(session.Name) && command.RealmId=="meadow" &&
            accounts[session.Name].Characters?.Any(c=>c.Id==command.CharacterId)==true)
            return admission.Reserve(session.Name,command.ChannelId);
        return null;
    }
    private async Task<string> Lobby(Session session,Command command,ChannelDirectory.Reservation? reservation)
    {
        if(!session.LobbyLogin)return "lobby_required";
        var name=session.Name!;var account=accounts[name];
        switch(command.Kind) {
            case "characters":return "ok";
            case "check_name":return await catalog.CheckName(command.DisplayName);
            case "create_character":
                if(players.ContainsKey(name))return "already_in_world";
                var created=await catalog.Create(account,command);
                if(created.Code=="ok")accounts[name]=created.Account;
                return created.Code;
            case "enter_world":
                if(command.RealmId!="meadow")return "unknown_realm";
                if(players.ContainsKey(name))return "already_in_world";
                var character=account.Characters?.FirstOrDefault(c=>c.Id==command.CharacterId);
                if(character is null)return "character_not_owned";
                if(command.ChannelId<0||command.ChannelId>Regions.ChannelCount)return "invalid_channel";
                if(reservation is null)return command.ChannelId==0?"world_full":"channel_full";
                session.Channel=reservation.Channel;channels[name]=session.Channel;
                if(Regions.Find(character.RegionId)==null)character=character with {RegionId="meadow",X=0,Z=0};
                if(Regions.BlockReason(character.RegionId,character.X,character.Z)!="ok")character=character with {X=0,Z=0};
                players[name]=character;reservation.Commit();session.LastMove=session.LastAttack=double.NegativeInfinity;
                return "ok";
            case "leave_world":
                if(players.TryGetValue(name,out var active)){await SaveProgress(name,active);players.TryRemove(name,out _);channels.TryRemove(name,out _);admission.Remove(name);}
                return "ok";
            default:return "unknown_command";
        }
    }
    private async Task<Update> Login(Session session,Command command)
    {
        if(session.Name!=null)return Snapshot(session,command.Sequence,"already_signed_in");
        if(++session.LoginAttempts>5)return Snapshot(session,command.Sequence,"login_limited");
        var name=command.Name.ToLowerInvariant();
        if(!Regex.IsMatch(name,"^[a-z0-9_]{3,16}$")||command.Password.Length<8||command.Password.Length>128)return Snapshot(session,command.Sequence,"invalid_login");
        var account=await store.Load(name);
        if(command.Kind=="authenticate"&&account is null)return Snapshot(session,command.Sequence,"invalid_login");
        if(command.Kind=="register"&&account is not null)return Snapshot(session,command.Sequence,"account_exists");
        if(account is null){account=Account.Create(name,command.Password);await store.Save(account,null);}
        else if(!account.Verify(command.Password))return Snapshot(session,command.Sequence,"invalid_login");
        if(accounts.ContainsKey(name))return Snapshot(session,command.Sequence,"duplicate_login");
        if(command.LobbyLogin){await catalogGate.WaitAsync();try {account=await catalog.Migrate(account);}finally {catalogGate.Release();}}
        using var legacyReservation=command.LobbyLogin?null:admission.Reserve(name,1);
        if(!command.LobbyLogin&&legacyReservation is null)return Snapshot(session,command.Sequence,"channel_full");
        accounts[name]=account;channels[name]=1;session.Name=name;session.LobbyLogin=command.LobbyLogin;
        session.Subscribed=command.SubscribeUpdates;online[name]=session;
        if(!command.LobbyLogin){players[name]=CharacterData.Legacy(account);legacyReservation!.Commit();}
        return Snapshot(session,command.Sequence,"ok");
    }
    private async Task SaveProgress(string name,CharacterData character)
    {
        var account=accounts[name];Account saved;
        if(character.Id=="legacy")saved=account with {X=character.X,Z=character.Z,Kills=character.Kills,Shards=character.Shards,Gold=character.Gold,QuestClaimed=character.QuestClaimed,RegionId=character.RegionId,Progress=character.Progress,Revision=account.Revision+1};
        else saved=account with {Characters=account.Characters!.Select(c=>c.Id==character.Id?character:c).ToArray(),Revision=account.Revision+1};
        await store.Save(saved,account.Revision);accounts[name]=saved;
    }
    public async Task Disconnect(Session session)
    {
        var accountGate=AccountGate(session.Name??"");await accountGate.WaitAsync();
        int channel=session.Channel;await channelGates[channel].WaitAsync();
        try {
            if(session.Name is null)return;var name=session.Name;
            try {if(players.TryGetValue(name,out var character))await SaveProgress(name,character);}
            finally {
                online.TryRemove(new KeyValuePair<string,Session>(name,session));
                players.TryRemove(name,out _);accounts.TryRemove(name,out _);channels.TryRemove(name,out _);admission.Remove(name);session.Name=null;
            }
        } finally {channelGates[channel].Release();accountGate.Release();}
    }
    // Reply to one command: per-player fields + the room the player stands in.
    private Update Snapshot(Session session,ulong sequence,string code,string message="")
    {
        var result=Personal(session,sequence,code,message,Interlocked.Increment(ref tick));
        if(session.Name is null)return result;
        result.Channels.AddRange(admission.Snapshot());
        if(!result.InWorld)return result;
        var room=GetRoom(session.Channel,result.RegionId);
        result.Chat.AddRange(room.Chat.After(session.ChatAfter));
        AddRoomState(result,room,session.Channel,result.RegionId);
        return result;
    }
    // Fields that differ per receiver. Never contains room-wide lists.
    private Update Personal(Session session,ulong sequence,string code,string message,long tickValue)
    {
        var result=new Update{Sequence=sequence,Code=code,Message=message,Tick=tickValue};
        if(session.Name is null||!accounts.TryGetValue(session.Name,out var account))return result;
        if(session.LobbyLogin)result.Characters.AddRange((account.Characters??[]).Select(c=>c.ToProtocol()));
        if(!players.TryGetValue(session.Name,out var selected))return result;
        result.SelfStats=CharacterStatsRules.View(selected.Progress);
        result.InWorld=true;result.SelectedCharacterId=selected.Id;result.RegionId=selected.RegionId;result.ChannelId=session.Channel;
        return result;
    }
    // Fields identical for everyone in one channel+region.
    private void AddRoomState(Update result,Room room,int channel,string region)
    {
        result.Monster=room.Monsters.Primary.View(); // Compatibility with the earlier demo.
        result.Monsters.AddRange(room.Monsters.Snapshot());
        result.Players.AddRange(players.Where(p=>p.Value.RegionId==region&&channels.TryGetValue(p.Key,out var c)&&c==channel).OrderBy(p=>p.Key).Select(p=>new PlayerView {
            Name=p.Key,CharacterId=p.Value.Id,DisplayName=p.Value.Name,Appearance=p.Value.Look.ToProtocol(),X=p.Value.X,Z=p.Value.Z,
            Kills=p.Value.Kills,Shards=p.Value.Shards,Gold=p.Value.Gold,QuestClaimed=p.Value.QuestClaimed,
            Hp=CharacterStatsRules.Normalize(p.Value.Progress).Hp,MaxHp=CharacterStatsRules.MaxHp(CharacterStatsRules.Normalize(p.Value.Progress).Level),
            Level=CharacterStatsRules.Normalize(p.Value.Progress).Level,Dead=CharacterStatsRules.IsDead(p.Value.Progress)
        }));
    }

    // ---- Server tick: the world advances and is broadcast without waiting for client requests. ----

    // Runs until cancelled. Each channel ticks independently: a channel blocked on a slow save
    // is skipped this round instead of delaying the other 29 channels.
    public async Task RunTicker(TimeSpan interval,CancellationToken token)
    {
        var running=new Task?[Regions.ChannelCount+1];
        using var timer=new PeriodicTimer(interval);
        try {
            while(await timer.WaitForNextTickAsync(token)) {
                foreach(int channel in ActiveChannels()) {
                    if(Interlocked.CompareExchange(ref tickRunning[channel],1,0)!=0)continue;
                    running[channel]=Task.Run(async()=> {
                        try {await TickChannel(channel);}
                        catch(Exception e) {GameHost.Log("tick_failed",null,channel+":"+e.GetType().Name);}
                        finally {Volatile.Write(ref tickRunning[channel],0);}
                    });
                }
            }
        }
        catch(OperationCanceledException) {}
        await Task.WhenAll(running.OfType<Task>());
    }
    // Deterministic single tick for tests: every channel, awaited.
    public Task TickOnce()=>Task.WhenAll(Enumerable.Range(1,Regions.ChannelCount).Select(TickChannel));

    private HashSet<int> ActiveChannels()
    {
        var active=new HashSet<int>();
        foreach(var entry in online)if(players.ContainsKey(entry.Key))active.Add(entry.Value.Channel);
        return active;
    }

    // Seconds without taking damage before natural regeneration starts, and its rate.
    public const double RegenDelay=5, RegenPerSecond=.01;

    private async Task TickChannel(int channel)
    {
        await channelGates[channel].WaitAsync();
        try {
            // Membership is re-read under the gate: portal, channel switch and disconnect also hold it.
            var members=new List<(string Name,Session Session)>();
            foreach(var entry in online)
                if(entry.Value.Channel==channel&&players.ContainsKey(entry.Key))members.Add((entry.Key,entry.Value));
            if(members.Count==0)return;
            var channelViews=admission.Snapshot();
            var time=now();
            foreach(var region in members.GroupBy(m=>players[m.Name].RegionId)) {
                var room=GetRoom(channel,region.Key);
                Simulate(room,region.Key,region.ToList(),time);
                var subscribers=region.Where(m=>m.Session.Subscribed&&m.Session.Deliver!=null).ToList();
                if(subscribers.Count==0)continue;
                // Serialize the shared part ONCE per room. Protobuf defines concatenated encodings
                // as a merge, so shared + personal bytes parse as one Update. This turns the
                // per-tick cost from O(players x room size) into O(players + room size).
                var shared=new Update{Push=true};
                shared.Channels.AddRange(channelViews);
                shared.Chat.AddRange(room.Chat.TakeUnpushed());
                shared.Events.AddRange(room.TakeEvents());
                AddRoomState(shared,room,channel,region.Key);
                var sharedBytes=shared.ToByteArray();
                long tickValue=Interlocked.Increment(ref tick);
                foreach(var (_,session) in subscribers) {
                    var personal=Personal(session,0,"tick","",tickValue).ToByteArray();
                    if(sharedBytes.Length+personal.Length>Framing.MaximumBytes){Interlocked.Increment(ref oversizedPushes);continue;}
                    var frame=new byte[sharedBytes.Length+personal.Length];
                    Buffer.BlockCopy(sharedBytes,0,frame,0,sharedBytes.Length);
                    Buffer.BlockCopy(personal,0,frame,sharedBytes.Length,personal.Length);
                    session.Deliver!(frame); // false = transport is closing that connection; nothing to undo here.
                }
            }
        }
        finally {channelGates[channel].Release();}
    }

    // One simulation step for a room, under its channel gate: respawn, monster AI, damage to
    // players, death, regeneration. HP changes stay in memory and are persisted with the next
    // save of that character (kill, portal, revive, disconnect) — a deliberate write-behind.
    private void Simulate(Room room,string region,List<(string Name,Session Session)> members,double time)
    {
        float dt=double.IsNaN(room.LastTick)?0:(float)Math.Clamp(time-room.LastTick,0,.5);
        room.LastTick=time;
        room.Monsters.Refresh(time); // Respawn is driven by time, not by someone asking.
        var sessions=members.ToDictionary(m=>m.Name,m=>m.Session);
        (float X,float Z)? Locate(string name)
        {
            if(!sessions.TryGetValue(name,out var s)||s.Travel!=null)return null;
            if(!players.TryGetValue(name,out var c)||c.RegionId!=region||CharacterStatsRules.IsDead(c.Progress))return null;
            return (c.X,c.Z);
        }
        foreach(var monster in room.Monsters.All) {
            if(monster.State==MonsterPopulation.Mode.Dead)continue;
            int attack=monster.Step(time,dt,region,Locate);
            if(attack==0||monster.Target is not string victimName)continue;
            var victim=players[victimName];
            int taken=CharacterStatsRules.DamageTaken(CharacterStatsRules.Normalize(victim.Progress).Level,attack);
            var after=CharacterStatsRules.Hurt(victim.Progress,taken);
            players[victimName]=victim with {Progress=after};sessions[victimName].LastHurt=time;
            room.Event(new CombatEvent{Kind="player_hit",Source=monster.Id,Target=victimName,Amount=taken,X=victim.X,Z=victim.Z});
            if(after.Hp>0)continue;
            room.Event(new CombatEvent{Kind="player_died",Source=monster.Id,Target=victimName,X=victim.X,Z=victim.Z});
            foreach(var other in room.Monsters.All)if(other.Target==victimName)other.LoseTarget();
        }
        foreach(var (name,session) in members) {
            if(!players.TryGetValue(name,out var c)||c.RegionId!=region)continue;
            var p=CharacterStatsRules.Normalize(c.Progress);int maxHp=CharacterStatsRules.MaxHp(p.Level);
            if(p.Hp==0||p.Hp>=maxHp||time-session.LastHurt<RegenDelay){session.RegenCarry=0;continue;}
            session.RegenCarry+=maxHp*RegenPerSecond*dt;
            int whole=(int)session.RegenCarry;if(whole<1)continue;
            session.RegenCarry-=whole;players[name]=c with {Progress=CharacterStatsRules.Heal(c.Progress,whole)};
        }
    }
}
