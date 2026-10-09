using PSXRecomp.Core;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Runtime.Gpu;

namespace PSXRecomp.Tests;

/// <summary>Issue #732: GPU DMA2 (block and linked list) and OTC DMA6 move their data, not only their duration.</summary>
[Test]
public sealed class GpuDmaTransferTests : IDisposable
{
    private const uint StartBusy = 1u << 24;
    private const uint StartTrigger = 1u << 28;
    private const uint Dma2InterruptFlag = 1u << (24 + GpuDmaTransfer.GpuChannel);

    private readonly PSXCoreWrapper _core = new();
    private readonly DmaMmioAdapter _dma;
    private readonly MemoryBus _memory;
    private readonly GpuDevice _gpu = new();
    private readonly GpuDmaTransfer _transfer;

    public GpuDmaTransferTests()
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

    /// <summary>LoadImage as libgpu does it (Persona's movie upload): GP0(A0h), then DMA2 block mode feeds the pixels.</summary>
    [Fact]
    public void BlockMode_FeedsAnImageUploadToGp0_ThenTheGpuIsReadyAgain()
    {
        _dma.WriteRegister(Ps1MemoryMap.Dicr, (1u << 23) | (1u << (16 + GpuDmaTransfer.GpuChannel)));
        _gpu.WriteGP0(0xA0000000);
        _gpu.WriteGP0(0x00000010);          // x=16, y=0
        _gpu.WriteGP0(0x00020004);          // 4x2 pixels = 4 words
        (_gpu.ReadGpustat() & (1u << 26)).Should().Be(0u, "the GPU waits for pixel data");
        for (uint i = 0; i < 4; i++)
        {
            _memory.Write32(0x1000 + 4 * i, 0x00020001u + 0x00020002u * i);
        }

        _dma.WriteRegister(Ps1MemoryMap.GetChannelMadr(2), 0x1000);
        _dma.WriteRegister(Ps1MemoryMap.GetChannelBcr(2), 0x00020002); // 2 blocks of 2 words
        _dma.WriteRegister(Ps1MemoryMap.GetChannelChcr(2), StartBusy | (1u << 9) | 1u);

        _transfer.TryTransfer().Should().Be(4u);

        _gpu.Vram[16, 0].Should().Be(0x0001);
        _gpu.Vram[17, 0].Should().Be(0x0002);
        _gpu.Vram[19, 1].Should().Be(0x0008);
        (_gpu.ReadGpustat() & (1u << 26)).Should().NotBe(0u, "the upload is complete");
        (_dma.ReadRegister(Ps1MemoryMap.GetChannelChcr(2)) & StartBusy).Should().Be(0u);
        _dma.ReadRegister(Ps1MemoryMap.GetChannelMadr(2)).Should().Be(0x1010u);
        (_dma.ReadRegister(Ps1MemoryMap.Dicr) & Dma2InterruptFlag).Should().Be(Dma2InterruptFlag);
    }

    /// <summary>ClearOTagR then DrawOTag: OTC builds the backward chain, DMA2 walks it and sends every packet's words.</summary>
    [Fact]
    public void OtcChain_ThenLinkedList_SendsEachPacketToGp0InChainOrder()
    {
        _dma.WriteRegister(Ps1MemoryMap.GetChannelMadr(6), 0x200C);
        _dma.WriteRegister(Ps1MemoryMap.GetChannelBcr(6), 4);
        _dma.WriteRegister(Ps1MemoryMap.GetChannelChcr(6), StartBusy | StartTrigger | 2u);
        _transfer.TryTransfer().Should().Be(4u);
        _memory.Read32(0x200C).Should().Be(0x2008u);
        _memory.Read32(0x2004).Should().Be(0x2000u);
        _memory.Read32(0x2000).Should().Be(0x00FFFFFFu, "the last entry is the end marker");
        (_dma.ReadRegister(Ps1MemoryMap.GetChannelChcr(6)) & StartBusy).Should().Be(0u);

        // Insert a one-word packet (GP0(E1h) texture page X base 5) between entries 0x2008 and 0x2004.
        _memory.Write32(0x3000, 0x01002004);
        _memory.Write32(0x3004, 0xE1000005);
        _memory.Write32(0x2008, 0x00003000);

        _dma.WriteRegister(Ps1MemoryMap.GetChannelMadr(2), 0x200C);
        _dma.WriteRegister(Ps1MemoryMap.GetChannelChcr(2), StartBusy | (2u << 9) | 1u);
        _transfer.TryTransfer().Should().Be(1u);

        (_gpu.ReadGpustat() & 0xFu).Should().Be(5u, "GPUSTAT bits 0-3 echo the texture page");
        _dma.ReadRegister(Ps1MemoryMap.GetChannelMadr(2)).Should().Be(0x00FFFFFFu);
        (_dma.ReadRegister(Ps1MemoryMap.GetChannelChcr(2)) & StartBusy).Should().Be(0u);
    }

    [Fact]
    public void UnstartedChannels_MoveNothing()
    {
        _dma.WriteRegister(Ps1MemoryMap.GetChannelChcr(2), (1u << 9) | 1u);
        _dma.WriteRegister(Ps1MemoryMap.GetChannelChcr(6), StartBusy | 2u); // manual mode without trigger
        _transfer.TryTransfer().Should().Be(0u);
    }
}
