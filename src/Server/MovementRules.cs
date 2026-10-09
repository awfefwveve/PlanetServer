using Planet.Protocol;

namespace Planet.Server;

// Independent movement policy: validation never trusts a client-supplied destination or elapsed time.
public static class MovementRules
{
    public const double InputInterval = .08;
    public const float Step = .24f;
    public static (string Code, float X, float Z) Evaluate(float x, float z, float dx, float dz, double elapsed)
    {
        if (!float.IsFinite(dx) || !float.IsFinite(dz)) return ("invalid", x, z);
        if (elapsed < InputInterval) return ("rate_limited", x, z);
        if ((double)dx*dx+(double)dz*dz > 1.0001) return ("invalid", x, z);
        float nextX=x+dx*Step, nextZ=z+dz*Step;
        string reason=MeadowMap.BlockReason(nextX,nextZ);
        return reason=="ok" ? (reason,nextX,nextZ) : (reason,x,z);
    }
}
