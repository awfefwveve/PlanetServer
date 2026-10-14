using Planet.Protocol;
namespace Planet.Server;
// Initial portfolio balance, not verified original-game formulas.
// Persist per-character progression; derive display bounds only on the server.
public sealed record CharacterProgress(int Level=1,long Experience=0,int Hp=275,int Mp=60);
public static class CharacterStatsRules {
    public const int MaxLevel=100;
    public static int MaxHp(int level)=>275+(level-1)*20;
    public static int MaxMp(int level)=>60+(level-1)*5;
    public static long NextLevel(int level)=>500L*level;
    public static int Attack(int level)=>1+(level-1)/3;
    public static int Defense(int level)=>(level-1)/4;
    // Fraction of the current level's requirement lost on death (never below zero).
    public const double DeathPenalty=.05;

    public static CharacterProgress Normalize(CharacterProgress? saved){
        var p=saved??new CharacterProgress();int level=Math.Clamp(p.Level,1,MaxLevel);
        return new CharacterProgress(level,Math.Clamp(p.Experience,0,NextLevel(level)-1),Math.Clamp(p.Hp,0,MaxHp(level)),Math.Clamp(p.Mp,0,MaxMp(level)));
    }
    public static CharacterStats View(CharacterProgress? saved){
        var p=Normalize(saved);
        return new CharacterStats{Level=p.Level,Experience=p.Experience,NextLevelExperience=NextLevel(p.Level),
            Hp=p.Hp,MaxHp=MaxHp(p.Level),Mp=p.Mp,MaxMp=MaxMp(p.Level),Attack=Attack(p.Level),Defense=Defense(p.Level)};
    }
    // Adds experience and applies every level-up it causes. Level-up restores HP/MP.
    public static (CharacterProgress Progress,int LevelsGained) GainExperience(CharacterProgress? saved,long amount){
        var p=Normalize(saved);long xp=p.Experience+Math.Max(0,amount);int level=p.Level,gained=0;
        while(level<MaxLevel&&xp>=NextLevel(level)){xp-=NextLevel(level);level++;gained++;}
        if(level==MaxLevel)xp=Math.Min(xp,NextLevel(level)-1);
        return gained==0?(p with {Experience=xp},0):(new CharacterProgress(level,xp,MaxHp(level),MaxMp(level)),gained);
    }
    public static int DamageTaken(int level,int monsterAttack)=>Math.Max(1,monsterAttack-Defense(level));
    // Applies damage. Reaching 0 HP is death: a small experience penalty, no level loss.
    public static CharacterProgress Hurt(CharacterProgress? saved,int damage){
        var p=Normalize(saved);int hp=Math.Max(0,p.Hp-damage);
        if(hp>0)return p with {Hp=hp};
        long penalty=(long)(NextLevel(p.Level)*DeathPenalty);
        return p with {Hp=0,Experience=Math.Max(0,p.Experience-penalty)};
    }
    public static CharacterProgress Heal(CharacterProgress? saved,int amount){
        var p=Normalize(saved);return p.Hp==0?p:p with {Hp=Math.Min(MaxHp(p.Level),p.Hp+amount)};
    }
    public static CharacterProgress Revive(CharacterProgress? saved){
        var p=Normalize(saved);return p with {Hp=Math.Max(1,MaxHp(p.Level)/2)};
    }
    public static bool IsDead(CharacterProgress? saved)=>Normalize(saved).Hp==0;
}
