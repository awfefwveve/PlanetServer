using System;

namespace Planet.Protocol
{
    // Logical map units. Presentation projects this bounded map onto a spherical patch.
    public static class MeadowMap
    {
        public const float PlanetRadius = 60f;
        public const float WalkRadius = 7f;
        public const float PlayerRadius = .22f;
        public static readonly float[] RockX = { -2f, 4f };
        public static readonly float[] RockZ = { 2f, -2f };
        public const float RockRadius = .7f;
        public static string BlockReason(float x, float z)
        {
            return Regions.BlockReason("meadow",x,z);
        }
    }
}
