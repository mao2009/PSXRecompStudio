using FluentAssertions;
using PSXRecomp.Core;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Runtime;

// B0:4B StartCARD2() — psx-spx function-summary (signature only), OpenBIOS startCard. Issue #710.
[Test]
public sealed class BiosStartCard2Tests : IDisposable
{
    private const uint VblankBit = 1u << DeviceScheduler.VblankIrq;
    private const uint ButtonDest = 0x800563F0;

    private readonly PSXCoreWrapper _core = new();
    private readonly InterruptControllerMmioAdapter _interrupts;
    private readonly RecompilerGuestMemory _ram = new();

    public BiosStartCard2Tests()
    {
        _interrupts = new InterruptControllerMmioAdapter(_core);
    }

    public void Dispose()
    {
        _interrupts.Dispose();
        _core.Dispose();
        GC.SuppressFinalize(this);
    }

    private GuestMemoryReader Reader => new(_ram.Read8);

    private GuestMemoryWriter Writer => new(_ram.Write8);

    private sealed class FakeDevices : IGuestDeviceAccess
    {
        public uint Mask;
        public bool FailRead;
        public bool FailWrite;
        public int Writes;

        public bool TryRead32(uint physicalAddress, out uint value)
        {
            value = physicalAddress == BiosCardState.InterruptMaskAddress ? Mask : 0;
            return !FailRead && physicalAddress == BiosCardState.InterruptMaskAddress;
        }

        public bool TryWrite32(uint physicalAddress, uint value)
        {
            Writes++;
            if (FailWrite || physicalAddress != BiosCardState.InterruptMaskAddress)
            {
                return false;
            }

            Mask = value;
            return true;
        }
    }

    private sealed class RejectingWriter : IGuestMemoryWriter
    {
        public bool TryWriteByte(uint address, byte value) => false;

        public bool TryWrite(uint address, ReadOnlySpan<byte> data) => false;
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

    private BiosHleRuntime Runtime(FakeDevices? devices = null, IGuestMemoryReader? reader = null, IGuestMemoryWriter? writer = null)
    {
        var runtime = new BiosHleRuntime(new CapturedOutputSink(), reader ?? Reader, writer ?? Writer);
        if (devices is not null)
        {
            runtime.AttachDevices(devices);
        }

        return runtime;
    }

    private BiosServiceResult Call(BiosHleRuntime runtime, BiosCallFamily family, byte function, params uint[] args) =>
        runtime.Invoke(new BiosCallIdentity(family, function, arguments: args));

    private BiosServiceResult InitCard2(uint padEnable) => Call(Runtime(), BiosCallFamily.B0, BiosHleRuntime.InitCard2Function, padEnable);

    private BiosServiceResult PadInitAndStart() =>
        Call(Runtime(), BiosCallFamily.B0, BiosHleRuntime.OutdatedPadInitAndStartFunction, 0x20000001u, ButtonDest, 0u, 0u);

    private BiosServiceResult StartCard2(FakeDevices? devices, params uint[] args) =>
        Call(Runtime(devices), BiosCallFamily.B0, BiosHleRuntime.StartCard2Function, args);

    private bool Started()
    {
        BiosCardState.TryGetStarted(Reader, out var started).Should().BeTrue();
        return started;
    }

    private (bool Initialized, uint PadEnable) Init()
    {
        BiosCardState.TryGetState(Reader, out var initialized, out var padEnable).Should().BeTrue();
        return (initialized, padEnable);
    }

    private (bool Enqueued, uint Destination) Pad()
    {
        BiosPadState.TryGetState(Reader, out var enqueued, out var destination).Should().BeTrue();
        return (enqueued, destination);
    }

    private byte[] Snapshot()
    {
        var bytes = new byte[0x10000];
        Reader.TryRead(0, bytes).Should().BeTrue();
        return bytes;
    }

    // ---- registration / arity -------------------------------------------------------------------------

    [Fact]
    public void Is_Registered_With_Arity_Zero()
    {
        Runtime().TryGetServiceArgumentCount(BiosCallFamily.B0, BiosHleRuntime.StartCard2Function, out var count).Should().BeTrue();
        count.Should().Be(0);
    }

    [Fact]
    public void An_Argument_Is_Invalid_And_Changes_Nothing()
    {
        InitCard2(1);
        var before = Snapshot();

        StartCard2(new FakeDevices(), 1).Diagnostic!.Code.Should().Be("BIOS_HLE_INVALID_ARGUMENTS");

        Snapshot().Should().Equal(before);
    }

    // ---- prerequisite ---------------------------------------------------------------------------------

    [Fact]
    public void Before_InitCARD2_It_Fails_Closed_And_Changes_Nothing()
    {
        var devices = new FakeDevices { Mask = 0x8 };
        var runtime = Runtime(devices); // construction seeds the jump-table slots; snapshot after it
        var before = Snapshot();

        Call(runtime, BiosCallFamily.B0, BiosHleRuntime.StartCard2Function).Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");

        Snapshot().Should().Equal(before);
        devices.Writes.Should().Be(0);
        devices.Mask.Should().Be(0x8u);
    }

    [Fact]
    public void Without_Attached_Devices_It_Fails_Closed()
    {
        InitCard2(1);
        var before = Snapshot();

        StartCard2(null).Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");

        Snapshot().Should().Equal(before);
    }

    // ---- first / repeated start -----------------------------------------------------------------------

    [Fact]
    public void The_First_Start_Returns_One_Starts_The_Card_Enqueues_PadCardIrq_And_Unmasks_Irq0()
    {
        InitCard2(1);
        var devices = new FakeDevices { Mask = 0x8 };

        var result = StartCard2(devices);

        result.Status.Should().Be(BiosServiceStatus.Supported);
        result.ReturnValue.Should().Be(1u);
        Started().Should().BeTrue();
        Pad().Should().Be((true, 0u));
        devices.Mask.Should().Be(0x9u, "I_MASK |= IRQ0 keeps the other bits");
        Init().Should().Be((true, 1u), "pad_enable is InitCARD2's and is kept");
    }

    [Fact]
    public void A_Repeated_Start_Is_Idempotent_And_Keeps_ButtonDest()
    {
        PadInitAndStart();
        InitCard2(1);
        var devices = new FakeDevices();

        StartCard2(devices).ReturnValue.Should().Be(1u);
        StartCard2(devices).ReturnValue.Should().Be(1u);

        Pad().Should().Be((true, ButtonDest));
        Started().Should().BeTrue();
    }

    [Fact]
    public void A_Repeated_InitCARD2_Keeps_The_Card_Started()
    {
        InitCard2(1);
        StartCard2(new FakeDevices());

        InitCard2(0).ReturnValue.Should().Be(1u);

        Started().Should().BeTrue("OpenBIOS initCard leaves s_cardStarted alone");
        Init().Should().Be((true, 0u));
    }

    [Fact]
    public void The_State_Survives_A_Rebuilt_Runtime()
    {
        InitCard2(1);
        StartCard2(new FakeDevices());

        Started().Should().BeTrue();
        Call(Runtime(), BiosCallFamily.B0, BiosHleRuntime.InitCard2Function, 1u).ReturnValue.Should().Be(1u);
    }

    // ---- B0:15 interaction ----------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void B0_15_InitCARD2_And_StartCARD2_Keep_Each_Others_State_In_Any_Order(int order)
    {
        var devices = new FakeDevices();
        switch (order)
        {
            case 0: // B0:15 -> InitCARD2 -> StartCARD2
                PadInitAndStart();
                InitCard2(1);
                StartCard2(devices);
                break;
            case 1: // InitCARD2 -> StartCARD2 -> B0:15
                InitCard2(1);
                StartCard2(devices);
                PadInitAndStart();
                break;
            default: // InitCARD2 -> B0:15 -> StartCARD2
                InitCard2(1);
                PadInitAndStart();
                StartCard2(devices);
                break;
        }

        Init().Should().Be((true, 1u));
        Started().Should().BeTrue();
        Pad().Should().Be((true, ButtonDest), "button_dest is B0:15's and survives StartCARD2");
    }

    // ---- no speculative side effects ------------------------------------------------------------------

    [Fact]
    public void It_Touches_Only_The_Pad_And_Card_Variables_And_Leaves_AutoAck_RootCounter_And_Irq_State_Alone()
    {
        Call(Runtime(), BiosCallFamily.B0, BiosHleRuntime.ChangeClearPadFunction, 0u);
        Call(Runtime(), BiosCallFamily.C0, BiosHleRuntime.ChangeClearRCntFunction, 3u, 0u);
        InitCard2(1);
        _interrupts.SetMask(VblankBit);
        _interrupts.Raise(DeviceScheduler.VblankIrq);
        var status = _interrupts.Status;
        var mask = _interrupts.Mask;
        var runtime = Runtime(new FakeDevices());
        var before = Snapshot();

        Call(runtime, BiosCallFamily.B0, BiosHleRuntime.StartCard2Function);

        var after = Snapshot();
        var changed = Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]).Select(i => (uint)i).ToArray();
        changed.Should().NotBeEmpty();
        changed.Should().OnlyContain(a =>
            (a >= BiosPadState.VariableAddress && a < BiosPadState.VariableAddress + 8) ||
            (a >= BiosCardState.VariableAddress && a < BiosCardState.VariableAddress + 8));
        BiosPadCardAutoAck.TryGetSetting(Reader, out var setting).Should().BeTrue();
        setting.Should().Be(BiosPadCardAutoAckSetting.Zero, "the auto-ack forcing is #661's");
        BiosRootCounterClearPolicy.TryGetFlag(Reader, 3, out var flag).Should().BeTrue();
        flag.Should().Be(0u);
        _interrupts.Status.Should().Be(status);
        _interrupts.Mask.Should().Be(mask);
    }

    // ---- the priority-2 guard -------------------------------------------------------------------------

    private BiosExceptionHandlerOutcome Handle() =>
        BiosExceptionHandler.Handle(
            Reader, Writer, _interrupts, new uint[32], new BiosExceptionContext(0x80025CBC, 0x400, 0x404, 0, 0));

    [Fact]
    public void StartCARD2_Alone_Enqueues_PadCardIrq_So_The_Guard_Stops_A_Claimed_VBlank_Exception()
    {
        InitCard2(1);
        StartCard2(new FakeDevices());
        _interrupts.SetMask(VblankBit);
        _interrupts.Raise(DeviceScheduler.VblankIrq);

        var outcome = Handle();

        outcome.Handled.Should().BeFalse();
        outcome.DiagnosticCode.Should().Be(BiosExceptionHandler.ChainUnsupportedDiagnosticCode);
        outcome.DiagnosticMessage.Should().Contain("PadCardIrq").And.Contain("#661");
        (_interrupts.Status & VblankBit).Should().Be(VblankBit, "nothing is acknowledged");
    }

    [Fact]
    public void After_Start_An_Exception_The_Element_Would_Not_Claim_Still_Completes()
    {
        InitCard2(1);
        StartCard2(new FakeDevices());
        _interrupts.SetMask(0);

        Handle().Handled.Should().BeTrue();
    }

    // ---- fail closed ----------------------------------------------------------------------------------

    [Fact]
    public void Unreadable_State_Fails_Closed()
    {
        InitCard2(1);
        var runtime = Runtime(new FakeDevices(), new UnreadableReader());

        Call(runtime, BiosCallFamily.B0, BiosHleRuntime.StartCard2Function).Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
    }

    [Fact]
    public void An_Unwritable_State_Variable_Fails_Closed()
    {
        InitCard2(1);
        var runtime = Runtime(new FakeDevices(), writer: new RejectingWriter());

        Call(runtime, BiosCallFamily.B0, BiosHleRuntime.StartCard2Function).Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
        Started().Should().BeFalse();
    }

    [Fact]
    public void An_Unreadable_Or_Unwritable_I_Mask_Fails_Closed_Before_Any_State_Write()
    {
        InitCard2(1);

        StartCard2(new FakeDevices { FailRead = true }).Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
        StartCard2(new FakeDevices { FailWrite = true }).Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");

        Started().Should().BeFalse();
        Pad().Should().Be((false, 0u));
    }

    // ---- VBlank root-counter clear policy (t = 3 := 0) ------------------------------------------------

    private void ChangeClearRCnt(uint t, uint flag) =>
        Call(Runtime(), BiosCallFamily.C0, BiosHleRuntime.ChangeClearRCntFunction, t, flag).Status.Should().Be(BiosServiceStatus.Supported);

    private uint Flag(uint t)
    {
        BiosRootCounterClearPolicy.TryGetFlag(Reader, t, out var flag).Should().BeTrue();
        return flag;
    }

    [Fact]
    public void StartCARD2_Forces_The_VBlank_Policy_To_Zero_And_Keeps_The_Other_Sources()
    {
        ChangeClearRCnt(0, 1);
        ChangeClearRCnt(1, 1);
        ChangeClearRCnt(2, 0);
        ChangeClearRCnt(3, 1);
        InitCard2(1);

        StartCard2(new FakeDevices()).ReturnValue.Should().Be(1u);

        (Flag(0), Flag(1), Flag(2), Flag(3)).Should().Be((1u, 1u, 0u, 0u));
    }

    [Fact]
    public void StartCARD2_Succeeds_When_The_VBlank_Policy_Is_Already_Zero()
    {
        ChangeClearRCnt(3, 0);
        InitCard2(1);

        StartCard2(new FakeDevices()).Status.Should().Be(BiosServiceStatus.Supported);

        Flag(3).Should().Be(0u);
    }

    private sealed class RangeFaultReader(IGuestMemoryReader inner) : IGuestMemoryReader
    {
        public bool TryReadByte(uint address, out byte value)
        {
            value = 0;
            return !InRange(address) && inner.TryReadByte(address, out value);
        }

        public bool TryRead(uint address, Span<byte> buffer)
        {
            for (var i = 0u; i < buffer.Length; i++)
            {
                if (InRange(address + i))
                {
                    return false;
                }
            }

            return inner.TryRead(address, buffer);
        }

        private static bool InRange(uint a) => a >= BiosRootCounterClearPolicy.VariableAddress && a < BiosRootCounterClearPolicy.VariableAddress + 16;
    }

    private sealed class RangeFaultWriter(IGuestMemoryWriter inner) : IGuestMemoryWriter
    {
        public bool TryWriteByte(uint address, byte value) => !InRange(address) && inner.TryWriteByte(address, value);

        public bool TryWrite(uint address, ReadOnlySpan<byte> data)
        {
            for (var i = 0u; i < data.Length; i++)
            {
                if (InRange(address + i))
                {
                    return false;
                }
            }

            return inner.TryWrite(address, data);
        }

        private static bool InRange(uint a) => a >= BiosRootCounterClearPolicy.VariableAddress && a < BiosRootCounterClearPolicy.VariableAddress + 16;
    }

    [Fact]
    public void An_Unreadable_Root_Counter_State_Fails_Closed_Before_Any_Write()
    {
        InitCard2(1);
        var devices = new FakeDevices { Mask = 0x8 };
        var runtime = Runtime(devices, reader: new RangeFaultReader(Reader));

        Call(runtime, BiosCallFamily.B0, BiosHleRuntime.StartCard2Function).Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");

        devices.Writes.Should().Be(0);
        devices.Mask.Should().Be(0x8u);
        Started().Should().BeFalse();
        Pad().Should().Be((false, 0u));
    }

    [Fact]
    public void An_Unwritable_Root_Counter_State_Fails_Closed_Before_I_Mask_Enqueue_And_Started()
    {
        InitCard2(1);
        var devices = new FakeDevices { Mask = 0x8 };
        var runtime = Runtime(devices, writer: new RangeFaultWriter(Writer));

        Call(runtime, BiosCallFamily.B0, BiosHleRuntime.StartCard2Function).Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");

        devices.Writes.Should().Be(0, "the VBlank policy is the first write");
        Started().Should().BeFalse();
        Pad().Should().Be((false, 0u));
    }

    [Fact]
    public void After_StartCARD2_The_Next_VBlank_Reaches_The_Priority_2_Guard_Instead_Of_Being_Acknowledged_At_Priority_1()
    {
        PadInitAndStart();
        ChangeClearRCnt(3, 1);
        InitCard2(1);
        StartCard2(new FakeDevices());
        _interrupts.SetMask(VblankBit);
        _interrupts.Raise(DeviceScheduler.VblankIrq);

        var outcome = Handle();

        outcome.Handled.Should().BeFalse("with the old flag 1 priority 1 would acknowledge IRQ0 and return from the exception");
        outcome.DiagnosticCode.Should().Be(BiosExceptionHandler.ChainUnsupportedDiagnosticCode);
        outcome.DiagnosticMessage.Should().Contain("PadCardIrq").And.Contain("#661");
        (_interrupts.Status & VblankBit).Should().Be(VblankBit, "IRQ0 stays pending");
    }

    [Fact]
    public void Without_StartCARD2_The_Old_VBlank_Flag_One_Acknowledges_And_Returns_At_Priority_1()
    {
        PadInitAndStart();
        ChangeClearRCnt(3, 1);
        InitCard2(1);
        _interrupts.SetMask(VblankBit);
        _interrupts.Raise(DeviceScheduler.VblankIrq);

        Handle().Handled.Should().BeTrue();
        (_interrupts.Status & VblankBit).Should().Be(0u);
    }
}
