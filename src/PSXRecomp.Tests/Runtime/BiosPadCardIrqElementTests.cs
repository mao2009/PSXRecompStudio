using FluentAssertions;
using PSXRecomp.Core;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Runtime;

// The priority-2 PadCardIrq chain element (OpenBIOS sio0Handler; psx-spx PadCardIrq, B(16h) PAD_dr). Issue #661 S2.
[Test]
public sealed class BiosPadCardIrqElementTests : IDisposable
{
    private const uint VblankBit = 1u << DeviceScheduler.VblankIrq;
    private const uint CdRomBit = 1u << DeviceScheduler.CdRomIrq;
    private const uint Timer0Bit = 1u << DeviceScheduler.Timer0Irq;
    private const uint Sio0Bit = 1u << DeviceScheduler.Sio0Irq;
    private const uint ButtonDest = 0x800563F0;
    private const uint Sentinel = 0x12345678;

    private readonly PSXCoreWrapper _core = new();
    private readonly InterruptControllerMmioAdapter _interrupts;
    private readonly RecompilerGuestMemory _ram = new();

    public BiosPadCardIrqElementTests()
    {
        _interrupts = new InterruptControllerMmioAdapter(_core);
        WriteWord(ButtonDest, Sentinel);
    }

    public void Dispose()
    {
        _interrupts.Dispose();
        _core.Dispose();
        GC.SuppressFinalize(this);
    }

    private GuestMemoryReader Reader => new(_ram.Read8);

    private GuestMemoryWriter Writer => new(_ram.Write8);

    private sealed class MaskDevices : IGuestDeviceAccess
    {
        public bool TryRead32(uint physicalAddress, out uint value)
        {
            value = 0;
            return physicalAddress == BiosCardState.InterruptMaskAddress;
        }

        public bool TryWrite32(uint physicalAddress, uint value) => physicalAddress == BiosCardState.InterruptMaskAddress;
    }

    private uint Call(BiosCallFamily family, byte function, params uint[] args)
    {
        var runtime = new BiosHleRuntime(new CapturedOutputSink(), Reader, Writer);
        runtime.AttachDevices(new MaskDevices());
        var result = runtime.Invoke(new BiosCallIdentity(family, function, arguments: args));
        result.Status.Should().Be(BiosServiceStatus.Supported, result.Diagnostic?.Message);
        return result.ReturnValue ?? 0;
    }

    private void PadInitAndStart(uint buttonDest = ButtonDest) =>
        Call(BiosCallFamily.B0, BiosHleRuntime.OutdatedPadInitAndStartFunction, 0x20000001u, buttonDest, 0u, 0u);

    private void InitCard2(uint padEnable) => Call(BiosCallFamily.B0, BiosHleRuntime.InitCard2Function, padEnable);

    private void StartCard2() => Call(BiosCallFamily.B0, BiosHleRuntime.StartCard2Function);

    private void ChangeClearPad(uint value) => Call(BiosCallFamily.B0, BiosHleRuntime.ChangeClearPadFunction, value);

    /// <summary>The Persona sequence: B0:5B(0), B0:15, InitCARD2(1), StartCARD2 (auto-ack ends at 1).</summary>
    private void PersonaStart()
    {
        ChangeClearPad(0);
        PadInitAndStart();
        InitCard2(1);
        StartCard2();
    }

    private void Pending(uint mask, params int[] irqs)
    {
        _interrupts.SetMask(mask);
        foreach (var irq in irqs)
        {
            _interrupts.Raise(irq);
        }
    }

    private BiosExceptionChainContext Context =>
        new(Reader, Writer, _interrupts, new BiosExceptionContext(0x80025CBC, 0x400, 0x404, 0, 0));

    private BiosExceptionChainResult Run() => BiosPadCardIrqHandler.Run(Context);

    private void WriteWord(uint address, uint value)
    {
        for (var i = 0; i < 4; i++)
        {
            _ram.Write8(address + (uint)i, (byte)(value >> (8 * i)));
        }
    }

    private byte[] Snapshot()
    {
        var bytes = new byte[0x10000];
        Reader.TryRead(0, bytes).Should().BeTrue();
        return bytes;
    }

    // ---- dispatch / claim ---------------------------------------------------------------------------------

    [Fact]
    public void Not_Enqueued_It_Does_Nothing()
    {
        ChangeClearPad(1);
        Pending(VblankBit, DeviceScheduler.VblankIrq);
        var before = Snapshot();

        Run().Status.Should().Be(BiosExceptionChainStatus.Completed);

        _interrupts.Status.Should().Be(VblankBit);
        Snapshot().Should().Equal(before);
        _ram.Read32(ButtonDest).Should().Be(Sentinel);
    }

    [Theory]
    [InlineData(VblankBit, false)]           // enabled, not pending
    [InlineData(0u, true)]                   // pending, masked
    [InlineData(Timer0Bit | CdRomBit, false)] // other IRQs only
    public void Enqueued_It_Claims_Only_A_Pending_And_Enabled_Irq0(uint mask, bool raiseVblank)
    {
        PersonaStart();
        _interrupts.SetMask(mask);
        _interrupts.Raise(DeviceScheduler.Timer0Irq);
        _interrupts.Raise(DeviceScheduler.CdRomIrq);
        if (raiseVblank)
        {
            _interrupts.Raise(DeviceScheduler.VblankIrq);
        }

        var status = _interrupts.Status;

        Run().Status.Should().Be(BiosExceptionChainStatus.Completed);

        _interrupts.Status.Should().Be(status);
        _ram.Read32(ButtonDest).Should().Be(Sentinel, "an element that does not claim runs no stage");
    }

    // ---- pad stage ----------------------------------------------------------------------------------------

    [Fact]
    public void A_Claimed_Exception_Stores_The_Disconnected_Pad_Value_At_ButtonDest()
    {
        PersonaStart();
        Pending(VblankBit, DeviceScheduler.VblankIrq);

        Run().Status.Should().Be(BiosExceptionChainStatus.Completed);

        _ram.Read32(ButtonDest).Should().Be(0xFFFFFFFFu);
    }

    [Fact]
    public void A_Zero_ButtonDest_Is_Not_Written()
    {
        ChangeClearPad(0);
        PadInitAndStart(buttonDest: 0);
        ChangeClearPad(0);
        Pending(VblankBit, DeviceScheduler.VblankIrq);
        var before = Snapshot();

        Run().Status.Should().Be(BiosExceptionChainStatus.Completed);

        Snapshot().Should().Equal(before);
    }

    [Fact]
    public void With_The_Pad_Not_Started_ButtonDest_Is_Not_Written_But_The_Card_Still_Runs()
    {
        PadInitAndStart();
        InitCard2(0); // s_padStarted = pad_enable = 0, after B0:15 set it to 1
        StartCard2();
        Pending(VblankBit, DeviceScheduler.VblankIrq);

        Run().Status.Should().Be(BiosExceptionChainStatus.Completed);

        _ram.Read32(ButtonDest).Should().Be(Sentinel);
        (_interrupts.Status & VblankBit).Should().Be(0u, "the auto-ack does not depend on the pad stage");
    }

    // ---- IRQ0 auto-ack ------------------------------------------------------------------------------------

    [Fact]
    public void Auto_Ack_One_Acknowledges_Only_Irq0()
    {
        PersonaStart();
        Pending(VblankBit | Timer0Bit | CdRomBit, DeviceScheduler.VblankIrq, DeviceScheduler.Timer0Irq, DeviceScheduler.CdRomIrq);

        Run().Status.Should().Be(BiosExceptionChainStatus.Completed);

        _interrupts.Status.Should().Be(Timer0Bit | CdRomBit);
        _interrupts.Mask.Should().Be(VblankBit | Timer0Bit | CdRomBit);
    }

    [Fact]
    public void Auto_Ack_Zero_Leaves_Irq0_Pending()
    {
        PersonaStart();
        ChangeClearPad(0);
        Pending(VblankBit, DeviceScheduler.VblankIrq);

        Run().Status.Should().Be(BiosExceptionChainStatus.Completed);

        _interrupts.Status.Should().Be(VblankBit);
        _ram.Read32(ButtonDest).Should().Be(0xFFFFFFFFu, "the pad stage runs either way");
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(0xFFFFFFFFu)]
    public void An_Undocumented_Auto_Ack_Value_Fails_Closed_Before_Any_Write(uint value)
    {
        PersonaStart();
        ChangeClearPad(value);
        Pending(VblankBit, DeviceScheduler.VblankIrq);
        var before = Snapshot();

        var result = Run();

        result.Status.Should().Be(BiosExceptionChainStatus.Unsupported);
        result.Detail.Should().Contain("PadCardIrq").And.Contain($"0x{value:X}");
        _interrupts.Status.Should().Be(VblankBit);
        Snapshot().Should().Equal(before);
        _ram.Read32(ButtonDest).Should().Be(Sentinel);
    }

    [Fact]
    public void An_Unconfigured_Auto_Ack_Fails_Closed()
    {
        PersonaStart();
        WriteWord(BiosPadCardAutoAck.VariableAddress, 0); // not reachable through the services, which set it at enqueue
        Pending(VblankBit, DeviceScheduler.VblankIrq);

        var result = Run();

        result.Status.Should().Be(BiosExceptionChainStatus.Unsupported);
        result.Detail.Should().Contain("not configured");
        _interrupts.Status.Should().Be(VblankBit);
        _ram.Read32(ButtonDest).Should().Be(Sentinel);
    }

    // ---- card stage ---------------------------------------------------------------------------------------

    private uint OpenEnabledEvent(uint eventClass, uint spec, uint mode = BiosEventControlBlocks.ModeReady, uint func = 0)
    {
        var handle = Call(BiosCallFamily.B0, BiosHleRuntime.OpenEventFunction, eventClass, spec, mode, func);
        Call(BiosCallFamily.B0, BiosHleRuntime.EnableEventFunction, handle);
        return handle;
    }

    private uint EventStatus(uint handle)
    {
        var table = _ram.Read32(BiosEventControlBlocks.TableAddressPointer);
        return _ram.Read32(table + (handle & 0xFFFF) * BiosEventControlBlocks.EventControlBlockSize + 4);
    }

    private void Deliver(uint eventClass, uint spec) =>
        BiosEventControlBlocks.Deliver(Reader, Writer, eventClass, spec).Should().BeTrue();

    [Fact]
    public void A_Started_Card_Undelivers_The_Five_Card_Events_And_Nothing_Else()
    {
        uint[] specs = [0x0004, 0x8000, 0x0100, 0x0200, 0x2000];
        var card = specs.Select(s => OpenEnabledEvent(BiosPadCardIrqHandler.CardEventClass, s)).ToArray();
        var write = OpenEnabledEvent(BiosPadCardIrqHandler.CardEventClass, 0x8001);
        var other = OpenEnabledEvent(0xF0000009, 0x0020);
        foreach (var s in specs.Append(0x8001u))
        {
            Deliver(BiosPadCardIrqHandler.CardEventClass, s);
        }

        Deliver(0xF0000009, 0x0020);
        PersonaStart();
        Pending(VblankBit, DeviceScheduler.VblankIrq);

        Run().Status.Should().Be(BiosExceptionChainStatus.Completed);

        card.Select(EventStatus).Should().OnlyContain(s => s == BiosEventControlBlocks.StatusEnabled);
        EventStatus(write).Should().Be(BiosEventControlBlocks.StatusReady, "8001h is not undelivered");
        EventStatus(other).Should().Be(BiosEventControlBlocks.StatusReady);
    }

    [Fact]
    public void Without_StartCARD2_No_Card_Event_Is_Touched()
    {
        var handle = OpenEnabledEvent(BiosPadCardIrqHandler.CardEventClass, 0x0100);
        Deliver(BiosPadCardIrqHandler.CardEventClass, 0x0100);
        PadInitAndStart();
        Pending(VblankBit, DeviceScheduler.VblankIrq);

        Run().Status.Should().Be(BiosExceptionChainStatus.Completed);

        EventStatus(handle).Should().Be(BiosEventControlBlocks.StatusReady);
    }

    [Fact]
    public void An_Unusable_Event_Table_With_A_Started_Card_Fails_Closed()
    {
        PersonaStart();
        WriteWord(BiosEventControlBlocks.TableAddressPointer, 0xE400);
        WriteWord(BiosEventControlBlocks.TableSizePointer, 5); // not a multiple of 1Ch
        Pending(VblankBit, DeviceScheduler.VblankIrq);
        var before = Snapshot();

        var result = Run();

        result.Status.Should().Be(BiosExceptionChainStatus.Unsupported);
        result.Detail.Should().Contain("EVENT_CARD");
        _interrupts.Status.Should().Be(VblankBit, "invalid EvCB must not acknowledge IRQ0");
        Snapshot().Should().Equal(before, "invalid table must be detected before writes");
        _ram.Read32(ButtonDest).Should().Be(Sentinel);
    }

    [Fact]
    public void A_Pending_Enabled_Irq7_Fails_Closed_Before_Any_Write()
    {
        PersonaStart();
        Pending(VblankBit | Sio0Bit, DeviceScheduler.VblankIrq, DeviceScheduler.Sio0Irq);
        var before = Snapshot();

        var result = Run();

        result.Status.Should().Be(BiosExceptionChainStatus.Unsupported);
        result.Detail.Should().Contain("IRQ7").And.Contain("#712");
        _interrupts.Status.Should().Be(VblankBit | Sio0Bit, "neither IRQ is acknowledged");
        Snapshot().Should().Equal(before);
        _ram.Read32(ButtonDest).Should().Be(Sentinel);
    }

    [Fact]
    public void A_Masked_Irq7_Does_Not_Stop_The_Element()
    {
        PersonaStart();
        Pending(VblankBit, DeviceScheduler.VblankIrq, DeviceScheduler.Sio0Irq);

        Run().Status.Should().Be(BiosExceptionChainStatus.Completed);

        _interrupts.Status.Should().Be(Sio0Bit);
    }

    // ---- state ownership ----------------------------------------------------------------------------------

    [Fact]
    public void It_Writes_Only_ButtonDest_And_Leaves_The_Kernel_Variables_Alone()
    {
        PersonaStart();
        Call(BiosCallFamily.C0, BiosHleRuntime.ChangeClearRCntFunction, 1u, 1u);
        Pending(VblankBit, DeviceScheduler.VblankIrq);
        var before = Snapshot();

        Run().Status.Should().Be(BiosExceptionChainStatus.Completed);

        var after = Snapshot();
        Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]).Should().BeEmpty(
            "the pad, card, auto-ack, root-counter and EvCB variables are not changed (button_dest is above 0x10000)");
        _ram.Read32(ButtonDest).Should().Be(0xFFFFFFFFu);
    }

    [Fact]
    public void An_Unwritable_ButtonDest_Fails_Closed()
    {
        PersonaStart();
        Pending(VblankBit, DeviceScheduler.VblankIrq);
        var context = new BiosExceptionChainContext(Reader, new RefusingWriter(Writer, ButtonDest), _interrupts, Context.Exception);

        var result = BiosPadCardIrqHandler.Run(context);

        result.Status.Should().Be(BiosExceptionChainStatus.Unsupported);
        result.Detail.Should().Contain("0x800563F0");
        _interrupts.Status.Should().Be(VblankBit);
    }

    [Fact]
    public void An_Unreadable_Pad_State_Fails_Closed()
    {
        Pending(VblankBit, DeviceScheduler.VblankIrq);
        var context = new BiosExceptionChainContext(new UnreadableReader(), Writer, _interrupts, Context.Exception);

        BiosPadCardIrqHandler.Run(context).Status.Should().Be(BiosExceptionChainStatus.Unsupported);
    }

    private sealed class RefusingWriter(IGuestMemoryWriter inner, uint refused) : IGuestMemoryWriter
    {
        public bool TryWriteByte(uint address, byte value) => address != refused && inner.TryWriteByte(address, value);

        public bool TryWrite(uint address, ReadOnlySpan<byte> data) => address != refused && inner.TryWrite(address, data);
    }

    private sealed class UnreadableReader : IGuestMemoryReader
    {
        public bool TryReadByte(uint address, out byte value)
        {
            value = 0;
            return false;
        }

        public bool TryRead(uint address, Span<byte> buffer) => false;
    }

    // ---- the chain: priority 1 -> 2 -> 3 -> completion ---------------------------------------------------

    private BiosExceptionHandlerOutcome HandleException() =>
        BiosExceptionHandler.Handle(Reader, Writer, _interrupts, new uint[32], Context.Exception);

    [Fact]
    public void After_Priority_2_Acknowledged_Irq0_Priority_3_Delivers_No_VBlank_Event_And_The_Exception_Completes()
    {
        var vblankEvent = OpenEnabledEvent(BiosDefaultInterruptHandler.VblankEventClass, BiosDefaultInterruptHandler.EventSpec);
        PersonaStart();
        Pending(VblankBit, DeviceScheduler.VblankIrq);

        var outcome = HandleException();

        outcome.Handled.Should().BeTrue(outcome.DiagnosticMessage);
        outcome.RestoredSr.Should().NotBeNull("no hook: the default Exit returns from the exception");
        EventStatus(vblankEvent).Should().Be(BiosEventControlBlocks.StatusEnabled, "DefInt saw no IRQ0");
        _interrupts.Status.Should().Be(0u);
    }

    [Fact]
    public void With_Auto_Ack_Zero_The_Walk_Continues_To_Priority_3_Which_Delivers_The_VBlank_Event()
    {
        var vblankEvent = OpenEnabledEvent(BiosDefaultInterruptHandler.VblankEventClass, BiosDefaultInterruptHandler.EventSpec);
        PersonaStart();
        ChangeClearPad(0);
        Pending(VblankBit, DeviceScheduler.VblankIrq);

        var outcome = HandleException();

        outcome.Handled.Should().BeTrue(outcome.DiagnosticMessage);
        EventStatus(vblankEvent).Should().Be(BiosEventControlBlocks.StatusReady, "priority 2 did not return from the exception");
        _interrupts.Status.Should().Be(VblankBit, "left for the guest's hook");
        _ram.Read32(ButtonDest).Should().Be(0xFFFFFFFFu);
    }

    [Fact]
    public void A_Priority_1_Flag_One_Return_Skips_Priority_2()
    {
        PersonaStart();
        Call(BiosCallFamily.C0, BiosHleRuntime.ChangeClearRCntFunction, 3u, 1u);
        Pending(VblankBit, DeviceScheduler.VblankIrq);

        HandleException().Handled.Should().BeTrue();

        _ram.Read32(ButtonDest).Should().Be(Sentinel, "priority 1 returned from the exception before priority 2");
        _interrupts.Status.Should().Be(0u);
    }

    [Fact]
    public void A_Priority_2_Stop_Is_Reported_As_The_Chain_Diagnostic()
    {
        PersonaStart();
        ChangeClearPad(2);
        Pending(VblankBit, DeviceScheduler.VblankIrq);

        var outcome = HandleException();

        outcome.Handled.Should().BeFalse();
        outcome.DiagnosticCode.Should().Be(BiosExceptionHandler.ChainUnsupportedDiagnosticCode);
        outcome.DiagnosticMessage.Should().Contain("priority 2 PadCardIrq");
    }

    // ---- UnDeliverEvent -----------------------------------------------------------------------------------

    [Fact]
    public void Undeliver_Without_A_Table_Is_A_No_Op()
    {
        var before = Snapshot();

        BiosEventControlBlocks.Undeliver(Reader, Writer, BiosPadCardIrqHandler.CardEventClass, 0x0100).Should().BeTrue();

        Snapshot().Should().Equal(before);
    }

    [Fact]
    public void Undeliver_Returns_Only_Ready_Mode_2000h_Matches_To_Enabled()
    {
        var match = OpenEnabledEvent(0xF0000011, 0x0100);
        var otherSpec = OpenEnabledEvent(0xF0000011, 0x0200);
        var busy = OpenEnabledEvent(0xF0000011, 0x0100);
        Deliver(0xF0000011, 0x0100);
        Deliver(0xF0000011, 0x0200);
        Call(BiosCallFamily.B0, BiosHleRuntime.EnableEventFunction, busy); // back to busy, never ready
        var callback = OpenEnabledEvent(0xF0000011, 0x0100, BiosEventControlBlocks.ModeCallback);

        BiosEventControlBlocks.Undeliver(Reader, Writer, 0xF0000011, 0x0100).Should().BeTrue();

        EventStatus(match).Should().Be(BiosEventControlBlocks.StatusEnabled);
        EventStatus(otherSpec).Should().Be(BiosEventControlBlocks.StatusReady);
        EventStatus(busy).Should().Be(BiosEventControlBlocks.StatusEnabled);
        EventStatus(callback).Should().Be(BiosEventControlBlocks.StatusEnabled);
    }

    [Fact]
    public void Undeliver_On_An_Unusable_Table_Fails()
    {
        WriteWord(BiosEventControlBlocks.TableAddressPointer, 0xE400);
        WriteWord(BiosEventControlBlocks.TableSizePointer, 0);

        BiosEventControlBlocks.Undeliver(Reader, Writer, 0xF0000011, 0x0100).Should().BeFalse();
    }
}
