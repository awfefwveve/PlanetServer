using Planet.Protocol;

namespace Planet.Server;

// Balance for one kind of monster. Portfolio values, not original-game data.
public sealed record MonsterKind(string Name,int MaxHp,int Attack,long Experience,
    float Speed,float AttackRange,double AttackInterval,float Leash)
{
    // Passive: only fights back after being hit, then gives up when pulled too far from home.
    public static readonly MonsterKind Slime=new("slime",MaxHp:3,Attack:12,Experience:40,
        Speed:1.6f,AttackRange:1.0f,AttackInterval:1.6,Leash:7f);
}

// Owned by one room and accessed under its channel gate. Client input never
// supplies HP, spawn location, damage, position or the respawn deadline.
public sealed class MonsterPopulation
{
    public enum Mode { Idle, Chase, Return, Dead }
    public sealed class Monster
    {
        private readonly string prefix;
        private int incarnation=1;
        public string Id => prefix+":"+incarnation;
        public string Zone {get;}
        public MonsterKind Kind {get;}
        public float HomeX {get;}
        public float HomeZ {get;}
        public float X {get;private set;}
        public float Z {get;private set;}
        public int Hp {get;private set;}
        public int MaxHp=>Kind.MaxHp;
        public Mode State {get;private set;}=Mode.Idle;
        // Account name this monster is fighting. Set by being hit, cleared by leash/death/target loss.
        public string? Target {get;private set;}
        private double respawnAt,nextAttackAt;
        internal Monster(string prefix,string zone,float x,float z,MonsterKind kind)
        {this.prefix=prefix;Zone=zone;HomeX=X=x;HomeZ=Z=z;Kind=kind;Hp=kind.MaxHp;}
        public bool WouldDie(int damage)=>damage>=Hp;
        // Non-lethal hit only: lethal hits go through CommitDeath after the reward is saved.
        public void Damage(int damage,string attacker,double time)
        {
            if(damage<1||WouldDie(damage))throw new InvalidOperationException("Fatal damage must commit its reward first.");
            Hp-=damage;Provoke(attacker,time);
        }
        private void Provoke(string attacker,double time)
        {
            if(State==Mode.Chase&&Target!=null)return; // keeps its first opponent
            Target=attacker;State=Mode.Chase;nextAttackAt=Math.Max(nextAttackAt,time+.6); // short wind-up
        }
        public void CommitDeath(double time)
        {
            if(Hp<=0)throw new InvalidOperationException("Invalid death transition.");
            Hp=0;State=Mode.Dead;Target=null;respawnAt=time+3;
        }
        internal void Refresh(double time)
        {
            if(State!=Mode.Dead||time<respawnAt)return;
            incarnation++;Hp=Kind.MaxHp;X=HomeX;Z=HomeZ;State=Mode.Idle;Target=null;
        }
        public void LoseTarget(){if(State==Mode.Chase){Target=null;State=Mode.Return;}}

        // One AI step. findTarget returns the target's live position, or null if it is gone,
        // dead, in transit or in another room. Returns damage to deal to Target this step.
        internal int Step(double time,float dt,string region,Func<string,(float X,float Z)?> findTarget)
        {
            switch(State) {
                case Mode.Chase: {
                    var at=Target==null?null:findTarget(Target);
                    if(at is null||Distance(X,Z,HomeX,HomeZ)>Kind.Leash){LoseTarget();return 0;}
                    var (tx,tz)=at.Value;float d=Distance(X,Z,tx,tz);
                    if(d>Kind.AttackRange*.9f)MoveToward(tx,tz,Kind.Speed*dt,region);
                    if(Distance(X,Z,tx,tz)<=Kind.AttackRange&&time>=nextAttackAt){nextAttackAt=time+Kind.AttackInterval;return Kind.Attack;}
                    return 0;
                }
                case Mode.Return:
                    MoveToward(HomeX,HomeZ,Kind.Speed*1.5f*dt,region);
                    if(Distance(X,Z,HomeX,HomeZ)<.05f){X=HomeX;Z=HomeZ;Hp=Kind.MaxHp;State=Mode.Idle;} // resets like most MMOs
                    return 0;
                default:return 0;
            }
        }
        private void MoveToward(float tx,float tz,float step,string region)
        {
            float dx=tx-X,dz=tz-Z,d=MathF.Sqrt(dx*dx+dz*dz);if(d<1e-4f)return;
            if(step>d)step=d;float nx=X+dx/d*step,nz=Z+dz/d*step;
            // Same collision rules as players. Slide along one axis if the diagonal is blocked.
            if(Regions.BlockReason(region,nx,nz)=="ok"){X=nx;Z=nz;}
            else if(Regions.BlockReason(region,nx,Z)=="ok")X=nx;
            else if(Regions.BlockReason(region,X,nz)=="ok")Z=nz;
            else if(State==Mode.Return){X=HomeX;Z=HomeZ;} // never strand a returning monster
        }
        private static float Distance(float ax,float az,float bx,float bz){float dx=ax-bx,dz=az-bz;return MathF.Sqrt(dx*dx+dz*dz);}
        public MonsterView View()=>new(){Id=Id,SpawnZone=Zone,X=X,Z=Z,Hp=Hp,MaxHp=MaxHp,State=State.ToString().ToLowerInvariant(),Target=Target??""};
    }
    private readonly Monster[] monsters;
    public Monster Primary=>monsters[0];
    public IReadOnlyList<Monster> All=>monsters;
    public MonsterPopulation(int channel,string region)
    {
        // The first slot preserves the old single-slime demo. The hunting area
        // has four additional authored slots, all on walkable meadow ground.
        (float X,float Z)[] slots=region=="meadow"?[(3,0),(7,-4),(10,-4),(7,1),(11,1)]:[(3,0)];
        monsters=slots.Select((p,i)=>new Monster(channel+":"+region+":"+i,i==0?"practice":"east_meadow",p.X,p.Z,MonsterKind.Slime)).ToArray();
        if(monsters.Any(m=>Regions.BlockReason(region,m.X,m.Z)!="ok"))throw new InvalidOperationException("Spawn slot is not walkable.");
    }
    public void Refresh(double time){foreach(var monster in monsters)monster.Refresh(time);}
    public Monster? Find(string id)=>monsters.FirstOrDefault(m=>m.Id==id);
    public IEnumerable<MonsterView> Snapshot()=>monsters.Select(m=>m.View());
}
