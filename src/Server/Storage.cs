using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

namespace Planet.Server;

public sealed record Account(string Name, string Salt, string Hash, float X = 0, float Z = 0,
    int Kills = 0, int Shards = 0, int Gold = 0, bool QuestClaimed = false, long Revision = 0, CharacterData[]? Characters = null, string RegionId = "meadow", CharacterProgress? Progress = null,
    InventoryData? Inventory = null, SkillData? Skills = null)
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
    // All-or-nothing save of several accounts (player trade). Every revision must still match,
    // otherwise nothing is written.
    Task SaveMany(IReadOnlyList<(Account Account, long? ExpectedRevision)> changes);
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
    // Account names are case-insensitive (World lowercases logins), so file names are too.
    private string PathOf(string name) => Path.Combine(directory, name.ToLowerInvariant() + ".json");
    public Task<Account?> Load(string name)
    {
        var path = PathOf(name);
        return Task.FromResult(File.Exists(path) ? JsonSerializer.Deserialize<Account>(File.ReadAllText(path)) : null);
    }
    public async Task Save(Account account, long? expectedRevision)
    {
        Directory.CreateDirectory(directory);
        var old = await Load(account.Name);
        if (old?.Revision != expectedRevision) throw new InvalidOperationException("Revision conflict");
        var path = PathOf(account.Name);
        var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, account);
            file.Flush(true);
        }
        File.Move(temporary, path, true);
    }
    // Development store: checks every revision first, writes every temp file, then swaps them in.
    // The swap of two files is not one atomic step; a crash exactly between the two renames can
    // leave one side applied. PostgresStore (PostgresStore.cs) is the store that guarantees all-or-nothing.
    public async Task SaveMany(IReadOnlyList<(Account Account, long? ExpectedRevision)> changes)
    {
        Directory.CreateDirectory(directory);
        foreach(var (account,expected) in changes)
            if((await Load(account.Name))?.Revision != expected) throw new InvalidOperationException("Revision conflict");
        var staged=new List<(string Temporary,string Path)>();
        foreach(var (account,_) in changes) {
            var path=PathOf(account.Name);var temporary=path+".tmp";
            using(var file=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(file,account);file.Flush(true);}
            staged.Add((temporary,path));
        }
        foreach(var (temporary,path) in staged)File.Move(temporary,path,true);
    }
}
