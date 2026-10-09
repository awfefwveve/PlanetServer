using Planet.Protocol;

namespace Planet.Server;

// Owned by one room and accessed under its channel gate. Client input never
// supplies HP, spawn location, damage, or the respawn deadline.
public sealed class MonsterPopulation
{
    public sealed class Monster
    {
        private readonly string prefix;
        private int incarnation=1;
        public string Id => prefix+":"+incarnation;
        public string Zone {get;}
        public float X {get;}
        public float Z {get;}
        public int Hp {get;private set;}=3;
        public const int MaxHp=3;
        private double respawnAt;
        internal Monster(string prefix,string zone,float x,float z){this.prefix=prefix;Zone=zone;X=x;Z=z;}
        public void Damage(){if(Hp>1)Hp--;else throw new InvalidOperationException("Fatal damage must commit its reward first.");}
        public void CommitDeath(double time){if(Hp!=1)throw new InvalidOperationException("Invalid death transition.");Hp=0;respawnAt=time+3;}
        internal void Refresh(double time){if(Hp==0&&time>=respawnAt){incarnation++;Hp=MaxHp;}}
        public MonsterView View()=>new(){Id=Id,SpawnZone=Zone,X=X,Z=Z,Hp=Hp,MaxHp=MaxHp};
    }
    private readonly Monster[] monsters;
    public Monster Primary=>monsters[0];
    public MonsterPopulation(int channel,string region)
    {
        // The first slot preserves the old single-slime demo. The hunting area
        // has four additional authored slots, all on walkable meadow ground.
        (float X,float Z)[] slots=region=="meadow"?[(3,0),(7,-4),(10,-4),(7,1),(11,1)]:[(3,0)];
        monsters=slots.Select((p,i)=>new Monster(channel+":"+region+":"+i,i==0?"practice":"east_meadow",p.X,p.Z)).ToArray();
        if(monsters.Any(m=>Regions.BlockReason(region,m.X,m.Z)!="ok"))throw new InvalidOperationException("Spawn slot is not walkable.");
    }
    public void Refresh(double time){foreach(var monster in monsters)monster.Refresh(time);}
    public Monster? Find(string id)=>monsters.FirstOrDefault(m=>m.Id==id);
    public IEnumerable<MonsterView> Snapshot()=>monsters.Select(m=>m.View());
}
