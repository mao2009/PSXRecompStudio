using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Gte;

/// <summary>Outputs of one AVSZ3/AVSZ4 command: MAC0 (truncated to 32 bits), OTZ, and the FLAG value the command produces.</summary>
public readonly record struct GteAvszResult(int Mac0, ushort Otz, uint Flag);

/// <summary>
/// Pure AVSZ3/AVSZ4 arithmetic (Issue #584), independent of any GTE register bank.
/// <c>MAC0 = ZSF3*(SZ1+SZ2+SZ3)</c> or <c>MAC0 = ZSF4*(SZ0+SZ1+SZ2+SZ3)</c>;
/// <c>OTZ = MAC0 &gt;&gt; 12</c> saturated to 0..0xFFFF. The shift is unconditional (no sf bit).
/// MAC0 overflow is judged on the untruncated product and OTZ is derived from it too.
/// </summary>
[Domain]
public static class GteAvszKernel
{
    /// <summary>FLAG bit 15: MAC0 result smaller than -2^31.</summary>
    public const uint Mac0NegativeOverflow = 1u << 15;

    /// <summary>FLAG bit 16: MAC0 result larger than 2^31-1.</summary>
    public const uint Mac0PositiveOverflow = 1u << 16;

    /// <summary>FLAG bit 18: SZ3/OTZ saturated to 0..0xFFFF.</summary>
    public const uint OtzSaturated = 1u << 18;

    /// <summary>FLAG bit 31: error summary, set when any of bits 30..23 or 18..13 is set.</summary>
    public const uint Error = 1u << 31;

    public static GteAvszResult Avsz3(ushort sz1, ushort sz2, ushort sz3, short zsf3) =>
        Compute((long)zsf3 * (sz1 + sz2 + sz3));

    public static GteAvszResult Avsz4(ushort sz0, ushort sz1, ushort sz2, ushort sz3, short zsf4) =>
        Compute((long)zsf4 * (sz0 + sz1 + sz2 + sz3));

    private static GteAvszResult Compute(long mac0)
    {
        uint flag = 0;
        if (mac0 > int.MaxValue) flag |= Mac0PositiveOverflow;
        else if (mac0 < int.MinValue) flag |= Mac0NegativeOverflow;

        long otz = mac0 >> 12;
        if (otz < 0 || otz > ushort.MaxValue)
        {
            otz = Math.Clamp(otz, 0, ushort.MaxValue);
            flag |= OtzSaturated;
        }

        if (flag != 0) flag |= Error;
        return new GteAvszResult(unchecked((int)mac0),(ushort)otz, flag);
    }
}
