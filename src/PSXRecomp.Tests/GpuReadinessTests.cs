using PSXRecomp.Core.Runtime.Gpu;

namespace PSXRecomp.Tests;

/// <summary>
/// GPUSTAT readiness bits (Issue #467), asserted per bit against psx-spx:
///   bit 25 = DMA data request (DRQ), meaning depends on GP1(04h) DMA direction
///   bit 26 = ready to receive a command word (GP0)
///   bit 27 = read FIFO data available (VRAM-to-CPU, via GPUREAD)
///   bit 28 = write FIFO empty / ready to receive a DMA block
/// Bit 28 stays set during a CPU→VRAM data phase (so a feeding DMA in modes
/// 1/2 can keep requesting); bit 26 clears because the GPU wants transfer data,
/// not a command word.
/// </summary>
[Test]
public class GpuReadinessTests
{
    private const uint CpuToVram = 0xA0000000;
    private const uint VramToCpu = 0xC0000000;

    private static void AssertReady(
        GpuDevice gpu,
        bool dreq,
        bool readyCmd,
        bool readData,
        bool dmaBlock)
    {
        uint s = gpu.ReadGpustat();
        (((s >> 25) & 1) != 0).Should().Be(dreq, "bit 25 = DMA data request");
        (((s >> 26) & 1) != 0).Should().Be(readyCmd, "bit 26 = ready to receive command word");
        (((s >> 27) & 1) != 0).Should().Be(readData, "bit 27 = read FIFO data available");
        (((s >> 28) & 1) != 0).Should().Be(dmaBlock, "bit 28 = ready to receive DMA block");
    }

    [Fact]
    public void Reset_AndIdle_ReadyToReceiveButNoDataPending()
    {
        using var gpu = new GpuDevice();
        AssertReady(gpu, dreq: false, readyCmd: true, readData: false, dmaBlock: true);

        gpu.WriteGP0(0x02FF0000); // mid-packet: opcode only, params pending
        gpu.IsBusy.Should().BeTrue();
        gpu.WriteGP1(0x00000000); // GP1(00h) reset
        gpu.IsBusy.Should().BeFalse();
        AssertReady(gpu, dreq: false, readyCmd: true, readData: false, dmaBlock: true);
    }

    [Fact]
    public void Idle_AfterSingleWordCommand_AcceptsNextCommandWord()
    {
        using var gpu = new GpuDevice();
        gpu.WriteGP0(0xE1000000 | 0x1234); // GP0(E1h) completes immediately
        gpu.IsBusy.Should().BeFalse();
        AssertReady(gpu, dreq: false, readyCmd: true, readData: false, dmaBlock: true);
    }

    [Fact]
    public void MidMultiWordPacket_NotReadyForCommandOrDmaBlock()
    {
        using var gpu = new GpuDevice();
        gpu.WriteGP0(0x02FF0000); // fill: command word received, params pending
        AssertReady(gpu, dreq: false, readyCmd: false, readData: false, dmaBlock: false);
    }

    [Fact]
    public void CpuToVram_DataPhase_NotReadyForCommandButReadyForDmaBlock()
    {
        using var gpu = new GpuDevice();

        gpu.WriteGP0(CpuToVram);
        gpu.WriteGP0(0x00000000);
        gpu.WriteGP0(0x00010004); // 4x1 => 4 halfwords => 2 data words
        gpu.IsBusy.Should().BeTrue();
        AssertReady(gpu, dreq: false, readyCmd: false, readData: false, dmaBlock: true);

        gpu.WriteGP0(0xBBBBAAAA); // first data word, still streaming
        gpu.IsBusy.Should().BeTrue();
        AssertReady(gpu, dreq: false, readyCmd: false, readData: false, dmaBlock: true);

        gpu.WriteGP0(0xDDDDCCCC); // final data word
        gpu.IsBusy.Should().BeFalse();
        AssertReady(gpu, dreq: false, readyCmd: true, readData: false, dmaBlock: true);
    }

    [Fact]
    public void VramToCpu_DataAvailable_ThenClearsAfterDrained()
    {
        using var gpu = new GpuDevice();
        gpu.Vram[0, 0] = 0x1111;
        gpu.Vram[1, 0] = 0x2222;
        gpu.Vram[2, 0] = 0x3333;
        gpu.Vram[3, 0] = 0x4444;

        gpu.WriteGP0(VramToCpu);
        gpu.WriteGP0(0x00000000);
        gpu.WriteGP0(0x00010004); // 4x1 => 2 words queued
        AssertReady(gpu, dreq: false, readyCmd: true, readData: true, dmaBlock: true);

        gpu.ReadGpuread().Should().Be(0x22221111);
        AssertReady(gpu, dreq: false, readyCmd: true, readData: true, dmaBlock: true);
        gpu.ReadGpuread().Should().Be(0x44443333);
        AssertReady(gpu, dreq: false, readyCmd: true, readData: false, dmaBlock: true);
    }

    [Fact]
    public void DmaDirection_Off_DreqAlwaysZeroWithNoDataReady()
    {
        using var gpu = new GpuDevice();
        gpu.WriteGP1(0x04000000); // Off
        AssertReady(gpu, dreq: false, readyCmd: true, readData: false, dmaBlock: true);
    }

    [Fact]
    public void DmaDirection_OneToGpu_DreqFollowsWriteReady()
    {
        using var gpu = new GpuDevice();
        gpu.WriteGP1(0x04000001); // FIFO mode: DRQ when write FIFO not more than half full
        ((gpu.ReadGpustat() >> 29) & 3).Should().Be(1u);
        AssertReady(gpu, dreq: true, readyCmd: true, readData: false, dmaBlock: true);
    }

    [Fact]
    public void DmaDirection_TwoToGp0_DreqFollowsDmaBlockReady()
    {
        using var gpu = new GpuDevice();
        gpu.WriteGP1(0x04000002); // CPUtoGP0: DRQ = GPUSTAT.28
        ((gpu.ReadGpustat() >> 29) & 3).Should().Be(2u);
        AssertReady(gpu, dreq: true, readyCmd: true, readData: false, dmaBlock: true);
    }

    [Fact]
    public void DmaDirection_ThreeFromGpu_DreqFollowsReadDataAvailable()
    {
        using var gpu = new GpuDevice();
        gpu.WriteGP1(0x04000003); // GPUREADtoCPU: DRQ = GPUSTAT.27
        ((gpu.ReadGpustat() >> 29) & 3).Should().Be(3u);
        AssertReady(gpu, dreq: false, readyCmd: true, readData: false, dmaBlock: true);

        gpu.Vram[0, 0] = 0x1111;
        gpu.WriteGP0(VramToCpu);
        gpu.WriteGP0(0x00000000);
        gpu.WriteGP0(0x00010002);
        AssertReady(gpu, dreq: true, readyCmd: true, readData: true, dmaBlock: true);
    }

    [Fact]
    public void CpuToVram_DataPhase_DreqStaysActiveInModesOneAndTwo()
    {
        using var gpu = new GpuDevice();

        gpu.WriteGP1(0x04000002); // CPUtoGP0: DRQ = GPUSTAT.28
        gpu.WriteGP0(CpuToVram);
        gpu.WriteGP0(0x00000000);
        gpu.WriteGP0(0x00010004); // 4x1 => 4 halfwords => 2 data words
        AssertReady(gpu, dreq: true, readyCmd: false, readData: false, dmaBlock: true);

        gpu.WriteGP1(0x04000001); // FIFO mode
        AssertReady(gpu, dreq: true, readyCmd: false, readData: false, dmaBlock: true);

        gpu.WriteGP0(0xBBBBAAAA);
        AssertReady(gpu, dreq: true, readyCmd: false, readData: false, dmaBlock: true);

        gpu.WriteGP0(0xDDDDCCCC);
        AssertReady(gpu, dreq: true, readyCmd: true, readData: false, dmaBlock: true);
    }
}