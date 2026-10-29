using PSXRecomp.Core;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Runtime.Gpu;

namespace PSXRecomp.Tests;

/// <summary>
/// Issue #440 audit: boundary conditions of the GPU DMA2 / OTC DMA6 word counts, taken from
/// <see cref="GpuDmaTransfer"/> (a zero BCR count means 0x10000 words in manual mode, and a one-entry OTC
/// writes only its end marker).
/// </summary>
[Test]
public sealed class GpuDmaBoundaryTests : IDisposable
{
    private const uint StartBusy = 1u << 24;
    private const uint StartTrigger = 1u << 28;

    private readonly PSXCoreWrapper _core = new();
    private readonly DmaMmioAdapter _dma;
    private readonly MemoryBus _memory;
    private readonly GpuDevice _gpu = new();
    private readonly GpuDmaTransfer _transfer;

    public GpuDmaBoundaryTests()
    {
        _dma = new DmaMmioAdapter(_core);
        _memory = new MemoryBus(_core);
        _memory.AttachDmaAdapter(_dma);
        _transfer = new GpuDmaTransfer(_gpu, _dma, _memory);
        _dma.WriteRegister(Ps1MemoryMap.Dpcr, 0x07654321u | (1u << (3 + 4 * 2)) | (1u << (3 + 4 * 6)));
    }

    public void Dispose()
    {
        _gpu.Dispose();
        _memory.Dispose();
        _dma.Dispose();
        _core.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Manual-mode DMA2 with BCR word count 0 moves 0x10000 words, not zero. The image header GP0(A0h) asks for
    /// 1024x512 halfwords (262144 words), so the transfer stays open after that burst.
    /// </summary>
    [Fact]
    public void DmaManualMode_ZeroWordCount_MovesSixtyFiveThousandWords()
    {
        _gpu.WriteGP0(0xA0000000);
        _gpu.WriteGP0(0x00000000); // x=0, y=0
        _gpu.WriteGP0(0x00000000); // width 0 -> 1024, height 0 -> 512
        _gpu.IsBusy.Should().BeTrue();

        _dma.WriteRegister(Ps1MemoryMap.GetChannelMadr(2), 0x1000);
        _dma.WriteRegister(Ps1MemoryMap.GetChannelBcr(2), 0);
        _dma.WriteRegister(Ps1MemoryMap.GetChannelChcr(2), StartBusy | StartTrigger | 1u);

        _transfer.TryTransfer().Should().Be(0x10000u);
        _gpu.IsBusy.Should().BeTrue("only 65536 of 262144 image words were delivered");
        (_dma.ReadRegister(Ps1MemoryMap.GetChannelChcr(2)) & StartBusy).Should().Be(0u);
    }

    /// <summary>A one-entry OTC (BCR = 1) writes only the end marker at MADR and leaves the word below it untouched.</summary>
    [Fact]
    public void Otc_SingleEntry_WritesOnlyEndMarker()
    {
        _memory.Write32(0x1FFC, 0x12345678u);
        _dma.WriteRegister(Ps1MemoryMap.GetChannelMadr(6), 0x2000);
        _dma.WriteRegister(Ps1MemoryMap.GetChannelBcr(6), 1);
        _dma.WriteRegister(Ps1MemoryMap.GetChannelChcr(6), StartBusy | StartTrigger | 2u);

        _transfer.TryTransfer().Should().Be(1u);
        _memory.Read32(0x2000).Should().Be(0x00FFFFFFu, "the only entry is the end marker");
        _memory.Read32(0x1FFC).Should().Be(0x12345678u, "OTC does not write past its own word count");
        (_dma.ReadRegister(Ps1MemoryMap.GetChannelChcr(6)) & StartBusy).Should().Be(0u);
    }
}
