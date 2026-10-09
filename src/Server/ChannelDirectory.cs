using Planet.Protocol;

namespace Planet.Server;

// Short, in-process admission transactions. No disk/network I/O while holding sync.
// Reservations live only for their request scope; this is not a distributed lease.
public sealed class ChannelDirectory(int capacity)
{
    private readonly object sync=new();
    private readonly int limit=capacity>0?capacity:throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly Dictionary<string,int> members=new();
    private readonly int[] pending=new int[Regions.ChannelCount+1];
    private int Occupied(int id)=>members.Values.Count(value=>value==id)+pending[id];
    public Reservation? Reserve(string account,int requested)
    {
        lock(sync) {
            if(requested<0||requested>Regions.ChannelCount)return null;
            int target=requested==0?Enumerable.Range(1,Regions.ChannelCount).OrderBy(Occupied).ThenBy(x=>x).First():requested;
            bool same=members.TryGetValue(account,out var current)&&current==target;
            if(!same&&Occupied(target)>=limit)return null;
            if(!same)pending[target]++;
            return new Reservation(this,account,target,!same);
        }
    }
    public void Remove(string account){lock(sync)members.Remove(account);}
    public ChannelView[] Snapshot()
    {
        lock(sync)return Enumerable.Range(1,Regions.ChannelCount).Select(id=>new ChannelView {
            Id=id,Capacity=limit,Online=members.Values.Count(x=>x==id),Reserved=pending[id]
        }).ToArray();
    }
    public sealed class Reservation:IDisposable
    {
        private readonly ChannelDirectory owner;
        private readonly string account;
        private readonly bool counted;
        private bool finished;
        public int Channel {get;}
        internal Reservation(ChannelDirectory owner,string account,int channel,bool counted)
        {this.owner=owner;this.account=account;Channel=channel;this.counted=counted;}
        public void Commit()
        {
            lock(owner.sync) {
                if(finished)throw new InvalidOperationException("Reservation already completed");
                owner.members[account]=Channel;
                if(counted)owner.pending[Channel]--;
                finished=true;
            }
        }
        public void Dispose()
        {
            lock(owner.sync) {if(finished)return;if(counted)owner.pending[Channel]--;finished=true;}
        }
    }
}
