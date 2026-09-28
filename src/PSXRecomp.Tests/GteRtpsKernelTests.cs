using PSXRecomp.Core.Runtime.Gte;
using static PSXRecomp.Core.Runtime.Gte.GteRtpsKernel;

namespace PSXRecomp.Tests;

/// <summary>
/// RTPS kernel tests (Issue #582). Expected values are hand-derived from the
/// public psx-spx RTPS formulas, UNR division algorithm and FLAG table.
/// </summary>
[Test]
public class GteRtpsKernelTests
{
    private const short One = 0x1000; // 1.0 in 4.12

    private static GteRtpsInput Identity(
        short vx, short vy, short vz, int trx = 0, int trY = 0, int trz = 1000,
        ushort h = 1000, int ofx = 160 << 16, int ofy = 120 << 16, short dqa = 0x80, int dqb = 0)
        => new(One, 0, 0, 0, One, 0, 0, 0, One, trx, trY, trz, vx, vy, vz, h, ofx, ofy, dqa, dqb);

    [Fact]
    public void KnownVector_IdentityRotation_Sf1()
    {
        // MAC = TR + V; SZ3 = 1000; H/SZ3 = 1.0 -> n = 0x10000 (UNR exact here).
        var r = Execute(Identity(100, 50, 0), sf: true, lm: false);

        r.Should().Be(new GteRtpsResult(
            Mac0: 0x800000, Mac1: 100, Mac2: 50, Mac3: 1000,
            Ir0: 0x800, Ir1: 100, Ir2: 50, Ir3: 1000,
            Sx: 260, Sy: 170, Sz: 1000, Flag: 0));
    }

    [Fact]
    public void KnownVector_RotationAboutZ_WithTranslation()
    {
        // 90 degrees about Z: X' = -Y, Y' = X. MAC = (10-50, 20+100, 300+200).
        // n = ((250*20000h/500)+1)/2 = 0x8000 (0.5).
        var input = new GteRtpsInput(
            0, -One, 0, One, 0, 0, 0, 0, One,
            10, 20, 300, 100, 50, 200, 250, 160 << 16, 120 << 16, 0x80, 0);

        var r = Execute(input, sf: true, lm: false);

        r.Should().Be(new GteRtpsResult(
            Mac0: 0x400000, Mac1: -40, Mac2: 120, Mac3: 500,
            Ir0: 0x400, Ir1: -40, Ir2: 120, Ir3: 500,
            Sx: 140, Sy: 180, Sz: 500, Flag: 0));
    }

    [Fact]
    public void Division_UsesUnrApproximation_NotExactQuotient()
    {
        // H=6, SZ3=5: exact ((6*20000h/5)+1)/2 = 78643, hardware UNR yields 78644.
        // IR0 = (78644*1000h) >> 12 saturates; use DQA=1 so MAC0 = n exactly.
        var r = Execute(Identity(0, 0, 0, trz: 5, h: 6, dqa: 1), sf: true, lm: false);

        r.Mac0.Should().Be(78644);
        r.Sz.Should().Be(5);
        r.Flag.Should().Be(0);
    }

    [Fact]
    public void Sf0_KeepsUnshiftedMac_SaturatesIr_AndAppliesIr3FlagQuirk()
    {
        var r = Execute(Identity(100, 50, 0), sf: false, lm: false);

        r.Mac1.Should().Be(100 << 12);
        r.Mac2.Should().Be(50 << 12);
        r.Mac3.Should().Be(1000 << 12);
        r.Ir1.Should().Be(0x7FFF);
        r.Ir2.Should().Be(0x7FFF);
        r.Ir3.Should().Be(0x7FFF, "IR3 is saturated from MAC3");
        r.Sz.Should().Be(1000, "SZ3 is always MAC3 at 12-bit fraction");
        // SX MAC0 = 1.0*7FFFh + 160 (16.16) overflows 32 bits; SX/SY clamp to 3FFh.
        r.Sx.Should().Be(0x3FF);
        r.Sy.Should().Be(0x3FF);
        r.Ir0.Should().Be(0x800);
        r.Mac0.Should().Be(0x800000);
        // No FLAG.22: (MAC3 SAR 12) = 1000 is in range even though IR3 saturated.
        r.Flag.Should().Be(FlagError | FlagIr1Saturated | FlagIr2Saturated
            | FlagMac0Positive | FlagSx2Saturated | FlagSy2Saturated);
    }

    [Fact]
    public void Sf0_Ir3FlagSet_WhenMac3Sar12OutOfRange()
    {
        var r = Execute(Identity(0, 0, 0, trz: 0x8000, h: 0xFFFF), sf: false, lm: false);

        r.Ir3.Should().Be(0x7FFF);
        r.Sz.Should().Be(0x8000);
        (r.Flag & FlagIr3Saturated).Should().Be(FlagIr3Saturated);
        (r.Flag & FlagError).Should().Be(0, "FLAG.22 is not part of the error summary");
    }

    [Theory]
    [InlineData(true, (short)0, (short)0, (short)160, (short)120, FlagError | FlagIr1Saturated | FlagIr2Saturated)]
    [InlineData(false, (short)-100, (short)-50, (short)60, (short)70, 0u)]
    public void Lm_ControlsNegativeIrSaturation(bool lm, short ir1, short ir2, short sx, short sy, uint flag)
    {
        var r = Execute(Identity(-100, -50, 0), sf: true, lm: lm);

        r.Mac1.Should().Be(-100);
        r.Mac2.Should().Be(-50);
        r.Ir1.Should().Be(ir1);
        r.Ir2.Should().Be(ir2);
        r.Sx.Should().Be(sx);
        r.Sy.Should().Be(sy);
        r.Flag.Should().Be(flag);
    }

    [Theory]
    [InlineData(-1, 0u)] // MAC3 = -1: lm clamps IR3 to 0, but -1 is within -8000h..7FFFh.
    [InlineData(-0x8001, FlagIr3Saturated)] // MAC3 = -8001h: out of the fixed range.
    public void Sf1Lm1_Ir3Flag_UsesFixedRange_NotLmRange(int trz, uint ir3Flag)
    {
        var r = Execute(Identity(0, 0, 0, trz: trz), sf: true, lm: true);

        r.Mac3.Should().Be(trz);
        r.Ir3.Should().Be(0);
        (r.Flag & FlagIr3Saturated).Should().Be(ir3Flag);
    }

    [Fact]
    public void Ir_SaturatesAtBothEnds_WithLm0()
    {
        // MAC1 = -8000h - 1, MAC2 = 7FFFh + 1.
        var r = Execute(Identity(-0x8000, 0x7FFF, 0, trx: -1, trY: 1), sf: true, lm: false);

        r.Ir1.Should().Be(-0x8000);
        r.Ir2.Should().Be(0x7FFF);
        (r.Flag & (FlagIr1Saturated | FlagIr2Saturated)).Should().Be(FlagIr1Saturated | FlagIr2Saturated);
    }

    [Fact]
    public void ZeroVector_ZeroSz_IsDivideOverflow_NotACrash()
    {
        var r = Execute(Identity(0, 0, 0, trz: 0), sf: true, lm: false);

        r.Sz.Should().Be(0);
        r.Sx.Should().Be(160);
        r.Sy.Should().Be(120);
        // n = 1FFFFh; MAC0 = 1FFFFh * 80h; IR0 = MAC0 >> 12 = FFFh.
        r.Mac0.Should().Be(0x1FFFF * 0x80);
        r.Ir0.Should().Be(0xFFF);
        r.Flag.Should().Be(FlagError | FlagDivideOverflow);
    }

    [Fact]
    public void Divide_Overflows_WhenHIsAtLeastTwiceSz3()
    {
        var r = Execute(Identity(0, 0, 0, trz: 500, h: 1000), sf: true, lm: false);

        r.Flag.Should().Be(FlagError | FlagDivideOverflow);
    }

    [Theory]
    [InlineData(-1000, (ushort)0)]
    [InlineData(0x10000, (ushort)0xFFFF)]
    public void Sz3_SaturatesToUnsigned16(int trz, ushort sz)
    {
        var r = Execute(Identity(0, 0, 0, trz: trz), sf: true, lm: false);

        r.Sz.Should().Be(sz);
        (r.Flag & (FlagSzSaturated | FlagError)).Should().Be(FlagSzSaturated | FlagError);
    }

    [Fact]
    public void Mac1_Positive44BitOverflow_WrapsAndFlags()
    {
        // 7FFFFFFFh*1000h + 7FFFh*7FFFh = 8003FFEF001h > 2^43-1; wraps in 44 bits.
        var input = new GteRtpsInput(0x7FFF, 0, 0, 0, One, 0, 0, 0, One,
            int.MaxValue, 0, 1000, 0x7FFF, 0, 0, 1000, 0, 0, 0, 0);

        var r = Execute(input, sf: true, lm: false);

        r.Mac1.Should().Be(-2147221521);
        r.Ir1.Should().Be(-0x8000);
        r.Flag.Should().Be(FlagError | FlagMac1Positive | FlagIr1Saturated | FlagSx2Saturated);
    }

    [Fact]
    public void Mac3_Negative44BitOverflow_Flags()
    {
        // 80000000h*1000h + 7FFFh*(-8000h) < -2^43.
        var input = new GteRtpsInput(One, 0, 0, 0, One, 0, 0, 0, 0x7FFF,
            0, 0, int.MinValue, 0, 0, -0x8000, 1000, 0, 0, 0, 0);

        var r = Execute(input, sf: true, lm: false);

        (r.Flag & (FlagMac3Negative | FlagError)).Should().Be(FlagMac3Negative | FlagError);
    }

    [Fact]
    public void Mac0_NegativeOverflow_AndScreenNegativeSaturation()
    {
        // SX MAC0 = 10000h*(-1) + 80000000h underflows; SY = 50 - 2000 clamps to -400h.
        var r = Execute(Identity(-1, 50, 0, ofx: int.MinValue, ofy: -2000 << 16), sf: true, lm: false);

        r.Sx.Should().Be(-0x400);
        r.Sy.Should().Be(-0x400);
        r.Flag.Should().Be(FlagError | FlagMac0Negative | FlagSx2Saturated | FlagSy2Saturated);
    }

    [Fact]
    public void Ir0_SaturatesTo1000h_WithoutErrorBit()
    {
        // MAC0 = 10000h*100h + 100000h = 1100000h -> IR0 = 1100h -> 1000h.
        var r = Execute(Identity(0, 0, 0, dqa: 0x100, dqb: 0x100000), sf: true, lm: false);

        r.Ir0.Should().Be(0x1000);
        r.Flag.Should().Be(FlagIr0Saturated);
    }
}
