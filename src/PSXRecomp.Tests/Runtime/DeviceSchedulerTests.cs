using FluentAssertions;
using PSXRecomp.Core;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Core.Runtime.CdRom;
using PSXRecomp.Core.Runtime.Gpu;

namespace PSXRecomp.Tests.Runtime;

/// <summary>
/// Issue #442: the scheduler over the real Rust-backed Timer/DMA/interrupt
/// controllers (no mocks of device semantics).
/// </summary>
[Test]
public sealed class DeviceSchedulerTests : IDisposable
{
    private const uint Timer2Mode = 0x1F801124u;
    private const uint Timer2Target = 0x1F801128u;
    private const uint ModeIrqOnTarget = 0x0010;
    private const uint ModeResetOnTarget = 0x0008;
    private const uint ModeIrqRepeat = 0x0040;

    private const uint Dpcr = 0x1F8010F0u;
    private const uint Dicr = 0x1F8010F4u;
    private const uint Ch6Bcr = 0x1F8010E4u;
    private const uint Ch6Chcr = 0x1F8010E8u;
    private const uint Ch3Bcr = 0x1F8010B4u;
    private const uint Ch3Chcr = 0x1F8010B8u;
    private const uint Dma3DicrFlag = 1u << 27;
    private const uint ChcrStartTrigger = 0x11000000u;
    private const uint ChcrBusy = 1u << 24;

    private const uint Sio0Base = 0x1F801040u;
    private const uint Sio0Data = Sio0Base + 0x00;
    private const uint Sio0Control = Sio0Base + 0x0A;
    private const ushort Sio0CtrlSelect = 0x0003; // TXEN | SIO_CTRL.1 (select)

    private const uint VblankBit = 1u << DeviceScheduler.VblankIrq;
    private const uint GpuBit = 1u << DeviceScheduler.GpuIrq;
    private const uint CdRomBit = 1u << DeviceScheduler.CdRomIrq;
    private const uint DmaBit = 1u << DeviceScheduler.DmaIrq;
    private const uint Timer2Bit = 1u << (DeviceScheduler.Timer0Irq + 2);
    private const uint Sio0Bit = 1u << DeviceScheduler.Sio0Irq;

    private readonly PSXCoreWrapper _core = new();
    private readonly GpuDevice _gpu = new();
    private readonly InterruptControllerMmioAdapter _interrupts;
    private readonly DeviceScheduler _scheduler;

    public DeviceSchedulerTests()
    {
        _interrupts = new InterruptControllerMmioAdapter(_core);
        _scheduler = new DeviceScheduler(_core, _interrupts, _gpu);
    }

    public void Dispose()
    {
        _interrupts.Dispose();
        _gpu.Dispose();
        _core.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GuestCycleCounter_IsIndependentOfAdvanceChunking()
    {
        _scheduler.Advance(0);
        _scheduler.ElapsedCycles.Should().Be(0);
        _scheduler.Advance(100);
        _scheduler.Advance(200);
        _scheduler.ElapsedCycles.Should().Be(300);
    }

    [Fact]
    public void DeadlineTracksTimerReschedulingCancellationAndReadOnlyQueries()
    {
        ArmTimer2(target: 100, ModeIrqOnTarget);
        _scheduler.NextEventCycles.Should().Be(100);
        _scheduler.Advance(40);
        _scheduler.NextEventCycles.Should().Be(60);
        _core.WriteTimerRegister(Timer2Target, 50);
        _scheduler.NextEventCycles.Should().Be(10);
        _core.WriteTimerRegister(Timer2Mode, 1); // Timer2 stopped
        _scheduler.NextEventCycles.Should().BeGreaterThan(10);
        _core.WriteTimerRegister(Timer2Mode, ModeIrqOnTarget | 0x200);
        _scheduler.Advance(7);
        _scheduler.NextEventCycles.Should().Be(393);
        _scheduler.Advance(393);
        _interrupts.Status.Should().Be(Timer2Bit);
        var before = _scheduler.ElapsedCycles;
        _ = _scheduler.NextEventCycles;
        _scheduler.ElapsedCycles.Should().Be(before);
        (_core.PeekTimerRegister(Timer2Mode) & 0x800).Should().Be(0x800);
        (_core.PeekTimerRegister(Timer2Mode) & 0x800).Should().Be(0x800);
        (_core.ReadTimerRegister(Timer2Mode) & 0x800).Should().Be(0x800,
            "deadline queries must not clear MODE's reached-target flag");
    }

    [Fact]
    public void ExactAdvanceAcrossMultipleDeadlines_MatchesSingleInstructionAdvances()
    {
        ArmTimer2(target: 5, ModeIrqOnTarget | ModeResetOnTarget | ModeIrqRepeat);
        _scheduler.AdvanceExact(19);
        var count = _core.ReadTimerRegister(0x1F801120u);
        var mode = _core.PeekTimerRegister(Timer2Mode);
        var status = _interrupts.Status;
        _core.ResetTimers();
        _interrupts.Acknowledge(0);
        ArmTimer2(target: 5, ModeIrqOnTarget | ModeResetOnTarget | ModeIrqRepeat);
        for (var i = 0; i < 19; i++) _scheduler.Advance(1);
        _core.ReadTimerRegister(0x1F801120u).Should().Be(count);
        _core.PeekTimerRegister(Timer2Mode).Should().Be(mode);
        _interrupts.Status.Should().Be(status);
    }

    [Fact]
    public void ExactAdvance_DeliversEarlierEventsFirstAndSimultaneousTimersInStageOrder()
    {
        var recorder = new RecordingInterrupts(_interrupts);
        var scheduler = new DeviceScheduler(_core, recorder);
        _core.WriteTimerRegister(0x1F801108u, 5);
        _core.WriteTimerRegister(0x1F801104u, ModeIrqOnTarget);
        _core.WriteTimerRegister(0x1F801118u, 3);
        _core.WriteTimerRegister(0x1F801114u, ModeIrqOnTarget);
        ArmTimer2(5, ModeIrqOnTarget);
        scheduler.AdvanceExact(3);
        recorder.Raised.Should().Equal(DeviceScheduler.Timer0Irq + 1);
        scheduler.AdvanceExact(2);
        recorder.Raised.Should().Equal(DeviceScheduler.Timer0Irq + 1, DeviceScheduler.Timer0Irq, DeviceScheduler.Timer0Irq + 2);
    }

    [Fact]
    public void Timer2Target_RaisesIrq6ExactlyAtTheTargetCycle()
    {
        ArmTimer2(target: 100, ModeIrqOnTarget);

        for (var i = 0; i < 99; i++)
        {
            _scheduler.Advance(1);
        }
        _interrupts.Status.Should().Be(0u, "the counter has not reached its target yet");

        _scheduler.Advance(1);
        _interrupts.Status.Should().Be(Timer2Bit);
    }

    [Fact]
    public void Timer2RepeatMode_DeliversEveryTargetHit_AfterAcknowledge()
    {
        ArmTimer2(target: 10, ModeIrqOnTarget | ModeResetOnTarget | ModeIrqRepeat);

        _scheduler.Advance(10);
        _interrupts.Status.Should().Be(Timer2Bit);
        _interrupts.Acknowledge(~Timer2Bit);

        _scheduler.Advance(9);
        _interrupts.Status.Should().Be(0u);
        _scheduler.Advance(1);
        _interrupts.Status.Should().Be(Timer2Bit, "the timer's latch is consumed on delivery, so a repeat hit raises again");
    }

    [Fact]
    public void Timer2_IrqSequenceIsDeterministicAcrossRuns()
    {
        static uint[] Run()
        {
            using var core = new PSXCoreWrapper();
            using var interrupts = new InterruptControllerMmioAdapter(core);
            var scheduler = new DeviceScheduler(core, interrupts);
            core.WriteTimerRegister(Timer2Target, 7);
            core.WriteTimerRegister(Timer2Mode, ModeIrqOnTarget | ModeResetOnTarget | ModeIrqRepeat);
            var trace = new uint[64];
            for (var i = 0; i < trace.Length; i++)
            {
                scheduler.Advance((uint)(i % 3) + 1);
                trace[i] = interrupts.Status;
                interrupts.Acknowledge(0);
            }
            return trace;
        }

        var first = Run();
        first.Should().Contain(Timer2Bit);
        Run().Should().Equal(first);
    }

    [Fact]
    public void DmaTransfer_CompletesAfterItsWordCount_AndRaisesIrq3Once()
    {
        ArmOtc(words: 8, irqEnabled: true);

        _scheduler.Advance(7);
        _interrupts.Status.Should().Be(0u);
        (_core.ReadDmaRegister(Ch6Chcr) & ChcrBusy).Should().NotBe(0u);

        _scheduler.Advance(1);
        (_core.ReadDmaRegister(Ch6Chcr) & ChcrBusy).Should().Be(0u, "the transfer completed");
        (_core.ReadDmaRegister(Dicr) >> 31).Should().Be(1u);
        _interrupts.Status.Should().Be(DmaBit);

        _interrupts.Acknowledge(0);
        _scheduler.Advance(100);
        _interrupts.Status.Should().Be(0u, "IRQ3 is edge-triggered: a line that stays high does not re-raise");
    }

    [Fact]
    public void Dma3_WithoutCdRomBridge_CompletesThroughTheGenericTick()
    {
        ArmDma3(words: 4);

        _scheduler.Advance(4);

        (_core.ReadDmaRegister(Ch3Chcr) & ChcrBusy).Should().Be(0u,
            "without a CD-ROM DMA3 bridge, channel 3 keeps the generic per-word model");
        (_core.ReadDmaRegister(Dicr) & Dma3DicrFlag).Should().Be(Dma3DicrFlag);
        _interrupts.Status.Should().Be(DmaBit);
    }

    [Fact]
    public void Dma3_WithCdRomBridge_IsExcludedFromTheGenericTick()
    {
        var cdRom = new CdRomDevice(CdRomDiscIdentity.LicensedMode2()); // no data loaded
        using var dma = new DmaMmioAdapter(_core);
        using var bus = new MemoryBus(_core);
        bus.AttachDmaAdapter(dma);
        var scheduler = new DeviceScheduler(
            _core, _interrupts, _gpu, cdRom, new CdRomDmaTransfer(cdRom, dma, bus));
        ArmDma3(words: 4);

        scheduler.Advance(1000);

        (_core.ReadDmaRegister(Ch3Chcr) & ChcrBusy).Should().NotBe(0u,
            "the bridge owns channel 3, so elapsed cycles alone must not complete it");
        (_core.ReadDmaRegister(Dicr) & Dma3DicrFlag).Should().Be(0u);
        _interrupts.Status.Should().Be(0u);
    }

    [Fact]
    public void DmaTransfer_WithoutDicrEnable_CompletesWithoutIrq3()
    {
        ArmOtc(words: 4, irqEnabled: false);

        _scheduler.Advance(4);

        (_core.ReadDmaRegister(Ch6Chcr) & ChcrBusy).Should().Be(0u);
        _interrupts.Status.Should().Be(0u);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Vblank_ChunkedAdvancePreservesInterlaceFieldParity(int intervals)
    {
        _gpu.WriteGP1(0x08000027);
        var field = _gpu.ReadGpustat() >> 13 & 1;
        _scheduler.Advance((uint)intervals * DeviceScheduler.VblankIntervalCycles);

        var expectedField = field ^ (uint)(intervals & 1);
        (_gpu.ReadGpustat() >> 13 & 1).Should().Be(expectedField);
        (_gpu.ReadGpustat() >> 31 & 1).Should().Be(expectedField ^ 1u);
        _interrupts.Status.Should().Be(VblankBit);
    }

    [Fact]
    public void Vblank_StartsTheGpusNextInterlaceField()
    {
        _gpu.WriteGP1(0x08000027); // 640x480 interlaced
        var field = _gpu.ReadGpustat() >> 13 & 1;

        _scheduler.Advance(DeviceScheduler.VblankIntervalCycles - 1);
        (_gpu.ReadGpustat() >> 13 & 1).Should().Be(field);
        _scheduler.Advance(1);

        (_gpu.ReadGpustat() >> 13 & 1).Should().Be(field ^ 1, "the field polled by OpenBIOS's waitVSync flips at VBlank (Issue #732)");
        _interrupts.Status.Should().Be(VblankBit);
    }

    [Fact]
    public void CdRomCommandResponse_RaisesIrq2OnceUntilANewPacketActivates()
    {
        var cdRom = new CdRomDevice(CdRomDiscIdentity.LicensedMode2());
        EnableAllCdRomInterrupts(cdRom);
        var scheduler = new DeviceScheduler(_core, _interrupts, _gpu, cdRom);

        cdRom.WriteCommand(0x01); // GetStat -> INT3 generation 1
        scheduler.Advance(1);
        _interrupts.Status.Should().Be(CdRomBit);

        _interrupts.Acknowledge(~CdRomBit);
        scheduler.Advance(1);
        _interrupts.Status.Should().Be(0u, "the same CD-ROM response generation must not re-raise");

        cdRom.ReadRegister(1); // drain status response
        cdRom.AcknowledgeInterrupt();
        cdRom.WriteCommand(0x01); // generation 2

        scheduler.Advance(1);
        _interrupts.Status.Should().Be(CdRomBit, "a new response packet must raise a fresh IRQ2");
    }

    [Fact]
    public void CdRomCommandFromAGuestThatNeverWritesTheEnable_RaisesIrq2()
    {
        var cdRom = new CdRomDevice(CdRomDiscIdentity.LicensedMode2());
        var scheduler = new DeviceScheduler(_core, _interrupts, _gpu, cdRom);

        cdRom.WriteCommand(0x01); // CdlNop as libcd issues it; the enable register is left at its reset value
        scheduler.Advance(1);

        _interrupts.Status.Should().Be(CdRomBit);
    }

    [Fact]
    public void CdlDemute_Response_RaisesIrq2_AndStaysLowAfterTheGuestAcknowledge()
    {
        var cdRom = new CdRomDevice(CdRomDiscIdentity.LicensedMode2());
        var scheduler = new DeviceScheduler(_core, _interrupts, _gpu, cdRom);

        cdRom.WriteCommand(0x0C);
        scheduler.Advance(1);
        _interrupts.Status.Should().Be(CdRomBit);

        cdRom.ReadRegister(1);
        cdRom.SetInterruptFlag(0x1F);
        _interrupts.Acknowledge(~CdRomBit);
        scheduler.Advance(1);
        _interrupts.Status.Should().Be(0u);
    }

    [Fact]
    public void CdRomRead_Int3ThenInt1_AreDistinctIrq2Generations()
    {
        var cdRom = new CdRomDevice(CdRomDiscIdentity.LicensedMode2());
        EnableAllCdRomInterrupts(cdRom);
        var scheduler = new DeviceScheduler(_core, _interrupts, _gpu, cdRom);

        cdRom.WriteCommand(0x06); // ReadN: INT3 followed by queued INT1

        scheduler.Advance(1);
        _interrupts.Status.Should().Be(CdRomBit);

        // Guest acknowledges I_STAT separately from the CD-ROM controller.
        _interrupts.Acknowledge(~CdRomBit);
        cdRom.ReadRegister(1).Should().Be(0x22);
        cdRom.AcknowledgeInterrupt(); // immediately promotes INT1 generation

        scheduler.Advance(1);
        _interrupts.Status.Should().Be(CdRomBit, "INT1 must not be lost because INT3->INT1 had no sampled low gap");

        _interrupts.Acknowledge(~CdRomBit);
        cdRom.ReadRegister(1).Should().Be(0x22);
        cdRom.AcknowledgeInterrupt();
        scheduler.Advance(1);
        _interrupts.Status.Should().Be(0u, "both controller and I_STAT acknowledgements are deterministic");
    }

    [Fact]
    public void Sio0EmptyPortByte_DoesNotRaiseIrq7()
    {
        // Issue #716: an empty port never drives /ACK, so a transferred byte
        // latches no IRQ7 and the scheduler has nothing to deliver.
        _core.WriteMemory16(Sio0Control, Sio0CtrlSelect);
        _core.WriteMemory8(Sio0Data, 0x01);

        _scheduler.Advance(100);
        _interrupts.Status.Should().Be(0u);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeliveredSioControllerLatch_DoesNotForceOneCycleCreditsOrRepeatDelivery(bool unmasked)
    {
        var recorder = new RecordingInterrupts(_interrupts);
        var scheduler = new DeviceScheduler(_core, recorder);
        _core.GetSio0InterruptPending().Should().BeFalse();
        _scheduler.NextEventCycles.Should().BeGreaterThan(1);
        _core.WriteInterruptControllerRegister(0x1F801074u, unmasked ? Sio0Bit : 0);
        _interrupts.Raise(DeviceScheduler.Sio0Irq); // controller latch AFTER source delivery
        _interrupts.HasPendingInterrupts.Should().Be(unmasked);
        ArmTimer2(5, ModeIrqOnTarget);
        _core.WriteDmaRegister(Dpcr, 1u << 27);
        _core.WriteDmaRegister(Ch6Bcr, 3);
        _core.WriteDmaRegister(Ch6Chcr, ChcrStartTrigger);
        scheduler.NextEventCycles.Should().Be(3, "held I_STAT.7 is not an undelivered SIO pulse");
        scheduler.AdvanceExact(3);
        scheduler.NextEventCycles.Should().Be(2);
        scheduler.AdvanceExact(2);
        recorder.Raised.Should().NotContain(DeviceScheduler.Sio0Irq);
        (_interrupts.Status & Sio0Bit).Should().Be(Sio0Bit);
        scheduler.NextEventCycles.Should().BeGreaterThan(1);
        _core.WriteInterruptControllerRegister(0x1F801074u, Sio0Bit);
        _interrupts.HasPendingInterrupts.Should().BeTrue();
        _interrupts.Acknowledge(~Sio0Bit);
        _interrupts.HasPendingInterrupts.Should().BeFalse();
        scheduler.Advance(1);
        (_interrupts.Status & Sio0Bit).Should().Be(0);
        scheduler.NextEventCycles.Should().BeGreaterThan(1);
        _interrupts.Raise(DeviceScheduler.Sio0Irq);
        _interrupts.HasPendingInterrupts.Should().BeTrue();
        scheduler.NextEventCycles.Should().BeGreaterThan(1);
    }

    [Fact]
    public void GpuCommandIrq_RaisesIrq1OnEdge_AndKeepsGpuAndIStatAcksIndependent()
    {
        _gpu.WriteGP0(0x1F000000);

        _scheduler.Advance(1);

        _gpu.HasCommandInterrupt.Should().BeTrue();
        (_gpu.ReadGpustat() & (1u << 24)).Should().NotBe(0u);
        _interrupts.Status.Should().Be(GpuBit);

        // I_STAT is an edge latch. Clearing it while the GPU source is still
        // asserted must not create a second edge by itself.
        _interrupts.Acknowledge(~GpuBit);
        _scheduler.Advance(1);
        _interrupts.Status.Should().Be(0u);

        // GP1(02h) clears only the GPU source; it must not be responsible for
        // clearing I_STAT. This low sample rearms the scheduler for the next
        // GP0(1Fh) request.
        _gpu.WriteGP1(0x02000000);
        _gpu.HasCommandInterrupt.Should().BeFalse();
        (_gpu.ReadGpustat() & (1u << 24)).Should().Be(0u);
        _scheduler.Advance(1);
        _interrupts.Status.Should().Be(0u);

        _gpu.WriteGP0(0x1F000000);
        _scheduler.Advance(1);
        _interrupts.Status.Should().Be(GpuBit, "a new low-to-high GPU request must deliver a fresh IRQ1");
    }

    [Fact]
    public void GpuReset_DeassertsCommandIrqSource_AndRearmsTheNextRequest()
    {
        _gpu.WriteGP0(0x1F000000);
        _scheduler.Advance(1);
        _interrupts.Status.Should().Be(GpuBit);

        _interrupts.Acknowledge(~GpuBit);
        _gpu.WriteGP1(0x00000000); // GP1(00h): full GPU reset clears IrqRequested
        _gpu.HasCommandInterrupt.Should().BeFalse();

        // The scheduler must observe the low source before a later request can
        // form a new rising edge.
        _scheduler.Advance(1);
        _interrupts.Status.Should().Be(0u);

        _gpu.WriteGP0(0x1F000000);
        _scheduler.Advance(1);

        _gpu.HasCommandInterrupt.Should().BeTrue();
        _interrupts.Status.Should().Be(GpuBit, "a post-reset GP0(1Fh) must deliver a fresh IRQ1");
    }

    [Fact]
    public void Gp1Acknowledge_DoesNotClearAnAlreadyLatchedIStatIrq1()
    {
        _gpu.WriteGP0(0x1F000000);
        _scheduler.Advance(1);
        _interrupts.Status.Should().Be(GpuBit);

        _gpu.WriteGP1(0x02000000);

        _gpu.HasCommandInterrupt.Should().BeFalse();
        _interrupts.Status.Should().Be(GpuBit, "GPU source acknowledge and I_STAT acknowledge are separate hardware contracts");
    }

    [Fact]
    public void Vblank_RaisesIrq0AtEachIntervalBoundary()
    {
        _scheduler.Advance(DeviceScheduler.VblankIntervalCycles - 1);
        _interrupts.Status.Should().Be(0u);
        _scheduler.Advance(1);
        _interrupts.Status.Should().Be(VblankBit);

        _interrupts.Acknowledge(~VblankBit);
        _scheduler.Advance(DeviceScheduler.VblankIntervalCycles - 1);
        _interrupts.Status.Should().Be(0u);
        _scheduler.Advance(1);
        _interrupts.Status.Should().Be(VblankBit);
    }

    [Fact]
    public void Vblank_SeveralIntervalsInOneAdvance_LatchOneIrq0_AndKeepThePhase()
    {
        _scheduler.Advance((2 * DeviceScheduler.VblankIntervalCycles) + 3);
        _interrupts.Status.Should().Be(VblankBit);

        _interrupts.Acknowledge(0);
        _scheduler.Advance(DeviceScheduler.VblankIntervalCycles - 4);
        _interrupts.Status.Should().Be(0u, "3 cycles of the next interval had already elapsed");
        _scheduler.Advance(1);
        _interrupts.Status.Should().Be(VblankBit);
    }

    [Fact]
    public void OneAdvance_RaisesLinesInStageOrder_TimerThenDmaThenCdRomThenGpuThenVblank()
    {
        var cdRom = new CdRomDevice(CdRomDiscIdentity.LicensedMode2());
        EnableAllCdRomInterrupts(cdRom);
        cdRom.WriteCommand(0x01);

        var recorder = new RecordingInterrupts(_interrupts);
        var scheduler = new DeviceScheduler(_core, recorder, _gpu, cdRom);
        ArmTimer2(target: 100, ModeIrqOnTarget);
        ArmOtc(words: 8, irqEnabled: true);
        // Issue #716: no SIO0 source here. Every SIO0 port is empty and never ACKs, so the
        // SIO0 stage (between CD-ROM and GPU) cannot be exercised until a device model exists.
        _gpu.WriteGP0(0x1F000000);

        scheduler.Advance(DeviceScheduler.VblankIntervalCycles);

        recorder.Raised.Should().Equal(
            DeviceScheduler.Timer0Irq + 2,
            DeviceScheduler.DmaIrq,
            DeviceScheduler.CdRomIrq,
            DeviceScheduler.GpuIrq,
            DeviceScheduler.VblankIrq);
    }

    private static void EnableAllCdRomInterrupts(CdRomDevice cdRom)
    {
        cdRom.WriteRegister(0, 1);
        cdRom.WriteRegister(2, 0x1F);
        cdRom.WriteRegister(0, 0);
    }

    private void ArmTimer2(uint target, uint mode)
    {
        _core.WriteTimerRegister(Timer2Target, target);
        _core.WriteTimerRegister(Timer2Mode, mode);
    }

    private void ArmOtc(uint words, bool irqEnabled)
    {
        _core.WriteDmaRegister(Dpcr, 0x07654321u | (1u << 27));
        if (irqEnabled)
        {
            _core.WriteDmaRegister(Dicr, (1u << 23) | (1u << 22));
        }
        _core.WriteDmaRegister(Ch6Bcr, words);
        _core.WriteDmaRegister(Ch6Chcr, ChcrStartTrigger | 0x2u);
    }

    private void ArmDma3(uint words)
    {
        _core.WriteDmaRegister(Dpcr, 0x07654321u | (1u << 15));
        _core.WriteDmaRegister(Dicr, (1u << 23) | (1u << 19));
        _core.WriteDmaRegister(Ch3Bcr, words);
        _core.WriteDmaRegister(Ch3Chcr, ChcrStartTrigger);
    }

    /// <summary>Records <see cref="Raise"/> order and forwards to the real controller.</summary>
    private sealed class RecordingInterrupts(IInterruptController inner) : IInterruptController
    {
        public List<int> Raised { get; } = [];

        public bool HasPendingInterrupts => inner.HasPendingInterrupts;

        public uint Status => inner.Status;

        public uint Mask => inner.Mask;

        public void Raise(int irq)
        {
            Raised.Add(irq);
            inner.Raise(irq);
        }

        public void Clear(int irq) => inner.Clear(irq);

        public void Acknowledge(uint value) => inner.Acknowledge(value);

        public void SetMask(uint value) => inner.SetMask(value);

        public void Reset() => inner.Reset();
    }
}
