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
}

public sealed class World(IStore store, Func<double>? clock = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Account> accounts = new();
    private readonly Dictionary<string, CharacterData> players = new();
    private readonly CharacterCatalog catalog=new(store);
    private readonly Func<double> now = clock ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
    private sealed class Room {public int Hp=3;public double RespawnAt;}
    private readonly Dictionary<string,Room> rooms=new();
    private readonly Dictionary<string,int> channels=new();
    private Room GetRoom(int channel,string region){string key=channel+":"+region;if(!rooms.TryGetValue(key,out var room)){room=new();rooms.Add(key,room);}if(room.Hp==0&&now()>=room.RespawnAt)room.Hp=3;return room;}
    private long tick;

    public async Task<Update> Handle(Session session, Command command)
    {
        await gate.WaitAsync();
        try {
            var time=now();
            if(command.Sequence==0||command.Sequence<=session.Sequence)return Snapshot(session,command.Sequence,"replay");
            session.Sequence=command.Sequence;
            if(command.Kind=="login")return await Login(session,command);
            if(session.Name is null)return Snapshot(session,command.Sequence,"unauthorized");
            string name=session.Name,code="ok",message="";
            if(command.Kind is "characters" or "check_name" or "create_character" or "enter_world" or "leave_world") {
                try {code=await Lobby(session,command);}
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
                    session.Channel=command.ChannelId;channels[name]=session.Channel;session.LastTransfer=time;
                    break;
                case "attack":
                    if(time-session.LastAttack<.4){code="cooldown";break;}
                    session.LastAttack=time;
                    if(room.Hp==0){code="no_target";break;}
                    if((current.X-3)*(current.X-3)+current.Z*current.Z>2.25f){code="out_of_range";break;}
                    if(room.Hp>1){room.Hp--;break;}
                    var reward=current with {Kills=current.Kills+1,Shards=current.Shards+1};
                    await SaveProgress(name,reward);
                    players[name]=reward;room.Hp=0;room.RespawnAt=time+3;message="Slime defeated. +1 shard. Progress saved.";
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
        } finally {gate.Release();}
    }
    private async Task<string> Lobby(Session session,Command command)
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
                session.Channel=command.ChannelId==0?1:command.ChannelId;channels[name]=session.Channel;
                if(Regions.Find(character.RegionId)==null)character=character with {RegionId="meadow",X=0,Z=0};
                if(Regions.BlockReason(character.RegionId,character.X,character.Z)!="ok")character=character with {X=0,Z=0};
                players.Add(name,character);session.LastMove=session.LastAttack=double.NegativeInfinity;
                return "ok";
            case "leave_world":
                if(players.TryGetValue(name,out var active)){await SaveProgress(name,active);players.Remove(name);}
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
        if(command.LobbyLogin)account=await catalog.Migrate(account);
        accounts.Add(name,account);channels[name]=1;session.Name=name;session.LobbyLogin=command.LobbyLogin;
        if(!command.LobbyLogin)players.Add(name,CharacterData.Legacy(account));
        return Snapshot(session,command.Sequence,"ok");
    }
    private async Task SaveProgress(string name,CharacterData character)
    {
        var account=accounts[name];Account saved;
        if(character.Id=="legacy")saved=account with {X=character.X,Z=character.Z,Kills=character.Kills,Shards=character.Shards,Gold=character.Gold,QuestClaimed=character.QuestClaimed,RegionId=character.RegionId,Revision=account.Revision+1};
        else saved=account with {Characters=account.Characters!.Select(c=>c.Id==character.Id?character:c).ToArray(),Revision=account.Revision+1};
        await store.Save(saved,account.Revision);accounts[name]=saved;
    }
    public async Task Disconnect(Session session)
    {
        await gate.WaitAsync();
        try {
            if(session.Name is null)return;var name=session.Name;
            try {if(players.TryGetValue(name,out var character))await SaveProgress(name,character);}
            finally {players.Remove(name);accounts.Remove(name);channels.Remove(name);session.Name=null;}
        } finally {gate.Release();}
    }
    private Update Snapshot(Session session,ulong sequence,string code,string message="")
    {
        var result=new Update{Sequence=sequence,Code=code,Message=message,Tick=++tick};
        if(session.Name is null)return result;
        var account=accounts[session.Name];
        if(session.LobbyLogin)result.Characters.AddRange((account.Characters??[]).Select(c=>c.ToProtocol()));
        if(!players.TryGetValue(session.Name,out var selected))return result;
        result.InWorld=true;result.SelectedCharacterId=selected.Id;result.RegionId=selected.RegionId;result.ChannelId=session.Channel;
        var room=GetRoom(session.Channel,selected.RegionId);
        result.Monster=new MonsterView{X=3,Z=0,Hp=room.Hp,MaxHp=3};
        result.Players.AddRange(players.Where(p=>p.Value.RegionId==selected.RegionId&&channels[p.Key]==session.Channel).OrderBy(p=>p.Key).Select(p=>new PlayerView {
            Name=p.Key,CharacterId=p.Value.Id,DisplayName=p.Value.Name,Appearance=p.Value.Look.ToProtocol(),X=p.Value.X,Z=p.Value.Z,
            Kills=p.Value.Kills,Shards=p.Value.Shards,Gold=p.Value.Gold,QuestClaimed=p.Value.QuestClaimed
        }));
        return result;
    }
}
