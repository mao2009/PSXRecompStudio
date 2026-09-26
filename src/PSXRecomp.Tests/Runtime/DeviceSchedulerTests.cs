using FluentAssertions;
using PSXRecomp.Core;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Runtime;
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
    private const uint ChcrStartTrigger = 0x11000000u;
    private const uint ChcrBusy = 1u << 24;

    private const uint Sio0Base = 0x1F801040u;
    private const uint Sio0Data = Sio0Base + 0x00;
    private const uint Sio0Control = Sio0Base + 0x0A;
    private const ushort Sio0CtrlSelect = 0x0003; // TXEN | SIO_CTRL.1 (select)

    private const uint VblankBit = 1u << DeviceScheduler.VblankIrq;
    private const uint GpuBit = 1u << DeviceScheduler.GpuIrq;
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
    public void DmaTransfer_WithoutDicrEnable_CompletesWithoutIrq3()
    {
        ArmOtc(words: 4, irqEnabled: false);

        _scheduler.Advance(4);

        (_core.ReadDmaRegister(Ch6Chcr) & ChcrBusy).Should().Be(0u);
        _interrupts.Status.Should().Be(0u);
    }

    [Fact]
    public void Sio0ByteReceived_RaisesIrq7OnTheNextAdvance()
    {
        // Issue #543: SIO0 has no clock of its own, so a 1-cycle Advance is
        // enough for the scheduler to notice a byte the guest already
        // transferred (unlike Timer/DMA, which need cycles to elapse).
        _core.WriteMemory16(Sio0Control, Sio0CtrlSelect);
        _core.WriteMemory8(Sio0Data, 0x01);

        _scheduler.Advance(1);
        _interrupts.Status.Should().Be(Sio0Bit);

        _interrupts.Acknowledge(~Sio0Bit);
        _scheduler.Advance(100);
        _interrupts.Status.Should().Be(0u, "the latch was already delivered and cleared; nothing re-raises it");
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
    public void OneAdvance_RaisesLinesInStageOrder_TimerThenDmaThenSio0ThenGpuThenVblank()
    {
        var recorder = new RecordingInterrupts(_interrupts);
        var scheduler = new DeviceScheduler(_core, recorder, _gpu);
        ArmTimer2(target: 100, ModeIrqOnTarget);
        ArmOtc(words: 8, irqEnabled: true);
        _core.WriteMemory16(Sio0Control, Sio0CtrlSelect);
        _core.WriteMemory8(Sio0Data, 0x01);
        _gpu.WriteGP0(0x1F000000);

        scheduler.Advance(DeviceScheduler.VblankIntervalCycles);

        recorder.Raised.Should().Equal(
            DeviceScheduler.Timer0Irq + 2,
            DeviceScheduler.DmaIrq,
            DeviceScheduler.Sio0Irq,
            DeviceScheduler.GpuIrq,
            DeviceScheduler.VblankIrq);
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
            _core.WriteDmaRegister(Dicr, (1u << 23) | (1u << 30));
        }
        _core.WriteDmaRegister(Ch6Bcr, words);
        _core.WriteDmaRegister(Ch6Chcr, ChcrStartTrigger | 0x2u);
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
