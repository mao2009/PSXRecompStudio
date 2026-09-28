using PSXRecomp.Core.Runtime.Gte;

namespace PSXRecomp.Tests;

// Reference vectors are hand-derived from the documented formulas
// MAC0 = ZSF3*(SZ1+SZ2+SZ3) / ZSF4*(SZ0+SZ1+SZ2+SZ3), OTZ = sat16u(MAC0 >> 12).
[Test]
public class GteAvszKernelTests
{
    private const uint SatFlags = GteAvszKernel.OtzSaturated | GteAvszKernel.Error;

    [Fact]
    public void Avsz3_TypicalValues()
    {
        // 0x555 * 600 = 819000; 819000 >> 12 = 199
        GteAvszKernel.Avsz3(100, 200, 300, 0x555).Should().Be(new GteAvszResult(819000, 199, 0));
    }

    [Fact]
    public void Avsz4_TypicalValues()
    {
        // 0x400 * 10000 = 10240000; >> 12 = 2500
        GteAvszKernel.Avsz4(1000, 2000, 3000, 4000, 0x400).Should().Be(new GteAvszResult(10240000, 2500, 0));
    }

    [Fact]
    public void SingleNonZeroSz_WithUnityScale_PassesThroughToOtz()
    {
        GteAvszKernel.Avsz3(0, 0, 0x1000, 0x1000).Otz.Should().Be(0x1000);
        GteAvszKernel.Avsz4(0x1000, 0, 0, 0, 0x1000).Otz.Should().Be(0x1000);
    }

    [Fact]
    public void ZeroInputs_ProduceZeroWithoutFlags()
    {
        GteAvszKernel.Avsz3(0, 0, 0, 0x7FFF).Should().Be(new GteAvszResult(0, 0, 0));
        GteAvszKernel.Avsz4(0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF, 0).Should().Be(new GteAvszResult(0, 0, 0));
    }

    [Fact]
    public void NegativeMac0_SaturatesOtzToZero()
    {
        // -1 * 3 = -3; -3 >> 12 = -1 -> clamped to 0
        GteAvszKernel.Avsz3(1, 1, 1, -1).Should().Be(new GteAvszResult(-3, 0, SatFlags));
        GteAvszKernel.Avsz4(1, 1, 1, 1, -1).Should().Be(new GteAvszResult(-4, 0, SatFlags));
    }

    [Fact]
    public void OtzUpperBoundary_ExactMaxIsNotSaturated()
    {
        // 0x1000 * 0xFFFF = 0x0FFFF000 -> OTZ 0xFFFF exactly
        GteAvszKernel.Avsz4(0x3FFF, 0x4000, 0x4000, 0x4000, 0x1000)
            .Should().Be(new GteAvszResult(0x0FFFF000, 0xFFFF, 0));
        GteAvszKernel.Avsz3(0x5555, 0x5555, 0x5555, 0x1000)
            .Should().Be(new GteAvszResult(0x0FFFF000, 0xFFFF, 0));
    }

    [Fact]
    public void OtzUpperBoundary_OneStepOverSaturates()
    {
        // 0x1000 * 0x10000 = 0x10000000 -> OTZ 0x10000 -> clamped to 0xFFFF
        GteAvszKernel.Avsz4(0x4000, 0x4000, 0x4000, 0x4000, 0x1000)
            .Should().Be(new GteAvszResult(0x10000000, 0xFFFF, SatFlags));
        GteAvszKernel.Avsz3(0x5555, 0x5555, 0x5556, 0x1000)
            .Should().Be(new GteAvszResult(0x10000000, 0xFFFF, SatFlags));
    }

    [Fact]
    public void Mac0PositiveBoundary()
    {
        // 0x4000 * 0x1FFFF = 0x7FFFC000 fits in int32: only OTZ saturates
        GteAvszKernel.Avsz4(0x7FFF, 0x8000, 0x8000, 0x8000, 0x4000)
            .Should().Be(new GteAvszResult(0x7FFFC000, 0xFFFF, SatFlags));

        // 0x4000 * 0x20000 = 2^31: positive overflow, MAC0 wraps to int.MinValue
        GteAvszKernel.Avsz4(0x8000, 0x8000, 0x8000, 0x8000, 0x4000)
            .Should().Be(new GteAvszResult(int.MinValue, 0xFFFF, SatFlags | GteAvszKernel.Mac0PositiveOverflow));
    }

    [Fact]
    public void Mac0PositiveOverflow_MaximumInputs()
    {
        // 0x7FFF * 0x3FFFC = 0x1_FFFA_0004 -> MAC0 low 32 bits 0xFFFA0004
        GteAvszKernel.Avsz4(0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF, 0x7FFF)
            .Should().Be(new GteAvszResult(unchecked((int)0xFFFA0004), 0xFFFF, SatFlags | GteAvszKernel.Mac0PositiveOverflow));
    }

    [Fact]
    public void Mac0NegativeBoundary()
    {
        // -0x8000 * 0x10000 = -2^31 fits in int32: only OTZ saturates
        GteAvszKernel.Avsz3(0x5555, 0x5555, 0x5556, short.MinValue)
            .Should().Be(new GteAvszResult(int.MinValue, 0, SatFlags));

        // -0x8000 * 0x10001 = -0x80008000: negative overflow, MAC0 wraps to 0x7FFF8000
        GteAvszKernel.Avsz3(0x5555, 0x5556, 0x5556, short.MinValue)
            .Should().Be(new GteAvszResult(0x7FFF8000, 0, SatFlags | GteAvszKernel.Mac0NegativeOverflow));
    }

    [Fact]
    public void FlagBitPositions()
    {
        GteAvszKernel.Mac0NegativeOverflow.Should().Be(0x0000_8000u);
        GteAvszKernel.Mac0PositiveOverflow.Should().Be(0x0001_0000u);
        GteAvszKernel.OtzSaturated.Should().Be(0x0004_0000u);
        GteAvszKernel.Error.Should().Be(0x8000_0000u);
    }
}
