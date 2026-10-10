using FluentAssertions;
using PSXRecomp.Core;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Runtime;

// B0:4A InitCARD2(pad_enable) — psx-spx function-summary/memory-card-functions/joypad-functions, OpenBIOS initCard. Issue #708.
[Test]
public sealed class BiosCardStateTests : IDisposable
{
    private const uint VblankBit = 1u << DeviceScheduler.VblankIrq;
    private const uint ButtonDest = 0x800563F0;

    private readonly PSXCoreWrapper _core = new();
    private readonly InterruptControllerMmioAdapter _interrupts;
    private readonly RecompilerGuestMemory _ram = new();

    public BiosCardStateTests()
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

    // The measured Persona call has a0 = 1 (a1 = 0x20 is a leftover; the registered arity is 1).
    private BiosServiceResult InitCard2(params uint[] args) =>
        Runtime().Invoke(new BiosCallIdentity(BiosCallFamily.B0, BiosHleRuntime.InitCard2Function, arguments: args));

    private BiosServiceResult PadInitAndStart() =>
        Runtime().Invoke(new BiosCallIdentity(
            BiosCallFamily.B0, BiosHleRuntime.OutdatedPadInitAndStartFunction, arguments: [0x20000001u, ButtonDest, 0u, 0u]));

    private (bool Initialized, uint PadEnable) State()
    {
        BiosCardState.TryGetState(Reader, out var initialized, out var padEnable).Should().BeTrue();
        return (initialized, padEnable);
    }

    private byte[] Snapshot()
    {
        var bytes = new byte[0x10000];
        Reader.TryRead(0, bytes).Should().BeTrue();
        return bytes;
    }

    // ---- registration / arity -------------------------------------------------------------------------

    [Fact]
    public void Is_Registered_With_Arity_One()
    {
        Runtime().TryGetServiceArgumentCount(BiosCallFamily.B0, BiosHleRuntime.InitCard2Function, out var count).Should().BeTrue();
        count.Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void Wrong_Arity_Is_Invalid_And_Changes_Nothing(int count)
    {
        var runtime = Runtime();
        var before = Snapshot();

        var result = runtime.Invoke(new BiosCallIdentity(BiosCallFamily.B0, BiosHleRuntime.InitCard2Function, arguments: new uint[count]));

        result.Diagnostic!.Code.Should().Be("BIOS_HLE_INVALID_ARGUMENTS");
        Snapshot().Should().Equal(before);
    }

    // ---- pad_enable and the return value --------------------------------------------------------------

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(0xFFFFFFFFu)]
    public void The_First_Call_Returns_Zero_And_Records_Pad_Enable_As_Given(uint padEnable)
    {
        var result = InitCard2(padEnable);

        result.Status.Should().Be(BiosServiceStatus.Supported);
        result.ReturnValue.Should().Be(0u);
        State().Should().Be((true, padEnable));
    }

    [Fact]
    public void A_Repeat_Call_Returns_One_And_Stores_The_Latest_Pad_Enable()
    {
        InitCard2(1).ReturnValue.Should().Be(0u);

        InitCard2(0).ReturnValue.Should().Be(1u);
        State().Should().Be((true, 0u));
        InitCard2(1).ReturnValue.Should().Be(1u);
        State().Should().Be((true, 1u));
    }

    [Fact]
    public void The_State_Survives_A_Rebuilt_Runtime()
    {
        InitCard2(1);

        Runtime().Invoke(new BiosCallIdentity(BiosCallFamily.B0, BiosHleRuntime.InitCard2Function, arguments: [1u])).ReturnValue.Should().Be(1u);
    }

    [Fact]
    public void Before_Any_Call_The_State_Is_Not_Initialized()
    {
        State().Should().Be((false, 0u));
    }

    // ---- no side effects ------------------------------------------------------------------------------

    [Fact]
    public void The_Call_Touches_Only_Its_Own_Variable()
    {
        _interrupts.SetMask(VblankBit);
        _interrupts.Raise(DeviceScheduler.VblankIrq);
        var status = _interrupts.Status;
        var mask = _interrupts.Mask;
        var runtime = Runtime(); // construction seeds the jump-table slots; snapshot after it
        var before = Snapshot();

        runtime.Invoke(new BiosCallIdentity(BiosCallFamily.B0, BiosHleRuntime.InitCard2Function, arguments: [1u]));

        var after = Snapshot();
        var changed = Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]).Select(i => (uint)i).ToArray();
        changed.Should().NotBeEmpty();
        changed.Should().OnlyContain(a => a >= BiosCardState.VariableAddress && a < BiosCardState.VariableAddress + 8);
        _interrupts.Status.Should().Be(status);
        _interrupts.Mask.Should().Be(mask);
        BiosPadCardAutoAck.TryGetSetting(Reader, out var setting).Should().BeTrue();
        setting.Should().Be(BiosPadCardAutoAckSetting.NotConfigured);
    }

    // ---- interaction with B0:15 -----------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void B0_15_And_InitCARD2_Do_Not_Disturb_Each_Others_State_In_Either_Order(bool padFirst)
    {
        if (padFirst)
        {
            PadInitAndStart();
            InitCard2(1);
        }
        else
        {
            InitCard2(1);
            PadInitAndStart();
        }

        State().Should().Be((true, 1u));
        BiosPadState.TryGetState(Reader, out var enqueued, out var destination).Should().BeTrue();
        (enqueued, destination).Should().Be((true, ButtonDest));
    }

    [Fact]
    public void InitCARD2_Alone_Does_Not_Enqueue_PadCardIrq()
    {
        InitCard2(1);

        BiosPadState.TryGetState(Reader, out var enqueued, out _).Should().BeTrue();
        enqueued.Should().BeFalse("the enqueue is StartCARD2/StartPAD2's, not InitCARD2's");
    }

    // ---- the shared pad-started flag (OpenBIOS s_padStarted; #661) ------------------------------------

    private uint PadStarted()
    {
        BiosCardState.TryGetPadStarted(Reader, out var padStarted).Should().BeTrue();
        return padStarted;
    }

    [Fact]
    public void The_Pad_Started_Flag_Is_Whichever_Of_B0_15_And_InitCARD2_Ran_Last()
    {
        PadStarted().Should().Be(0u);
        InitCard2(0);
        PadStarted().Should().Be(0u);
        PadInitAndStart();
        PadStarted().Should().Be(1u, "B0:15's InitPad sets s_padStarted = 1");
        InitCard2(0);
        PadStarted().Should().Be(0u, "InitCARD2 stores its pad_enable");
    }

    [Fact]
    public void B0_15_Sets_The_Pad_Started_Flag_Without_Marking_InitCARD2_As_Run()
    {
        PadInitAndStart();

        PadStarted().Should().Be(1u);
        State().Should().Be((false, 0u));
        BiosCardState.TryGetStarted(Reader, out var started).Should().BeTrue();
        started.Should().BeFalse();
    }

    // ---- the exception chain (priority 2 runs since #661) ---------------------------------------------

    private BiosExceptionHandlerOutcome Handle() =>
        BiosExceptionHandler.Handle(
            Reader, Writer, _interrupts, new uint[32], new BiosExceptionContext(0x80025CBC, 0x400, 0x404, 0, 0));

    [Fact]
    public void After_B0_15_And_InitCARD2_The_Priority_2_Element_Runs_And_The_Exception_Completes()
    {
        PadInitAndStart();
        InitCard2(1);
        _interrupts.SetMask(VblankBit);
        _interrupts.Raise(DeviceScheduler.VblankIrq);

        Handle().Handled.Should().BeTrue();
        _ram.Read32(ButtonDest).Should().Be(BiosPadCardIrqHandler.DisconnectedPadButtons);
    }

    [Fact]
    public void InitCARD2_Alone_Does_Not_Make_The_Chain_Stop()
    {
        InitCard2(1);
        _interrupts.SetMask(VblankBit);
        _interrupts.Raise(DeviceScheduler.VblankIrq);

        Handle().Handled.Should().BeTrue();
    }

    // ---- fail closed ----------------------------------------------------------------------------------

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

    [Fact]
    public void An_Unwritable_State_Variable_Fails_Closed()
    {
        var runtime = new BiosHleRuntime(new CapturedOutputSink(), Reader, new RejectingWriter());

        runtime.Invoke(new BiosCallIdentity(BiosCallFamily.B0, BiosHleRuntime.InitCard2Function, arguments: [1u]))
            .Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
    }

    [Fact]
    public void An_Unreadable_State_Variable_Fails_Closed()
    {
        var runtime = new BiosHleRuntime(new CapturedOutputSink(), new UnreadableReader(), Writer);

        runtime.Invoke(new BiosCallIdentity(BiosCallFamily.B0, BiosHleRuntime.InitCard2Function, arguments: [1u]))
            .Diagnostic!.Code.Should().Be("BIOS_HLE_UNSUPPORTED_STATE");
        State().Should().Be((false, 0u));
    }
}
