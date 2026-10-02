using FluentAssertions;
using PSXRecomp.Core;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Runtime;

// The priority-1 timer/VBlank chain element consuming the C0:0A flags. Issue #658.
// Runs over the real Rust-backed interrupt controller.
[Test]
public sealed class BiosTimerVblankIrqHandlerTests : IDisposable
{
    private const uint Epc = 0x80025CBC;
    private const uint HookBuffer = 0x00001000;

    private readonly PSXCoreWrapper _core = new();
    private readonly InterruptControllerMmioAdapter _interrupts;
    private readonly RecompilerGuestMemory _ram = new();

    public BiosTimerVblankIrqHandlerTests()
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

    private BiosExceptionChainContext Context(IGuestMemoryReader? reader = null) =>
        new(reader ?? Reader, Writer, _interrupts, new BiosExceptionContext(Epc, 0x400, 0x404, 1, 2));

    private void SetFlag(uint t, uint flag) =>
        new BiosHleRuntime(new CapturedOutputSink(), Reader, Writer)
            .Invoke(new BiosCallIdentity(BiosCallFamily.C0, BiosHleRuntime.ChangeClearRCntFunction, arguments: [t, flag]))
            .Status.Should().Be(BiosServiceStatus.Supported);

    private void Pend(int irq, uint mask)
    {
        _interrupts.SetMask(mask);
        _interrupts.Raise(irq);
    }

    private static BiosRootCounterEventDelivery Delivered(List<uint> log) => (_, t) =>
    {
        log.Add(t);
        return true;
    };

    // t -> IRQ: t0..2 = Timer0..2 / IRQ4..6, t3 = VBlank / IRQ0.
    public static TheoryData<uint, int> Sources => new() { { 0u, 4 }, { 1u, 5 }, { 2u, 6 }, { 3u, 0 } };

    [Theory]
    [MemberData(nameof(Sources))]
    public void A_Pending_Enabled_Source_Is_Claimed_And_Its_Events_Are_Delivered(uint t, int irq)
    {
        Pend(irq, 1u << irq);
        var log = new List<uint>();

        var result = BiosTimerVblankIrqHandler.Run(Context(), Delivered(log));

        log.Should().Equal(t);
        result.Status.Should().Be(BiosExceptionChainStatus.Completed, "flag 0 lets the chain continue");
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void A_Source_That_Is_Not_Pending_Is_Not_Claimed(uint t, int irq)
    {
        _interrupts.SetMask(1u << irq);
        SetFlag(t, 1);
        var log = new List<uint>();

        var result = BiosTimerVblankIrqHandler.Run(Context(), Delivered(log));

        log.Should().BeEmpty();
        result.Status.Should().Be(BiosExceptionChainStatus.Completed);
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void A_Masked_Source_Is_Not_Claimed_And_Stays_Pending(uint t, int irq)
    {
        Pend(irq, 0);
        SetFlag(t, 1);
        var log = new List<uint>();

        var result = BiosTimerVblankIrqHandler.Run(Context(), Delivered(log));

        log.Should().BeEmpty();
        result.Status.Should().Be(BiosExceptionChainStatus.Completed);
        (_interrupts.Status & (1u << irq)).Should().Be(1u << irq);
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void Flag_0_Neither_Acknowledges_Nor_Returns(uint t, int irq)
    {
        Pend(irq, 1u << irq);
        SetFlag(t, 0);

        var result = BiosTimerVblankIrqHandler.Run(Context(), Delivered([]));

        result.Status.Should().Be(BiosExceptionChainStatus.Completed);
        (_interrupts.Status & (1u << irq)).Should().Be(1u << irq, "flag 0 leaves the IRQ pending");
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void Flag_1_Acknowledges_Only_Its_Own_Bit_And_Returns_From_The_Exception(uint t, int irq)
    {
        const int Cdrom = 2;
        var bits = (1u << irq) | (1u << Cdrom);
        _interrupts.SetMask(bits);
        _interrupts.Raise(irq);
        _interrupts.Raise(Cdrom);
        SetFlag(t, 1);

        var result = BiosTimerVblankIrqHandler.Run(Context(), Delivered([]));

        result.Status.Should().Be(BiosExceptionChainStatus.ReturnedFromException);
        (_interrupts.Status & bits).Should().Be(1u << Cdrom, "W0C clears the source's bit and nothing else");
    }

    [Fact]
    public void A_Flag_Is_Per_Source()
    {
        Pend(0, 1u << 0);
        SetFlag(3, 0);
        SetFlag(2, 1);

        BiosTimerVblankIrqHandler.Run(Context(), Delivered([])).Status.Should().Be(BiosExceptionChainStatus.Completed);
    }

    [Fact]
    public void Elements_Run_In_Priority_Order_And_An_Early_Return_Skips_The_Rest()
    {
        _interrupts.SetMask(0xFF);
        _interrupts.Raise(0);
        _interrupts.Raise(6);
        _interrupts.Raise(4);
        SetFlag(2, 1);
        var log = new List<uint>();

        var result = BiosTimerVblankIrqHandler.Run(Context(), Delivered(log));

        log.Should().Equal(3u, 2u); // VBlank (flag 0, continues), Timer2 (flag 1, returns); Timer0 never runs
        result.Status.Should().Be(BiosExceptionChainStatus.ReturnedFromException);
        (_interrupts.Status & 0x71).Should().Be(0x11u, "VBlank and Timer0 stay pending");
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(0xFFFFFFFFu)]
    public void A_Flag_Other_Than_0_Or_1_Fails_Closed_Without_Touching_Guest_State(uint flag)
    {
        Pend(0, 1u << 0);
        Writer.TryWrite(BiosRootCounterClearPolicy.VariableAddress + 3 * 4, BitConverter.GetBytes(flag)).Should().BeTrue();

        var result = BiosTimerVblankIrqHandler.Run(Context(), Delivered([]));

        result.Status.Should().Be(BiosExceptionChainStatus.Unsupported);
        result.Detail.Should().Contain("VBlank IRQ0").And.Contain("not 0/1");
        (_interrupts.Status & 1u).Should().Be(1u);
        BiosRootCounterClearPolicy.TryGetFlag(Reader, 3, out var after).Should().BeTrue();
        after.Should().Be(flag, "the guest's value is not corrected");
    }

    [Fact]
    public void An_Unreadable_Policy_Fails_Closed()
    {
        Pend(0, 1u << 0);

        var result = BiosTimerVblankIrqHandler.Run(Context(new UnreadableReader()), Delivered([]));

        result.Status.Should().Be(BiosExceptionChainStatus.Unsupported);
        result.Detail.Should().Contain("unreadable");
        (_interrupts.Status & 1u).Should().Be(1u);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void Without_Event_Delivery_A_Claimed_Source_Fails_Closed_Naming_660(uint flag)
    {
        Pend(0, 1u << 0);
        SetFlag(3, flag);

        var result = BiosTimerVblankIrqHandler.Run(Context());

        result.Status.Should().Be(BiosExceptionChainStatus.Unsupported);
        result.Detail.Should().Contain("VBlank IRQ0").And.Contain($"flag={flag}").And.Contain("#660");
        (_interrupts.Status & 1u).Should().Be(1u, "nothing was serviced, so nothing is acknowledged");
    }

    [Fact]
    public void A_Delivery_That_Fails_Fails_Closed_Without_Acknowledging()
    {
        Pend(0, 1u << 0);
        SetFlag(3, 1);

        var result = BiosTimerVblankIrqHandler.Run(Context(), (_, _) => false);

        result.Status.Should().Be(BiosExceptionChainStatus.Unsupported);
        (_interrupts.Status & 1u).Should().Be(1u);
    }

    // ---- inside the exception handler -------------------------------------------------------

    [Fact]
    public void The_Default_Chain_Reaches_The_VBlank_Element_Persona_Stops_At()
    {
        Pend(0, 0x000D); // I_STAT=0x0001, I_MASK=0x000D as measured

        var outcome = BiosExceptionHandler.Handle(
            Reader, Writer, _interrupts, new uint[32], new BiosExceptionContext(Epc, 0x400, 0x404, 0, 0));

        outcome.Handled.Should().BeFalse();
        outcome.DiagnosticCode.Should().Be(BiosExceptionHandler.ChainUnsupportedDiagnosticCode);
        outcome.DiagnosticMessage.Should().Contain("I_STAT=0x0001").And.Contain("I_MASK=0x000D")
            .And.Contain("VBlank IRQ0").And.Contain("#660");
    }

    [Fact]
    public void A_Remaining_Non_Timer_Irq_Fails_Closed_With_A_Source_Neutral_Diagnostic()
    {
        Pend(2, 1u << 2); // CD-ROM: not a timer/VBlank source

        var outcome = BiosExceptionHandler.Handle(
            Reader, Writer, _interrupts, new uint[32], new BiosExceptionContext(Epc, 0x400, 0x404, 0, 0));

        outcome.Handled.Should().BeFalse();
        outcome.DiagnosticCode.Should().Be(BiosExceptionHandler.ChainUnsupportedDiagnosticCode);
        outcome.DiagnosticMessage.Should().Contain("I_STAT=0x0004").And.Contain("I_MASK=0x0004")
            .And.Contain("pendingEnabled=0x0004").And.Contain("priority-chain element the Runtime does not model")
            .And.NotContain("#661").And.NotContain("Pad/Card").And.NotContain("#660");
    }

    [Fact]
    public void A_Flag_1_Early_Return_Skips_The_Hook_And_Returns_Through_The_Saved_Context()
    {
        Pend(0, 1u << 0);
        SetFlag(3, 1);
        var hookRegisters = new uint[32];
        for (var i = 1; i < 32; i++)
        {
            hookRegisters[i] = 0x1000u + (uint)i;
        }

        // A registered hook must not run: the element returned from the exception itself.
        Writer.TryWrite(HookBuffer, new[] { 31, 29, 30, 16, 17, 18, 19, 20, 21, 22, 23, 28 }
            .SelectMany(r => BitConverter.GetBytes(hookRegisters[r])).ToArray()).Should().BeTrue();
        new BiosHleRuntime(new CapturedOutputSink(), Reader, Writer)
            .Invoke(new BiosCallIdentity(BiosCallFamily.B0, BiosHleRuntime.HookEntryIntFunction, arguments: [HookBuffer]))
            .Status.Should().Be(BiosServiceStatus.Supported);
        var gpr = new uint[32];
        for (var i = 1; i < 32; i++)
        {
            gpr[i] = 0x5000u + (uint)i;
        }

        var outcome = BiosExceptionHandler.Handle(
            Reader, Writer, _interrupts, gpr, new BiosExceptionContext(Epc, 0x400, 0x404, 7, 8),
            c => BiosTimerVblankIrqHandler.Run(c, Delivered([])));

        outcome.Handled.Should().BeTrue();
        outcome.NextPc.Should().Be(Epc, "ReturnFromException resumes at the interrupted PC, not the hook");
        outcome.RestoredSr.Should().Be(0x404u);
        outcome.Gpr.Should().Equal(gpr, "the hook (which would load the saved jmp_buf registers) was skipped");
        (_interrupts.Status & 1u).Should().Be(0u);
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
}
