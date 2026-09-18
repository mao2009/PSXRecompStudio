using PSXRecomp.Core.Runtime.Gpu;

namespace PSXRecomp.Tests;

[Test]
public class GpuTransferTests
{
    private static readonly uint CpuToVram = 0xA0000000;
    private static readonly uint VramToCpu = 0xC0000000;

    [Fact]
    public void CpuToVram_WritesHalfwordsInOrder()
    {
        using var gpu = new GpuDevice();

        gpu.WriteGP0(CpuToVram);
        gpu.WriteGP0(0x00000002); // dest x=2, y=0
        gpu.WriteGP0(0x00010004); // 4x1
        gpu.IsBusy.Should().BeTrue();

        gpu.WriteGP0(0xBBBBAAAA);
        gpu.WriteGP0(0xDDDDCCCC);

        gpu.IsBusy.Should().BeFalse();
        gpu.Vram[2, 0].Should().Be(0xAAAA);
        gpu.Vram[3, 0].Should().Be(0xBBBB);
        gpu.Vram[4, 0].Should().Be(0xCCCC);
        gpu.Vram[5, 0].Should().Be(0xDDDD);
        gpu.Vram[6, 0].Should().Be(0);
    }

    [Fact]
    public void CpuToVram_MultiRow_WrapsAtRowWidth()
    {
        using var gpu = new GpuDevice();

        gpu.WriteGP0(CpuToVram);
        gpu.WriteGP0(0x00000000); // x=0, y=0
        gpu.WriteGP0(0x00020002); // 2x2
        gpu.WriteGP0(0xBBBBAAAA);
        gpu.WriteGP0(0xDDDDCCCC);

        gpu.Vram[0, 0].Should().Be(0xAAAA);
        gpu.Vram[1, 0].Should().Be(0xBBBB);
        gpu.Vram[0, 1].Should().Be(0xCCCC);
        gpu.Vram[1, 1].Should().Be(0xDDDD);
    }

    [Fact]
    public void CpuToVram_OddHalfwordCount_IgnoresTrailingHighHalf()
    {
        using var gpu = new GpuDevice();

        gpu.WriteGP0(CpuToVram);
        gpu.WriteGP0(0x00000000);
        gpu.WriteGP0(0x00010003); // 3x1 => 3 halfwords => 2 words, last high half unused
        gpu.WriteGP0(0xBBBBAAAA);
        gpu.WriteGP0(0xFFFFCCCC);

        gpu.Vram[0, 0].Should().Be(0xAAAA);
        gpu.Vram[1, 0].Should().Be(0xBBBB);
        gpu.Vram[2, 0].Should().Be(0xCCCC);
        gpu.Vram[3, 0].Should().Be(0);
        gpu.IsBusy.Should().BeFalse();
    }

    [Fact]
    public void CpuToVram_CoordinatesAndSizeAreClamped()
    {
        using var gpu = new GpuDevice();

        gpu.WriteGP0(CpuToVram);
        gpu.WriteGP0(0x00000000);
        gpu.WriteGP0(0x00010000); // width param 0 => max 1024, height param 0 => max 512
        // No data needed: verify geometry by writing the first word and checking wraparound.
        gpu.WriteGP0(0x0000AAAA);
        gpu.Vram[0, 0].Should().Be(0xAAAA);
        gpu.Vram[1, 0].Should().Be(0);
        gpu.IsBusy.Should().BeTrue();
    }

    [Fact]
    public void VramToCpu_ReadsHalfwordsAndRepeatsLastWord()
    {
        using var gpu = new GpuDevice();
        gpu.Vram[0, 0] = 0x1111;
        gpu.Vram[1, 0] = 0x2222;
        gpu.Vram[2, 0] = 0x3333;
        gpu.Vram[3, 0] = 0x4444;

        gpu.WriteGP0(VramToCpu);
        gpu.WriteGP0(0x00000000);
        gpu.WriteGP0(0x00010004);
        gpu.IsBusy.Should().BeFalse();
        ((gpu.ReadGpustat() >> 27) & 1).Should().Be(1u);

        gpu.ReadGpuread().Should().Be(0x22221111);
        gpu.ReadGpuread().Should().Be(0x44443333);
        ((gpu.ReadGpustat() >> 27) & 1).Should().Be(0u);

        gpu.ReadGpuread().Should().Be(0x44443333); // last word repeated
    }

    [Fact]
    public void VramToCpu_OddHalfwordCount_PadsHighHalfWithZero()
    {
        using var gpu = new GpuDevice();
        gpu.Vram[0, 0] = 0xAAAA;
        gpu.Vram[1, 0] = 0xBBBB;
        gpu.Vram[2, 0] = 0xCCCC;

        gpu.WriteGP0(VramToCpu);
        gpu.WriteGP0(0x00000000);
        gpu.WriteGP0(0x00010003);

        gpu.ReadGpuread().Should().Be(0xBBBBAAAA);
        gpu.ReadGpuread().Should().Be(0x0000CCCC);
    }

    [Fact]
    public void RoundTrip_CpuToVramThenVramToCpu_PreservesData()
    {
        using var source = new GpuDevice();
        source.Vram[10, 0] = 0x1234;
        source.Vram[11, 0] = 0x5678;
        source.Vram[12, 0] = 0x9ABC;
        source.Vram[13, 0] = 0xDEF0;

        source.WriteGP0(VramToCpu);
        source.WriteGP0(0x0000000A); // x=10, y=0
        source.WriteGP0(0x00010004);
        var word0 = source.ReadGpuread();
        var word1 = source.ReadGpuread();

        using var destination = new GpuDevice();
        destination.WriteGP0(CpuToVram);
        destination.WriteGP0(0x00000200); // x=512, y=0
        destination.WriteGP0(0x00010004);
        destination.WriteGP0(word0);
        destination.WriteGP0(word1);

        destination.Vram[512, 0].Should().Be(0x1234);
        destination.Vram[513, 0].Should().Be(0x5678);
        destination.Vram[514, 0].Should().Be(0x9ABC);
        destination.Vram[515, 0].Should().Be(0xDEF0);
    }
}