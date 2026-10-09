using System;
namespace Planet.Protocol
{
    // Shared level dimensions. Gameplay uses X/Z; height is presentation derived from them.
    public static class RegionTerrain
    {
        public const float StairHalfWidth=2.3f,StairStart=4,StairEnd=8,PlateauHeight=1.6f;
        public static float Height(string region,float x,float z)
        {
            if(region!="meadow")return 0;
            if(Math.Abs(x)<=StairHalfWidth) {
                if(z<StairStart)return 0;
                if(z>=StairEnd)return PlateauHeight;
                return (float)(Math.Floor((z-StairStart)/.5)+1)*.2f;
            }
            float t=Math.Max(0,Math.Min(1,(z-6.1f)/.6f));
            float shoulder=Math.Max(0,Math.Min(1,(Math.Abs(x)-3)/4))*Math.Max(0,Math.Min(1,(Math.Abs(z)-2.5f)/3));
            float rolling=(float)(.16*Math.Sin(x*.6)*Math.Sin(z*.45)+.10*Math.Sin(x*.29+z*.7));
            return t*t*(3-2*t)*PlateauHeight+rolling*shoulder;
        }
        public static bool BlocksCliff(string region,float x,float z)
            =>region=="meadow"&&Math.Abs(x)>StairHalfWidth-MeadowMap.PlayerRadius&&z>5.85f&&z<6.95f;
    }
}
