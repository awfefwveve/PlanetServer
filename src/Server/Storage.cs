using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

namespace Planet.Server;

public sealed record Account(string Name, string Salt, string Hash, float X = 0, float Z = 0,
    int Kills = 0, int Shards = 0, int Gold = 0, bool QuestClaimed = false, long Revision = 0, CharacterData[]? Characters = null, string RegionId = "meadow", CharacterProgress? Progress = null)
{
    public static Account Create(string name, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return new(name, Convert.ToBase64String(salt), Convert.ToBase64String(hash), Characters: []);
    }
    public bool Verify(string password) => CryptographicOperations.FixedTimeEquals(
        Convert.FromBase64String(Hash), Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(Salt),
            120_000, HashAlgorithmName.SHA256, 32));
}

public interface IStore
{
    Task<Account?> Load(string name);
    Task Save(Account account, long? expectedRevision);
    Task<bool> CharacterNameExists(string normalizedName);
}

// Local development only: one server process, atomic replacement on the same volume.
// PostgreSQL is required for deployment and stronger durability guarantees.
public sealed class FileStore(string directory) : IStore
{
    public Task<bool> CharacterNameExists(string normalizedName)
    {
        if(!Directory.Exists(directory))return Task.FromResult(false);
        foreach(var path in Directory.EnumerateFiles(directory,"*.json")) {
            var account=JsonSerializer.Deserialize<Account>(File.ReadAllText(path));
            if(account is not null && (account.Characters is null ? CharacterCatalog.NameKey(account.Name)==normalizedName : account.Characters.Any(c=>CharacterCatalog.NameKey(c.Name)==normalizedName)))return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
    public Task<Account?> Load(string name)
    {
        var path = Path.Combine(directory, name + ".json");
        return Task.FromResult(File.Exists(path) ? JsonSerializer.Deserialize<Account>(File.ReadAllText(path)) : null);
    }
    public async Task Save(Account account, long? expectedRevision)
    {
        Directory.CreateDirectory(directory);
        var old = await Load(account.Name);
        if (old?.Revision != expectedRevision) throw new InvalidOperationException("Revision conflict");
        var path = Path.Combine(directory, account.Name + ".json");
        var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, account);
            file.Flush(true);
        }
        File.Move(temporary, path, true);
    }
}

public sealed class PostgresStore(string connectionString) : IStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource source = NpgsqlDataSource.Create(connectionString);
    public async Task Initialize()
    {
        await using var cmd = source.CreateCommand("""
            CREATE TABLE IF NOT EXISTS planet_accounts (
                name text PRIMARY KEY, revision bigint NOT NULL, payload jsonb NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS planet_character_names (
                character_id text PRIMARY KEY, name_key text UNIQUE NOT NULL,
                account_name text NOT NULL REFERENCES planet_accounts(name)
            );
            INSERT INTO planet_character_names(character_id,name_key,account_name)
            SELECT c->>'Id', upper(c->>'Name'), a.name FROM planet_accounts a,
                jsonb_array_elements(COALESCE(NULLIF(a.payload->'Characters','null'::jsonb),'[]'::jsonb)) c
            ON CONFLICT(character_id) DO NOTHING;
            """);
        await cmd.ExecuteNonQueryAsync();
    }
    public async Task<Account?> Load(string name)
    {
        await using var cmd = source.CreateCommand("SELECT payload::text FROM planet_accounts WHERE name=$1");
        cmd.Parameters.AddWithValue(name);
        var result = await cmd.ExecuteScalarAsync();
        return result is string json ? JsonSerializer.Deserialize<Account>(json) : null;
    }
    public async Task<bool> CharacterNameExists(string normalizedName)
    {
        await using var cmd=source.CreateCommand("SELECT EXISTS(SELECT 1 FROM planet_character_names WHERE name_key=$1 UNION ALL SELECT 1 FROM planet_accounts WHERE (payload->'Characters' IS NULL OR payload->'Characters'='null'::jsonb) AND upper(name)=$1)");
        cmd.Parameters.AddWithValue(normalizedName);return (bool)(await cmd.ExecuteScalarAsync())!;
    }
    public async Task Save(Account account, long? expectedRevision)
    {
        await using var connection=await source.OpenConnectionAsync();
        await using var transaction=await connection.BeginTransactionAsync();
        await using var cmd=connection.CreateCommand();cmd.Transaction=transaction;
        cmd.CommandText=expectedRevision is null
            ? "INSERT INTO planet_accounts(name,revision,payload) VALUES($1,$2,$3::jsonb) ON CONFLICT DO NOTHING"
            : "UPDATE planet_accounts SET revision=$2,payload=$3::jsonb,updated_at=now() WHERE name=$1 AND revision=$4";
        cmd.Parameters.AddWithValue(account.Name);cmd.Parameters.AddWithValue(account.Revision);
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(account));
        if(expectedRevision is not null)cmd.Parameters.AddWithValue(expectedRevision.Value);
        if(await cmd.ExecuteNonQueryAsync()!=1)throw new InvalidOperationException("Revision conflict");
        foreach(var character in account.Characters??[]) {
            await using var reserve=connection.CreateCommand();reserve.Transaction=transaction;
            reserve.CommandText="INSERT INTO planet_character_names(character_id,name_key,account_name) VALUES($1,$2,$3) ON CONFLICT(character_id) DO NOTHING";
            reserve.Parameters.AddWithValue(character.Id);reserve.Parameters.AddWithValue(CharacterCatalog.NameKey(character.Name));reserve.Parameters.AddWithValue(account.Name);
            await reserve.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
