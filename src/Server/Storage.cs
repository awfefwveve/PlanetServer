using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

namespace Planet.Server;

public sealed record Account(string Name, string Salt, string Hash, float X = 0, float Z = 0,
    int Kills = 0, int Shards = 0, int Gold = 0, bool QuestClaimed = false, long Revision = 0)
{
    public static Account Create(string name, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return new(name, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }
    public bool Verify(string password) => CryptographicOperations.FixedTimeEquals(
        Convert.FromBase64String(Hash), Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(Salt),
            120_000, HashAlgorithmName.SHA256, 32));
}

public interface IStore
{
    Task<Account?> Load(string name);
    Task Save(Account account, long? expectedRevision);
}

// Local development only: one server process, atomic replacement on the same volume.
// PostgreSQL is required for deployment and stronger durability guarantees.
public sealed class FileStore(string directory) : IStore
{
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
    public async Task Save(Account account, long? expectedRevision)
    {
        await using var cmd = source.CreateCommand(expectedRevision is null
            ? "INSERT INTO planet_accounts(name,revision,payload) VALUES($1,$2,$3::jsonb) ON CONFLICT DO NOTHING"
            : "UPDATE planet_accounts SET revision=$2,payload=$3::jsonb,updated_at=now() WHERE name=$1 AND revision=$4");
        cmd.Parameters.AddWithValue(account.Name);
        cmd.Parameters.AddWithValue(account.Revision);
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(account));
        if (expectedRevision is not null) cmd.Parameters.AddWithValue(expectedRevision.Value);
        if (await cmd.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("Revision conflict");
    }
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
