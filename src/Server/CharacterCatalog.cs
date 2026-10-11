using System.Text;
using System.Text.RegularExpressions;
using Planet.Protocol;

namespace Planet.Server;

public sealed record CharacterLook(string Archetype = "모험가", int Gender = 0, int Skin = 0,
    int Face = 0, int Hair = 0, int HairColor = 0)
{
    public Appearance ToProtocol() => new() { Archetype=Archetype, Gender=Gender, Skin=Skin, Face=Face, Hair=Hair, HairColor=HairColor };
    public static CharacterLook From(Appearance a) => new(a.Archetype,a.Gender,a.Skin,a.Face,a.Hair,a.HairColor);
}
public sealed record CharacterData(string Id, string Name, CharacterLook Look, float X=0,float Z=0,
    int Kills=0,int Shards=0,int Gold=0,bool QuestClaimed=false,string RegionId="meadow",CharacterProgress? Progress=null)
{
    public CharacterInfo ToProtocol()=>new(){Id=Id,Name=Name,Appearance=Look.ToProtocol()};
    public static CharacterData Legacy(Account a)=>new("legacy",a.Name,new(),a.X,a.Z,a.Kills,a.Shards,a.Gold,a.QuestClaimed,a.RegionId,a.Progress);
}
public sealed class CharacterCatalog(IStore store)
{
    public static string Normalize(string value)=>value.Trim().Normalize(NormalizationForm.FormC);
    public static string NameKey(string value)=>Normalize(value).ToUpperInvariant();
    public async Task<string> CheckName(string name)
    {
        name=Normalize(name);
        if(!Regex.IsMatch(name,"^[가-힣a-zA-Z0-9]{2,12}$"))return "invalid_name";
        return await store.CharacterNameExists(NameKey(name))?"name_taken":"ok";
    }
    public async Task<(string Code,Account Account)> Create(Account account,Command command)
    {
        var characters=account.Characters??[];
        if(characters.Length>=12)return ("slots_full",account);
        var a=command.Appearance;
        if(a is null || !new[]{"모험가","나린","에리프","루나","전사","마법사","궁수","도적","타니"}.Contains(a.Archetype) ||
           a.Gender<0||a.Gender>1||a.Skin<0||a.Skin>2||a.Face<0||a.Face>2||a.Hair<0||a.Hair>2||a.HairColor<0||a.HairColor>2)
            return ("invalid_appearance",account);
        string code=await CheckName(command.DisplayName);
        if(code!="ok")return (code,account);
        var character=new CharacterData(Guid.NewGuid().ToString("N"),Normalize(command.DisplayName),CharacterLook.From(a),X:-6.2f,Z:-2.2f,Progress:new CharacterProgress());
        var updated=account with {Characters=[..characters,character],Revision=account.Revision+1};
        await store.Save(updated,account.Revision);
        return ("ok",updated);
    }
    public async Task<Account> Migrate(Account account)
    {
        if(account.Characters is not null)return account;
        var old=CharacterData.Legacy(account) with {Id=Guid.NewGuid().ToString("N")};
        var updated=account with {Characters=[old],Revision=account.Revision+1};
        await store.Save(updated,account.Revision);return updated;
    }
}
