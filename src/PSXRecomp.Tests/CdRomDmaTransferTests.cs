using PSXRecomp.Core;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Runtime.CdRom;

namespace PSXRecomp.Tests;

/// <summary>Issue #587: bounded CD-ROM data FIFO -> DMA3 -> guest RAM integration.</summary>
[Test]
public sealed class CdRomDmaTransferTests : IDisposable
{
    private const uint Dma3NormalChcr = 0x11000000u;
    private const uint StartBusy = 1u << 24;
    private const uint StartTrigger = 1u << 28;
    private const uint DmaMasterEnable = 1u << 23;
    private const uint Dma3InterruptEnable = 1u << (24 + CdRomDmaTransfer.Channel);
    private const uint Dma3InterruptFlag = 1u << CdRomDmaTransfer.Channel;
    private const uint DmaInterruptStatus = 1u << 31;
    private const uint Dma3DpcrEnable = 1u << (3 + 4 * CdRomDmaTransfer.Channel);

    private readonly PSXCoreWrapper _core = new();
    private readonly DmaMmioAdapter _dma;
    private readonly MemoryBus _memory;
    private readonly CdRomDevice _cdRom;
    private readonly CdRomDmaTransfer _transfer;

    public CdRomDmaTransferTests()
    {
        _dma = new DmaMmioAdapter(_core);
        _memory = new MemoryBus(_core);
        _memory.AttachDmaAdapter(_dma);
        _cdRom = new CdRomDevice(CdRomDiscIdentity.LicensedMode2());
        _transfer = new CdRomDmaTransfer(_cdRom, _dma, _memory);
    }

    public void Dispose()
    {
        _memory.Dispose();
        _dma.Dispose();
        _core.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TwoWordFixture_TransfersLittleEndianIntoRam_AndCompletesNativeDma3()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        PrepareDataReady(payload);

        // Keep an unrelated channel value as a regression sentinel.
        _dma.WriteRegister(Ps1MemoryMap.GetChannelMadr(2), 0x00123450u);

        const uint destination = 0x00000100u;
        ArmDma3(destination, words: 2, enableDmaInterrupt: true);

        var result = _transfer.TryTransfer();

        result.Should().Be(new CdRomDmaTransferResult(
            CdRomDmaTransferStatus.Completed, 2, destination));
        _memory.Read32(destination).Should().Be(0x04030201u);
        _memory.Read32(destination + 4).Should().Be(0x08070605u);
        _cdRom.DataBytesAvailable.Should().Be(0);

        var chcr = _dma.ReadRegister(Ps1MemoryMap.GetChannelChcr(CdRomDmaTransfer.Channel));
        (chcr & (StartBusy | StartTrigger)).Should().Be(0u, "the Rust DMA controller owns completion");

        var dicr = _dma.ReadRegister(Ps1MemoryMap.Dicr);
        (dicr & Dma3InterruptFlag).Should().Be(Dma3InterruptFlag);
        (dicr & DmaInterruptStatus).Should().Be(DmaInterruptStatus);
        _dma.GetInterruptPending().Should().BeTrue();

        _dma.ReadRegister(Ps1MemoryMap.GetChannelMadr(2)).Should().Be(
            0x00123450u, "DMA3 must not mutate another channel");
    }

    [Fact]
    public void UnrelatedActiveChannel_NeitherAdvancesNorCompletes_WhenCdRomDma3Completes()
    {
        // Issue #587: servicing channel 3 must not advance the generic
        // per-cycle model at all, so another channel that is actively
        // in-flight (not merely idle, as the MADR sentinel above proves)
        // must not age, complete, or raise its own DICR flag/IRQ3 either.
        const int unrelatedChannel = 6;
        const uint unrelatedDpcrEnable = 1u << (3 + 4 * unrelatedChannel);
        const uint unrelatedDicrEnable = 1u << (24 + unrelatedChannel);
        const uint unrelatedDicrFlag = 1u << unrelatedChannel;

        var payload = new byte[] { 1, 2, 3, 4 };
        PrepareDataReady(payload);

        var dpcr = _dma.ReadRegister(Ps1MemoryMap.Dpcr) | unrelatedDpcrEnable;
        _dma.WriteRegister(Ps1MemoryMap.Dpcr, dpcr);
        _dma.WriteRegister(Ps1MemoryMap.Dicr, DmaMasterEnable | Dma3InterruptEnable | unrelatedDicrEnable);
        _dma.WriteRegister(Ps1MemoryMap.GetChannelMadr(unrelatedChannel), 0x00456780u);
        _dma.WriteRegister(Ps1MemoryMap.GetChannelBcr(unrelatedChannel), 4u);
        _dma.WriteRegister(Ps1MemoryMap.GetChannelChcr(unrelatedChannel), Dma3NormalChcr);
        var unrelatedChcrBefore = _dma.ReadRegister(Ps1MemoryMap.GetChannelChcr(unrelatedChannel));
        var unrelatedMadrBefore = _dma.ReadRegister(Ps1MemoryMap.GetChannelMadr(unrelatedChannel));

        const uint destination = 0x00000400u;
        ArmDma3(destination, words: 1, enableDmaInterrupt: true);

        var result = _transfer.TryTransfer();

        result.Status.Should().Be(CdRomDmaTransferStatus.Completed);
        _dma.ReadRegister(Ps1MemoryMap.GetChannelChcr(unrelatedChannel)).Should().Be(
            unrelatedChcrBefore, "an unrelated active channel's CHCR must not change");
        _dma.ReadRegister(Ps1MemoryMap.GetChannelMadr(unrelatedChannel)).Should().Be(unrelatedMadrBefore);
        (_dma.ReadRegister(Ps1MemoryMap.Dicr) & unrelatedDicrFlag).Should().Be(
            0u, "no unrelated DICR flag/IRQ3 may fire from servicing channel 3");
    }

    [Fact]
    public void AcknowledgingInt1_DoesNotDiscardUnconsumedData_AndDma3StillSucceeds()
    {
        // Issue #587: a valid guest ordering is ReadN -> INT1 data-ready ->
        // guest acknowledges INT1 -> DMA3 starts. Acknowledging the response
        // interrupt must not drop bytes still sitting in the data FIFO.
        var payload = new byte[] { 1, 2, 3, 4 };
        PrepareDataReady(payload);

        _cdRom.AcknowledgeInterrupt(); // acks INT1 itself, not just INT3
        _cdRom.DataReady.Should().BeTrue("unconsumed data must survive the INT1 acknowledgement");
        _cdRom.DataBytesAvailable.Should().Be(4);

        const uint destination = 0x00000500u;
        ArmDma3(destination, words: 1, enableDmaInterrupt: true);

        var result = _transfer.TryTransfer();

        result.Should().Be(new CdRomDmaTransferResult(
            CdRomDmaTransferStatus.Completed, 1, destination));
        _memory.Read32(destination).Should().Be(0x04030201u);
        _cdRom.DataBytesAvailable.Should().Be(0);
    }

    [Fact]
    public void InsufficientData_FailsClosedWithoutPartialRamOrDmaCompletion()
    {
        PrepareDataReady(new byte[] { 0x11, 0x22, 0x33, 0x44 });

        const uint destination = 0x00000200u;
        ArmDma3(destination, words: 2, enableDmaInterrupt: true);

        var result = _transfer.TryTransfer();

        result.Status.Should().Be(CdRomDmaTransferStatus.WaitingForData);
        result.WordsTransferred.Should().Be(0);
        _memory.Read32(destination).Should().Be(0u);
        _memory.Read32(destination + 4).Should().Be(0u);
        _cdRom.DataBytesAvailable.Should().Be(4);

        var chcr = _dma.ReadRegister(Ps1MemoryMap.GetChannelChcr(CdRomDmaTransfer.Channel));
        (chcr & StartBusy).Should().Be(StartBusy, "the unserviced DMA request remains armed");
        _dma.GetInterruptPending().Should().BeFalse();
    }

    [Fact]
    public void DestinationCrossingRamMirrorEnd_FailsClosedBeforeAnyWrite()
    {
        PrepareDataReady(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var destination = Ps1MemoryMap.RamMirrorEnd - sizeof(uint);
        ArmDma3(destination, words: 2, enableDmaInterrupt: true);

        var result = _transfer.TryTransfer();

        result.Status.Should().Be(CdRomDmaTransferStatus.InvalidDestination);
        result.WordsTransferred.Should().Be(0);
        _memory.Read32(destination).Should().Be(0u);
        _cdRom.DataBytesAvailable.Should().Be(8);
        (_dma.ReadRegister(Ps1MemoryMap.GetChannelChcr(CdRomDmaTransfer.Channel)) & StartBusy)
            .Should().Be(StartBusy);
    }

    [Theory]
    [InlineData(0x11000001u)] // RAM -> device direction
    [InlineData(0x11000200u)] // sync mode 1
    [InlineData(0x11000002u)] // decrementing address
    public void UnsupportedChannel3Mode_FailsClosed(uint chcr)
    {
        PrepareDataReady(new byte[] { 1, 2, 3, 4 });
        ArmDma3(0x300, words: 1, enableDmaInterrupt: false, chcr: chcr);

        var result = _transfer.TryTransfer();

        result.Status.Should().Be(CdRomDmaTransferStatus.UnsupportedMode);
        _memory.Read32(0x300).Should().Be(0u);
        _cdRom.DataBytesAvailable.Should().Be(4);
    }

    private void PrepareDataReady(byte[] payload)
    {
        _cdRom.WriteCommand(0x06); // ReadN: INT3 then bounded INT1
        _cdRom.LoadData(payload);

        _cdRom.ReadRegister(1).Should().Be(0x22);
        _cdRom.AcknowledgeInterrupt();

        _cdRom.DataReady.Should().BeTrue();
        (_cdRom.GetInterruptFlag() & 0x1F).Should().Be(CdRomDevice.IntDataReady);
    }

    private void ArmDma3(
        uint destination,
        uint words,
        bool enableDmaInterrupt,
        uint chcr = Dma3NormalChcr)
    {
        var dpcr = _dma.ReadRegister(Ps1MemoryMap.Dpcr) | Dma3DpcrEnable;
        _dma.WriteRegister(Ps1MemoryMap.Dpcr, dpcr);

        if (enableDmaInterrupt)
            _dma.WriteRegister(Ps1MemoryMap.Dicr, DmaMasterEnable | Dma3InterruptEnable);

        _dma.WriteRegister(Ps1MemoryMap.GetChannelMadr(CdRomDmaTransfer.Channel), destination);
        _dma.WriteRegister(Ps1MemoryMap.GetChannelBcr(CdRomDmaTransfer.Channel), words);
        _dma.WriteRegister(Ps1MemoryMap.GetChannelChcr(CdRomDmaTransfer.Channel), chcr);
    }
}
