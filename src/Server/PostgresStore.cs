using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Planet.Server;

// Relational store. One account is spread over five tables (see docs/DATABASE.md):
//   accounts            login data + revision (optimistic lock for the whole account)
//   characters          one row per character: look, position, progress, gold
//   inventory_items     one row per occupied bag slot
//   equipment           one row per worn item
//   character_skills    one row per learned skill
// Every Save/SaveMany is ONE transaction: the revision check, all character rows and all item
// rows commit together or not at all, so a crash can never leave an item half-moved.
public sealed class PostgresStore(string connectionString) : IStore, IAsyncDisposable
{
    // Character id used for the pre-lobby "direct login" state kept on the account itself.
    private const string LegacyId = "legacy";
    private readonly NpgsqlDataSource source = NpgsqlDataSource.Create(connectionString);

    public async Task Initialize()
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS accounts (
                account_key   text PRIMARY KEY,          -- lower-case login name
                name          text NOT NULL,
                salt          text NOT NULL,
                password_hash text NOT NULL,
                revision      bigint NOT NULL,
                has_roster    boolean NOT NULL,          -- false: account predates character slots
                created_at    timestamptz NOT NULL DEFAULT now(),
                updated_at    timestamptz NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS characters (
                account_key   text NOT NULL REFERENCES accounts(account_key) ON DELETE CASCADE,
                character_id  text NOT NULL,
                slot_order    int  NOT NULL,
                name          text NOT NULL,
                name_key      text UNIQUE,               -- NULL only for the legacy row
                archetype     text NOT NULL, gender smallint NOT NULL, skin smallint NOT NULL,
                face          smallint NOT NULL, hair smallint NOT NULL, hair_color smallint NOT NULL,
                region_id     text NOT NULL,
                x real NOT NULL, z real NOT NULL,
                kills int NOT NULL, shards int NOT NULL,
                gold          int NOT NULL CHECK (gold >= 0),
                quest_claimed boolean NOT NULL,
                level int, experience bigint, hp int, mp int,  -- all NULL = progress never started
                bag_size      smallint,                  -- NULL = inventory never created
                skill_points  int,                       -- NULL = skills never created
                PRIMARY KEY (account_key, character_id)
            );
            CREATE TABLE IF NOT EXISTS inventory_items (
                account_key text NOT NULL, character_id text NOT NULL,
                slot    smallint NOT NULL CHECK (slot >= 0),
                item_id text NOT NULL,
                count   int NOT NULL CHECK (count > 0),
                PRIMARY KEY (account_key, character_id, slot),
                FOREIGN KEY (account_key, character_id) REFERENCES characters ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS equipment (
                account_key text NOT NULL, character_id text NOT NULL,
                equip_slot text NOT NULL,
                item_id    text NOT NULL,
                PRIMARY KEY (account_key, character_id, equip_slot),
                FOREIGN KEY (account_key, character_id) REFERENCES characters ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS character_skills (
                account_key text NOT NULL, character_id text NOT NULL,
                skill_id text NOT NULL,
                level    int NOT NULL CHECK (level > 0),
                PRIMARY KEY (account_key, character_id, skill_id),
                FOREIGN KEY (account_key, character_id) REFERENCES characters ON DELETE CASCADE
            );
            -- Operations queries such as "who holds item X" (duplication audits) scan by item.
            CREATE INDEX IF NOT EXISTS inventory_items_item_id ON inventory_items(item_id);
            """);
        await MigrateDocuments(connection, transaction);
        await transaction.CommitAsync();
    }

    // Earlier versions stored each account as one jsonb document in planet_accounts.
    // Copy any account not yet in the new tables; the old table is left untouched as a backup.
    private static async Task MigrateDocuments(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('planet_accounts') IS NOT NULL", connection, transaction))
            if (!(bool)(await exists.ExecuteScalarAsync())!) return;
        var pending = new List<Account>();
        await using (var select = new NpgsqlCommand("""
            SELECT payload::text FROM planet_accounts p
            WHERE NOT EXISTS (SELECT 1 FROM accounts a WHERE a.account_key = lower(p.name))
            """, connection, transaction))
        await using (var reader = await select.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                if (JsonSerializer.Deserialize<Account>(reader.GetString(0)) is { } account) pending.Add(account);
        foreach (var account in pending) await Write(connection, transaction, account, null);
    }

    public async Task<Account?> Load(string name)
    {
        await using var connection = await source.OpenConnectionAsync();
        var key = name.ToLowerInvariant();
        string displayName, salt, hash; long revision; bool hasRoster;
        await using (var cmd = new NpgsqlCommand(
            "SELECT name, salt, password_hash, revision, has_roster FROM accounts WHERE account_key=$1", connection))
        {
            cmd.Parameters.AddWithValue(key);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            (displayName, salt, hash, revision, hasRoster) =
                (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetBoolean(4));
        }

        var rows = new List<(CharacterData Character, short? BagSize, int? SkillPoints)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT character_id, name, archetype, gender, skin, face, hair, hair_color, region_id, x, z,
                   kills, shards, gold, quest_claimed, level, experience, hp, mp, bag_size, skill_points
            FROM characters WHERE account_key=$1 ORDER BY slot_order
            """, connection))
        {
            cmd.Parameters.AddWithValue(key);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var look = new CharacterLook(r.GetString(2), r.GetInt16(3), r.GetInt16(4), r.GetInt16(5), r.GetInt16(6), r.GetInt16(7));
                CharacterProgress? progress = r.IsDBNull(15) ? null
                    : new(r.GetInt32(15), r.GetInt64(16), r.GetInt32(17), r.GetInt32(18));
                var character = new CharacterData(r.GetString(0), r.GetString(1), look, r.GetFloat(9), r.GetFloat(10),
                    r.GetInt32(11), r.GetInt32(12), r.GetInt32(13), r.GetBoolean(14), r.GetString(8), progress);
                rows.Add((character, r.IsDBNull(19) ? null : r.GetInt16(19), r.IsDBNull(20) ? null : r.GetInt32(20)));
            }
        }

        var slots = new Dictionary<string, List<(short Slot, ItemStack Item)>>();
        var equipped = new Dictionary<string, Dictionary<string, string>>();
        var skills = new Dictionary<string, Dictionary<string, int>>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT 'bag', character_id, slot, NULL, item_id, count FROM inventory_items WHERE account_key=$1
            UNION ALL SELECT 'worn', character_id, 0, equip_slot, item_id, 0 FROM equipment WHERE account_key=$1
            UNION ALL SELECT 'skill', character_id, 0, skill_id, NULL, level FROM character_skills WHERE account_key=$1
            """, connection))
        {
            // One query for the three child tables keeps Load at three round trips in total.
            cmd.Parameters.AddWithValue(key);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var id = r.GetString(1);
                switch (r.GetString(0))
                {
                    case "bag": Bucket(slots, id).Add((r.GetInt16(2), new ItemStack(r.GetString(4), r.GetInt32(5)))); break;
                    case "worn": Bucket(equipped, id)[r.GetString(3)] = r.GetString(4); break;
                    default: Bucket(skills, id)[r.GetString(3)] = r.GetInt32(5); break;
                }
            }
        }

        var characters = rows.Select(row =>
        {
            var id = row.Character.Id;
            InventoryData? inventory = null;
            if (row.BagSize is { } size)
            {
                var bag = new ItemStack?[size];
                foreach (var (slot, item) in slots.GetValueOrDefault(id) ?? []) bag[slot] = item;
                inventory = new InventoryData(bag, equipped.GetValueOrDefault(id) ?? new());
            }
            SkillData? learned = row.SkillPoints is { } points ? new SkillData(skills.GetValueOrDefault(id) ?? new(), points) : null;
            return row.Character with { Inventory = inventory, Skills = learned };
        }).ToList();

        var legacy = characters.FirstOrDefault(c => c.Id == LegacyId);
        var account = new Account(displayName, salt, hash, Revision: revision,
            Characters: hasRoster ? characters.Where(c => c.Id != LegacyId).ToArray() : null);
        return legacy is null ? account : account with
        {
            X = legacy.X, Z = legacy.Z, Kills = legacy.Kills, Shards = legacy.Shards, Gold = legacy.Gold,
            QuestClaimed = legacy.QuestClaimed, RegionId = legacy.RegionId, Progress = legacy.Progress,
            Inventory = legacy.Inventory, Skills = legacy.Skills,
        };
    }

    public async Task<bool> CharacterNameExists(string normalizedName)
    {
        await using var cmd = source.CreateCommand("""
            SELECT EXISTS(SELECT 1 FROM characters WHERE name_key=$1
                   UNION ALL SELECT 1 FROM accounts WHERE NOT has_roster AND upper(name)=$1)
            """);
        cmd.Parameters.AddWithValue(normalizedName);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task Save(Account account, long? expectedRevision) => await SaveMany([(account, expectedRevision)]);

    // One database transaction: every account must still be at its expected revision,
    // otherwise everything rolls back (player trade moves items between two accounts).
    public async Task SaveMany(IReadOnlyList<(Account Account, long? ExpectedRevision)> changes)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var (account, expected) in changes) await Write(connection, transaction, account, expected);
        await transaction.CommitAsync();
    }

    private static async Task Write(NpgsqlConnection connection, NpgsqlTransaction transaction, Account account, long? expectedRevision)
    {
        var key = account.Name.ToLowerInvariant();
        await using (var cmd = new NpgsqlCommand(expectedRevision is null
            ? """
              INSERT INTO accounts(account_key, name, salt, password_hash, revision, has_roster)
              VALUES($1,$2,$3,$4,$5,$6) ON CONFLICT DO NOTHING
              """
            : """
              UPDATE accounts SET revision=$5, has_roster=$6, updated_at=now()
              WHERE account_key=$1 AND revision=$7
              """, connection, transaction))
        {
            cmd.Parameters.AddWithValue(key); cmd.Parameters.AddWithValue(account.Name);
            cmd.Parameters.AddWithValue(account.Salt); cmd.Parameters.AddWithValue(account.Hash);
            cmd.Parameters.AddWithValue(account.Revision); cmd.Parameters.AddWithValue(account.Characters is not null);
            if (expectedRevision is not null) cmd.Parameters.AddWithValue(expectedRevision.Value);
            if (await cmd.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("Revision conflict");
        }

        // The legacy row holds the account-level play state used by direct (non-lobby) login.
        var characters = new List<(CharacterData Character, string? NameKey)>
            { (CharacterData.Legacy(account) with { Name = account.Name }, null) };
        characters.AddRange((account.Characters ?? []).Select(c => (c, (string?)CharacterCatalog.NameKey(c.Name))));

        // Rows are replaced per account inside the transaction; one batch = one network round trip.
        await using var batch = new NpgsqlBatch(connection, transaction);
        batch.BatchCommands.Add(Command("DELETE FROM characters WHERE account_key=$1", key));
        int order = -1;
        foreach (var (c, nameKey) in characters)
        {
            var p = c.Progress;
            batch.BatchCommands.Add(Command("""
                INSERT INTO characters(account_key, character_id, slot_order, name, name_key, archetype, gender, skin, face, hair,
                    hair_color, region_id, x, z, kills, shards, gold, quest_claimed, level, experience, hp, mp, bag_size, skill_points)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22,$23,$24)
                """, key, c.Id, order++, c.Name, Nullable(nameKey, NpgsqlDbType.Text), c.Look.Archetype,
                (short)c.Look.Gender, (short)c.Look.Skin, (short)c.Look.Face, (short)c.Look.Hair, (short)c.Look.HairColor,
                c.RegionId, c.X, c.Z, c.Kills, c.Shards, c.Gold, c.QuestClaimed,
                Nullable(p?.Level, NpgsqlDbType.Integer), Nullable(p?.Experience, NpgsqlDbType.Bigint),
                Nullable(p?.Hp, NpgsqlDbType.Integer), Nullable(p?.Mp, NpgsqlDbType.Integer),
                Nullable((short?)c.Inventory?.Slots.Length, NpgsqlDbType.Smallint), Nullable(c.Skills?.Points, NpgsqlDbType.Integer)));
            if (c.Inventory is { } inventory)
            {
                for (short slot = 0; slot < inventory.Slots.Length; slot++)
                    if (inventory.Slots[slot] is { } item)
                        batch.BatchCommands.Add(Command("INSERT INTO inventory_items VALUES($1,$2,$3,$4,$5)", key, c.Id, slot, item.ItemId, item.Count));
                foreach (var (equipSlot, itemId) in inventory.Equipped)
                    batch.BatchCommands.Add(Command("INSERT INTO equipment VALUES($1,$2,$3,$4)", key, c.Id, equipSlot, itemId));
            }
            foreach (var (skillId, level) in c.Skills?.Levels ?? [])
                batch.BatchCommands.Add(Command("INSERT INTO character_skills VALUES($1,$2,$3,$4)", key, c.Id, skillId, level));
        }
        await batch.ExecuteNonQueryAsync();
    }

    private static NpgsqlBatchCommand Command(string sql, params object[] values)
    {
        var command = new NpgsqlBatchCommand(sql);
        foreach (var value in values) command.Parameters.Add(value as NpgsqlParameter ?? new NpgsqlParameter { Value = value });
        return command;
    }
    private static NpgsqlParameter Nullable(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, transaction);
        await cmd.ExecuteNonQueryAsync();
    }
    private static TValue Bucket<TValue>(Dictionary<string, TValue> map, string id) where TValue : new()
        => map.TryGetValue(id, out var value) ? value : map[id] = new();

    public ValueTask DisposeAsync() => source.DisposeAsync();
}
