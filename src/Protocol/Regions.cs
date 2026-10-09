using System;
using System.Collections.Generic;

namespace Planet.Protocol
{
    public sealed class PortalDefinition
    {
        public string Id {get;} public string Target {get;}
        public float X {get;} public float Z {get;} public float SpawnX {get;} public float SpawnZ {get;}
        public PortalDefinition(string id,string target,float x,float z,float spawnX,float spawnZ)
        {Id=id;Target=target;X=x;Z=z;SpawnX=spawnX;SpawnZ=spawnZ;}
    }
    public sealed class RegionDefinition
    {
        public string Id {get;} public string Name {get;}
        public IReadOnlyList<PortalDefinition> Portals {get;}
        public RegionDefinition(string id,string name,params PortalDefinition[] portals){Id=id;Name=name;Portals=Array.AsReadOnly(portals);}
    }
    public static class Regions
    {
        public const float HalfWidth=18, HalfDepth=22;
        public const int ChannelCount=30;
        public static readonly float[] GroveX={-7,9,-8,11};
        public static readonly float[] GroveZ={7,7,-9,-11};
        private static readonly Dictionary<string,RegionDefinition> all=new Dictionary<string,RegionDefinition> {
            {"meadow",new RegionDefinition("meadow","초원 마을",
                new PortalDefinition("east","coast",17,0,-15.5f,0),new PortalDefinition("west","forest",-17,0,15.5f,0),
                new PortalDefinition("north","hill",0,21,0,-19.5f),new PortalDefinition("south","garden",0,-21,0,19.5f))},
            {"coast",new RegionDefinition("coast","동쪽 해안",new PortalDefinition("west","meadow",-17,0,15.5f,0))},
            {"forest",new RegionDefinition("forest","서쪽 숲",new PortalDefinition("east","meadow",17,0,-15.5f,0))},
            {"hill",new RegionDefinition("hill","북쪽 언덕",new PortalDefinition("south","meadow",0,-21,0,19.5f))},
            {"garden",new RegionDefinition("garden","남쪽 정원",new PortalDefinition("north","meadow",0,21,0,-19.5f))}
        };
        public static RegionDefinition Find(string id)=>id!=null&&all.TryGetValue(id,out var region)?region:null;
        public static string BlockReason(string id,float x,float z)
        {
            if(Find(id)==null)return "unknown_region";
            if(float.IsNaN(x)||float.IsInfinity(x)||float.IsNaN(z)||float.IsInfinity(z))return "invalid";
            if(Math.Abs(x)>HalfWidth||Math.Abs(z)>HalfDepth)return "boundary";
            for(int i=0;i<MeadowMap.RockX.Length;i++) {
                float dx=x-MeadowMap.RockX[i],dz=z-MeadowMap.RockZ[i],r=MeadowMap.RockRadius+MeadowMap.PlayerRadius;
                if(dx*dx+dz*dz<r*r)return "blocked";
            }
            for(int i=0;i<GroveX.Length;i++){float dx=x-GroveX[i],dz=z-GroveZ[i];if(dx*dx+dz*dz<7.3f)return "blocked";}
            return "ok";
        }
        public static PortalDefinition Nearby(string id,float x,float z)
        {
            var region=Find(id);if(region==null)return null;
            foreach(var portal in region.Portals){float dx=x-portal.X,dz=z-portal.Z;if(dx*dx+dz*dz<=2.25f)return portal;}
            return null;
        }
    }
}
