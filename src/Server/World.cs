using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Planet.Protocol;

namespace Planet.Server;

public sealed class Session
{
    public string? Name;
    public bool LobbyLogin;
    public int Channel=1;
    public double LastTransfer=double.NegativeInfinity;
    public ulong Sequence;
    public double LastMove = double.NegativeInfinity;
    public double LastAttack = double.NegativeInfinity;
    public int LoginAttempts;
    public double LastChat = double.NegativeInfinity;
    public long ChatAfter;
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
    private sealed class Room(int channel,string region) {public readonly MonsterPopulation Monsters=new(channel,region);public readonly RoomChat Chat=new();}
    private readonly ConcurrentDictionary<string,Room> rooms=new();
    private readonly ConcurrentDictionary<string,int> channels=new();
    private Room GetRoom(int channel,string region){string key=channel+":"+region;if(!rooms.TryGetValue(key,out var room)){room=rooms.GetOrAdd(key,_=>new Room(channel,region));}room.Monsters.Refresh(now());return room;}
    private long tick;
    private long chatId;

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
            if(command.Kind=="login")return await Login(session,command);
            if(session.Name is null)return Snapshot(session,command.Sequence,"unauthorized");
            string name=session.Name,code="ok",message="";
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
                    if(target.Hp>1){target.Damage();break;}
                    var reward=current with {Kills=current.Kills+1,Shards=current.Shards+1};
                    await SaveProgress(name,reward);
                    players[name]=reward;target.CommitDeath(now());message="Slime defeated. +1 shard. Progress saved.";
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
        if(account is null){account=Account.Create(name,command.Password);await store.Save(account,null);}
        else if(!account.Verify(command.Password))return Snapshot(session,command.Sequence,"invalid_login");
        if(accounts.ContainsKey(name))return Snapshot(session,command.Sequence,"duplicate_login");
        if(command.LobbyLogin){await catalogGate.WaitAsync();try {account=await catalog.Migrate(account);}finally {catalogGate.Release();}}
        using var legacyReservation=command.LobbyLogin?null:admission.Reserve(name,1);
        if(!command.LobbyLogin&&legacyReservation is null)return Snapshot(session,command.Sequence,"channel_full");
        accounts[name]=account;channels[name]=1;session.Name=name;session.LobbyLogin=command.LobbyLogin;
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
            finally {players.TryRemove(name,out _);accounts.TryRemove(name,out _);channels.TryRemove(name,out _);admission.Remove(name);session.Name=null;}
        } finally {channelGates[channel].Release();accountGate.Release();}
    }
    private Update Snapshot(Session session,ulong sequence,string code,string message="")
    {
        var result=new Update{Sequence=sequence,Code=code,Message=message,Tick=Interlocked.Increment(ref tick)};
        if(session.Name is null)return result;
        result.Channels.AddRange(admission.Snapshot());
        var account=accounts[session.Name];
        if(session.LobbyLogin)result.Characters.AddRange((account.Characters??[]).Select(c=>c.ToProtocol()));
        if(!players.TryGetValue(session.Name,out var selected))return result;
        result.SelfStats=CharacterStatsRules.View(selected.Progress);
        result.InWorld=true;result.SelectedCharacterId=selected.Id;result.RegionId=selected.RegionId;result.ChannelId=session.Channel;
        var room=GetRoom(session.Channel,selected.RegionId);
        result.Chat.AddRange(room.Chat.After(session.ChatAfter));
        result.Monster=room.Monsters.Primary.View(); // Compatibility with the earlier demo.
        result.Monsters.AddRange(room.Monsters.Snapshot());
        result.Players.AddRange(players.Where(p=>p.Value.RegionId==selected.RegionId&&channels.TryGetValue(p.Key,out var channel)&&channel==session.Channel).OrderBy(p=>p.Key).Select(p=>new PlayerView {
            Name=p.Key,CharacterId=p.Value.Id,DisplayName=p.Value.Name,Appearance=p.Value.Look.ToProtocol(),X=p.Value.X,Z=p.Value.Z,
            Kills=p.Value.Kills,Shards=p.Value.Shards,Gold=p.Value.Gold,QuestClaimed=p.Value.QuestClaimed
        }));
        return result;
    }
}
