using FluentAssertions;
using PSXRecomp.Core;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Runtime;

// The Pad/Card IRQ handler's IRQ0 auto-ack decision driven by B0:5B. Issue #654.
// Runs over the real Rust-backed interrupt controller and DeviceScheduler.
[Test]
public sealed class BiosPadCardIrqHandlerTests : IDisposable
{
    private const uint VblankBit = 1u << DeviceScheduler.VblankIrq;
    private const uint Sio0Bit = 1u << DeviceScheduler.Sio0Irq;
    private const uint Timer0Bit = 1u << DeviceScheduler.Timer0Irq;
    private const uint Sio0Data = 0x1F801040u;
    private const uint Sio0Control = 0x1F80104Au;
    private const ushort Sio0CtrlSelect = 0x0003;

    private readonly PSXCoreWrapper _core = new();
    private readonly InterruptControllerMmioAdapter _interrupts;
    private readonly DeviceScheduler _scheduler;
    private readonly RecompilerGuestMemory _ram = new();

    public BiosPadCardIrqHandlerTests()
    {
        _interrupts = new InterruptControllerMmioAdapter(_core);
        _scheduler = new DeviceScheduler(_core, _interrupts);
    }

    public void Dispose()
    {
        _interrupts.Dispose();
        _core.Dispose();
        GC.SuppressFinalize(this);
    }

    private BiosHleRuntime CreateRuntime() =>
        new(new CapturedOutputSink(), new GuestMemoryReader(_ram.Read8), new GuestMemoryWriter(_ram.Write8));

    private void ChangeClearPad(uint value) =>
        CreateRuntime().Invoke(new BiosCallIdentity(
            BiosCallFamily.B0, BiosHleRuntime.ChangeClearPadFunction, arguments: [value]))
            .Status.Should().Be(BiosServiceStatus.Supported);

    private BiosPadCardIrqOutcome Handle() =>
        BiosPadCardIrqHandler.Handle(new GuestMemoryReader(_ram.Read8), _interrupts);

    private void RaiseVblank() => _scheduler.Advance(DeviceScheduler.VblankIntervalCycles);

    [Theory]
    [InlineData(0u, BiosPadCardIrqOutcome.LeftPending, VblankBit)]
    [InlineData(1u, BiosPadCardIrqOutcome.Acknowledged, 0u)]
    [InlineData(2u, BiosPadCardIrqOutcome.UnknownSetting, VblankBit)]
    [InlineData(0xFFFFFFFFu, BiosPadCardIrqOutcome.UnknownSetting, VblankBit)]
    public void The_Raw_Setting_Decides_The_Irq0_Acknowledge(
        uint argument, BiosPadCardIrqOutcome expected, uint expectedStatus)
    {
        _interrupts.SetMask(VblankBit);
        ChangeClearPad(argument);
        RaiseVblank();

        Handle().Should().Be(expected);
        _interrupts.Status.Should().Be(expectedStatus);
    }

    [Fact]
    public void Unconfigured_Leaves_Irq0_Pending()
    {
        _interrupts.SetMask(VblankBit);
        RaiseVblank();

        Handle().Should().Be(BiosPadCardIrqOutcome.NotConfigured);
        _interrupts.Status.Should().Be(VblankBit);
    }

    // Write-0-to-clear of bit 0 only: SIO0 and timer IRQs stay pending.
    [Fact]
    public void Acknowledge_Clears_Only_Irq0()
    {
        _interrupts.SetMask(VblankBit | Sio0Bit | Timer0Bit);
        ChangeClearPad(1);
        _core.WriteMemory16(Sio0Control, Sio0CtrlSelect);
        _core.WriteMemory8(Sio0Data, 0x01);
        _interrupts.Raise(DeviceScheduler.Timer0Irq);
        RaiseVblank();
        _interrupts.Status.Should().Be(VblankBit | Sio0Bit | Timer0Bit);

        Handle().Should().Be(BiosPadCardIrqOutcome.Acknowledged);

        _interrupts.Status.Should().Be(Sio0Bit | Timer0Bit);
    }

    [Fact]
    public void Non_Target_Irqs_Are_Not_Claimed_Or_Touched()
    {
        _interrupts.SetMask(Sio0Bit | Timer0Bit | VblankBit);
        ChangeClearPad(1);
        _interrupts.Raise(DeviceScheduler.Sio0Irq);
        _interrupts.Raise(DeviceScheduler.Timer0Irq);

        Handle().Should().Be(BiosPadCardIrqOutcome.NotClaimed);
        _interrupts.Status.Should().Be(Sio0Bit | Timer0Bit);
    }

    [Fact]
    public void A_Masked_Irq0_Is_Not_Claimed()
    {
        _interrupts.SetMask(0);
        ChangeClearPad(1);
        RaiseVblank();

        Handle().Should().Be(BiosPadCardIrqOutcome.NotClaimed);
        _interrupts.Status.Should().Be(VblankBit);
    }

    // B0:5B itself never acknowledges; only the handler does.
    [Fact]
    public void ChangeClearPad_Does_Not_Clear_A_Pending_Irq0()
    {
        _interrupts.SetMask(VblankBit);
        RaiseVblank();

        ChangeClearPad(1);

        _interrupts.Status.Should().Be(VblankBit);
    }

    [Fact]
    public void The_Scheduler_Raises_Irq0_Again_After_An_Acknowledge()
    {
        _interrupts.SetMask(VblankBit);
        ChangeClearPad(1);
        RaiseVblank();
        Handle().Should().Be(BiosPadCardIrqOutcome.Acknowledged);

        RaiseVblank();

        _interrupts.Status.Should().Be(VblankBit);
        Handle().Should().Be(BiosPadCardIrqOutcome.Acknowledged);
        _interrupts.Status.Should().Be(0u);
    }

    // Some engines rebuild the Runtime every segment; the setting lives in guest RAM.
    [Fact]
    public void The_Setting_Is_Consumed_After_The_Runtime_Is_Rebuilt()
    {
        _interrupts.SetMask(VblankBit);
        ChangeClearPad(1);
        _ = CreateRuntime();
        RaiseVblank();

        Handle().Should().Be(BiosPadCardIrqOutcome.Acknowledged);
        _interrupts.Status.Should().Be(0u);
    }

    [Fact]
    public void An_Unreadable_Setting_Changes_Nothing()
    {
        _interrupts.SetMask(VblankBit);
        RaiseVblank();

        BiosPadCardIrqHandler.Handle(new RejectingReader(), _interrupts)
            .Should().Be(BiosPadCardIrqOutcome.InvalidState);
        _interrupts.Status.Should().Be(VblankBit);
    }

    private sealed class RejectingReader : IGuestMemoryReader
    {
        public bool TryReadByte(uint address, out byte value)
        {
            value = 0;
            return false;
        }

        public bool TryRead(uint address, Span<byte> buffer) => false;
    }
}
