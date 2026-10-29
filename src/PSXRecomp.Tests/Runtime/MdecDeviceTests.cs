using PSXRecomp.Core;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Core.Runtime.Mdec;

namespace PSXRecomp.Tests.Runtime;

/// <summary>Issue #732: the MDEC command protocol, its decode output formats, and its DMA0/DMA1 bridge.</summary>
[Test]
public sealed class MdecDeviceTests : IDisposable
{
    private const uint StartBusy = 1u << 24;
    private const uint BlockToDevice = StartBusy | (1u << 9) | 1u;
    private const uint BlockFromDevice = StartBusy | (1u << 9);

    private readonly MdecDevice _mdec = new();
    private readonly PSXCoreWrapper _core = new();
    private readonly DmaMmioAdapter _dma;
    private readonly MemoryBus _memory;

    public MdecDeviceTests()
    {
        _dma = new DmaMmioAdapter(_core);
        _memory = new MemoryBus(_core);
        _memory.AttachDmaAdapter(_dma);
    }

    public void Dispose()
    {
        _memory.Dispose();
        _dma.Dispose();
        _core.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>One all-zero block: DC 0 with q_scale 0, then the FE00h end code (one word).</summary>
    private const uint ZeroBlock = 0xFE000000u;

    [Fact]
    public void Reset_ReportsTheDocumentedStatus()
    {
        _mdec.WriteRegister(4, 0x80000000u);
        _mdec.ReadRegister(4).Should().Be(MdecDevice.ResetStatus);
    }

    [Fact]
    public void ACommand_IsBusyUntilItsLastParameter_AndCountsThemDown()
    {
        _mdec.WriteRegister(0, 0x28000000u | 0); // 8-bit mono decode: no params -> immediately idle
        (_mdec.ReadRegister(4) & (1u << 29)).Should().Be(0u);

        _mdec.WriteRegister(0, 0x60000000u); // scale table: 32 parameter words
        var status = _mdec.ReadRegister(4);
        (status & (1u << 29)).Should().NotBe(0u, "collecting parameters");
        (status & 0xFFFF).Should().Be(31u, "remaining words minus one");
        for (var i = 0; i < 32; i++) _mdec.WriteRegister(0, 0);
        (_mdec.ReadRegister(4) & 0xFFFFu).Should().Be(0xFFFFu);
        (_mdec.ReadRegister(4) & (1u << 29)).Should().Be(0u);
    }

    [Fact]
    public void MonoEightBit_ZeroBlock_IsMidGrey()
    {
        _mdec.WriteRegister(0, 0x28000000u | 1); // decode, depth 1 (8-bit), unsigned, 1 word
        _mdec.WriteRegister(0, ZeroBlock);

        _mdec.FifoWordCount.Should().Be(16);
        Enumerable.Range(0, 16).Select(_ => _mdec.ReadData()).ToArray().Should().AllBeEquivalentTo(0x80808080u);
        (_mdec.ReadRegister(4) & (1u << 31)).Should().NotBe(0u, "output FIFO empty again");
    }

    [Fact]
    public void FifteenBit_ZeroMacroblock_Is256MidGreyPixels_WithBit15FromTheCommand()
    {
        _mdec.WriteRegister(0, 0x3A000000u | 6); // decode, depth 3 (15-bit), bit15 set, 6 words (Cr Cb Y1-Y4)
        for (var i = 0; i < 6; i++) _mdec.WriteRegister(0, ZeroBlock);

        _mdec.FifoWordCount.Should().Be(128);
        Enumerable.Range(0, 128).Select(_ => _mdec.ReadData()).ToArray().Should().AllBeEquivalentTo(0xC210C210u);
    }

    [Fact]
    public void Dma_FeedsInputAndDrainsOutput_AndAnEarlyOutputBurstWaitsForData()
    {
        _dma.WriteRegister(Ps1MemoryMap.Dpcr, 0x07654321u | (1u << 3) | (1u << 7));
        var bridge = new MdecDmaTransfer(_mdec, _dma, _memory);
        _mdec.WriteRegister(4, 0x60000000u); // enable DMA in/out

        // DMA1 armed first: 16 words to 0x3000, nothing decoded yet.
        // 現在の実装を固定する。実機未検証（UNVERIFIED）。
        _dma.WriteRegister(Ps1MemoryMap.GetChannelMadr(1), 0x3000);
        _dma.WriteRegister(Ps1MemoryMap.GetChannelBcr(1), 0x00010010);
        _dma.WriteRegister(Ps1MemoryMap.GetChannelChcr(1), BlockFromDevice);
        bridge.TryTransfer();
        (_dma.ReadRegister(Ps1MemoryMap.GetChannelChcr(1)) & StartBusy).Should().NotBe(0u, "no output yet");

        _mdec.WriteRegister(0, 0x28000000u | 1);
        // 現在の実装を固定する。実機未検証（UNVERIFIED）: bit 28 は DMA0 要求が保留中のときに立つ、と実装が定めている。
        (_mdec.ReadRegister(4) & (1u << 28)).Should().NotBe(0u, "data-in request");
        _memory.Write32(0x2000, ZeroBlock);
        _dma.WriteRegister(Ps1MemoryMap.GetChannelMadr(0), 0x2000);
        _dma.WriteRegister(Ps1MemoryMap.GetChannelBcr(0), 0x00010001);
        _dma.WriteRegister(Ps1MemoryMap.GetChannelChcr(0), BlockToDevice);
        bridge.TryTransfer();

        (_dma.ReadRegister(Ps1MemoryMap.GetChannelChcr(0)) & StartBusy).Should().Be(0u);
        (_dma.ReadRegister(Ps1MemoryMap.GetChannelChcr(1)) & StartBusy).Should().Be(0u);
        _memory.Read32(0x3000).Should().Be(0x80808080u);
        _memory.Read32(0x303C).Should().Be(0x80808080u);
        _dma.ReadRegister(Ps1MemoryMap.GetChannelMadr(1)).Should().Be(0x3040u);
    }

    /// <summary>現在の実装を固定する。実機未検証（UNVERIFIED）: コマンド 0 と 4-7 はパラメータ数 word&amp;0xFFFF で受理され、実行は何もしない。</summary>
    [Fact]
    public void InvalidCommandWithoutParameters_IsIgnored_AndLeavesTheDeviceIdle()
    {
        _mdec.WriteRegister(0, 0x00000000u); // command 0, no parameter words
        _mdec.WriteRegister(0, 0x80000000u); // command 4, no parameter words

        _mdec.IsBusy.Should().BeFalse();
        _mdec.FifoWordCount.Should().Be(0);
        (_mdec.ReadRegister(4) & (1u << 31)).Should().NotBe(0u, "no output was produced");
    }

    [Fact]
    public void MonoDecode_WithNoData_ProducesNoOutput()
    {
        _mdec.WriteRegister(0, 0x28000000u); // 8-bit mono decode, zero parameter words

        _mdec.IsBusy.Should().BeFalse();
        _mdec.FifoWordCount.Should().Be(0);
    }

    /// <summary>現在の実装を固定する。実機未検証（UNVERIFIED）: 不完全なマクロブロックは例外もエラーもなく出力を破棄する。</summary>
    [Fact]
    public void FifteenBit_ShortData_ProducesNoOutputForTheIncompleteMacroblock()
    {
        _mdec.WriteRegister(0, 0x3A000000u | 1); // 15-bit macroblock needs 6 blocks, but only one word is sent
        _mdec.WriteRegister(0, ZeroBlock);

        _mdec.IsBusy.Should().BeFalse("the declared word count was reached");
        _mdec.FifoWordCount.Should().Be(0);
    }

    [Fact]
    public void OutputFifo_DrainsExactlyAtItsWordCount_AndTheDataOutRequestFollows()
    {
        _mdec.WriteRegister(4, 0x20000000u); // enable DMA out
        _mdec.WriteRegister(0, 0x28000000u | 1);
        _mdec.WriteRegister(0, ZeroBlock);

        for (var i = 0; i < 15; i++) _mdec.ReadData().Should().Be(0x80808080u);
        (_mdec.ReadRegister(4) & (1u << 27)).Should().NotBe(0u, "one word still queued");
        (_mdec.ReadRegister(4) & (1u << 31)).Should().Be(0u, "FIFO not empty");

        _mdec.ReadData().Should().Be(0x80808080u);
        (_mdec.ReadRegister(4) & (1u << 27)).Should().Be(0u, "FIFO drained");
        (_mdec.ReadRegister(4) & (1u << 31)).Should().NotBe(0u, "FIFO empty");
        _mdec.ReadData().Should().Be(0u, "reading an empty FIFO returns zero");
        _mdec.FifoWordCount.Should().Be(0);
    }

    [Fact]
    public void Dma_OutputBurstLargerThanTheFifo_WaitsWithoutConsumingWords()
    {
        _dma.WriteRegister(Ps1MemoryMap.Dpcr, 0x07654321u | (1u << 3) | (1u << 7));
        var bridge = new MdecDmaTransfer(_mdec, _dma, _memory);
        _mdec.WriteRegister(4, 0x60000000u);
        _mdec.WriteRegister(0, 0x28000000u | 1);
        _mdec.WriteRegister(0, ZeroBlock); // 16 output words

        _dma.WriteRegister(Ps1MemoryMap.GetChannelMadr(1), 0x3000);
        _dma.WriteRegister(Ps1MemoryMap.GetChannelBcr(1), 0x00010011); // 17 words: one more than the FIFO holds
        _dma.WriteRegister(Ps1MemoryMap.GetChannelChcr(1), BlockFromDevice);
        bridge.TryTransfer();

        (_dma.ReadRegister(Ps1MemoryMap.GetChannelChcr(1)) & StartBusy).Should().NotBe(0u, "the burst is not complete");
        _mdec.FifoWordCount.Should().Be(16, "no word was consumed");
    }

    /// <summary>
    /// Issue #774 (B-7): the MDEC DMA0 transfer runs only when the scheduler is wired to it. A scheduler built without the
    /// bridged MDEC channels leaves the parameter words undelivered, so the decoder stays busy (the standalone host did this
    /// before the wiring was shared with the interpreter).
    /// </summary>
    [Fact]
    public void SchedulerFromTheHostWiring_DeliversTheMdecDmaInputWords()
    {
        using var graph = new PsxDeviceGraph();
        graph.TryWrite(0x1F801824, 4, 0x60000000u); // enable DMA in/out
        graph.TryWrite(0x1F801820, 4, 0x40000001u); // set quantisation tables: 32 parameter words follow
        for (var i = 0u; i < 32; i++) graph.Bus.Write32(0x2000 + 4 * i, 0x01010101u);
        graph.DmaAdapter.WriteRegister(Ps1MemoryMap.Dpcr, 0x07654321u | (1u << 3) | (1u << 7));
        graph.DmaAdapter.WriteRegister(Ps1MemoryMap.GetChannelMadr(0), 0x2000);
        graph.DmaAdapter.WriteRegister(Ps1MemoryMap.GetChannelBcr(0), 0x00010020);
        graph.DmaAdapter.WriteRegister(Ps1MemoryMap.GetChannelChcr(0), BlockToDevice);
        graph.Mdec.IsBusy.Should().BeTrue("parameters still pending before the DMA runs");

        var scheduler = graph.CreateScheduler();
        scheduler.Advance(1);

        graph.Mdec.IsBusy.Should().BeFalse("all 32 DMA0 words reached the decoder");
        (graph.DmaAdapter.ReadRegister(Ps1MemoryMap.GetChannelChcr(0)) & StartBusy).Should().Be(0u);
    }

    [Fact]
    public void DeviceGraph_RoutesGuestAccessesAt1F801820And1F801824ToTheMdec()
    {
        using var graph = new PsxDeviceGraph();
        graph.TryWrite(0x1F801824, 4, 0x80000000u).Should().Be(PsxDeviceAccessStatus.Completed);
        graph.TryRead(0x1F801824, 4, out var status);
        status.Should().Be(MdecDevice.ResetStatus);
        graph.TryWrite(0x1F801820, 4, 0x60000000u);
        graph.Mdec.IsBusy.Should().BeTrue();
    }
}
