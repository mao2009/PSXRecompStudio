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

    // --- End-to-end over the generated host (gcc, RAM continuity) -------------

    [Fact]
    public void Host_GuestRamSurvivesAcrossSegmentProcessRestarts()
    {
        // Segment 1 writes 0xAA to scratch. A loop then parks execution between
        // segments (block budget 3), and the final read of scratch lands in S1.
        // If the engine lost RAM between process restarts, the read would see 0.
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