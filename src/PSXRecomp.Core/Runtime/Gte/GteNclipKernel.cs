using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Gte;

/// <summary>Result of one NCLIP evaluation: the MAC0 value and the FLAG bits NCLIP can set.</summary>
[Domain]
public readonly record struct GteNclipResult(int Mac0, uint Flag);

/// <summary>
/// Pure NCLIP (normal clipping) arithmetic, independent of CPU dispatch and the GTE register bank.
/// MAC0 = SX0*SY1 + SX1*SY2 + SX2*SY0 - SX0*SY2 - SX1*SY0 - SX2*SY1, i.e. twice the signed
/// screen-space area of triangle SXY0/SXY1/SXY2. With PS1 screen Y pointing down, a positive
/// result is a clockwise-on-screen winding, negative is counter-clockwise, and zero is a
/// degenerate (collinear / zero-area) triangle — a valid result, not an error.
/// MAC0 keeps the low 32 bits of the exact sum; FLAG reports MAC0 overflow of the signed 32-bit range.
/// </summary>
[Domain]
public static class GteNclipKernel
{
    /// <summary>FLAG bit 16: MAC0 result larger than 31 bits and positive.</summary>
    public const uint FlagMac0PositiveOverflow = 1u << 16;

    /// <summary>FLAG bit 15: MAC0 result larger than 31 bits and negative.</summary>
    public const uint FlagMac0NegativeOverflow = 1u << 15;

    /// <summary>FLAG bit 31: error summary (OR of bits 30..23 and 18..13), so set with either MAC0 overflow bit.</summary>
    public const uint FlagError = 1u << 31;

    public static GteNclipResult Execute(short sx0, short sy0, short sx1, short sy1, short sx2, short sy2)
    {
        // ponytail: overflow is checked on the final exact sum only, not per partial accumulation step.
        long _sum = (long)sx0 * sy1 + (long)sx1 * sy2 + (long)sx2 * sy0
                 - (long)sx0 * sy2 - (long)sx1 * sy0 - (long)sx2 * sy1;

        uint _flag = _sum > int.MaxValue ? FlagMac0PositiveOverflow | FlagError
                  : _sum < int.MinValue ? FlagMac0NegativeOverflow | FlagError
                  : 0u;

        return new GteNclipResult(unchecked((int)_sum), _flag);
    }
}
