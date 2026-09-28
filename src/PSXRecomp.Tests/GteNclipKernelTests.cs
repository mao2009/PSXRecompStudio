using PSXRecomp.Core.Runtime.Gte;

namespace PSXRecomp.Tests;

// Reference vectors are hand-computed from MAC0 = SX0*(SY1-SY2) + SX1*(SY2-SY0) + SX2*(SY0-SY1).
[Test]
public class GteNclipKernelTests
{
    private const uint PosOverflow = GteNclipKernel.FlagMac0PositiveOverflow | GteNclipKernel.FlagError;
    private const uint NegOverflow = GteNclipKernel.FlagMac0NegativeOverflow | GteNclipKernel.FlagError;

    [Theory]
    // Normal: right triangle, clockwise on a Y-down screen -> positive.
    [InlineData(0, 0, 10, 0, 0, 10, 100)]
    // Same triangle with SXY1/SXY2 swapped (opposite winding) -> negative.
    [InlineData(0, 0, 0, 10, 10, 0, -100)]
    // Mixed-sign coordinates: 3*(5-9) + (-12)*(9+7) + 20*(-7-5) = -12 - 192 - 240.
    [InlineData(3, -7, -12, 5, 20, 9, -444)]
    [InlineData(3, -7, 20, 9, -12, 5, 444)]
    // Degenerate: collinear points and a single repeated point have zero area.
    [InlineData(1, 1, 2, 2, 5, 5, 0)]
    [InlineData(-300, 40, -300, 40, -300, 40, 0)]
    [InlineData(-32768, -32768, 0, 0, 32767, 32767, 0)]
    public void Execute_ProducesReferenceMac0WithoutFlags(short sx0, short sy0, short sx1, short sy1, short sx2, short sy2, int expected)
    {
        var result = GteNclipKernel.Execute(sx0, sy0, sx1, sy1, sx2, sy2);

        result.Mac0.Should().Be(expected);
        result.Flag.Should().Be(0u);
    }

    [Theory]
    // (x1-x0)(y2-y0) - (x2-x0)(y1-y0) = 65535*32769 - c*1 with c chosen to land on each boundary.
    [InlineData(-32768, -32768, 32767, -32767, 0, 1, int.MaxValue, 0u)]            // 2^31 - 1: fits
    [InlineData(-32768, -32768, 32767, -32767, -1, 1, int.MinValue, PosOverflow)]  // 2^31: positive overflow, wraps
    [InlineData(-32768, -32768, -1, 1, 32767, -32767, int.MinValue, 0u)]           // -2^31: fits
    [InlineData(-32768, -32768, -2, 1, 32767, -32767, int.MaxValue, NegOverflow)]  // -2^31 - 1: negative overflow, wraps
    // Extreme full-range triangle: 65535^2 = 4294836225 -> low 32 bits = -131071.
    [InlineData(-32768, -32768, 32767, -32768, -32768, 32767, -131071, PosOverflow)]
    [InlineData(-32768, -32768, -32768, 32767, 32767, -32768, 131071, NegOverflow)]
    public void Execute_Mac0OverflowBoundaries_SetFlagsAndKeepLow32Bits(short sx0, short sy0, short sx1, short sy1, short sx2, short sy2, int expectedMac0, uint expectedFlag)
    {
        var result = GteNclipKernel.Execute(sx0, sy0, sx1, sy1, sx2, sy2);

        result.Mac0.Should().Be(expectedMac0);
        result.Flag.Should().Be(expectedFlag);
    }

    [Fact]
    public void FlagBits_MatchDocumentedPositions()
    {
        GteNclipKernel.FlagMac0PositiveOverflow.Should().Be(0x0001_0000u);
        GteNclipKernel.FlagMac0NegativeOverflow.Should().Be(0x0000_8000u);
        GteNclipKernel.FlagError.Should().Be(0x8000_0000u);
    }
}
