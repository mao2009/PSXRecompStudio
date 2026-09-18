using PSXRecomp.Core.Runtime.Gpu;

namespace PSXRecomp.Tests;

[Test]
public class GpuVramTests
{
    [Fact]
    public void Dimensions_MatchPs1Vram()
    {
        GpuVram.Width.Should().Be(1024);
        GpuVram.Height.Should().Be(512);
        GpuVram.HalfwordCount.Should().Be(1024 * 512);
    }

    [Fact]
    public void NewVram_IsDeterministicallyZeroed()
    {
        using var vram = new GpuVram();
        vram[0, 0].Should().Be(0);
        vram[1023, 0].Should().Be(0);
        vram[0, 511].Should().Be(0);
        vram[1023, 511].Should().Be(0);
    }

    [Fact]
    public void Indexer_ReadWriteRoundTrips()
    {
        using var vram = new GpuVram();
        vram[123, 45] = 0xABCD;
        vram[123, 45].Should().Be(0xABCD);
        vram[122, 45].Should().Be(0);
        vram[123, 44].Should().Be(0);
    }

    [Fact]
    public void Indexer_XOutOfRange_Throws()
    {
        using var vram = new GpuVram();
        var act = () => { _ = vram[1024, 0]; };
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Indexer_YOutOfRange_Throws()
    {
        using var vram = new GpuVram();
        var act = () => { vram[0, 512] = 1; };
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Clear_ZeroesEveryHalfword()
    {
        using var vram = new GpuVram();
        vram[10, 10] = 0xFFFF;
        vram.Clear();
        vram[10, 10].Should().Be(0);
    }

    [Fact]
    public void Pointer_IsStableAndNonZero()
    {
        using var vram = new GpuVram();
        var ptr = vram.Pointer;
        ptr.Should().NotBe(IntPtr.Zero);
        vram.Pointer.Should().Be(ptr);
    }
}