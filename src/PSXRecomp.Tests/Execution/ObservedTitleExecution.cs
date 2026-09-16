using FluentAssertions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Tests.Execution;

/// <summary>
/// Issue #378: a correctness oracle for an <see cref="ExecutionOrchestrator"/>
/// run that does not depend on any second implementation-under-test agreeing.
///
/// It is a pass-through decorator around the engine and the handoff, so the run
/// it observes is the real run, and it records what the orchestrator actually
/// did: every state it seeded a segment with, every snapshot the engine handed
/// back, and every handoff decision. <see cref="AssertInvariantsHold"/> then
/// checks that trace against facts that are true independently of whether the
/// recompiled guest computed the right values:
/// <list type="bullet">
/// <item>R3000A architectural facts (<c>gpr[0]</c> is hardwired to zero; a PC
/// the CPU can fetch from is translatable).</item>
/// <item>The orchestrator's own documented loop contract, restated here rather
/// than read back out of <see cref="ExecutionOrchestrator"/>: outer-loop state
/// continuity between segments, and the segment-termination to
/// <see cref="TitleExecutionState"/> classification map.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>What this does prove.</b> That the terminal
/// <see cref="TitleExecutionState"/> is the correct classification of what the
/// run actually did (so the state is real evidence rather than a value that any
/// outcome satisfies), that architectural state was carried across every
/// segment boundary without being dropped, reset or silently rewritten, that
/// the reported segment count and final snapshot are the run's own, and that
/// the guest was never resumed at an address the CPU cannot fetch from.</para>
/// <para><b>What this does not prove.</b> Nothing here claims a register or a
/// memory word holds the <i>right</i> value — that needs a source of truth this
/// repository does not have for real ROM data. BIOS-call semantics are equally
/// out of scope: <c>BiosVectorDispatch</c> is deliberately shared by every
/// engine, so no test built on running those engines can validate it. Those
/// remain open, separately tracked gaps.</para>
/// <para>The decorator does not own the engine it wraps; disposal stays with
/// the caller that constructed it.</para>
/// </remarks>
[Test]
internal sealed class ObservedTitleExecution : IRecompiledExecutionEngine, ITitleExecutionHandoff
{
    private readonly IRecompiledExecutionEngine _engine;
    private readonly ITitleExecutionHandoff? _handoff;
    private readonly List<Segment> _segments = [];

    public ObservedTitleExecution(IRecompiledExecutionEngine engine, ITitleExecutionHandoff? handoff)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _handoff = handoff;
    }

    public string Name => _engine.Name;

    public void Load(TitleExecutionRequest request) => _engine.Load(request);

    public RecompilerExecutionResult RunSegment(TitleExecutionSegmentRequest segmentRequest)
    {
        var result = _engine.RunSegment(segmentRequest);
        _segments.Add(new Segment(segmentRequest, result));
        return result;
    }

    /// <summary>
    /// Records the decision for the segment that just ran. The orchestrator only
    /// consults the handoff immediately after a segment, so the open segment is
    /// always the last recorded one.
    /// </summary>
    public TitleExecutionHandoffResult? Decide(RecompilerStateSnapshot segmentState)
    {
        var decision = _handoff?.Decide(segmentState);
        _segments[^1].Decision = decision;
        _segments[^1].HandoffConsulted = true;
        return decision;
    }

    /// <summary>The decorator borrows the engine; the owner disposes it.</summary>
    public void Dispose() => GC.SuppressFinalize(this);

    /// <summary>
    /// Asserts every invariant of the observed run. <paramref name="context"/> is
    /// prefixed to each failure so a real-ROM failure names its fixture.
    /// </summary>
    public void AssertInvariantsHold(TitleExecutionRequest request, TitleExecutionResult result, string context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        // The reported segment count must be the run's own, or every assertion
        // below would be checking a different execution than the one reported.
        result.SegmentsRetired.Should().Be(
            (uint)_segments.Count,
            $"{context}: the orchestrator must report exactly the segments it ran");

        result.EngineName.Should().Be(_engine.Name, $"{context}: the result must name the engine that ran");

        for (var i = 0; i < _segments.Count; i++)
        {
            var segment = _segments[i];
            var where = $"{context}: segment {i}";

            segment.Request.Budget.Should().Be(
                request.SegmentBudget, $"{where} must run under the requested per-segment budget");

            // R3000A: instruction fetch only happens at a translatable address.
            // The orchestrator validates this for a handoff-chosen continuation
            // but not for the budget-cut resume path, so checking every seeded PC
            // is strictly stronger than what the loop enforces itself.
            Ps1AddressTranslation.TryTranslate(segment.Request.Pc, out _).Should().BeTrue(
                $"{where} was seeded at 0x{segment.Request.Pc:X8}, which the CPU cannot fetch from");

            if (segment.Result.Snapshot is RecompilerStateSnapshot snapshot)
            {
                // R3000A: $zero is hardwired. An engine that reports otherwise has
                // corrupted the register file it is reporting.
                snapshot.Gpr[0].Should().Be(0u, $"{where} reported a non-zero $zero");
            }

            if (i == 0)
            {
                AssertSeededFrom(segment.Request, request.EntryPc, request.InitialGpr, request.InitialHi,
                    request.InitialLo, where + " (entry)");
                continue;
            }

            AssertContinuesFrom(_segments[i - 1], segment.Request, where);
        }

        // The reported final snapshot must be the run's own last snapshot, not a
        // stale or substituted one.
        result.FinalSnapshot.Should().BeSameAs(
            _segments.Count == 0 ? null : _segments[^1].Result.Snapshot,
            $"{context}: the final snapshot must be the last segment's own snapshot");

        result.State.Should().Be(
            ExpectedTerminalState(),
            $"{context}: the terminal state must classify what the run actually did " +
            $"(diag={result.DiagnosticCode} {result.DiagnosticMessage})");
    }

    /// <summary>
    /// Asserts that a segment was seeded with the state the previous segment left,
    /// which is the whole point of the outer loop: a segment boundary must be
    /// invisible to the guest. Only two transitions may reach another segment —
    /// the per-segment budget cutting a still-running guest, and a handoff that
    /// asked to continue at a chosen PC (optionally folding a return value into
    /// V0, as a BIOS dispatch would have).
    /// </summary>
    private static void AssertContinuesFrom(Segment previous, TitleExecutionSegmentRequest next, string where)
    {
        var snapshot = previous.Result.Snapshot;
        snapshot.Should().NotBeNull($"{where} followed a segment that produced no state to continue from");

        var expectedGpr = snapshot!.Gpr.ToArray();
        uint expectedPc;

        if (previous.Decision is TitleExecutionHandoffResult decision)
        {
            decision.Action.Should().Be(
                TitleExecutionHandoffAction.ContinueAt,
                $"{where} followed a handoff decision that must have terminated the run");
            if (decision.ReturnValue is uint returnValue)
            {
                expectedGpr[(int)R3000aRegister.V0] = returnValue;
            }
            expectedPc = decision.NextPc;
        }
        else
        {
            previous.HandoffConsulted.Should().BeFalse(
                $"{where} followed a declined handoff, which must have terminated the run");
            snapshot.Termination.Should().Be(
                RecompilerIrTerminationReason.ExecutionBudgetExceeded,
                $"{where} followed a segment that was not cut by the per-segment budget");
            expectedPc = snapshot.PC;
        }

        AssertSeededFrom(next, expectedPc, expectedGpr, snapshot.HI, snapshot.LO, where);
    }

    private static void AssertSeededFrom(
        TitleExecutionSegmentRequest actual,
        uint pc,
        IReadOnlyList<uint> gpr,
        uint hi,
        uint lo,
        string where)
    {
        actual.Pc.Should().Be(pc, $"{where} must resume at the state's own PC");
        actual.Hi.Should().Be(hi, $"{where} must carry HI across the boundary");
        actual.Lo.Should().Be(lo, $"{where} must carry LO across the boundary");
        actual.Gpr.Should().Equal(gpr, $"{where} must carry the whole register file across the boundary");
    }

    /// <summary>
    /// Re-derives the terminal <see cref="TitleExecutionState"/> the run is
    /// required to report, from the observed last segment alone. This restates
    /// the orchestrator's documented classification contract; it deliberately
    /// does not call into <see cref="ExecutionOrchestrator"/>, so a change to the
    /// mapping there shows up here as a failure rather than being absorbed.
    /// </summary>
    private TitleExecutionState ExpectedTerminalState()
    {
        if (_segments.Count == 0)
        {
            // The outer budget never permitted a segment.
            return TitleExecutionState.BudgetExhausted;
        }

        var last = _segments[^1];
        if (last.Result.Status != RecompilerExecutionStatus.Completed)
        {
            return TitleExecutionState.RuntimeFailure;
        }

        if (last.Result.Snapshot is not RecompilerStateSnapshot snapshot)
        {
            return TitleExecutionState.InvalidState;
        }

        switch (snapshot.Termination)
        {
            // The guest was still running when the loop ran out of segments.
            case RecompilerIrTerminationReason.ExecutionBudgetExceeded:
                return TitleExecutionState.BudgetExhausted;

            case RecompilerIrTerminationReason.UnresolvedIndirectFlow:
                return last.Result.DiagnosticCode is not null
                    ? TitleExecutionState.RuntimeFailure
                    : TitleExecutionState.RuntimeHandoff;

            case RecompilerIrTerminationReason.Success:
                return last.Decision switch
                {
                    null => TitleExecutionState.UnsupportedTransfer,
                    { Action: TitleExecutionHandoffAction.Exit } => TitleExecutionState.Completed,
                    { Action: TitleExecutionHandoffAction.Return } => TitleExecutionState.Returned,
                    { Action: TitleExecutionHandoffAction.Pause } => TitleExecutionState.RuntimeHandoff,
                    { Action: TitleExecutionHandoffAction.ContinueAt } continueAt =>
                        Ps1AddressTranslation.TryTranslate(continueAt.NextPc, out _)
                            // The continuation was accepted, so this segment was
                            // only the last one because the outer budget expired.
                            ? TitleExecutionState.BudgetExhausted
                            : TitleExecutionState.InvalidState,
                    _ => TitleExecutionState.InvalidState,
                };

            case RecompilerIrTerminationReason.Exception:
            case RecompilerIrTerminationReason.UnsupportedInstruction:
            case RecompilerIrTerminationReason.UnsupportedIr:
            case RecompilerIrTerminationReason.UnsupportedMemory:
            case RecompilerIrTerminationReason.UnsupportedMmio:
            case RecompilerIrTerminationReason.StateMismatch:
                return TitleExecutionState.RuntimeFailure;

            default:
                return TitleExecutionState.InvalidState;
        }
    }

    private sealed class Segment(TitleExecutionSegmentRequest request, RecompilerExecutionResult result)
    {
        public TitleExecutionSegmentRequest Request { get; } = request;
        public RecompilerExecutionResult Result { get; } = result;

        /// <summary>Whether the orchestrator asked the handoff about this segment.</summary>
        public bool HandoffConsulted { get; set; }

        /// <summary>The decision for this segment; null means declined (or never asked).</summary>
        public TitleExecutionHandoffResult? Decision { get; set; }
    }
}
