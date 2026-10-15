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
    public double RegenCarry, MpCarry;
    // Ready times for skills and item use. In memory only: a reconnect resets them, which is
    // acceptable for short cooldowns and avoids a write per cast.
    public readonly Dictionary<string,double> Cooldowns=new();
    // Party and whisper messages addressed to this connection; drained into the next Update.
    public readonly ConcurrentQueue<ChatMessage> Mail=new();
    public void Post(ChatMessage message){Mail.Enqueue(message);while(Mail.Count>50)Mail.TryDequeue(out _);}
    public long ChatAfter;
    // Set at login from Command.subscribe_updates. Only subscribed sessions receive tick pushes.
    public bool Subscribed;
    // Supplied by the transport (GameHost). Must not block: it enqueues a serialized Update
    // and returns false when the connection cannot keep up or is closing.
    public Func<byte[], bool>? Deliver;
}

public sealed class World(IStore store, Func<double>? clock = null, int channelCapacity=1000, Func<double>? roll = null)
{
    // Random source for drops. Injected in tests so loot is deterministic.
    private readonly Func<double> random=roll??Random.Shared.NextDouble;
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
    // Player trades. Both names map to the same Trade. Invitations map invitee -> inviter.
    // Touched only under the channel gate shared by both players (they must stand in one room).
    private sealed class Trade(string a,string b)
    {
        public readonly string A=a,B=b;
        public readonly Dictionary<string,List<(int Slot,int Count)>> Items=new(){{a,[]},{b,[]}};
        public readonly Dictionary<string,int> Gold=new(){{a,0},{b,0}};
        public readonly Dictionary<string,bool> Confirmed=new(){{a,false},{b,false}};
        public string Other(string name)=>name==A?B:A;
        public void Unconfirm(){Confirmed[A]=false;Confirmed[B]=false;}
    }
    private readonly ConcurrentDictionary<string,Trade> trades=new();
    // Parties span channels and regions, so they are guarded by their own short lock (no awaits inside).
    public const int PartyLimit=4;
    private sealed class Party { public string Leader=""; public readonly List<string> Members=[]; }
    private readonly object partyGate=new();
    private readonly Dictionary<string,Party> parties=new();       // member account -> party
    private readonly Dictionary<string,string> partyInvites=new(); // invitee -> inviter
    private string? ResolvePlayer(string text)
    {
        if(online.ContainsKey(text)&&players.ContainsKey(text))return text;
        foreach(var p in players)if(online.ContainsKey(p.Key)&&string.Equals(p.Value.Name,text,StringComparison.OrdinalIgnoreCase))return p.Key;
        return null;
    }
    private void LeaveParty(string name)
    {
        lock(partyGate) {
            partyInvites.Remove(name);foreach(var k in partyInvites.Where(p=>p.Value==name).Select(p=>p.Key).ToList())partyInvites.Remove(k);
            if(!parties.Remove(name,out var party))return;
            party.Members.Remove(name);
            if(party.Members.Count<=1){foreach(var m in party.Members)parties.Remove(m);party.Members.Clear();return;}
            if(party.Leader==name)party.Leader=party.Members[0];
        }
    }
    private readonly ConcurrentDictionary<string,string> invites=new();
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
            if(CharacterStatsRules.IsDead(current.Progress)&&command.Kind is "move" or "attack" or "skill" or "use_item" or "portal" or "travel_begin" or "switch_channel" or "claim")
                return Snapshot(session,command.Sequence,"dead");
            if(trades.ContainsKey(name)&&command.Kind is "move_item" or "discard_item" or "use_item" or "equip" or "unequip" or "shop_buy" or "shop_sell"
                or "portal" or "travel_begin" or "switch_channel")
                return Snapshot(session,command.Sequence,"trading");
            switch(command.Kind) {
                case "chat":
                    if(!RoomChat.Valid(command.ChatText)){code="invalid_chat";break;}
                    if(time-session.LastChat<1){code="chat_rate_limited";break;}
                    if(command.ChatScope=="party") {
                        List<string> members;lock(partyGate)members=parties.TryGetValue(name,out var party)?[..party.Members]:[];
                        if(members.Count==0){code="no_party";break;}
                        session.LastChat=time;var line=new ChatMessage{Id=Interlocked.Increment(ref chatId),Sender=current.Name,Text=command.ChatText.Trim(),Scope="party"};
                        foreach(var m in members)if(online.TryGetValue(m,out var to))to.Post(line.Clone());
                        break;
                    }
                    if(command.ChatScope=="whisper") {
                        var whisperTo=ResolvePlayer(command.TargetId);
                        if(whisperTo is null||whisperTo==name){code="no_such_player";break;}
                        session.LastChat=time;var line=new ChatMessage{Id=Interlocked.Increment(ref chatId),Sender=current.Name,Text=command.ChatText.Trim(),Scope="whisper",To=players[whisperTo].Name};
                        online[whisperTo].Post(line.Clone());session.Post(line.Clone());
                        break;
                    }
                    session.LastChat=time;
                    room.Chat.Add(new ChatMessage{Id=Interlocked.Increment(ref chatId),Sender=current.Name,Text=command.ChatText.Trim(),ChannelId=session.Channel,RegionId=current.RegionId,Scope="room"});
                    break;
                case "party_invite": {
                    var invitee=ResolvePlayer(command.TargetId);
                    if(invitee is null||invitee==name){code="no_such_player";break;}
                    lock(partyGate) {
                        if(parties.ContainsKey(invitee)){code="already_in_party";break;}
                        if(parties.TryGetValue(name,out var party)&&(party.Leader!=name)){code="not_leader";break;}
                        if(party!=null&&party.Members.Count>=PartyLimit){code="party_full";break;}
                        partyInvites[invitee]=name;
                    }
                    if(code=="ok")online[invitee].Post(new ChatMessage{Id=Interlocked.Increment(ref chatId),Sender=current.Name,Text=current.Name+" 님이 파티에 초대했어요.",Scope="system"});
                    break;
                }
                case "party_accept":
                    lock(partyGate) {
                        if(!partyInvites.TryGetValue(name,out var inviter)||inviter!=command.TargetId){code="no_invite";break;}
                        partyInvites.Remove(name);
                        if(!online.ContainsKey(inviter)){code="no_such_player";break;}
                        if(parties.ContainsKey(name)){code="already_in_party";break;}
                        if(!parties.TryGetValue(inviter,out var party)){party=new Party{Leader=inviter};party.Members.Add(inviter);parties[inviter]=party;}
                        if(party.Members.Count>=PartyLimit){code="party_full";break;}
                        party.Members.Add(name);parties[name]=party;
                    }
                    break;
                case "party_decline":
                    lock(partyGate)if(!partyInvites.Remove(name))code="no_invite";
                    break;
                case "party_leave":
                    lock(partyGate)if(!parties.ContainsKey(name)){code="no_party";break;}
                    LeaveParty(name);
                    break;
                case "party_kick":
                    lock(partyGate) {
                        if(!parties.TryGetValue(name,out var party)){code="no_party";break;}
                        if(party.Leader!=name){code="not_leader";break;}
                        if(command.TargetId==name||!party.Members.Contains(command.TargetId)){code="not_member";break;}
                    }
                    if(code=="ok")LeaveParty(command.TargetId);
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
                    message=await Strike(name,current,room,[(target,CharacterStatsRules.TotalAttack(current.Progress,current.Inventory))],time);
                    break;
                case "skill":
                    (code,message)=await CastSkill(session,name,current,room,command,time);
                    break;
                case "learn_skill": {
                    var (learned,skills)=SkillRules.Learn(current.Skills,command.SkillId,CharacterStatsRules.Normalize(current.Progress).Level);
                    code=learned=="ok"?await Commit(name,current with {Skills=skills}):learned;
                    break;
                }
                case "use_item": {
                    var bag=InventoryRules.Load(current.Inventory);
                    var item=command.Slot>=0&&command.Slot<bag.Slots.Length&&bag.Slots[command.Slot] is {} stack?ItemCatalog.Find(stack.ItemId):null;
                    if(item is null){code="empty_slot";break;}
                    if(item.Kind!=ItemKind.Consumable){code="not_usable";break;}
                    if(session.Cooldowns.TryGetValue("item",out var itemReady)&&time<itemReady){code="item_cooldown";break;}
                    var (_,afterUse,_)=InventoryRules.TakeOne(bag,command.Slot);
                    var healed=CharacterStatsRules.RestoreMp(CharacterStatsRules.Heal(current.Progress,item.HealHp),item.HealMp);
                    code=await Commit(name,current with {Inventory=afterUse,Progress=healed});
                    if(code!="ok")break;
                    session.Cooldowns["item"]=time+.5;
                    room.Event(new CombatEvent{Kind="healed",Source=item.Id,Target=name,Amount=item.HealHp>0?item.HealHp:item.HealMp,X=current.X,Z=current.Z});
                    break;
                }
                case "equip": {
                    var (equipped,inventory)=InventoryRules.Equip(InventoryRules.Load(current.Inventory),command.Slot,CharacterStatsRules.Normalize(current.Progress).Level);
                    code=equipped=="ok"?await Commit(name,current with {Inventory=inventory}):equipped;
                    break;
                }
                case "unequip": {
                    var (removed,inventory)=InventoryRules.Unequip(InventoryRules.Load(current.Inventory),command.EquipSlot);
                    code=removed=="ok"?await Commit(name,current with {Inventory=inventory}):removed;
                    break;
                }
                case "move_item": {
                    var (moved,inventory)=InventoryRules.Move(InventoryRules.Load(current.Inventory),command.Slot,command.TargetSlot);
                    code=moved=="ok"?await Commit(name,current with {Inventory=inventory}):moved;
                    break;
                }
                case "discard_item": {
                    var (discarded,inventory)=InventoryRules.Discard(InventoryRules.Load(current.Inventory),command.Slot);
                    code=discarded=="ok"?await Commit(name,current with {Inventory=inventory}):discarded;
                    break;
                }
                case "revive":
                    if(!CharacterStatsRules.IsDead(current.Progress)){code="not_dead";break;}
                    // Back to the region's safe point with half HP. Saved so a reconnect cannot skip it.
                    var revived=current with {X=0,Z=0,Progress=CharacterStatsRules.Revive(current.Progress)};
                    try {await SaveProgress(name,revived);}
                    catch(Exception e) when(e is IOException or InvalidOperationException or Npgsql.NpgsqlException){code="save_failed";break;}
                    players[name]=revived;session.LastHurt=time;session.LastMove=time;
                    room.Event(new CombatEvent{Kind="revived",Source=name,Target=name,X=0,Z=0});
                    break;
                case "shop_buy": {
                    var item=ItemCatalog.Find(command.TargetId);int price=ShopCatalog.Price(command.TargetId);
                    if(item is null||price==0){code="not_sold";break;}
                    if(current.RegionId!=ShopCatalog.Region){code="no_shop";break;}
                    if(command.Count<1||command.Count>item.MaxStack){code="invalid_count";break;}
                    long cost=(long)price*command.Count;if(cost>current.Gold){code="not_enough_gold";break;}
                    var (fit,bag)=InventoryRules.Add(InventoryRules.Load(current.Inventory),[new ItemStack(item.Id,command.Count)]);
                    if(bag is null){code=fit;break;}
                    code=await Commit(name,current with {Gold=current.Gold-(int)cost,Inventory=bag});
                    break;
                }
                case "shop_sell": {
                    if(current.RegionId!=ShopCatalog.Region){code="no_shop";break;}
                    var bag=InventoryRules.Load(current.Inventory);
                    if(command.Slot<0||command.Slot>=bag.Slots.Length||bag.Slots[command.Slot] is not {} stack){code="empty_slot";break;}
                    if(command.Count<1||command.Count>stack.Count){code="invalid_count";break;}
                    var item=ItemCatalog.Find(stack.ItemId)!;var slots=(ItemStack?[])bag.Slots.Clone();
                    slots[command.Slot]=stack.Count==command.Count?null:stack with {Count=stack.Count-command.Count};
                    code=await Commit(name,current with {Gold=current.Gold+item.SellPrice*command.Count,Inventory=bag with {Slots=slots}});
                    break;
                }
                case "trade_request": {
                    var partner=command.TargetId;
                    if(partner==name||!online.ContainsKey(partner)||!players.TryGetValue(partner,out var other)){code="no_partner";break;}
                    if(online[partner].Channel!=session.Channel||other.RegionId!=current.RegionId||MathF.Sqrt((other.X-current.X)*(other.X-current.X)+(other.Z-current.Z)*(other.Z-current.Z))>4){code="too_far";break;}
                    if(trades.ContainsKey(name)||trades.ContainsKey(partner)){code="busy";break;}
                    invites[partner]=name;
                    room.Event(new CombatEvent{Kind="trade_invite",Source=name,Target=partner,X=current.X,Z=current.Z});
                    break;
                }
                case "trade_accept": {
                    var inviter=command.TargetId;
                    if(!invites.TryGetValue(name,out var asked)||asked!=inviter){code="no_invite";break;}
                    invites.TryRemove(name,out _);
                    if(!online.TryGetValue(inviter,out var inviterSession)||inviterSession.Channel!=session.Channel||!players.TryGetValue(inviter,out var other)||other.RegionId!=current.RegionId){code="no_partner";break;}
                    if(trades.ContainsKey(name)||trades.ContainsKey(inviter)){code="busy";break;}
                    var trade=new Trade(inviter,name);trades[inviter]=trade;trades[name]=trade;
                    break;
                }
                case "trade_offer": {
                    if(!trades.TryGetValue(name,out var trade)){code="not_trading";break;}
                    var bag=InventoryRules.Load(current.Inventory);var offer=trade.Items[name];
                    if(command.Count>0) {
                        if(command.Slot<0||command.Slot>=bag.Slots.Length||bag.Slots[command.Slot] is not {} stack){code="empty_slot";break;}
                        if(command.Count>stack.Count){code="invalid_count";break;}
                        offer.RemoveAll(o=>o.Slot==command.Slot);
                        if(offer.Count>=8){code="offer_full";break;}
                        offer.Add((command.Slot,command.Count));
                    }
                    else if(command.Count==0&&command.Slot>=0)offer.RemoveAll(o=>o.Slot==command.Slot);
                    if(command.Gold<0||command.Gold>current.Gold){code="not_enough_gold";break;}
                    trade.Gold[name]=(int)command.Gold;
                    trade.Unconfirm(); // any change means both sides must look again
                    break;
                }
                case "trade_confirm": {
                    if(!trades.TryGetValue(name,out var trade)){code="not_trading";break;}
                    trade.Confirmed[name]=true;
                    if(trade.Confirmed[trade.A]&&trade.Confirmed[trade.B])code=await ExecuteTrade(trade,room,time);
                    break;
                }
                case "trade_cancel":
                    if(invites.TryGetValue(name,out _))invites.TryRemove(name,out _);
                    foreach(var pair in invites)if(pair.Value==name)invites.TryRemove(pair.Key,out _);
                    if(trades.TryGetValue(name,out var cancelled))EndTrade(cancelled,room,"trade_cancelled");
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
    // Saves a changed character and publishes it. Inventory and skill changes are always
    // persisted immediately: an item must never exist only in memory.
    private async Task<string> Commit(string name,CharacterData updated)
    {
        try {await SaveProgress(name,updated);}
        catch(Exception e) when(e is IOException or InvalidOperationException or Npgsql.NpgsqlException){return "save_failed";}
        players[name]=updated;return "ok";
    }

    // One player action hitting one or more monsters. Non-lethal hits apply at once. All lethal
    // hits are rewarded together (EXP, kills, drops, skill points) in ONE save; only after that
    // save succeeds do those monsters die. A failed save leaves them alive and grants nothing.
    private async Task<string> Strike(string name,CharacterData current,Room room,List<(MonsterPopulation.Monster Target,int Damage)> hits,double time)
    {
        var lethal=new List<MonsterPopulation.Monster>();
        foreach(var (target,damage) in hits) {
            if(target.WouldDie(damage)){lethal.Add(target);continue;}
            target.Damage(damage,name,time);
            room.Event(new CombatEvent{Kind="monster_hit",Source=name,Target=target.Id,Amount=damage,X=target.X,Z=target.Z});
        }
        if(lethal.Count==0){players[name]=current;return "";}
        long experience=lethal.Sum(m=>m.Kind.Experience);
        var (progress,levels)=CharacterStatsRules.GainExperience(current.Progress,experience);
        var bag=InventoryRules.Load(current.Inventory);var gained=new List<ItemStack>();bool overflow=false;
        foreach(var drop in lethal.SelectMany(_=>DropTables.Slime(random))) {
            var (fit,next)=InventoryRules.Add(bag,[drop]);
            if(next is null){overflow=true;continue;}
            bag=next;gained.Add(drop);
        }
        // Party members standing in the same room share the experience (+10% per extra member).
        var sharers=PartyNearby(name,current);
        if(sharers.Count>0) {
            long share=experience*(100+10*sharers.Count)/100/(sharers.Count+1);
            (progress,levels)=CharacterStatsRules.GainExperience(current.Progress,share);
        }
        var reward=current with {Kills=current.Kills+lethal.Count,Shards=current.Shards+lethal.Count,Progress=progress,Inventory=bag,
            Skills=levels>0?SkillRules.GrantPoints(current.Skills,levels):current.Skills};
        var partners=new List<(string Name,CharacterData Data,int Levels)>();
        foreach(var m in sharers) {
            var data=players[m];long share=experience*(100+10*sharers.Count)/100/(sharers.Count+1);
            var (p,l)=CharacterStatsRules.GainExperience(data.Progress,share);
            partners.Add((m,data with {Progress=p,Skills=l>0?SkillRules.GrantPoints(data.Skills,l):data.Skills},l));
        }
        if(partners.Count==0)await SaveProgress(name,reward);
        else {
            // Killer and every sharing member are written together: all gain, or nobody does.
            var docs=new List<(Account,long?)>{(Document(name,reward),accounts[name].Revision)};
            foreach(var (m,data,_) in partners)docs.Add((Document(m,data),accounts[m].Revision));
            await store.SaveMany(docs);
            accounts[name]=docs[0].Item1;for(int i=0;i<partners.Count;i++)accounts[partners[i].Name]=docs[i+1].Item1;
            foreach(var (m,data,l) in partners){players[m]=data;if(l>0)room.Event(new CombatEvent{Kind="level_up",Source=m,Target=m,Amount=CharacterStatsRules.Normalize(data.Progress).Level,X=data.X,Z=data.Z});}
        }
        players[name]=reward;
        foreach(var target in lethal) {
            room.Event(new CombatEvent{Kind="monster_hit",Source=name,Target=target.Id,Amount=target.Hp,X=target.X,Z=target.Z});
            target.CommitDeath(now());
        }
        foreach(var item in gained)room.Event(new CombatEvent{Kind="item_gained",Source=item.ItemId,Target=name,Amount=item.Count,X=current.X,Z=current.Z});
        if(levels>0)room.Event(new CombatEvent{Kind="level_up",Source=name,Target=name,Amount=progress.Level,X=current.X,Z=current.Z});
        return $"Slime defeated. +{lethal.Count} shard. +{experience} EXP."+(overflow?" Bag full: some drops were lost.":"")+" Progress saved.";
    }

    private async Task<(string Code,string Message)> CastSkill(Session session,string name,CharacterData current,Room room,Command command,double time)
    {
        var def=SkillCatalog.Find(command.SkillId);
        if(def is null)return ("unknown_skill","");
        int level=SkillRules.Load(current.Skills).Levels.GetValueOrDefault(def.Id);
        if(level==0)return ("not_learned","");
        if(session.Cooldowns.TryGetValue(def.Id,out var ready)&&time<ready)return ("skill_cooldown","");
        if(time-session.LastAttack<.4)return ("cooldown","");
        if(CharacterStatsRules.Normalize(current.Progress).Mp<def.MpCost)return ("no_mp","");
        int attack=CharacterStatsRules.TotalAttack(current.Progress,current.Inventory);
        int Damage()=>Math.Max(1,attack*def.Power(level)/100);
        var hits=new List<(MonsterPopulation.Monster,int)>();
        switch(def.Target) {
            case SkillTarget.Single: {
                var target=room.Monsters.Find(command.TargetId);
                if(target is null||target.Hp==0)return ("no_target","");
                if(Distance(current,target)>def.Range)return ("out_of_range","");
                hits.Add((target,Damage()));break;
            }
            case SkillTarget.Area:
                foreach(var m in room.Monsters.All)if(m.Hp>0&&Distance(current,m)<=def.Range)hits.Add((m,Damage()));
                if(hits.Count==0)return ("no_target","");
                break;
        }
        // Checks passed: the cast happens. Cooldown and MP are spent even if a lethal save fails.
        session.Cooldowns[def.Id]=time+def.Cooldown;session.LastAttack=time;
        var caster=current with {Progress=CharacterStatsRules.SpendMp(current.Progress,def.MpCost)};
        room.Event(new CombatEvent{Kind="skill_used",Source=name,Target=def.Id,Amount=level,X=current.X,Z=current.Z});
        if(def.Target==SkillTarget.Self) {
            int heal=CharacterStatsRules.MaxHp(CharacterStatsRules.Normalize(caster.Progress).Level)*def.Power(level)/100;
            players[name]=caster with {Progress=CharacterStatsRules.Heal(caster.Progress,heal)};
            room.Event(new CombatEvent{Kind="healed",Source=def.Id,Target=name,Amount=heal,X=current.X,Z=current.Z});
            return ("ok","");
        }
        players[name]=caster;
        return ("ok",await Strike(name,caster,room,hits,time));
    }
    private static float Distance(CharacterData c,MonsterPopulation.Monster m){float dx=c.X-m.X,dz=c.Z-m.Z;return MathF.Sqrt(dx*dx+dz*dz);}

    private List<string> PartyNearby(string name,CharacterData self)
    {
        List<string> members;lock(partyGate)members=parties.TryGetValue(name,out var party)?[..party.Members]:[];
        int channel=online.TryGetValue(name,out var s)?s.Channel:0;
        return members.Where(m=>m!=name&&online.TryGetValue(m,out var ms)&&ms.Channel==channel&&ms.Travel==null&&players.TryGetValue(m,out var c)&&c.RegionId==self.RegionId&&!CharacterStatsRules.IsDead(c.Progress)).ToList();
    }
    private PartyView? PartyFor(string name)
    {
        lock(partyGate) {
            var view=new PartyView();
            if(partyInvites.TryGetValue(name,out var inviter)){view.InvitedBy=inviter;view.InvitedByName=players.TryGetValue(inviter,out var i)?i.Name:inviter;}
            if(parties.TryGetValue(name,out var party))foreach(var m in party.Members) {
                if(!players.TryGetValue(m,out var c))continue;var p=CharacterStatsRules.Normalize(c.Progress);
                view.Members.Add(new PartyMemberView{Name=m,DisplayName=c.Name,Level=p.Level,Hp=p.Hp,MaxHp=CharacterStatsRules.MaxHp(p.Level),
                    ChannelId=online.TryGetValue(m,out var ms)?ms.Channel:0,RegionId=c.RegionId,Leader=party.Leader==m});
            }
            return view.Members.Count==0&&view.InvitedBy==""?null:view;
        }
    }
    private TradeView? TradeFor(string name,CharacterData self)
    {
        if(trades.TryGetValue(name,out var t)) {
            string other=t.Other(name);var view=new TradeView{Partner=other,PartnerName=players.TryGetValue(other,out var o)?o.Name:other,State="open",
                MyGold=t.Gold[name],TheirGold=t.Gold[other],MyConfirmed=t.Confirmed[name],TheirConfirmed=t.Confirmed[other]};
            view.Mine.AddRange(Offered(name,t));view.Theirs.AddRange(Offered(other,t));return view;
        }
        if(invites.TryGetValue(name,out var inviter))return new TradeView{Partner=inviter,PartnerName=players.TryGetValue(inviter,out var i)?i.Name:inviter,State="incoming"};
        foreach(var pair in invites)if(pair.Value==name)return new TradeView{Partner=pair.Key,PartnerName=players.TryGetValue(pair.Key,out var p)?p.Name:pair.Key,State="invited"};
        return null;
    }
    private IEnumerable<TradeItemView> Offered(string owner,Trade t)
    {
        var bag=InventoryRules.Load(players.TryGetValue(owner,out var c)?c.Inventory:null);
        foreach(var (slot,count) in t.Items[owner])if(bag.Slots[slot] is {} s)yield return new TradeItemView{Slot=slot,ItemId=s.ItemId,Count=count};
    }
    private void EndTrade(Trade t,Room room,string kind)
    {
        trades.TryRemove(t.A,out _);trades.TryRemove(t.B,out _);
        room.Event(new CombatEvent{Kind=kind,Source=t.A,Target=t.B});
    }
    // Both players' new states are computed from the current server state, validated, and saved
    // in ONE multi-account write. Any failure (bag full, gold changed, storage error) changes nothing.
    private async Task<string> ExecuteTrade(Trade t,Room room,double time)
    {
        var a=players[t.A];var b=players[t.B];
        (string Code,CharacterData? Result) Side(CharacterData self,string owner,CharacterData partner,string partnerName)
        {
            var bag=InventoryRules.Load(self.Inventory);var slots=(ItemStack?[])bag.Slots.Clone();var outgoing=new List<ItemStack>();
            foreach(var (slot,count) in t.Items[owner]) {
                if(slots[slot] is not {} s||s.Count<count)return ("items_changed",null);
                slots[slot]=s.Count==count?null:s with {Count=s.Count-count};outgoing.Add(new(s.ItemId,count));
            }
            var incoming=new List<ItemStack>();var partnerBag=InventoryRules.Load(partner.Inventory);
            foreach(var (slot,count) in t.Items[partnerName])if(partnerBag.Slots[slot] is {} s)incoming.Add(new(s.ItemId,count));
            var (fit,next)=InventoryRules.Add(bag with {Slots=slots},incoming);
            if(next is null)return (fit,null);
            int gold=self.Gold-t.Gold[owner]+t.Gold[partnerName];
            if(self.Gold<t.Gold[owner])return ("not_enough_gold",null);
            return ("ok",self with {Inventory=next,Gold=gold});
        }
        var (codeA,newA)=Side(a,t.A,b,t.B);var (codeB,newB)=Side(b,t.B,a,t.A);
        if(newA is null||newB is null){t.Unconfirm();return newA is null?codeA:codeB;}
        var docA=Document(t.A,newA);var docB=Document(t.B,newB);
        try {await store.SaveMany([(docA,accounts[t.A].Revision),(docB,accounts[t.B].Revision)]);}
        catch(Exception e) when(e is IOException or InvalidOperationException or Npgsql.NpgsqlException){t.Unconfirm();return "save_failed";}
        accounts[t.A]=docA;accounts[t.B]=docB;players[t.A]=newA;players[t.B]=newB;
        EndTrade(t,room,"trade_done");
        return "ok";
    }
    // The account document that results from saving one character.
    private Account Document(string name,CharacterData character)
    {
        var account=accounts[name];
        return character.Id=="legacy"
            ? account with {X=character.X,Z=character.Z,Kills=character.Kills,Shards=character.Shards,Gold=character.Gold,QuestClaimed=character.QuestClaimed,RegionId=character.RegionId,Progress=character.Progress,Inventory=character.Inventory,Skills=character.Skills,Revision=account.Revision+1}
            : account with {Characters=account.Characters!.Select(c=>c.Id==character.Id?character:c).ToArray(),Revision=account.Revision+1};
    }

    private async Task SaveProgress(string name,CharacterData character)
    {
        var account=accounts[name];var saved=Document(name,character);
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
                if(trades.TryGetValue(name,out var open)){trades.TryRemove(open.A,out _);trades.TryRemove(open.B,out _);}
                LeaveParty(name);
                invites.TryRemove(name,out _);foreach(var pair in invites)if(pair.Value==name)invites.TryRemove(pair.Key,out _);
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
        result.SelfStats=CharacterStatsRules.View(selected.Progress,selected.Inventory);
        result.Inventory=InventoryRules.View(selected.Inventory);
        var learned=SkillRules.Load(selected.Skills);var at=now();result.SkillPoints=learned.Points;
        result.Trade=TradeFor(session.Name,selected);
        result.Party=PartyFor(session.Name);
        while(session.Mail.TryDequeue(out var mail))result.Chat.Add(mail);
        foreach(var (id,level) in learned.Levels)
            result.Skills.Add(new SkillView{Id=id,Level=level,CooldownRemaining=session.Cooldowns.TryGetValue(id,out var ready)?(float)Math.Max(0,ready-at):0});
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
    public const double RegenDelay=5, RegenPerSecond=.01, MpRegenPerSecond=.02;

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
            int taken=CharacterStatsRules.DamageTaken(CharacterStatsRules.TotalDefense(victim.Progress,victim.Inventory),attack);
            var after=CharacterStatsRules.Hurt(victim.Progress,taken);
            players[victimName]=victim with {Progress=after};sessions[victimName].LastHurt=time;
            room.Event(new CombatEvent{Kind="player_hit",Source=monster.Id,Target=victimName,Amount=taken,X=victim.X,Z=victim.Z});
            if(after.Hp>0)continue;
            room.Event(new CombatEvent{Kind="player_died",Source=monster.Id,Target=victimName,X=victim.X,Z=victim.Z});
            foreach(var other in room.Monsters.All)if(other.Target==victimName)other.LoseTarget();
        }
        foreach(var (name,session) in members) {
            if(!players.TryGetValue(name,out var c)||c.RegionId!=region)continue;
            var p=CharacterStatsRules.Normalize(c.Progress);int maxHp=CharacterStatsRules.MaxHp(p.Level),maxMp=CharacterStatsRules.MaxMp(p.Level);
            if(p.Hp>0&&p.Mp<maxMp) {
                // MP always trickles back, even in combat, so skills stay usable.
                session.MpCarry+=maxMp*MpRegenPerSecond*dt;int mp=(int)session.MpCarry;
                if(mp>=1){session.MpCarry-=mp;c=c with {Progress=CharacterStatsRules.RestoreMp(c.Progress,mp)};p=CharacterStatsRules.Normalize(c.Progress);players[name]=c;}
            }
            if(p.Hp==0||p.Hp>=maxHp||time-session.LastHurt<RegenDelay){session.RegenCarry=0;continue;}
            session.RegenCarry+=maxHp*RegenPerSecond*dt;
            int whole=(int)session.RegenCarry;if(whole<1)continue;
            session.RegenCarry-=whole;players[name]=c with {Progress=CharacterStatsRules.Heal(c.Progress,whole)};
        }
    }
}
