using Planet.Protocol;
namespace Planet.Server;
// Initial portfolio balance, not verified original-game formulas.
// Persist per-character progression; derive display bounds only on the server.
public sealed record CharacterProgress(int Level=1,long Experience=0,int Hp=275,int Mp=60);
public static class CharacterStatsRules {
    public static CharacterStats View(CharacterProgress? saved){
        var p=saved??new CharacterProgress();int level=Math.Clamp(p.Level,1,100);
        int maxHp=275+(level-1)*20,maxMp=60+(level-1)*5;
        return new CharacterStats{Level=level,Experience=Math.Clamp(p.Experience,0,500L*level-1),NextLevelExperience=500L*level,
            Hp=Math.Clamp(p.Hp,0,maxHp),MaxHp=maxHp,Mp=Math.Clamp(p.Mp,0,maxMp),MaxMp=maxMp,Attack=1,Defense=0};
    }
}
