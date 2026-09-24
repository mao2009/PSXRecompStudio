using FluentAssertions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;
using PSXRecomp.Tests.Runtime;

namespace PSXRecomp.Tests.Execution;

[Test]
public sealed class ExecutionOrchestratorTests
{
    private const uint Entry = 0x80001000u;
    private const byte OriOpcodeField = 0x0D;
    private const byte AdduiOpcodeField = 0x09;

    // --- The orchestration loop over the pure-managed IR engine ---------------

    [Fact]
    public void StraightLineRun_ThatLeavesTheProgram_Completes()
    {
        using var engine = new RecompiledIrTitleExecutionEngine(Lower(Entry, MipsEncoding.Nop));

        var result = new ExecutionOrchestrator().Execute(
            engine, ExitHandoff(), Request(Entry, outer: 1, segment: 8));

        result.State.Should().Be(TitleExecutionState.Completed);
        result.SegmentsRetired.Should().Be(1);
        result.FinalSnapshot!.PC.Should().Be(Entry + 4);
        result.DiagnosticCode.Should().BeNull();
    }

    [Fact]
    public void ContinueAt_ResumesFromTheChosenPc_AndFoldsTheReturnValueIntoV0()
    {
        // Two single-block regions: entry, then a second region at entry+4.
        using var engine = new RecompiledIrTitleExecutionEngine(Lower(
            Entry,
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.S0, rs: 0, immediate: 0x11),
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.S0, rs: 0, immediate: 0x33)));

        var handoff = new ScriptedHandoff(
            TitleExecutionHandoffResult.ContinueAt(Entry + 4, returnValue: 0x99),
            TitleExecutionHandoffResult.Exit());

        var result = new ExecutionOrchestrator().Execute(
            engine, handoff, Request(Entry, outer: 4, segment: 8));

        result.State.Should().Be(TitleExecutionState.Completed);
        result.SegmentsRetired.Should().Be(2);
        result.FinalSnapshot!.Gpr[(int)R3000aRegister.V0].Should().Be(0x99);
        result.FinalSnapshot.Gpr[(int)R3000aRegister.S0].Should().Be(0x33);
    }

    [Fact]
    public void InnerBudget_SplitsOneLogicalRunAcrossSegments_AndStillCompletes()
    {
        using var engine = new RecompiledIrTitleExecutionEngine(Lower(
            Entry,
            MipsEncoding.I(AdduiOpcodeField, rt: (byte)R3000aRegister.T0, rs: 0, immediate: 3),
            MipsEncoding.I(AdduiOpcodeField, rt: (byte)R3000aRegister.T0, rs: (byte)R3000aRegister.T0, immediate: 0xFFFF),
            MipsEncoding.Branch(0x05, (byte)R3000aRegister.T0, 0, Entry + 8, Entry + 4),
            MipsEncoding.Nop,
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.S1, rs: 0, immediate: 0x77)));

        var result = new ExecutionOrchestrator().Execute(
            engine, ExitHandoff(), Request(Entry, outer: 8, segment: 2));

        result.State.Should().Be(TitleExecutionState.Completed);
        result.SegmentsRetired.Should().BeGreaterThanOrEqualTo(2);
        result.FinalSnapshot!.Gpr[(int)R3000aRegister.T0].Should().Be(0);
        result.FinalSnapshot.Gpr[(int)R3000aRegister.S1].Should().Be(0x77);
    }

    [Fact]
    public void OuterBudget_Exhausted_StopsACutOffGuest()
    {
        // Jump to self: the guest is always running, so only the outer budget
        // can stop it.
        using var engine = new RecompiledIrTitleExecutionEngine(Lower(
            Entry, MipsEncoding.Jump(Entry), MipsEncoding.Nop));

        var result = new ExecutionOrchestrator().Execute(
            engine, ExitHandoff(), Request(Entry, outer: 3, segment: 128));

        result.State.Should().Be(TitleExecutionState.BudgetExhausted);
        result.SegmentsRetired.Should().Be(3);
        result.DiagnosticCode.Should().Be("OUTER_BUDGET_EXHAUSTED");
        result.FinalSnapshot.Should().NotBeNull("the final cut-off segment still produced a snapshot");
    }

    [Fact]
    public void ZeroOuterBudget_ReportsBudgetExhausted_BeforeAnySegmentRuns()
    {
        using var engine = new RecompiledIrTitleExecutionEngine(Lower(Entry, MipsEncoding.Nop));

        var result = new ExecutionOrchestrator().Execute(
            engine, ExitHandoff(), Request(Entry, outer: 0, segment: 8));

        result.State.Should().Be(TitleExecutionState.BudgetExhausted);
        result.SegmentsRetired.Should().Be(0);
        result.FinalSnapshot.Should().BeNull();
    }

    [Fact]
    public void UnresolvedTransfer_WithNoHandoff_IsReportedAsUnsupported()
    {
        using var engine = new RecompiledIrTitleExecutionEngine(Lower(Entry, MipsEncoding.Nop));

        var result = new ExecutionOrchestrator().Execute(
            engine, handoff: null, Request(Entry, outer: 1, segment: 8));

        result.State.Should().Be(TitleExecutionState.UnsupportedTransfer);
        result.DiagnosticCode.Should().Be("UNRESOLVED_TRANSFER");
        result.DiagnosticMessage.Should().Contain("#249");
        result.FinalSnapshot!.PC.Should().Be(Entry + 4);
    }

    [Fact]
    public void UnresolvedTransfer_ThatTheHandoffDeclines_IsReportedAsUnsupported()
    {
        using var engine = new RecompiledIrTitleExecutionEngine(Lower(Entry, MipsEncoding.Nop));

        var result = new ExecutionOrchestrator().Execute(
            engine, new FunctionHandoff(_ => null), Request(Entry, outer: 1, segment: 8));

        result.State.Should().Be(TitleExecutionState.UnsupportedTransfer);
        result.DiagnosticCode.Should().Be("UNRESOLVED_TRANSFER");
    }

    [Fact]
    public void ContinueAt_WithAnUntranslatableTarget_IsInvalidState()
    {
        using var engine = new RecompiledIrTitleExecutionEngine(Lower(Entry, MipsEncoding.Nop));

        var result = new ExecutionOrchestrator().Execute(
            engine,
            new ScriptedHandoff(TitleExecutionHandoffResult.ContinueAt(0xC0000000u)),
            Request(Entry, outer: 1, segment: 8));

        result.State.Should().Be(TitleExecutionState.InvalidState);
        result.DiagnosticCode.Should().Be("INVALID_CONTINUATION_TARGET");
        result.SegmentsRetired.Should().Be(1);
    }

    [Fact]
    public void AnEngineMechanismFailure_IsARuntimeFailure_NotAContractViolation()
    {
        using var engine = new FailingEngine();

        var result = new ExecutionOrchestrator().Execute(
            engine, ExitHandoff(), Request(Entry, outer: 1, segment: 8));

        result.State.Should().Be(TitleExecutionState.RuntimeFailure);
        result.DiagnosticCode.Should().Be("FAKE_CRASH");
        result.SegmentsRetired.Should().Be(1);
        result.FinalSnapshot.Should().BeNull();
    }

    [Fact]
    public void ACompletedSegmentWithoutASnapshot_IsInvalidState()
    {
        using var engine = new NoSnapshotEngine();

        var result = new ExecutionOrchestrator().Execute(
            engine, ExitHandoff(), Request(Entry, outer: 1, segment: 8));

        result.State.Should().Be(TitleExecutionState.InvalidState);
        result.DiagnosticCode.Should().Be("MISSING_SNAPSHOT");
        result.SegmentsRetired.Should().Be(1);
        result.FinalSnapshot.Should().BeNull();
    }

    // --- End-to-end over the native interpreter (in-band BIOS dispatch) -------

    [Fact]
    public void Interpreter_BiosDispatchInsideTheLoop_ThenHandoffCompletes()
    {
        // jal B0-vector (puts), nop, tail sets S1. The Runtime's seeded patched
        // target returns through $ra, so the tail runs and the final unresolved
        // PC is handed to the orchestrator, which exits.
        const uint stringAddress = 0x00000400u;
        var words = new uint[]
        {
            MipsEncoding.JumpAndLink(BiosJumpTables.B0VectorAddress),
            MipsEncoding.Nop,
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.S1, rs: 0, immediate: ReturnedMarker),
        };

        var sink = new CapturedOutputSink();
        using var engine = new InterpreterTitleExecutionEngine(
            words,
            Entry,
            (reader, writer) => new BiosHleRuntime(sink, reader, writer));

        var gpr = new uint[TitleExecutionRequest.GprCount];
        gpr[(int)R3000aRegister.T1] = BiosHleRuntime.PutsAliasFunction;
        gpr[(int)R3000aRegister.A0] = stringAddress;

        var result = new ExecutionOrchestrator().Execute(
            engine, ExitHandoff(), Request(Entry, outer: 8, segment: 64, gpr,
                StringBytes(stringAddress, "hi").ToArray()));

        result.State.Should().Be(TitleExecutionState.Completed);
        result.SegmentsRetired.Should().Be(1);
        result.FinalSnapshot!.Gpr[(int)R3000aRegister.S1].Should().Be(ReturnedMarker);
        sink.Bytes.Should().BeEquivalentTo("hi"u8.ToArray(), static o => o.WithStrictOrdering());
    }

    [Fact]
    public void Interpreter_LiveGuestProgramReachesDmaScratchpadAndRamMirror()
    {
        // Issue #386: the Production backend (InterpreterTitleExecutionEngine over
        // the native core, driven by the orchestrator) must make the DMA/timer/
        // interrupt MMIO layer, the scratchpad and the low 8 MiB RAM mirror
        // reachable from a real guest program — not only from unit tests. This one
        // guest run round-trips a value through all three surfaces the issue
        // names, through real memory instructions:
        //
        //   SW/LW to DMA MADR 0x1F801080   -> the attached DMA controller
        //   SW via mirror 0x00200200, LW from 0x00000200 -> the 2 MiB alias
        //   SW/LW to scratchpad 0x1F800000 -> the private 1 KiB SRAM
        //
        // If the native memory-routing work were missing, the DMA read would see
        // a flat hw_regs word (state that would not round-trip a controller
        // register with side effects) and the mirror/scratchpad reads would be
        // unmapped zeros.
        var words = new uint[]
        {
            MipsEncoding.I(LuiOpcodeField, rt: (byte)R3000aRegister.T0, rs: 0, immediate: 0xDEAD),
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T0, rs: (byte)R3000aRegister.T0, immediate: 0xBEEF),
            MipsEncoding.I(LuiOpcodeField, rt: (byte)R3000aRegister.T1, rs: 0, immediate: 0x1F80),
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T1, rs: (byte)R3000aRegister.T1, immediate: 0x1080),
            MipsEncoding.Load(R3000aOpcode.Sw, rt: (byte)R3000aRegister.T0, baseRegister: (byte)R3000aRegister.T1, offset: 0),
            MipsEncoding.Load(R3000aOpcode.Lw, rt: (byte)R3000aRegister.S1, baseRegister: (byte)R3000aRegister.T1, offset: 0),
            MipsEncoding.Nop,
            MipsEncoding.I(LuiOpcodeField, rt: (byte)R3000aRegister.T2, rs: 0, immediate: 0x0020),
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T2, rs: (byte)R3000aRegister.T2, immediate: 0x0200),
            MipsEncoding.Load(R3000aOpcode.Sw, rt: (byte)R3000aRegister.T0, baseRegister: (byte)R3000aRegister.T2, offset: 0),
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T2, rs: 0, immediate: 0x0200),
            MipsEncoding.Load(R3000aOpcode.Lw, rt: (byte)R3000aRegister.S2, baseRegister: (byte)R3000aRegister.T2, offset: 0),
            MipsEncoding.Nop,
            MipsEncoding.I(LuiOpcodeField, rt: (byte)R3000aRegister.T3, rs: 0, immediate: 0x1F80),
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T3, rs: (byte)R3000aRegister.T3, immediate: 0x0000),
            MipsEncoding.Load(R3000aOpcode.Sw, rt: (byte)R3000aRegister.T0, baseRegister: (byte)R3000aRegister.T3, offset: 0),
            MipsEncoding.Load(R3000aOpcode.Lw, rt: (byte)R3000aRegister.S3, baseRegister: (byte)R3000aRegister.T3, offset: 0),
            MipsEncoding.Nop,
        };

        using var engine = new InterpreterTitleExecutionEngine(words, Entry);

        var result = new ExecutionOrchestrator().Execute(
            engine, ExitHandoff(), Request(Entry, outer: 8, segment: 64));

        result.State.Should().Be(TitleExecutionState.Completed, Describe(result));
        var snapshot = result.FinalSnapshot!;
        snapshot.Gpr[(int)R3000aRegister.S1].Should().Be(0xDEADBEEFu,
            "a guest SW/LW pair must round-trip through the DMA MMIO layer reachable from the real execution path");
        snapshot.Gpr[(int)R3000aRegister.S2].Should().Be(0xDEADBEEFu,
            "a guest SW through the 0x00200000 mirror must alias the low 2 MiB RAM");
        snapshot.Gpr[(int)R3000aRegister.S3].Should().Be(0xDEADBEEFu,
            "a guest SW/LW pair must round-trip through the scratchpad");
    }

    [Fact]
    public void Interpreter_LiveGuestObservesTimer2Irq_AtTheCycleTheSchedulerRaisedIt()
    {
        // Issue #442: nothing but the production path advances devices here. The
        // guest arms Timer 2 (target 100, IRQ on target) through real stores,
        // then polls I_STAT through real loads. IRQ6 can only appear if
        // InterpreterTitleExecutionEngine feeds retired instructions into the
        // DeviceScheduler, which ticks the Rust timer and raises the line.
        //
        // One cycle per instruction: the mode SW's own advance brings the counter
        // to 1, and each 5-instruction poll adds 5, so the LW of poll k sees
        // 1 + 5(k-1) cycles. The first k with >= 100 is 21, which the delay-slot
        // counter in $s0 records.
        const ushort target = 100;
        var result = RunPollingProgram(
            setup:
            [
                MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T2, rs: 0, immediate: target),
                MipsEncoding.Load(R3000aOpcode.Sw, rt: (byte)R3000aRegister.T2, baseRegister: (byte)R3000aRegister.T1, offset: 0x1128),
                MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T2, rs: 0, immediate: 0x0010),
                MipsEncoding.Load(R3000aOpcode.Sw, rt: (byte)R3000aRegister.T2, baseRegister: (byte)R3000aRegister.T1, offset: 0x1124),
            ],
            iStatBit: 1 << 6,
            segment: 1024);

        result.State.Should().Be(TitleExecutionState.Completed, Describe(result));
        result.FinalSnapshot!.Gpr[(int)R3000aRegister.T3].Should().Be(1u << 6, "only Timer 2's IRQ6 is latched");
        result.FinalSnapshot.Gpr[(int)R3000aRegister.S0].Should().Be(21u);
    }

    [Fact]
    public void Interpreter_LiveGuestObservesVblankIrq0_AfterOneVblankInterval()
    {
        // Issue #442: VBlank (IRQ0) fires during a real run, at the scheduler's
        // fixed interval. The LUI costs one cycle and each poll five, so the
        // first poll that sees IRQ0 is k = ceil((interval - 1) / 5) + 1.
        const uint pollLength = 5;
        var expectedPolls = ((DeviceScheduler.VblankIntervalCycles - 1 + pollLength - 1) / pollLength) + 1;

        var result = RunPollingProgram(setup: [], iStatBit: 1 << 0, segment: 1_000_000);

        result.State.Should().Be(TitleExecutionState.Completed, Describe(result));
        result.FinalSnapshot!.Gpr[(int)R3000aRegister.T3].Should().Be(1u, "only VBlank's IRQ0 is latched");
        result.FinalSnapshot.Gpr[(int)R3000aRegister.S0].Should().Be(expectedPolls);
    }

    [Fact]
    public void Interpreter_VblankInterrupt_RunsTheGuestHandler_ThenResumesAfterRfe()
    {
        // Issue #499: with I_MASK bit 0 and SR IEc/IM2 set, the scheduled IRQ0 is
        // taken as an INT exception. The guest's handler at 0x80000080 must run,
        // acknowledge I_STAT, return to EPC through JR + RFE, and the interrupted
        // wait loop must then finish. PR #493 could not continue here and held the
        // interrupt input low instead, so the handler never ran.
        var result = RunInterruptProgram(
            InterruptHandler(acknowledge: true),
            EnableInterruptsThenWaitForHandler(sr: SrIm2 | SrIec),
            segment: DeviceScheduler.VblankIntervalCycles + 10_000);

        result.State.Should().Be(TitleExecutionState.Completed, Describe(result));
        result.DiagnosticCode.Should().BeNull();
        var gpr = result.FinalSnapshot!.Gpr;
        gpr[(int)R3000aRegister.S1].Should().Be(1u, "the handler ran exactly once");
        ((gpr[(int)R3000aRegister.S4] >> 2) & 0x1Fu).Should().Be(0u, "the handler saw CAUSE.ExcCode = INT");
        (gpr[(int)R3000aRegister.S4] & SrIm2).Should().Be(SrIm2, "the handler saw CAUSE.IP2 pending");
        gpr[(int)R3000aRegister.K1].Should().Be(Entry + (WaitLoopIndex * 4u), "EPC is the interrupted BEQ");
        gpr[(int)R3000aRegister.S2].Should().Be(ReturnedMarker, "normal code after the wait loop ran");
        (gpr[(int)R3000aRegister.S3] & (SrIm2 | SrIec)).Should().Be(SrIm2 | SrIec, "RFE restored IEc");
    }

    [Fact]
    public void Interpreter_VblankInterruptNeverAcknowledged_RetakesTheInterrupt_UntilTheBudgetEnds()
    {
        // A handler that returns without clearing I_STAT leaves the line pending,
        // so RFE re-enables IEc and the CPU takes the interrupt again before the
        // interrupted instruction runs. That is the hardware behavior: the guest
        // never progresses, the engine never fails the run, and only the budget
        // stops it.
        var result = RunInterruptProgram(
            InterruptHandler(acknowledge: false),
            EnableInterruptsThenWaitForHandler(sr: SrIm2 | SrIec),
            segment: DeviceScheduler.VblankIntervalCycles + 10_000);

        result.State.Should().Be(TitleExecutionState.BudgetExhausted, Describe(result));
        result.DiagnosticCode.Should().Be("OUTER_BUDGET_EXHAUSTED");
        result.FinalSnapshot!.Gpr[(int)R3000aRegister.S1].Should().BeGreaterThan(1u, "the handler was re-entered");
        result.FinalSnapshot.Gpr[(int)R3000aRegister.S2].Should().Be(0u, "the interrupted code never resumed");
    }

    [Fact]
    public void Interpreter_VblankWithCpuInterruptsDisabled_LatchesInIStat_WithoutEnteringTheHandler()
    {
        // I_MASK and SR.IM2 are set but SR.IEc is not: IRQ0 must latch in I_STAT
        // where the guest polls it, and the installed handler must never run.
        const byte andiOpcodeField = 0x0C;
        const byte beqOpcodeField = 0x04;
        var main = new List<uint>(EnableInterrupts(sr: SrIm2));
        var poll = Entry + (uint)main.Count * 4u;
        main.AddRange(
        [
            MipsEncoding.Load(R3000aOpcode.Lw, rt: (byte)R3000aRegister.T3, baseRegister: (byte)R3000aRegister.T1, offset: 0x1070),
            MipsEncoding.Nop,
            MipsEncoding.I(andiOpcodeField, rt: (byte)R3000aRegister.T4, rs: (byte)R3000aRegister.T3, immediate: 1),
            MipsEncoding.Branch(beqOpcodeField, (byte)R3000aRegister.T4, 0, poll + 12u, poll),
            MipsEncoding.Nop,
        ]);

        var result = RunInterruptProgram(
            InterruptHandler(acknowledge: true), [.. main], segment: DeviceScheduler.VblankIntervalCycles + 10_000);

        result.State.Should().Be(TitleExecutionState.Completed, Describe(result));
        result.FinalSnapshot!.Gpr[(int)R3000aRegister.T3].Should().Be(1u, "VBlank's IRQ0 is latched");
        result.FinalSnapshot.Gpr[(int)R3000aRegister.S1].Should().Be(0u, "the handler never ran");
    }

    [Theory]
    [InlineData("SYSCALL", new uint[] { 0x0000000Cu })]
    [InlineData("BREAK", new uint[] { 0x0000000Du })]
    [InlineData("RI", new uint[] { 0xFC000000u })] // reserved primary opcode 0x3F
    [InlineData("AdEL", new uint[] { 0x8C080001u })] // LW $t0, 1($zero): misaligned
    [InlineData("Ov", new uint[] { 0x3C087FFFu, 0x01084020u })] // LUI $t0, 0x7FFF; ADD $t0, $t0, $t0
    // Software interrupt: SR = IM0 | IEc, CAUSE.IP0 set by MTC0. It is an INT
    // exception, but not one the hardware interrupt line raised.
    [InlineData("software INT", new uint[] { 0x340A0102u, 0x408A6000u, 0x340A0100u, 0x408A6800u, 0x00000000u })]
    public void Interpreter_NonHardwareInterruptException_StillFails_EvenWithAHandlerInstalled(string kind, uint[] words)
    {
        // Issue #499 continues only hardware interrupts. Every other exception —
        // including a software interrupt — must still end the run as
        // RuntimeFailure/CPU_EXCEPTION, never reach the handler, and never be
        // classified Completed, even though a handler is installed at the vector.
        var result = RunInterruptProgram(InterruptHandler(acknowledge: true), words, segment: 64);

        result.State.Should().Be(TitleExecutionState.RuntimeFailure, $"{kind}: {Describe(result)}");
        result.DiagnosticCode.Should().Be("CPU_EXCEPTION", kind);
        result.FinalSnapshot!.Termination.Should().Be(RecompilerIrTerminationReason.Exception, kind);
        result.FinalSnapshot.PC.Should().Be(0x80000080u, kind);
        result.FinalSnapshot.Gpr[(int)R3000aRegister.S1].Should().Be(0u, $"{kind}: the handler never ran");
    }

    private const uint ExceptionVector = 0x80000080u;
    private const ushort SrIec = 0x0002; // SR bit 1 (docs/cpu/cop0.md)
    private const ushort SrIm2 = 0x0400; // SR bit 10; also CAUSE.IP2's bit
    private const uint WaitLoopIndex = 5;

    /// <summary>
    /// The guest interrupt handler placed at the exception vector: reads CAUSE
    /// into <c>$s4</c>, optionally acknowledges every I_STAT bit, counts itself
    /// in <c>$s1</c>, then returns to EPC (left in <c>$k1</c>) with RFE in the
    /// JR delay slot.
    /// </summary>
    private static uint[] InterruptHandler(bool acknowledge) =>
    [
        MipsEncoding.I(LuiOpcodeField, rt: (byte)R3000aRegister.K0, rs: 0, immediate: 0x1F80),
        Mfc0(R3000aRegister.S4, 13), // CAUSE
        acknowledge
            ? MipsEncoding.Load(R3000aOpcode.Sw, rt: 0, baseRegister: (byte)R3000aRegister.K0, offset: 0x1070) // I_STAT &= 0
            : MipsEncoding.Nop,
        MipsEncoding.I(AdduiOpcodeField, rt: (byte)R3000aRegister.S1, rs: (byte)R3000aRegister.S1, immediate: 1),
        Mfc0(R3000aRegister.K1, 14), // EPC
        MipsEncoding.Nop,
        MipsEncoding.JumpRegister((byte)R3000aRegister.K1),
        0x42000010u, // RFE
    ];

    /// <summary><c>$t1 = 0x1F800000; I_MASK = IRQ0; SR = sr</c> — five instructions.</summary>
    private static uint[] EnableInterrupts(ushort sr) =>
    [
        MipsEncoding.I(LuiOpcodeField, rt: (byte)R3000aRegister.T1, rs: 0, immediate: 0x1F80),
        MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T2, rs: 0, immediate: 0x0001),
        MipsEncoding.Load(R3000aOpcode.Sw, rt: (byte)R3000aRegister.T2, baseRegister: (byte)R3000aRegister.T1, offset: 0x1074),
        MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T2, rs: 0, immediate: sr),
        Mtc0(R3000aRegister.T2, 12), // SR
    ];

    /// <summary>
    /// <see cref="EnableInterrupts"/>, then spin on <c>$s1 == 0</c> until the
    /// handler has run, then set <c>$s2</c> and read SR into <c>$s3</c>.
    /// </summary>
    private static uint[] EnableInterruptsThenWaitForHandler(ushort sr)
    {
        const byte beqOpcodeField = 0x04;
        var wait = Entry + (WaitLoopIndex * 4u);
        return
        [
            .. EnableInterrupts(sr),
            MipsEncoding.Branch(beqOpcodeField, (byte)R3000aRegister.S1, 0, wait, wait),
            MipsEncoding.Nop,
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.S2, rs: 0, immediate: ReturnedMarker),
            Mfc0(R3000aRegister.S3, 12), // SR
            MipsEncoding.Nop,
        ];
    }

    /// <summary>
    /// Runs <paramref name="main"/> at <see cref="Entry"/> on the production
    /// interpreter through the orchestrator, with <paramref name="handler"/>
    /// seeded at the exception vector as initial memory (the guest has no BIOS
    /// to install one). A guest that runs off its program image completes.
    /// </summary>
    private static TitleExecutionResult RunInterruptProgram(uint[] handler, uint[] main, uint segment)
    {
        var memory = new List<RecompilerInitialMemoryItem>();
        for (var i = 0; i < handler.Length; i++)
        {
            for (var b = 0; b < 4; b++)
            {
                memory.Add(new RecompilerInitialMemoryItem(
                    ExceptionVector + (uint)(i * 4 + b), (byte)(handler[i] >> (8 * b))));
            }
        }

        using var engine = new InterpreterTitleExecutionEngine(main, Entry);
        return new ExecutionOrchestrator().Execute(
            engine, ExitHandoff(), Request(Entry, outer: 1, segment, initialMemory: memory));
    }

    private static uint Mfc0(R3000aRegister rt, byte rd) => 0x40000000u | ((uint)rt << 16) | ((uint)rd << 11);

    private static uint Mtc0(R3000aRegister rt, byte rd) => 0x40800000u | ((uint)rt << 16) | ((uint)rd << 11);

    /// <summary>
    /// Runs <c>$t1 = 0x1F800000; setup; do { $t3 = I_STAT; $s0++ } while (!($t3 &amp; bit))</c>
    /// on the production interpreter through the orchestrator; the guest then
    /// runs off its program image and completes.
    /// </summary>
    private static TitleExecutionResult RunPollingProgram(uint[] setup, ushort iStatBit, uint segment)
    {
        const byte andiOpcodeField = 0x0C;
        const byte beqOpcodeField = 0x04;

        var words = new List<uint>
        {
            MipsEncoding.I(LuiOpcodeField, rt: (byte)R3000aRegister.T1, rs: 0, immediate: 0x1F80),
        };
        words.AddRange(setup);
        var poll = Entry + (uint)words.Count * 4u;
        words.AddRange(
        [
            MipsEncoding.Load(R3000aOpcode.Lw, rt: (byte)R3000aRegister.T3, baseRegister: (byte)R3000aRegister.T1, offset: 0x1070),
            MipsEncoding.Nop,
            MipsEncoding.I(andiOpcodeField, rt: (byte)R3000aRegister.T4, rs: (byte)R3000aRegister.T3, immediate: iStatBit),
            MipsEncoding.Branch(beqOpcodeField, (byte)R3000aRegister.T4, 0, poll + 12u, poll),
            MipsEncoding.I(AdduiOpcodeField, rt: (byte)R3000aRegister.S0, rs: (byte)R3000aRegister.S0, immediate: 1),
        ]);

        using var engine = new InterpreterTitleExecutionEngine(words, Entry);
        return new ExecutionOrchestrator().Execute(engine, ExitHandoff(), Request(Entry, outer: 1, segment));
    }

    [Fact]
    public void Interpreter_UnsupportedBiosService_IsARuntimeFailure()
    {
        var words = new uint[]
        {
            MipsEncoding.JumpAndLink(BiosJumpTables.A0VectorAddress),
            MipsEncoding.Nop,
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.S1, rs: 0, immediate: ReturnedMarker),
        };

        var sink = new CapturedOutputSink();
        using var engine = new InterpreterTitleExecutionEngine(
            words,
            Entry,
            (reader, writer) => new BiosHleRuntime(sink, reader, writer));

        var gpr = new uint[TitleExecutionRequest.GprCount];
        gpr[(int)R3000aRegister.T1] = UnregisteredA0Function;

        var result = new ExecutionOrchestrator().Execute(
            engine, ExitHandoff(), Request(Entry, outer: 4, segment: 64, gpr));

        result.State.Should().Be(TitleExecutionState.RuntimeFailure);
        result.DiagnosticCode.Should().Be("BIOS_HLE_UNSUPPORTED_CALL");
        result.DiagnosticMessage.Should().Contain($"A0:{UnregisteredA0Function:X2}");
        result.FinalSnapshot!.Termination.Should().Be(RecompilerIrTerminationReason.UnresolvedIndirectFlow);
    }

    [Theory]
    [InlineData(0x4A180001u)] // COP2/GTE RTPS
    [InlineData(0xC8010000u)] // LWC2 $1, 0($0)
    [InlineData(0xE8010000u)] // SWC2 $1, 0($0)
    public void Interpreter_ACop2FamilyFault_IsARuntimeFailure_NotCompleted(uint cop2Word)
    {
        // Issue #377. The GTE is unimplemented, so the interpreter raises CpU. The
        // fault must reach the orchestrator as Exception -> RuntimeFailure. Before
        // the fix it reported Success: the PC had simply left the program (it was
        // parked on the exception vector), the orchestrator asked the handoff, and
        // this exit handoff classified a faulted title as Completed.
        using var engine = new InterpreterTitleExecutionEngine(
            [MipsEncoding.Nop, cop2Word], Entry);

        var result = new ExecutionOrchestrator().Execute(
            engine, ExitHandoff(), Request(Entry, outer: 4, segment: 64));

        result.State.Should().Be(TitleExecutionState.RuntimeFailure);
        result.DiagnosticCode.Should().Be("CPU_EXCEPTION");
        result.FinalSnapshot!.Termination.Should().Be(RecompilerIrTerminationReason.Exception);
    }

    // --- Engine image validation ----------------------------------------------

    [Fact]
    public void EngineConstructor_ProgramImageOverflowing32Bits_Throws()
    {
        var act = () =>
        {
            using var engine = new InterpreterTitleExecutionEngine(
                new uint[] { 0u, 0u, 0u, 0u }, 0xFFFFFFF8u);
        };

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("overflows");
    }

    [Fact]
    public void EngineConstructor_ProgramSpanCrossingTranslationBoundary_Throws()
    {
        // The span [0x7FFFFF00..0x80000100) straddles KUSEG/KSEG0: the start
        // translates to itself and the last byte is masked into low RAM, so the
        // image would be written to non-contiguous physical addresses.
        var act = () =>
        {
            using var engine = new InterpreterTitleExecutionEngine(
                Enumerable.Repeat(0u, 128).ToArray(), 0x7FFFFF00u);
        };

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("contiguous");
    }

    [Fact]
    public void EngineConstructor_NontranslatableProgramStart_Throws()
    {
        var act = () =>
        {
            using var engine = new InterpreterTitleExecutionEngine(
                new uint[] { 0u, 0u }, 0xC0000000u);
        };

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("translatable");
    }

    [Fact]
    public void EngineConstructor_OrdinarySizedProgram_DoesNotThrow()
    {
        var act = () =>
        {
            using var engine = new InterpreterTitleExecutionEngine(
                Enumerable.Repeat(0u, 256).ToArray(), 0x80010000u);
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void EngineConstructor_InstructionCountMultiplicationWouldWrapInUint_Throws()
    {
        // Count = 0x4000_0001: (uint)Count * 4u wraps to 4 in 32-bit arithmetic, so a
        // constructor that multiplies in uint before widening to ulong sees a bogus
        // 4-byte program instead of the real ~4 GiB one and lets it through. The real
        // ulong length overflows the 32-bit address space from loadAddress 0, so a
        // correct constructor must reject it as an overflow. The list never indexes an
        // element; only Count is read before this must throw.
        var act = () =>
        {
            using var engine = new InterpreterTitleExecutionEngine(
                new HugeCountInstructions(0x4000_0001), 0u);
        };

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("overflows");
    }

    private sealed class HugeCountInstructions(int count) : IReadOnlyList<uint>
    {
        public int Count { get; } = count;
        public uint this[int index] => throw new NotSupportedException("Boundary check must not index the image.");
        public IEnumerator<uint> GetEnumerator() => throw new NotSupportedException("Boundary check must not enumerate the image.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // --- End-to-end over the generated host (gcc, RAM continuity) -------------

    [Fact]
    public void Host_GuestRamSurvivesAcrossSegmentProcessRestarts()
    {
        // Segment 1 writes 0xAA to scratch. A loop then parks execution between
        // segments (block budget 3), and the final read of scratch lands in S1.
        // If the engine lost RAM between process restarts, the read would see 0.
        // The NOP after the LW fills its load-delay slot: $t4 is not visible to
        // the immediately following instruction on real MIPS, so without it the
        // OR would read $t4's pre-load (stale) value instead of the loaded one.
        const ushort scratch = 0x0080;
        var words = new uint[]
        {
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T1, rs: 0, immediate: scratch),
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T2, rs: 0, immediate: 0xAA),
            MipsEncoding.Load(R3000aOpcode.Sw, rt: (byte)R3000aRegister.T2, baseRegister: (byte)R3000aRegister.T1, offset: 0),
            MipsEncoding.I(AdduiOpcodeField, rt: (byte)R3000aRegister.T3, rs: 0, immediate: 5),
            MipsEncoding.I(AdduiOpcodeField, rt: (byte)R3000aRegister.T3, rs: (byte)R3000aRegister.T3, immediate: 0xFFFF),
            MipsEncoding.Branch(0x05, (byte)R3000aRegister.T3, 0, Entry + 20, Entry + 16),
            MipsEncoding.Nop,
            MipsEncoding.Load(R3000aOpcode.Lw, rt: (byte)R3000aRegister.T4, baseRegister: (byte)R3000aRegister.T1, offset: 0),
            MipsEncoding.Nop,
            MipsEncoding.R(0x25, rd: (byte)R3000aRegister.S1, rs: (byte)R3000aRegister.T4, rt: 0, shamt: 0),
        };

        var fixture = new RecompilerDifferentialFixture(
            name: "execution-orchestrator-host-continuity",
            encodedInstructions: words,
            entryPc: Entry,
            stepBudget: 128,
            memoryWindow: [],
            referenceStepBudget: 32);

        var sink = new CapturedOutputSink();
        using var engine = new HostTitleExecutionEngine(
            fixture,
            (reader, writer) => new BiosHleRuntime(sink, reader, writer));

        var result = new ExecutionOrchestrator().Execute(
            engine, ExitHandoff(), Request(Entry, outer: 16, segment: 3));

        result.State.Should().Be(
            TitleExecutionState.Completed,
            $"state={result.State} segments={result.SegmentsRetired} diag={result.DiagnosticCode} " +
            $"pc=0x{result.FinalSnapshot?.PC:X8} t3=0x{result.FinalSnapshot?.Gpr[(int)R3000aRegister.T3]:X8} " +
            $"s1=0x{result.FinalSnapshot?.Gpr[(int)R3000aRegister.S1]:X8}");
        result.SegmentsRetired.Should().BeGreaterThanOrEqualTo(2);
        result.FinalSnapshot!.Gpr[(int)R3000aRegister.S1].Should().Be(0xAA,
            "RAM written in an early segment must survive process restarts into a later one");
    }

    [Fact]
    public void Host_DeviceRegisterStateSurvivesAcrossSegmentProcessRestarts()
    {
        // Issue #387. Like guest RAM, the guest-visible hardware-register window —
        // the DMA/timer/interrupt MMIO state the interpreter keeps in its one
        // persistent core — must round-trip the per-segment process fork, or a
        // DEVICE write in one segment would be invisible to the next process.
        // Segment 1 writes a DMA channel-0 MADR, a timer mode and the interrupt
        // mask; a loop then parks execution between segments; the final segment
        // reads all three registers back into S1/S2/S3. If the engine dropped the
        // window across the restart, every read would see the reset zero.
        const ushort DmaMadrLo = 0x1080;      // low 16 bits of 0x1F801080 (DMA channel 0 MADR)
        const ushort TimerModeLo = 0x1114;    // low 16 bits of 0x1F801114 (timer 1 mode)
        const ushort InterruptMaskLo = 0x1074; // low 16 bits of 0x1F801074 (I_MASK)

        var words = new uint[]
        {
            MipsEncoding.I(LuiOpcodeField, rt: (byte)R3000aRegister.T1, rs: 0, immediate: 0x1F80),
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T1, rs: (byte)R3000aRegister.T1, immediate: DmaMadrLo),
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T2, rs: 0, immediate: 0xA000),
            MipsEncoding.Load(R3000aOpcode.Sw, rt: (byte)R3000aRegister.T2, baseRegister: (byte)R3000aRegister.T1, offset: 0),
            MipsEncoding.I(LuiOpcodeField, rt: (byte)R3000aRegister.T3, rs: 0, immediate: 0x1F80),
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T3, rs: (byte)R3000aRegister.T3, immediate: TimerModeLo),
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T4, rs: 0, immediate: 0x0200),
            MipsEncoding.Load(R3000aOpcode.Sw, rt: (byte)R3000aRegister.T4, baseRegister: (byte)R3000aRegister.T3, offset: 0),
            MipsEncoding.I(LuiOpcodeField, rt: (byte)R3000aRegister.T5, rs: 0, immediate: 0x1F80),
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T5, rs: (byte)R3000aRegister.T5, immediate: InterruptMaskLo),
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T6, rs: 0, immediate: 0xFFFF),
            MipsEncoding.Load(R3000aOpcode.Sw, rt: (byte)R3000aRegister.T6, baseRegister: (byte)R3000aRegister.T5, offset: 0),
            MipsEncoding.I(AdduiOpcodeField, rt: (byte)R3000aRegister.T7, rs: 0, immediate: 5),
            MipsEncoding.I(AdduiOpcodeField, rt: (byte)R3000aRegister.T7, rs: (byte)R3000aRegister.T7, immediate: 0xFFFF),
            MipsEncoding.Branch(0x05, (byte)R3000aRegister.T7, 0, Entry + 56, Entry + 52),
            MipsEncoding.Nop,
            MipsEncoding.I(LuiOpcodeField, rt: (byte)R3000aRegister.T1, rs: 0, immediate: 0x1F80),
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T1, rs: (byte)R3000aRegister.T1, immediate: DmaMadrLo),
            MipsEncoding.Load(R3000aOpcode.Lw, rt: (byte)R3000aRegister.T4, baseRegister: (byte)R3000aRegister.T1, offset: 0),
            MipsEncoding.Nop,
            MipsEncoding.R(0x25, rd: (byte)R3000aRegister.S1, rs: (byte)R3000aRegister.T4, rt: 0, shamt: 0),
            MipsEncoding.I(LuiOpcodeField, rt: (byte)R3000aRegister.T3, rs: 0, immediate: 0x1F80),
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T3, rs: (byte)R3000aRegister.T3, immediate: TimerModeLo),
            MipsEncoding.Load(R3000aOpcode.Lw, rt: (byte)R3000aRegister.T4, baseRegister: (byte)R3000aRegister.T3, offset: 0),
            MipsEncoding.Nop,
            MipsEncoding.R(0x25, rd: (byte)R3000aRegister.S2, rs: (byte)R3000aRegister.T4, rt: 0, shamt: 0),
            MipsEncoding.I(LuiOpcodeField, rt: (byte)R3000aRegister.T5, rs: 0, immediate: 0x1F80),
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T5, rs: (byte)R3000aRegister.T5, immediate: InterruptMaskLo),
            MipsEncoding.Load(R3000aOpcode.Lw, rt: (byte)R3000aRegister.T4, baseRegister: (byte)R3000aRegister.T5, offset: 0),
            MipsEncoding.Nop,
            MipsEncoding.R(0x25, rd: (byte)R3000aRegister.S3, rs: (byte)R3000aRegister.T4, rt: 0, shamt: 0),
        };

        var fixture = new RecompilerDifferentialFixture(
            name: "execution-orchestrator-host-device-state-continuity",
            encodedInstructions: words,
            entryPc: Entry,
            stepBudget: 128,
            memoryWindow: [],
            referenceStepBudget: 32);

        var sink = new CapturedOutputSink();
        using var engine = new HostTitleExecutionEngine(
            fixture,
            (reader, writer) => new BiosHleRuntime(sink, reader, writer));

        var result = new ExecutionOrchestrator().Execute(
            engine, ExitHandoff(), Request(Entry, outer: 16, segment: 3));

        result.State.Should().Be(
            TitleExecutionState.Completed,
            $"state={result.State} segments={result.SegmentsRetired} diag={result.DiagnosticCode} " +
            $"pc=0x{result.FinalSnapshot?.PC:X8}");
        result.SegmentsRetired.Should().BeGreaterThanOrEqualTo(2,
            "the writes and the read-backs must be split across process restarts");
        result.FinalSnapshot!.Gpr[(int)R3000aRegister.S1].Should().Be(0xA000,
            "a DMA register written in an early segment must survive process restarts");
        result.FinalSnapshot.Gpr[(int)R3000aRegister.S2].Should().Be(0x0200,
            "a timer register written in an early segment must survive process restarts");
        result.FinalSnapshot.Gpr[(int)R3000aRegister.S3].Should().Be(0xFFFF,
            "the interrupt mask written in an early segment must survive process restarts");
    }

    [Fact]
    public void Host_InitialMemorySeedsTheFirstSegment()
    {
        // The request's initial memory must reach the generated host even though
        // segment 1's input is carried through the RAM preload rather than init.
        // Without it, the LW would read zero.
        const ushort scratch = 0x0090; // KUSEG: virtual == physical
        var words = new uint[]
        {
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T1, rs: 0, immediate: scratch),
            MipsEncoding.Load(R3000aOpcode.Lw, rt: (byte)R3000aRegister.S1, baseRegister: (byte)R3000aRegister.T1, offset: 0),
        };

        var fixture = new RecompilerDifferentialFixture(
            name: "execution-orchestrator-host-initial-memory",
            encodedInstructions: words,
            entryPc: Entry,
            stepBudget: 128,
            memoryWindow: [],
            referenceStepBudget: 16);

        var sink = new CapturedOutputSink();
        using var engine = new HostTitleExecutionEngine(
            fixture,
            (reader, writer) => new BiosHleRuntime(sink, reader, writer));

        var initialMemory = new[]
        {
            new RecompilerInitialMemoryItem(scratch, 0x5A),
        };

        var result = new ExecutionOrchestrator().Execute(
            engine, ExitHandoff(), Request(Entry, outer: 4, segment: 64, initialMemory: initialMemory));

        result.State.Should().Be(TitleExecutionState.Completed);
        result.FinalSnapshot!.Gpr[(int)R3000aRegister.S1].Should().Be(0x5A,
            "the request's initial memory must seed the first generated-host segment");
    }

    [Fact]
    public void InitialMemoryInTheHardwareRegisterWindow_SeedsBothEnginesAlike()
    {
        // Issue #387 follow-up. Load must route a seeded byte the way the
        // interpreter's core does: RAM to RAM, the hardware-register window to
        // hw_regs. The host engine dropped the window, so the first segment's
        // HWREG preload started at zero and a request that seeds MMIO state read
        // back zeros while the interpreter read the seeded values.
        //
        // The guest reads four 32-bit registers, which also pins the window's
        // edges: 0x1F801000 is its first valid byte, 0x1F801080 is inside it,
        // 0x1F802FFC..0x1F802FFF is its last valid word, and the seed at
        // 0x1F803000 (one byte past the end) must be dropped by both engines
        // rather than corrupt anything.
        const ushort FirstWordLo = 0x1000;     // 0x1F801000, first word of the 8 KiB window
        const ushort DmaMadrLo = 0x1080;       // 0x1F801080, DMA channel 0 MADR
        const ushort LastWordLo = 0x2FFC;      // 0x1F802FFC, last word of the 8 KiB window
        const ushort InterruptMaskLo = 0x1074; // 0x1F801074, I_MASK — seeded via its KSEG1 alias

        var words = new uint[]
        {
            MipsEncoding.I(LuiOpcodeField, rt: (byte)R3000aRegister.T1, rs: 0, immediate: 0x1F80),
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T1, rs: (byte)R3000aRegister.T1, immediate: DmaMadrLo),
            MipsEncoding.Load(R3000aOpcode.Lw, rt: (byte)R3000aRegister.S1, baseRegister: (byte)R3000aRegister.T1, offset: 0),
            MipsEncoding.Nop,
            MipsEncoding.I(LuiOpcodeField, rt: (byte)R3000aRegister.T2, rs: 0, immediate: 0x1F80),
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T2, rs: (byte)R3000aRegister.T2, immediate: LastWordLo),
            MipsEncoding.Load(R3000aOpcode.Lw, rt: (byte)R3000aRegister.S2, baseRegister: (byte)R3000aRegister.T2, offset: 0),
            MipsEncoding.Nop,
            MipsEncoding.I(LuiOpcodeField, rt: (byte)R3000aRegister.T3, rs: 0, immediate: 0x1F80),
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T3, rs: (byte)R3000aRegister.T3, immediate: InterruptMaskLo),
            MipsEncoding.Load(R3000aOpcode.Lw, rt: (byte)R3000aRegister.S3, baseRegister: (byte)R3000aRegister.T3, offset: 0),
            MipsEncoding.Nop,
            MipsEncoding.I(LuiOpcodeField, rt: (byte)R3000aRegister.T4, rs: 0, immediate: 0x1F80),
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T4, rs: (byte)R3000aRegister.T4, immediate: FirstWordLo),
            MipsEncoding.Load(R3000aOpcode.Lw, rt: (byte)R3000aRegister.S4, baseRegister: (byte)R3000aRegister.T4, offset: 0),
            MipsEncoding.Nop,
        };

        var initialMemory = new[]
        {
            // KUSEG: virtual == physical inside the hardware-register window.
            new RecompilerInitialMemoryItem(0x1F801080u, 0xEF),
            new RecompilerInitialMemoryItem(0x1F801081u, 0xCD),
            new RecompilerInitialMemoryItem(0x1F801082u, 0xAB),
            new RecompilerInitialMemoryItem(0x1F801083u, 0x00),

            // The window's first and last valid bytes must both land.
            new RecompilerInitialMemoryItem(0x1F801000u, 0x5A),
            new RecompilerInitialMemoryItem(0x1F802FFCu, 0x11),
            new RecompilerInitialMemoryItem(0x1F802FFFu, 0x77),

            // One byte past the end: out of range for both engines, dropped.
            new RecompilerInitialMemoryItem(0x1F803000u, 0xFF),

            // The same physical register reached through its KSEG1 alias, which
            // the shared translation masks down to 0x1F801074.
            new RecompilerInitialMemoryItem(0xBF801074u, 0x34),
            new RecompilerInitialMemoryItem(0xBF801075u, 0x12),
        };

        var fixture = new RecompilerDifferentialFixture(
            name: "execution-orchestrator-initial-hardware-registers",
            encodedInstructions: words,
            entryPc: Entry,
            stepBudget: 128,
            memoryWindow: [],
            referenceStepBudget: 32);

        var hostSink = new CapturedOutputSink();
        using var host = new HostTitleExecutionEngine(
            fixture,
            (reader, writer) => new BiosHleRuntime(hostSink, reader, writer));
        var hostResult = new ExecutionOrchestrator().Execute(
            host, ExitHandoff(), Request(Entry, outer: 4, segment: 64, initialMemory: initialMemory));

        var interpreterSink = new CapturedOutputSink();
        using var interpreter = new InterpreterTitleExecutionEngine(
            fixture.Instructions,
            fixture.EntryPc,
            (reader, writer) => new BiosHleRuntime(interpreterSink, reader, writer));
        var interpreterResult = new ExecutionOrchestrator().Execute(
            interpreter, ExitHandoff(), Request(Entry, outer: 4, segment: 64, initialMemory: initialMemory));

        hostResult.State.Should().Be(TitleExecutionState.Completed, Describe(hostResult));
        interpreterResult.State.Should().Be(TitleExecutionState.Completed, Describe(interpreterResult));

        var hostSnapshot = hostResult.FinalSnapshot!;
        hostSnapshot.Gpr[(int)R3000aRegister.S1].Should().Be(0x00ABCDEFu,
            "a hardware register seeded by the request must reach the first generated-host segment");
        hostSnapshot.Gpr[(int)R3000aRegister.S2].Should().Be(0x77000011u,
            "the window's last valid word must be seeded, and nothing past its end may spill into it");
        hostSnapshot.Gpr[(int)R3000aRegister.S3].Should().Be(0x00001234u,
            "a KSEG1 alias of a hardware register must translate into the same window slot");
        hostSnapshot.Gpr[(int)R3000aRegister.S4].Should().Be(0x0000005Au,
            "the window's first valid byte must be seeded, not skipped by an off-by-one lower bound");

        // The parity the finding is really about: one request, one observed
        // initial MMIO state, whichever backend executed it.
        var interpreterSnapshot = interpreterResult.FinalSnapshot!;
        foreach (var register in new[]
                 { R3000aRegister.S1, R3000aRegister.S2, R3000aRegister.S3, R3000aRegister.S4 })
        {
            hostSnapshot.Gpr[(int)register].Should().Be(
                interpreterSnapshot.Gpr[(int)register],
                $"both engines were given the same initial MMIO state ({register}). " +
                $"{Describe(interpreterResult)} vs {Describe(hostResult)}");
        }
    }

    // --- One contract, both engines (Issue #379) ------------------------------

    /// <summary>
    /// The BIOS jump-table dispatch situations both execution backends must agree
    /// on. Each one names a row in <see cref="DispatchScenarioTable"/>; the
    /// expected values live there once rather than being hand-typed per backend.
    /// </summary>
    public enum BiosDispatchScenario
    {
        /// <summary>An untouched entry that still dispatches to a registered HLE service.</summary>
        UnpatchedRegisteredEntry,

        /// <summary>A guest patch over a registered service's own slot, naming a compiled routine.</summary>
        PatchedOverRegisteredSlot,

        /// <summary>An untouched entry the Runtime can neither service nor redirect.</summary>
        UnregisteredService,

        /// <summary>
        /// A guest patch naming a translatable address neither backend can enter:
        /// outside the interpreter's program image and outside the generated
        /// host's block table. The divergence Issue #379 is about.
        /// </summary>
        PatchedTargetWithNoBlock,
    }

    [Theory]
    [InlineData(BiosDispatchScenario.UnpatchedRegisteredEntry)]
    [InlineData(BiosDispatchScenario.PatchedOverRegisteredSlot)]
    [InlineData(BiosDispatchScenario.UnregisteredService)]
    [InlineData(BiosDispatchScenario.PatchedTargetWithNoBlock)]
    public void BiosJumpTableDispatch_ReachesTheSameOrchestratorOutcome_OnBothEngines(
        BiosDispatchScenario scenario)
    {
        var (fixture, expected) = DispatchScenarioTable(scenario);

        var interpreterSink = new CapturedOutputSink();
        using var interpreter = new InterpreterTitleExecutionEngine(
            fixture.Instructions,
            fixture.EntryPc,
            (reader, writer) => new BiosHleRuntime(interpreterSink, reader, writer));
        var interpreterResult = new ExecutionOrchestrator().Execute(
            interpreter, DispatchHandoff(), DispatchRequest());

        var hostSink = new CapturedOutputSink();
        using var host = new HostTitleExecutionEngine(
            fixture,
            (reader, writer) => new BiosHleRuntime(hostSink, reader, writer));
        var hostResult = new ExecutionOrchestrator().Execute(
            host, DispatchHandoff(), DispatchRequest());

        // The whole point: one guest-level condition, one orchestrator-visible
        // outcome, whichever backend executed it.
        hostResult.State.Should().Be(
            interpreterResult.State,
            $"both engines run the identical guest program. {Describe(interpreterResult)} vs {Describe(hostResult)}");

        AssertDispatchOutcome(interpreterResult, expected);
        AssertDispatchOutcome(hostResult, expected);

        // A patch fully overrides the slot's registered service on both paths, so
        // no scenario here may produce service output.
        hostSink.Bytes.Should().BeEquivalentTo(interpreterSink.Bytes, static o => o.WithStrictOrdering());
        hostSink.Bytes.Should().BeEmpty("no scenario reaches a service with a host-visible effect");
    }

    private static void AssertDispatchOutcome(TitleExecutionResult result, DispatchExpectation expected)
    {
        var because = Describe(result);
        result.State.Should().Be(expected.State, because);
        result.DiagnosticCode.Should().Be(expected.DiagnosticCode, because);

        var snapshot = result.FinalSnapshot!;
        snapshot.PC.Should().Be(expected.FinalPc, because);
        snapshot.Gpr[(int)R3000aRegister.V0].Should().Be(expected.V0, because);
        snapshot.Gpr[(int)R3000aRegister.S0].Should().Be(expected.S0, because);
        snapshot.Gpr[(int)R3000aRegister.S1].Should().Be(expected.S1, because);
        snapshot.Gpr[(int)R3000aRegister.S2].Should().Be(expected.ScratchWord, because);
    }

    private static string Describe(TitleExecutionResult result) =>
        $"[{result.EngineName}] state={result.State} segments={result.SegmentsRetired} " +
        $"diag={result.DiagnosticCode} pc=0x{result.FinalSnapshot?.PC:X8} " +
        $"term={result.FinalSnapshot?.Termination}";

    /// <summary>
    /// One expected orchestration outcome, shared by every backend that runs the
    /// scenario. <see cref="ScratchWord"/> is the guest RAM the patched routine
    /// wrote, read back into <c>$s2</c> so a RAM side effect is comparable through
    /// the register file on both engines.
    /// </summary>
    private sealed record DispatchExpectation(
        TitleExecutionState State,
        string? DiagnosticCode,
        uint FinalPc,
        uint V0,
        uint S0,
        uint S1,
        uint ScratchWord);

    private static (RecompilerDifferentialFixture Fixture, DispatchExpectation Expected) DispatchScenarioTable(
        BiosDispatchScenario scenario) => scenario switch
    {
        // Baseline: the entry is untouched, so B0:56 GetC0Table is serviced by the
        // Runtime, returns through $ra, and the tail runs.
        BiosDispatchScenario.UnpatchedRegisteredEntry => (
            DispatchProgram(patchTheSlot: false, BiosCallFamily.B0, BiosHleRuntime.GetC0TableFunction),
            new DispatchExpectation(
                TitleExecutionState.Completed,
                DiagnosticCode: null,
                FinalPc: DispatchProgramEnd,
                V0: BiosJumpTables.C0TableAddress,
                S0: 0,
                S1: ReturnedMarker,
                ScratchWord: 0)),

        // A patch over a registered slot: control reaches the guest routine (which
        // the generated host has a block for), not the HLE service.
        BiosDispatchScenario.PatchedOverRegisteredSlot => (
            DispatchProgram(patchTheSlot: true, BiosCallFamily.A0, BiosHleRuntime.PutCharFunction),
            new DispatchExpectation(
                TitleExecutionState.Completed,
                DiagnosticCode: null,
                FinalPc: DispatchProgramEnd,
                V0: 0,
                S0: DispatchRoutineMarker,
                S1: ReturnedMarker,
                ScratchWord: DispatchRoutineMarker)),

        // Neither serviceable nor redirectable: both engines stop on the Runtime's
        // own diagnostic, at the trampoline vector, without consulting the handoff.
        BiosDispatchScenario.UnregisteredService => (
            DispatchProgram(patchTheSlot: false, BiosCallFamily.A0, UnregisteredA0Function),
            new DispatchExpectation(
                TitleExecutionState.RuntimeFailure,
                DiagnosticCode: "BIOS_HLE_UNSUPPORTED_CALL",
                FinalPc: DispatchVectorPc,
                V0: 0,
                S0: 0,
                S1: 0,
                ScratchWord: 0)),

        // The Issue #379 case: a patched target no backend can enter. Both must
        // end the segment at that PC and let the handoff resolve it — which it
        // does here, continuing at the tail with a return value in $v0.
        BiosDispatchScenario.PatchedTargetWithNoBlock => (
            DispatchProgram(
                patchTheSlot: true, BiosCallFamily.A0, UnregisteredA0Function,
                patchedTargetOverride: UncompiledTarget),
            new DispatchExpectation(
                TitleExecutionState.Completed,
                DiagnosticCode: null,
                FinalPc: DispatchProgramEnd,
                V0: HandoffReturnValue,
                S0: 0,
                S1: ReturnedMarker,
                ScratchWord: 0)),

        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };

    /// <summary>
    /// The one continuation rule every scenario runs under: an unresolvable
    /// patched target is resumed at the program's tail with a return value, and
    /// any other unresolved PC ends the run.
    /// </summary>
    private static ITitleExecutionHandoff DispatchHandoff() =>
        new FunctionHandoff(snapshot => snapshot.PC == UncompiledTarget
            ? TitleExecutionHandoffResult.ContinueAt(Entry + (DispatchTailIndex * 4u), HandoffReturnValue)
            : TitleExecutionHandoffResult.Exit());

    private static TitleExecutionRequest DispatchRequest() =>
        Request(Entry, outer: 8, segment: 64);

    // Shared dispatch-parity program. Identical words feed the interpreter (as a
    // guest RAM image) and the generated host (as compiled blocks):
    //
    //   0  LUI  $t0, hi(target)
    //   1  ORI  $t0, $t0, lo(target)
    //   2  ORI  $t2, $zero, slot          slot addresses are all below 0x10000
    //   3  SW   $t0, 0($t2)               the guest patches the entry (NOP if not)
    //   4  ORI  $t1, $zero, function      PS1 ABI: $t1 selects the BIOS function
    //   5  JAL  vector                    links $ra to index 7
    //   6  NOP                            branch delay slot
    //   7  J    tail                      reached only on return from the call
    //   8  NOP
    //   9  ORI  $s0, $zero, marker        the patched routine, reachable only
    //  10  ORI  $t3, $zero, scratch       through the BIOS dispatch under test
    //  11  J    return                    static return to index 7, so the whole
    //  12  SB   $s0, 0($t3)               program is representable as blocks
    //  13  ORI  $s1, $zero, returned      the tail
    //  14  ORI  $t3, $zero, scratch
    //  15  LW   $s2, 0($t3)               the routine's RAM effect, in a register
    //  16  NOP                            load delay slot, so $s2 is committed
    //                                     before control leaves the program
    private const uint DispatchCallIndex = 5;
    private const uint DispatchReturnIndex = 7;
    private const uint DispatchRoutineIndex = 9;
    private const uint DispatchTailIndex = 13;
    private const uint DispatchProgramLength = 17;

    private const byte LuiOpcodeField = 0x0F;
    private const uint DispatchScratch = 0x00000C00u;
    private const uint DispatchRoutineMarker = 0x5Au;
    private const uint HandoffReturnValue = 0x99u;

    /// <summary>The PC both engines fall off the program at, where the handoff exits.</summary>
    private const uint DispatchProgramEnd = Entry + (DispatchProgramLength * 4u);

    /// <summary>A translatable address outside the program and outside every block.</summary>
    private const uint UncompiledTarget = Entry + 0x800u;

    /// <summary>The A0 trampoline vector as reached from a KSEG0 program.</summary>
    private const uint DispatchVectorPc = (Entry & 0xF0000000u) | BiosJumpTables.A0VectorAddress;

    private static RecompilerDifferentialFixture DispatchProgram(
        bool patchTheSlot,
        BiosCallFamily family,
        byte function,
        uint? patchedTargetOverride = null)
    {
        var target = patchedTargetOverride ?? (Entry + (DispatchRoutineIndex * 4u));
        var slot = BiosJumpTables.EntryAddress(family, function);
        var vector = family switch
        {
            BiosCallFamily.A0 => BiosJumpTables.A0VectorAddress,
            BiosCallFamily.B0 => BiosJumpTables.B0VectorAddress,
            _ => BiosJumpTables.C0VectorAddress,
        };

        var words = new uint[DispatchProgramLength];
        words[0] = MipsEncoding.I(LuiOpcodeField, rt: (byte)R3000aRegister.T0, rs: 0, immediate: (ushort)(target >> 16));
        words[1] = MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T0, rs: (byte)R3000aRegister.T0, immediate: (ushort)target);
        words[2] = MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T2, rs: 0, immediate: (ushort)slot);
        words[3] = patchTheSlot
            ? MipsEncoding.Load(R3000aOpcode.Sw, rt: (byte)R3000aRegister.T0, baseRegister: (byte)R3000aRegister.T2, offset: 0)
            : MipsEncoding.Nop;
        words[4] = MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T1, rs: 0, immediate: function);
        words[DispatchCallIndex] = MipsEncoding.JumpAndLink(vector);
        words[6] = MipsEncoding.Nop;
        words[DispatchReturnIndex] = MipsEncoding.Jump(Entry + (DispatchTailIndex * 4u));
        words[8] = MipsEncoding.Nop;
        words[DispatchRoutineIndex] = MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.S0, rs: 0, immediate: (ushort)DispatchRoutineMarker);
        words[10] = MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T3, rs: 0, immediate: (ushort)DispatchScratch);
        words[11] = MipsEncoding.Jump(Entry + (DispatchReturnIndex * 4u));
        words[12] = MipsEncoding.Load(R3000aOpcode.Sb, rt: (byte)R3000aRegister.S0, baseRegister: (byte)R3000aRegister.T3, offset: 0);
        words[DispatchTailIndex] = MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.S1, rs: 0, immediate: (ushort)ReturnedMarker);
        words[14] = MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.T3, rs: 0, immediate: (ushort)DispatchScratch);
        words[15] = MipsEncoding.Load(R3000aOpcode.Lw, rt: (byte)R3000aRegister.S2, baseRegister: (byte)R3000aRegister.T3, offset: 0);
        words[16] = MipsEncoding.Nop;

        return new RecompilerDifferentialFixture(
            name: "execution-orchestrator-bios-dispatch-parity",
            encodedInstructions: words,
            entryPc: Entry,
            stepBudget: 64,
            memoryWindow: [],
            referenceStepBudget: 64);
    }

    // --- Harness ---------------------------------------------------------------

    private const byte UnregisteredA0Function = 0x17;
    private const ushort ReturnedMarker = 0x77;

    private static ITitleExecutionHandoff ExitHandoff() =>
        new FunctionHandoff(_ => TitleExecutionHandoffResult.Exit());

    private static TitleExecutionRequest Request(
        uint entryPc,
        uint outer,
        uint segment,
        IReadOnlyList<uint>? gpr = null,
        IReadOnlyList<RecompilerInitialMemoryItem>? initialMemory = null)
    {
        var gprValues = new uint[TitleExecutionRequest.GprCount];
        if (gpr is not null)
        {
            for (var i = 0; i < gpr.Count; i++) gprValues[i] = gpr[i];
        }

        return new TitleExecutionRequest(
            entryPc, gprValues, initialHi: 0, initialLo: 0, initialMemory ?? [], outer, segment);
    }

    private static RecompilerIrProgram Lower(uint entryPc, params uint[] words)
    {
        var instructions = new List<(R3000aInstruction Instruction, uint EntryPc)>();
        for (var i = 0; i < words.Length; i++)
        {
            instructions.Add((R3000aDecoder.Decode(words[i]), entryPc + unchecked((uint)i * 4u)));
        }
        return MipsToIrLowerer.LowerProgram(instructions);
    }

    private static IEnumerable<RecompilerInitialMemoryItem> StringBytes(uint address, string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            yield return new RecompilerInitialMemoryItem(address + (uint)i, (byte)value[i]);
        }

        yield return new RecompilerInitialMemoryItem(address + (uint)value.Length, 0);
    }

    private sealed class ScriptedHandoff : ITitleExecutionHandoff
    {
        private readonly IReadOnlyList<TitleExecutionHandoffResult?> _decisions;
        private int _index;

        public ScriptedHandoff(params TitleExecutionHandoffResult?[] decisions) => _decisions = decisions;

        public TitleExecutionHandoffResult? Decide(RecompilerStateSnapshot segmentState)
        {
            var index = Math.Min(_index, _decisions.Count - 1);
            _index++;
            return _decisions[index];
        }
    }

    private sealed class FunctionHandoff : ITitleExecutionHandoff
    {
        private readonly Func<RecompilerStateSnapshot, TitleExecutionHandoffResult?> _decide;

        public FunctionHandoff(Func<RecompilerStateSnapshot, TitleExecutionHandoffResult?> decide) => _decide = decide;

        public TitleExecutionHandoffResult? Decide(RecompilerStateSnapshot segmentState) => _decide(segmentState);
    }

    private sealed class FailingEngine : IRecompiledExecutionEngine
    {
        public string Name => "failing-test-engine";

        public void Load(TitleExecutionRequest request)
        {
        }

        public RecompilerExecutionResult RunSegment(TitleExecutionSegmentRequest segmentRequest) =>
            RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.ExecutionFailed, "FAKE_CRASH", "The engine crashed on purpose.");

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }

    private sealed class NoSnapshotEngine : IRecompiledExecutionEngine
    {
        public string Name => "no-snapshot-test-engine";

        public void Load(TitleExecutionRequest request)
        {
        }

        public RecompilerExecutionResult RunSegment(TitleExecutionSegmentRequest segmentRequest) =>
            new(RecompilerExecutionStatus.Completed, null, null, null);

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}