using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Gte;

/// <summary>
/// Explicit RTPS inputs: rotation matrix (RT, 4.12 fixed), translation vector
/// (TR), vertex V0, projection-plane distance H, screen offsets OFX/OFY (16.16),
/// and depth-cueing coefficients DQA/DQB. Deliberately independent of any GTE
/// register bank (Issue #582).
/// </summary>
[Domain]
public readonly record struct GteRtpsInput(
    short Rt11, short Rt12, short Rt13,
    short Rt21, short Rt22, short Rt23,
    short Rt31, short Rt32, short Rt33,
    int Trx, int Try, int Trz,
    short Vx, short Vy, short Vz,
    ushort H, int Ofx, int Ofy, short Dqa, int Dqb);

/// <summary>
/// RTPS outputs. <see cref="Sx"/>/<see cref="Sy"/>/<see cref="Sz"/> are the
/// values to push into the SXY/SZ FIFOs (the push itself is the caller's job).
/// <see cref="Mac0"/> is the final (depth-cue) MAC0 write.
/// </summary>
[Domain]
public readonly record struct GteRtpsResult(
    int Mac0, int Mac1, int Mac2, int Mac3,
    short Ir0, short Ir1, short Ir2, short Ir3,
    short Sx, short Sy, ushort Sz, uint Flag);

/// <summary>
/// Pure RTPS (perspective transformation, single) arithmetic following the
/// public PS1 GTE specification (psx-spx "GTE Coordinate Calculation Commands",
/// "GTE Division Inaccuracy" and FLAG register tables). Not wired to COP2.
/// </summary>
[Domain]
public static class GteRtpsKernel
{
    // FLAG bits (psx-spx GTE FLAG register).
    public const uint FlagError = 1u << 31;
    public const uint FlagMac1Positive = 1u << 30;
    public const uint FlagMac2Positive = 1u << 29;
    public const uint FlagMac3Positive = 1u << 28;
    public const uint FlagMac1Negative = 1u << 27;
    public const uint FlagMac2Negative = 1u << 26;
    public const uint FlagMac3Negative = 1u << 25;
    public const uint FlagIr1Saturated = 1u << 24;
    public const uint FlagIr2Saturated = 1u << 23;
    public const uint FlagIr3Saturated = 1u << 22;
    public const uint FlagSzSaturated = 1u << 18;
    public const uint FlagDivideOverflow = 1u << 17;
    public const uint FlagMac0Positive = 1u << 16;
    public const uint FlagMac0Negative = 1u << 15;
    public const uint FlagSx2Saturated = 1u << 14;
    public const uint FlagSy2Saturated = 1u << 13;
    public const uint FlagIr0Saturated = 1u << 12;

    /// <summary>Bit 31 = OR of bits 30..23 and 18..13 (IR3, color and IR0 excluded).</summary>
    public const uint ErrorMask = 0x7F87E000u;

    private const long Mac44Max = (1L << 43) - 1;
    private const long Mac44Min = -(1L << 43);

    public static GteRtpsResult Execute(in GteRtpsInput i, bool sf, bool lm)
    {
        uint flag = 0;
        int shift = sf ? 12 : 0;

        long x = Accumulate(i.Trx, i.Rt11, i.Rt12, i.Rt13, i, FlagMac1Positive, FlagMac1Negative, ref flag);
        long y = Accumulate(i.Try, i.Rt21, i.Rt22, i.Rt23, i, FlagMac2Positive, FlagMac2Negative, ref flag);
        long z = Accumulate(i.Trz, i.Rt31, i.Rt32, i.Rt33, i, FlagMac3Positive, FlagMac3Negative, ref flag);

        int mac1 = (int)(x >> shift);
        int mac2 = (int)(y >> shift);
        int mac3 = (int)(z >> shift);

        int irMin = lm ? 0 : -0x8000;
        short ir1 = (short)Saturate(mac1, irMin, 0x7FFF, FlagIr1Saturated, ref flag);
        short ir2 = (short)Saturate(mac2, irMin, 0x7FFF, FlagIr2Saturated, ref flag);

        // RTPS quirk: the IR3 flag always tracks (MAC3 SAR 12) against
        // -8000h..+7FFFh regardless of sf/lm, while the stored IR3 is
        // saturated from MAC3 using the lm-dependent range.
        short ir3 = (short)Math.Clamp(mac3, irMin, 0x7FFF);
        Saturate(z >> 12, -0x8000, 0x7FFF, FlagIr3Saturated, ref flag);

        ushort sz = (ushort)Saturate(z >> 12, 0, 0xFFFF, FlagSzSaturated, ref flag);

        long n = Divide(i.H, sz, ref flag);

        long sxMac = CheckMac0(n * ir1 + i.Ofx, ref flag);
        short sx = (short)Saturate(sxMac >> 16, -0x400, 0x3FF, FlagSx2Saturated, ref flag);
        long syMac = CheckMac0(n * ir2 + i.Ofy, ref flag);
        short sy = (short)Saturate(syMac >> 16, -0x400, 0x3FF, FlagSy2Saturated, ref flag);
        long dqMac = CheckMac0(n * i.Dqa + i.Dqb, ref flag);
        short ir0 = (short)Saturate(dqMac >> 12, 0, 0x1000, FlagIr0Saturated, ref flag);

        if ((flag & ErrorMask) != 0)
        {
            flag |= FlagError;
        }

        return new GteRtpsResult((int)dqMac, mac1, mac2, mac3, ir0, ir1, ir2, ir3, sx, sy, sz, flag);
    }

    /// <summary>
    /// Unsigned Newton-Raphson division of the hardware: approximately
    /// ((H*20000h/SZ3)+1)/2, saturated to 1FFFFh with FLAG.17 when H &gt;= SZ3*2
    /// (which includes the degenerate SZ3 = 0 case).
    /// </summary>
    private static long Divide(ushort h, ushort sz3, ref uint flag)
    {
        if (h >= sz3 * 2)
        {
            flag |= FlagDivideOverflow;
            return 0x1FFFF;
        }

        int z = 16 - (32 - System.Numerics.BitOperations.LeadingZeroCount((uint)sz3));
        long n = (long)h << z;
        long d = (long)sz3 << z;
        long u = UnrTable((int)((d - 0x7FC0) >> 7)) + 0x101;
        d = (0x2000080 - (d * u)) >> 8;
        d = (0x0000080 + (d * u)) >> 8;
        return Math.Min(0x1FFFF, ((n * d) + 0x8000) >> 16);
    }

    // psx-spx: unr_table[i] = max(0, (40000h/(i+100h)+1)/2 - 101h), i = 0..100h.
    private static int UnrTable(int index) => Math.Max(0, ((0x40000 / (index + 0x100)) + 1) / 2 - 0x101);

    private static long Accumulate(int tr, short m1, short m2, short m3, in GteRtpsInput i,
        uint positive, uint negative, ref uint flag)
    {
        long acc = (long)tr << 12;
        acc = Add44(acc, m1 * i.Vx, positive, negative, ref flag);
        acc = Add44(acc, m2 * i.Vy, positive, negative, ref flag);
        return Add44(acc, m3 * i.Vz, positive, negative, ref flag);
    }

    // Models the MAC1-3 accumulator as 44-bit wrap-around checked on every addition.
    private static long Add44(long acc, long term, uint positive, uint negative, ref uint flag)
    {
        long sum = acc + term;
        if (sum > Mac44Max)
        {
            flag |= positive;
        }
        else if (sum < Mac44Min)
        {
            flag |= negative;
        }

        return (sum << 20) >> 20;
    }

    private static long CheckMac0(long value, ref uint flag)
    {
        if (value > int.MaxValue)
        {
            flag |= FlagMac0Positive;
        }
        else if (value < int.MinValue)
        {
            flag |= FlagMac0Negative;
        }

        return value;
    }

    private static long Saturate(long value, long min, long max, uint bit, ref uint flag)
    {
        if (value < min)
        {
            flag |= bit;
            return min;
        }

        if (value > max)
        {
            flag |= bit;
            return max;
        }

        return value;
    }
}
