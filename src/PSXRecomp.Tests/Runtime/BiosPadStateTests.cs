using FluentAssertions;
using PSXRecomp.Core;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Runtime;

// B0:15 OutdatedPadInitAndStart(type, button_dest, unused, unused) — psx-spx kernelbios, OpenBIOS initPadHighLevel. Issue #703.
[Test]
public sealed class BiosPadStateTests : IDisposable
{
    private const uint VblankBit = 1u << DeviceScheduler.VblankIrq;
    private const uint ButtonDest = 0x800563F0;

    private readonly PSXCoreWrapper _core = new();
    private readonly InterruptControllerMmioAdapter _interrupts;
    private readonly RecompilerGuestMemory _ram = new();

    public BiosPadStateTests()
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

    private BiosHleRuntime Runtime() => new(new CapturedOutputSink(), Reader, Writer);

    private BiosServiceResult PadInitAndStart(params uint[] args) =>
        Runtime().Invoke(new BiosCallIdentity(BiosCallFamily.B0, BiosHleRuntime.OutdatedPadInitAndStartFunction, arguments: args));

    private (bool Enqueued, uint Destination) State()
    {
        BiosPadState.TryGetState(Reader, out var enqueued, out var destination).Should().BeTrue();
        return (enqueued, destination);
    }

    // ---- registration / arity -------------------------------------------------------------------------

    [Fact]
    public void Is_Registered_With_Arity_Four()
    {
        Runtime().TryGetServiceArgumentCount(BiosCallFamily.B0, BiosHleRuntime.OutdatedPadInitAndStartFunction, out var count).Should().BeTrue();
        count.Should().Be(4);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void Wrong_Arity_Is_Invalid_And_Changes_Nothing(int count)
    {
        var result = PadInitAndStart(new uint[count]);

        result.Diagnostic!.Code.Should().Be("BIOS_HLE_INVALID_ARGUMENTS");
        State().Should().Be((false, 0u));
    }

    // ---- valid initialization -------------------------------------------------------------------------

    [Theory]
    [InlineData(0x20000000u)]
    [InlineData(0x20000001u)]
    public void An_Accepted_Type_Returns_Two_Enqueues_PadCardIrq_And_Memorizes_ButtonDest(uint type)
    {
        var result = PadInitAndStart(type, ButtonDest, 0x14, 0);

        result.Status.Should().Be(BiosServiceStatus.Supported);
        result.ReturnValue.Should().Be(2u);
        State().Should().Be((true, ButtonDest));
    }

    [Fact]
    public void A_Zero_ButtonDest_Is_Valid_And_Is_Memorized_As_Zero()
    {
        PadInitAndStart(0x20000001, 0, 0, 0).ReturnValue.Should().Be(2u);

        State().Should().Be((true, 0u));
    }

    [Fact]
    public void The_Button_Destination_Is_Never_Dereferenced()
    {
        // An unmapped pointer is stored as given: psx-spx has the call only memorize it.
        PadInitAndStart(0x20000001, 0xDEADBEEF, 0, 0).ReturnValue.Should().Be(2u);

        State().Destination.Should().Be(0xDEADBEEFu);
    }

    [Fact]
    public void Calling_It_Twice_Keeps_One_Enqueue_And_The_Latest_ButtonDest()
    {
        PadInitAndStart(0x20000001, ButtonDest, 0, 0);
        PadInitAndStart(0x20000001, ButtonDest + 4, 0, 0).ReturnValue.Should().Be(2u);

        State().Should().Be((true, ButtonDest + 4));
    }

    // ---- type the BIOS dislikes ------------------------------------------------------------------------

    [Theory]
    [InlineData(0x10000001u)]
    [InlineData(0u)]
    [InlineData(0x20000002u)]
    public void Any_Other_Type_Returns_Zero_As_The_Bios_Does_And_Starts_Nothing(uint type)
    {
        var result = PadInitAndStart(type, ButtonDest, 0, 0);

        result.Status.Should().Be(BiosServiceStatus.Supported);
        result.ReturnValue.Should().Be(0u, "psx-spx: the function returns 0 when the type was disliked");
        State().Should().Be((false, 0u));
    }

    // ---- state that cannot be written / read -----------------------------------------------------------

    private sealed class RejectingWriter : IGuestMemoryWriter
    {
        public bool TryWriteByte(uint address, byte value) => false;

        public bool TryWrite(uint address, ReadOnlySpan<byte> data) => false;
    }

    [Fact]
    public void An_Unwritable_State_Variable_Fails_Closed_Without_Returning_Success()
    {
        var runtime = new BiosHleRuntime(new CapturedOutputSink(), Reader, new RejectingWriter());

        var result = runtime.Invoke(new BiosCallIdentity(
            BiosCallFamily.B0, BiosHleRuntime.OutdatedPadInitAndStartFunction, arguments: [0x20000001u, ButtonDest, 0u, 0u]));

        result.Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
    }

    // ---- no speculative IRQ --------------------------------------------------------------------------

    [Fact]
    public void The_Call_Raises_And_Acknowledges_No_Irq_And_Leaves_The_ChangeClearPad_Setting_Alone()
    {
        _interrupts.SetMask(VblankBit);
        _interrupts.Raise(DeviceScheduler.VblankIrq);
        var status = _interrupts.Status;

        PadInitAndStart(0x20000001, ButtonDest, 0, 0);

        _interrupts.Status.Should().Be(status);
        BiosPadCardAutoAck.TryGetSetting(Reader, out var setting).Should().BeTrue();
        setting.Should().Be(BiosPadCardAutoAckSetting.NotConfigured, "auto-ack initialisation is left to #661");
    }

    // ---- the exception chain (priority 2) -------------------------------------------------------------

    private BiosExceptionHandlerOutcome Handle() =>
        BiosExceptionHandler.Handle(
            Reader, Writer, _interrupts, new uint[32], new BiosExceptionContext(0x80025CBC, 0x400, 0x404, 0, 0));

    [Fact]
    public void An_Enqueued_PadCardIrq_That_Would_Claim_The_Exception_Stops_The_Chain_Closed()
    {
        PadInitAndStart(0x20000001, ButtonDest, 0, 0);
        _interrupts.SetMask(VblankBit);
        _interrupts.Raise(DeviceScheduler.VblankIrq);

        var outcome = Handle();

        outcome.Handled.Should().BeFalse();
        outcome.DiagnosticCode.Should().Be(BiosExceptionHandler.ChainUnsupportedDiagnosticCode);
        outcome.DiagnosticMessage.Should().Contain("PadCardIrq").And.Contain("#661");
        (_interrupts.Status & VblankBit).Should().Be(VblankBit, "nothing is acknowledged");
    }

    [Fact]
    public void Without_B0_15_The_Same_Exception_Still_Completes_As_Before()
    {
        _interrupts.SetMask(VblankBit);
        _interrupts.Raise(DeviceScheduler.VblankIrq);

        Handle().Handled.Should().BeTrue();
    }

    [Fact]
    public void An_Enqueued_PadCardIrq_Does_Not_Stop_An_Exception_It_Would_Not_Claim()
    {
        PadInitAndStart(0x20000001, ButtonDest, 0, 0);
        _interrupts.SetMask(0);

        Handle().Handled.Should().BeTrue();
    }

    [Fact]
    public void A_Priority_1_Early_Return_Is_Reached_Before_The_Enqueued_PadCardIrq()
    {
        PadInitAndStart(0x20000001, ButtonDest, 0, 0);
        _interrupts.SetMask(VblankBit);
        _interrupts.Raise(DeviceScheduler.VblankIrq);
        Runtime().Invoke(new BiosCallIdentity(
            BiosCallFamily.C0, BiosHleRuntime.ChangeClearRCntFunction, arguments: [3u, 1u]));

        var outcome = Handle();

        outcome.Handled.Should().BeTrue("flag 1 acknowledges and returns from the exception at priority 1");
        (_interrupts.Status & VblankBit).Should().Be(0u);
    }
}
