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

    // ---- default delivery (#660): DeliverEvent(F2000000h + t, 2) over the kernel EvCB table ----

    [Theory]
    [MemberData(nameof(Sources))]
    public void Default_Delivery_With_No_EvCB_Table_Keeps_Flag_0_Pending_And_Continuing(uint t, int irq)
    {
        Pend(irq, 1u << irq);
        SetFlag(t, 0);

        var result = BiosTimerVblankIrqHandler.Run(Context());

        result.Status.Should().Be(BiosExceptionChainStatus.Completed);
        (_interrupts.Status & (1u << irq)).Should().Be(1u << irq, "flag 0 leaves the IRQ pending");
        TableWords().Should().Be(0UL, "delivery writes nothing");
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void Default_Delivery_With_No_EvCB_Table_Then_Flag_1_Acks_Only_Its_Own_Bit(uint t, int irq)
    {
        const int Cdrom = 2;
        var bits = (1u << irq) | (1u << Cdrom);
        _interrupts.SetMask(bits);
        _interrupts.Raise(irq);
        _interrupts.Raise(Cdrom);
        SetFlag(t, 1);

        var result = BiosTimerVblankIrqHandler.Run(Context());

        result.Status.Should().Be(BiosExceptionChainStatus.ReturnedFromException);
        (_interrupts.Status & bits).Should().Be(1u << Cdrom);
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void A_Matching_Enabled_Callback_EvCB_Fails_Closed_Naming_Only_The_Sources_Event(uint t, int irq)
    {
        foreach (var flag in new[] { 0u, 1u })
        {
            Pend(irq, 1u << irq);
            SetFlag(t, flag);
            WriteTable(0x00008000, 0x1C0);
            WriteEvCb(0x00008000, 0xF2000000u + t, BiosEventControlBlocks.StatusEnabled, 2, BiosEventControlBlocks.ModeCallback, 0x80010000);

            var result = BiosTimerVblankIrqHandler.Run(Context());

            result.Status.Should().Be(BiosExceptionChainStatus.Unsupported);
            result.Detail.Should().Contain($"IRQ{irq}").And.Contain($"flag={flag}")
                .And.Contain($"event 0x{0xF2000000u + t:X8},2").And.Contain("#687");
            for (var other = 0u; other <= 3; other++)
            {
                if (other != t)
                {
                    result.Detail.Should().NotContain($"0x{0xF2000000u + other:X8}");
                }
            }

            (_interrupts.Status & (1u << irq)).Should().Be(1u << irq, "nothing was serviced, so nothing is acknowledged");
            TableWords().Should().Be(0x000001C0_00008000UL, "the guest's table is not corrected");
        }
    }

    [Theory]
    [InlineData(0x00008000u, 0u)]
    [InlineData(0u, 0x1C0u)]
    public void Either_Non_Zero_Table_Word_Means_The_Table_Exists(uint address, uint size)
    {
        WriteTable(address, size);

        BiosTimerVblankIrqHandler.DeliverEvents(Context(), 3).Should().BeFalse();
    }

    [Fact]
    public void Delivery_Fails_Closed_For_An_Unreadable_Table_Or_An_Unknown_Source()
    {
        BiosTimerVblankIrqHandler.DeliverEvents(Context(new UnreadableReader()), 3).Should().BeFalse();
        BiosTimerVblankIrqHandler.DeliverEvents(Context(), 4).Should().BeFalse();
        BiosTimerVblankIrqHandler.DeliverEvents(Context(), 3).Should().BeTrue();
    }

    private void WriteEvCb(uint address, uint eventClass, uint status, uint spec, uint mode, uint function) =>
        Writer.TryWrite(address, new[] { eventClass, status, spec, mode, function }.SelectMany(BitConverter.GetBytes).ToArray()).Should().BeTrue();

    [Fact]
    public void A_Usable_EvCB_Table_Without_A_Match_Is_A_Successful_No_Op()
    {
        WriteTable(0x00008000, 0x1C0);
        WriteEvCb(0x00008000, 0xF0000009, BiosEventControlBlocks.StatusEnabled, 0x20, BiosEventControlBlocks.ModeReady, 0);
        WriteEvCb(0x0000801C, 0xF2000003, BiosEventControlBlocks.StatusDisabled, 2, BiosEventControlBlocks.ModeReady, 0);

        BiosTimerVblankIrqHandler.DeliverEvents(Context(), 3).Should().BeTrue();
    }

    [Fact]
    public void A_Matching_Enabled_Ready_Mode_EvCB_Becomes_Ready()
    {
        WriteTable(0x00008000, 0x1C0);
        WriteEvCb(0x0000801C, 0xF2000003, BiosEventControlBlocks.StatusEnabled, 2, BiosEventControlBlocks.ModeReady, 0);

        BiosTimerVblankIrqHandler.DeliverEvents(Context(), 3).Should().BeTrue();

        var status = new byte[4];
        Reader.TryRead(0x00008020, status).Should().BeTrue();
        BitConverter.ToUInt32(status).Should().Be(BiosEventControlBlocks.StatusReady);
    }

    private void WriteTable(uint address, uint size) =>
        Writer.TryWrite(BiosEventControlBlocks.TableAddressPointer,
            BitConverter.GetBytes(address).Concat(BitConverter.GetBytes(size)).ToArray()).Should().BeTrue();

    private ulong TableWords()
    {
        var table = new byte[8];
        Reader.TryRead(BiosEventControlBlocks.TableAddressPointer, table).Should().BeTrue();
        return BitConverter.ToUInt64(table);
    }

    [Fact]
    public void A_Delivery_That_Fails_Fails_Closed_Without_Acknowledging()
    {
        Pend(0, 1u << 0);
        SetFlag(3, 1);

        var result = BiosTimerVblankIrqHandler.Run(Context(), (_, _) => false);

        result.Status.Should().Be(BiosExceptionChainStatus.Unsupported);
        result.Detail.Should().Contain("a custom delivery callback may also fail")
            .And.NotContain("is unreadable or exists");
        (_interrupts.Status & 1u).Should().Be(1u);
    }

    // ---- inside the exception handler -------------------------------------------------------

    [Fact]
    public void The_Default_Chain_Delivers_The_Persona_VBlank_And_Continues_With_It_Pending_Past_Priority_1()
    {
        Pend(0, 0x000D); // I_STAT=0x0001, I_MASK=0x000D as measured; C0:0A t=3 flag=0, no EvCB table

        var outcome = BiosExceptionHandler.Handle(
            Reader, Writer, _interrupts, new uint[32], new BiosExceptionContext(Epc, 0x400, 0x404, 0, 0));

        // Modelled delivery was a successful no-op with flag 0: no acknowledge, so IRQ0 is still pending past priority 1.
        // Priority 2 is empty and DefInt (#690) does not acknowledge, so the chain runs to the end (no hook here: the default Exit).
        outcome.Handled.Should().BeTrue();
        outcome.DiagnosticCode.Should().BeNull();
        outcome.NextPc.Should().Be(Epc);
        (_interrupts.Status & 1u).Should().Be(1u);
    }

    [Fact]
    public void A_Remaining_Non_Timer_Irq_Fails_Closed_With_A_Source_Neutral_Diagnostic()
    {
        Pend(3, 1u << 3); // DMA: not a timer/VBlank source (IRQ2 is DefInt's since #697)

        var outcome = BiosExceptionHandler.Handle(
            Reader, Writer, _interrupts, new uint[32], new BiosExceptionContext(Epc, 0x400, 0x404, 0, 0));

        outcome.Handled.Should().BeFalse();
        outcome.DiagnosticCode.Should().Be(BiosExceptionHandler.ChainUnsupportedDiagnosticCode);
        outcome.DiagnosticMessage.Should().Contain("I_STAT=0x0008").And.Contain("I_MASK=0x0008")
            .And.Contain("pendingEnabled=0x0008").And.Contain("priority-chain element the Runtime does not model")
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
