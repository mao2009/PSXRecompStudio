using FluentAssertions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Tests.Recompiler;
using Xunit.Sdk;

namespace PSXRecomp.Tests.Execution;

/// <summary>
/// Issue #378: the regression proof for the correctness oracle itself.
///
/// <c>RealRomTitleExecutionTests</c> can only exercise the oracle when an
/// operator has a real ROM locally, so these synthetic cases — which never need
/// a fixture and always run — are what proves the oracle actually rejects a
/// wrong run instead of passing everything. Each red case is built so that the
/// three pre-existing classified-end assertions (non-null snapshot, not
/// <see cref="TitleExecutionState.InvalidState"/>, segments retired &gt; 0) all
/// still hold, and only the new invariants fail.
/// </summary>
[Test]
public sealed class ObservedTitleExecutionTests
{
    private const uint Entry = 0x80001000u;
    private const byte OriOpcodeField = 0x0D;
    private const byte AdduiOpcodeField = 0x09;

    // --- Green: a real, correct run satisfies every invariant ------------------

    [Fact]
    public void ARunThatCarriesStateAcrossAHandoffContinuation_SatisfiesEveryInvariant()
    {
        using var engine = new RecompiledIrTitleExecutionEngine(Lower(
            Entry,
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.S0, rs: 0, immediate: 0x11),
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.S0, rs: 0, immediate: 0x33)));

        var observed = new ObservedTitleExecution(
            engine,
            new ScriptedHandoff(
                TitleExecutionHandoffResult.ContinueAt(Entry + 4, returnValue: 0x99),
                TitleExecutionHandoffResult.Exit()));

        var request = Request(outer: 4, segment: 8);
        var result = new ExecutionOrchestrator().Execute(observed, observed, request);

        result.State.Should().Be(TitleExecutionState.Completed);
        result.SegmentsRetired.Should().Be(2);
        observed.AssertInvariantsHold(request, result, "green/continue-at");
    }

    [Fact]
    public void ARunSplitAcrossSegmentsByTheInnerBudget_SatisfiesEveryInvariant()
    {
        // A counted loop the per-segment budget cuts mid-flight, so the run has
        // several ExecutionBudgetExceeded boundaries to carry state across.
        using var engine = new RecompiledIrTitleExecutionEngine(Lower(
            Entry,
            MipsEncoding.I(AdduiOpcodeField, rt: (byte)R3000aRegister.T0, rs: 0, immediate: 3),
            MipsEncoding.I(AdduiOpcodeField, rt: (byte)R3000aRegister.T0, rs: (byte)R3000aRegister.T0, immediate: 0xFFFF),
            MipsEncoding.Branch(0x05, (byte)R3000aRegister.T0, 0, Entry + 8, Entry + 4),
            MipsEncoding.Nop,
            MipsEncoding.I(OriOpcodeField, rt: (byte)R3000aRegister.S1, rs: 0, immediate: 0x77)));

        var observed = new ObservedTitleExecution(engine, ExitHandoff());
        var request = Request(outer: 8, segment: 2);
        var result = new ExecutionOrchestrator().Execute(observed, observed, request);

        result.SegmentsRetired.Should().BeGreaterThanOrEqualTo(2);
        observed.AssertInvariantsHold(request, result, "green/inner-budget");
    }

    [Fact]
    public void ARunTheOuterBudgetCutsOff_SatisfiesEveryInvariant()
    {
        using var engine = new RecompiledIrTitleExecutionEngine(Lower(
            Entry, MipsEncoding.Jump(Entry), MipsEncoding.Nop));

        var observed = new ObservedTitleExecution(engine, ExitHandoff());
        var request = Request(outer: 3, segment: 128);
        var result = new ExecutionOrchestrator().Execute(observed, observed, request);

        result.State.Should().Be(TitleExecutionState.BudgetExhausted);
        observed.AssertInvariantsHold(request, result, "green/outer-budget");
    }

    // --- Red: wrong runs that the pre-existing assertions all accept -----------

    [Fact]
    public void AGuestParkedAtAnUnfetchableAddress_IsCaught()
    {
        // The guest is cut by the per-segment budget while its PC sits outside
        // KUSEG/KSEG0/KSEG1, so the orchestrator resumes the next segment at an
        // address no R3000A can fetch from. The budget-cut resume path does not
        // validate the PC the way the handoff-continuation path does, so this run
        // ends BudgetExhausted with a real snapshot and several retired segments:
        // every classified-end assertion passes and only the oracle objects.
        using var engine = new ParkedEngine(0xC0000000u);
        var observed = new ObservedTitleExecution(engine, ExitHandoff());

        var request = Request(outer: 3, segment: 8);
        var result = new ExecutionOrchestrator().Execute(observed, observed, request);

        result.FinalSnapshot.Should().NotBeNull();
        result.State.Should().NotBe(TitleExecutionState.InvalidState);
        result.SegmentsRetired.Should().BeGreaterThan(0);

        observed.Invoking(o => o.AssertInvariantsHold(request, result, "red/unfetchable-pc"))
            .Should().Throw<XunitException>()
            .WithMessage("*0xC0000000*");
    }

    [Fact]
    public void ATerminalStateThatMisclassifiesTheRun_IsCaught()
    {
        // The exact failure mode of Issue #378: a run that actually failed is
        // reported as a clean end. Injected by rewriting the reported state, since
        // the orchestrator's own mapping is the thing under test.
        using var engine = new ParkedEngine(Entry, RecompilerIrTerminationReason.Exception);
        var observed = new ObservedTitleExecution(engine, ExitHandoff());

        var request = Request(outer: 3, segment: 8);
        var honest = new ExecutionOrchestrator().Execute(observed, observed, request);
        honest.State.Should().Be(TitleExecutionState.RuntimeFailure);

        var misreported = honest with { State = TitleExecutionState.Completed };
        misreported.FinalSnapshot.Should().NotBeNull();
        misreported.State.Should().NotBe(TitleExecutionState.InvalidState);
        misreported.SegmentsRetired.Should().BeGreaterThan(0);

        observed.Invoking(o => o.AssertInvariantsHold(request, misreported, "red/misclassified"))
            .Should().Throw<XunitException>()
            .WithMessage("*RuntimeFailure*");
    }

    [Fact]
    public void AResultCarryingASnapshotTheRunNeverProduced_IsCaught()
    {
        using var engine = new RecompiledIrTitleExecutionEngine(Lower(Entry, MipsEncoding.Nop));
        var observed = new ObservedTitleExecution(engine, ExitHandoff());

        var request = Request(outer: 2, segment: 8);
        var honest = new ExecutionOrchestrator().Execute(observed, observed, request);

        // A plausible-looking but substituted snapshot: same shape, not this run's.
        var substituted = honest with
        {
            FinalSnapshot = new RecompilerStateSnapshot(
                new uint[TitleExecutionRequest.GprCount], hi: 0, lo: 0, pc: Entry + 4),
        };

        observed.Invoking(o => o.AssertInvariantsHold(request, substituted, "red/substituted-snapshot"))
            .Should().Throw<XunitException>();
    }

    [Fact]
    public void AResultThatMisreportsHowManySegmentsRan_IsCaught()
    {
        using var engine = new RecompiledIrTitleExecutionEngine(Lower(Entry, MipsEncoding.Nop));
        var observed = new ObservedTitleExecution(engine, ExitHandoff());

        var request = Request(outer: 2, segment: 8);
        var honest = new ExecutionOrchestrator().Execute(observed, observed, request);

        observed.Invoking(o => o.AssertInvariantsHold(
                request, honest with { SegmentsRetired = honest.SegmentsRetired + 1 }, "red/segment-count"))
            .Should().Throw<XunitException>();
    }

    [Fact]
    public void ABudgetExhaustedRunThatStoppedBeforeSpendingTheBudget_IsCaught()
    {
        // The loop spends exactly one outer-budget unit per retired segment, so a
        // run that reports BudgetExhausted after fewer segments than the budget
        // stopped for some other reason. Reproduced by driving the run under a
        // smaller budget than the one the result is then checked against; the
        // observed trace is exactly what an orchestrator that bailed out early
        // would have produced.
        using var engine = new ParkedEngine(Entry);
        var observed = new ObservedTitleExecution(engine, ExitHandoff());

        var result = new ExecutionOrchestrator().Execute(observed, observed, Request(outer: 2, segment: 8));
        result.State.Should().Be(TitleExecutionState.BudgetExhausted);
        result.FinalSnapshot.Should().NotBeNull();
        result.SegmentsRetired.Should().BeGreaterThan(0);

        observed.Invoking(o => o.AssertInvariantsHold(
                Request(outer: 5, segment: 8), result with { SegmentsRetired = 2 }, "red/short-budget"))
            .Should().Throw<XunitException>()
            .WithMessage("*outer budget*");
    }

    [Fact]
    public void AnUnresolvedTransferTheHandoffWasNeverOfferedIsCaught()
    {
        // UnsupportedTransfer is only legitimate when the handoff was asked and
        // had no rule. Reproduced by running the orchestration with a null handoff
        // so the decorator is never consulted — the shape a run that silently
        // skipped the handoff would leave behind.
        using var engine = new RecompiledIrTitleExecutionEngine(Lower(Entry, MipsEncoding.Nop));
        var observed = new ObservedTitleExecution(engine, ExitHandoff());

        var request = Request(outer: 2, segment: 8);
        var result = new ExecutionOrchestrator().Execute(observed, handoff: null, request);

        result.State.Should().Be(TitleExecutionState.UnsupportedTransfer);
        result.FinalSnapshot.Should().NotBeNull();
        result.SegmentsRetired.Should().BeGreaterThan(0);

        observed.Invoking(o => o.AssertInvariantsHold(request, result, "red/handoff-skipped"))
            .Should().Throw<XunitException>()
            .WithMessage("*offered to the handoff*");
    }

    // --- Harness ---------------------------------------------------------------

    private static ITitleExecutionHandoff ExitHandoff() => new ScriptedHandoff(TitleExecutionHandoffResult.Exit());

    private static TitleExecutionRequest Request(uint outer, uint segment) =>
        new(Entry, new uint[TitleExecutionRequest.GprCount], initialHi: 0, initialLo: 0, [], outer, segment);

    private static RecompilerIrProgram Lower(uint entryPc, params uint[] words)
    {
        var instructions = new List<(R3000aInstruction Instruction, uint EntryPc)>();
        for (var i = 0; i < words.Length; i++)
        {
            instructions.Add((R3000aDecoder.Decode(words[i]), entryPc + unchecked((uint)i * 4u)));
        }
        return MipsToIrLowerer.LowerProgram(instructions);
    }

    private sealed class ScriptedHandoff(params TitleExecutionHandoffResult?[] decisions) : ITitleExecutionHandoff
    {
        private int _index;

        public TitleExecutionHandoffResult? Decide(RecompilerStateSnapshot segmentState)
        {
            var index = Math.Min(_index, decisions.Length - 1);
            _index++;
            return decisions[index];
        }
    }

    /// <summary>
    /// An engine that always parks the guest at a fixed PC with a fixed
    /// termination reason, so a test can choose exactly the segment result the
    /// orchestrator has to classify and resume from.
    /// </summary>
    private sealed class ParkedEngine(
        uint pc,
        RecompilerIrTerminationReason termination = RecompilerIrTerminationReason.ExecutionBudgetExceeded)
        : IRecompiledExecutionEngine
    {
        public string Name => "parked-test-engine";

        public void Load(TitleExecutionRequest request)
        {
        }

        public RecompilerExecutionResult RunSegment(TitleExecutionSegmentRequest segmentRequest) =>
            RecompilerExecutionResult.Completed(new RecompilerStateSnapshot(
                segmentRequest.Gpr,
                hi: segmentRequest.Hi,
                lo: segmentRequest.Lo,
                pc: pc,
                termination: termination));

        public void Dispose() => GC.SuppressFinalize(this);
    }
}
