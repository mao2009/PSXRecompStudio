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