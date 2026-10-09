using System;

namespace Planet.Protocol
{
    // Logical map units. Presentation projects this bounded map onto a spherical patch.
    public static class MeadowMap
    {
        public const float PlanetRadius = 12f;
        public const float WalkRadius = 7f;
        public const float PlayerRadius = .22f;
        public static readonly float[] RockX = { -2f, 4f };
        public static readonly float[] RockZ = { 2f, -2f };
        public const float RockRadius = .7f;
        public static string BlockReason(float x, float z)
        {
            if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(z) || float.IsInfinity(z)) return "invalid";
            if (x*x+z*z > WalkRadius*WalkRadius) return "boundary";
            for (int i=0;i<RockX.Length;i++) {
                float dx=x-RockX[i], dz=z-RockZ[i];
                if(dx*dx+dz*dz < (RockRadius+PlayerRadius)*(RockRadius+PlayerRadius)) return "blocked";
            }
            return "ok";
        }
    }
}
