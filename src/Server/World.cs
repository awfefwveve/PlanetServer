using System.Diagnostics;
using System.Text.RegularExpressions;
using Planet.Protocol;

namespace Planet.Server;

public sealed class Session
{
    public string? Name;
    public ulong Sequence;
    public double LastMove = double.NegativeInfinity;
    public double LastAttack = double.NegativeInfinity;
    public int LoginAttempts;
}

public sealed class World(IStore store, Func<double>? clock = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Account> players = new();
    private readonly Func<double> now = clock ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
    private int hp = 3;
    private double respawnAt;
    private long tick;

    public async Task<Update> Handle(Session session, Command command)
    {
        await gate.WaitAsync();
        try
        {
            var time = now();
            if (hp == 0 && time >= respawnAt) hp = 3;
            if (command.Sequence == 0 || command.Sequence <= session.Sequence)
                return Snapshot(command.Sequence, "replay", "Duplicate or old request rejected.", session.Name != null);
            session.Sequence = command.Sequence;
            if (command.Kind == "login") return await Login(session, command);
            if (session.Name is null) return Snapshot(command.Sequence, "unauthorized", "Sign in first.", false);
            var current = players[session.Name];
            var code = "ok";
            var message = "";
            switch (command.Kind)
            {
                case "snapshot": break;
                case "move":
                    var move = MovementRules.Evaluate(current.X, current.Z, command.Dx, command.Dz, time-session.LastMove);
                    code = move.Code;
                    if (code != "invalid" && code != "rate_limited") session.LastMove = time;
                    if (code == "ok") players[current.Name] = current with { X = move.X, Z = move.Z };
                    break;
                case "attack":
                    if (time - session.LastAttack < .4) { code = "cooldown"; break; }
                    session.LastAttack = time;
                    if (hp == 0) { code = "no_target"; break; }
                    if ((current.X-3)*(current.X-3) + current.Z*current.Z > 2.25f)
                    { code = "out_of_range"; message = "Move within 1.5 units of the slime."; break; }
                    if (hp > 1) { hp--; break; }
                    // Commit reward first. On failure neither the reward nor monster death is applied in memory.
                    var reward = current with { Kills = current.Kills + 1, Shards = current.Shards + 1, Revision = current.Revision + 1 };
                    await store.Save(reward, current.Revision);
                    players[current.Name] = reward;
                    hp = 0; respawnAt = time + 3;
                    message = "Slime defeated. +1 shard. Progress saved.";
                    break;
                case "claim":
                    if (current.QuestClaimed) { code = "already_claimed"; break; }
                    if (current.Kills < 3) { code = "not_ready"; message = "Defeat 3 slimes first."; break; }
                    var claimed = current with { Gold = current.Gold + 100, QuestClaimed = true, Revision = current.Revision + 1 };
                    await store.Save(claimed, current.Revision);
                    players[current.Name] = claimed;
                    message = "Quest complete. +100 gold. Progress saved.";
                    break;
                default: code = "unknown_command"; break;
            }
            return Snapshot(command.Sequence, code, message, true);
        }
        finally { gate.Release(); }
    }
    private async Task<Update> Login(Session session, Command command)
    {
        if (session.Name != null) return Snapshot(command.Sequence, "already_signed_in", "", true);
        if (++session.LoginAttempts > 5) return Snapshot(command.Sequence, "login_limited", "Reconnect to try again.", false);
        var name = command.Name.ToLowerInvariant();
        if (!Regex.IsMatch(name, "^[a-z0-9_]{3,16}$") || command.Password.Length < 8 || command.Password.Length > 128)
            return Snapshot(command.Sequence, "invalid_login", "Name: 3-16 letters/numbers. Password: 8-128 characters.", false);
        var account = await store.Load(name);
        if (account is null)
        {
            account = Account.Create(name, command.Password);
            await store.Save(account, null);
        }
        else if (!account.Verify(command.Password)) return Snapshot(command.Sequence, "invalid_login", "Invalid credentials.", false);
        if (players.ContainsKey(name)) return Snapshot(command.Sequence, "duplicate_login", "This character is already connected.", false);
        players.Add(name, account);
        session.Name = name;
        return Snapshot(command.Sequence, "ok", "Welcome. Defeat 3 slimes, then claim your reward.", true);
    }
    public async Task Disconnect(Session session)
    {
        await gate.WaitAsync();
        try
        {
            if (session.Name is null || !players.TryGetValue(session.Name, out var account)) return;
            try { await store.Save(account with { Revision = account.Revision + 1 }, account.Revision); }
            finally { players.Remove(session.Name); session.Name = null; }
        }
        finally { gate.Release(); }
    }
    private Update Snapshot(ulong sequence, string code, string message, bool authenticated)
    {
        var result = new Update { Sequence = sequence, Code = code, Message = message, Tick = ++tick };
        if (!authenticated) return result;
        result.Monster = new MonsterView { X = 3, Z = 0, Hp = hp, MaxHp = 3 };
        result.Players.AddRange(players.Values.OrderBy(p => p.Name).Select(p => new PlayerView {
            Name = p.Name, X = p.X, Z = p.Z, Kills = p.Kills, Shards = p.Shards, Gold = p.Gold, QuestClaimed = p.QuestClaimed
        }));
        return result;
    }
}

