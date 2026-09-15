using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Core.Execution;

/// <summary>
/// Drives recompiled guest code through a complete CPU execution loop across
/// segment boundaries.
/// </summary>
/// <remarks>
/// <para>
/// The orchestrator owns only the loop: it seeds an
/// <see cref="IRecompiledExecutionEngine"/>, runs bounded segments under an
/// outer budget, and turns each segment's CPU-level termination plus the
/// handoff's decision into a <see cref="TitleExecutionState"/>. It adds no CPU
/// semantics and no BIOS semantics — engines dispatch the shared
/// <c>BiosVectorDispatch</c> in-band, and this class only routes the segment
/// result and applies a handoff-chosen continuation. It is deliberately
/// title-agnostic: there is no knowledge of any specific game, function shape,
/// or BIOS personality here.
/// </para>
/// <para>
/// The loop always terminates: a guest is cut by the outer budget, by an
/// engine's per-segment budget, by a Runtime diagnostic, or by an unresolved
/// transfer the handoff declines, so any input ends in a classified state.
/// </para>
/// </remarks>
[Domain]
public sealed class ExecutionOrchestrator
{
    /// <summary>
    /// Runs a full-title execution over <paramref name="engine"/> from the
    /// initial state in <paramref name="request"/>, consulting
    /// <paramref name="handoff"/> whenever the guest lands on a PC the engine
    /// cannot continue from.
    /// </summary>
    /// <param name="engine">The recompiled-execution backend. The orchestrator
    /// seeds it via <see cref="IRecompiledExecutionEngine.Load"/>.</param>
    /// <param name="handoff">Optional continuation rule-set for unresolved
    /// segment ends. May be null; a null handoff (or one that returns null for
    /// a PC) reports <see cref="TitleExecutionState.UnsupportedTransfer"/> for
    /// that transfer.</param>
    /// <param name="request">The initial guest state and budgets.</param>
    /// <returns>A classified terminal outcome. Guest failure paths never throw;
    /// only contract-contradicting segments (engine or handoff) map to
    /// <see cref="TitleExecutionState.InvalidState"/>; engine mechanism and
    /// CPU-level execution stops are <see cref="TitleExecutionState.RuntimeFailure"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> or
    /// <paramref name="request"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The engine's
    /// <see cref="IRecompiledExecutionEngine.Load"/> failed (loading is a
    /// caller-side preparation step, not a guest failure, so it surfaces).</exception>
    public TitleExecutionResult Execute(
        IRecompiledExecutionEngine engine,
        ITitleExecutionHandoff? handoff,
        TitleExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(request);

        engine.Load(request);

        var gpr = request.InitialGpr.ToArray();
        uint hi = request.InitialHi;
        uint lo = request.InitialLo;
        uint pc = request.EntryPc;
        uint outer = request.OuterBudget;
        uint segments = 0;
        RecompilerStateSnapshot? last = null;

        while (true)
        {
            if (outer == 0)
            {
                return Terminal(
                    TitleExecutionState.BudgetExhausted,
                    last,
                    segments,
                    engine.Name,
                    "OUTER_BUDGET_EXHAUSTED",
                    "The outer execution budget expired while the guest was still running.");
            }

            var result = engine.RunSegment(new TitleExecutionSegmentRequest(gpr, hi, lo, pc, request.SegmentBudget));
            segments++;
            last = result.Snapshot;

            if (result.Status != RecompilerExecutionStatus.Completed)
            {
                // An engine mechanism failure (host process lost, timeout,
                // malformed output) is a failed run, not a handoff/contract
                // violation, so it is a RuntimeFailure with the engine's code.
                return Terminal(
                    TitleExecutionState.RuntimeFailure,
                    last,
                    segments,
                    engine.Name,
                    result.DiagnosticCode ?? "ENGINE_FAILED",
                    result.DiagnosticMessage ?? "The execution engine failed to complete the segment.");
            }

            if (result.Snapshot is null)
            {
                // A completed engine that produced no snapshot contradicts the
                // execution contract; that is the engine's contract violation.
                return Terminal(
                    TitleExecutionState.InvalidState,
                    last,
                    segments,
                    engine.Name,
                    "MISSING_SNAPSHOT",
                    "The execution engine reported a completed segment without a state snapshot.");
            }

            var snap = result.Snapshot;
            gpr = snap.Gpr.ToArray();
            hi = snap.HI;
            lo = snap.LO;
            pc = snap.PC;
            outer--;

            switch (snap.Termination)
            {
                case RecompilerIrTerminationReason.ExecutionBudgetExceeded:
                    // The inner (per-segment) budget cut the run while the guest
                    // was still progressing; the outer budget decides whether
                    // another segment is allowed.
                    continue;

                case RecompilerIrTerminationReason.UnresolvedIndirectFlow when result.DiagnosticCode is not null:
                    // A Runtime/BIOS dispatch stopped the run and said why.
                    return Terminal(
                        TitleExecutionState.RuntimeFailure,
                        snap,
                        segments,
                        engine.Name,
                        result.DiagnosticCode,
                        result.DiagnosticMessage);

                case RecompilerIrTerminationReason.UnresolvedIndirectFlow:
                    // The guest stopped cleanly at the Runtime boundary without a
                    // diagnostic: the outer world takes over from here.
                    return Terminal(TitleExecutionState.RuntimeHandoff, snap, segments, engine.Name, null, null);

                case RecompilerIrTerminationReason.Success:
                    // Control landed on a PC the engine has no code for (a return
                    // to an uncompiled caller, a patched target outside the block
                    // table, or any other unresolved transfer). Ask the handoff.
                    var decision = handoff?.Decide(snap);
                    if (decision is null)
                    {
                        return Terminal(
                            TitleExecutionState.UnsupportedTransfer,
                            snap,
                            segments,
                            engine.Name,
                            "UNRESOLVED_TRANSFER",
                            $"Guest control transferred to 0x{snap.PC:X8}, which the engine has no " +
                            "compiled code for and the handoff has no continuation rule for. Dynamic " +
                            "overlay recompilation (Issue #249) is the upgrade path.");
                    }

                    switch (decision.Value.Action)
                    {
                        case TitleExecutionHandoffAction.Exit:
                            return Terminal(TitleExecutionState.Completed, snap, segments, engine.Name, null, null);

                        case TitleExecutionHandoffAction.Return:
                            return Terminal(TitleExecutionState.Returned, snap, segments, engine.Name, null, null);

                        case TitleExecutionHandoffAction.Pause:
                            return Terminal(TitleExecutionState.RuntimeHandoff, snap, segments, engine.Name, null, null);

                        case TitleExecutionHandoffAction.ContinueAt:
                            // A continuation PC must be a translatable guest
                            // address; anything else is a handoff contract
                            // violation, not a guest failure.
                            if (!Ps1AddressTranslation.TryTranslate(decision.Value.NextPc, out _))
                            {
                                return Terminal(
                                    TitleExecutionState.InvalidState,
                                    snap,
                                    segments,
                                    engine.Name,
                                    "INVALID_CONTINUATION_TARGET",
                                    $"The handoff named continuation target 0x{decision.Value.NextPc:X8}, " +
                                    "which is not a translatable guest address.");
                            }

                            // Fold any return value into V0 (what a BIOS dispatch
                            // would have left there) and resume from the target.
                            if (decision.Value.ReturnValue is uint returnValue)
                            {
                                gpr[(int)R3000aRegister.V0] = returnValue;
                            }

                            pc = decision.Value.NextPc;
                            continue;

                        default:
                            return Terminal(
                                TitleExecutionState.InvalidState,
                                snap,
                                segments,
                                engine.Name,
                                "INVALID_HANDOFF_ACTION",
                                $"The handoff returned an undefined action {decision.Value.Action}.");
                    }

                case RecompilerIrTerminationReason.Exception:
                    return Terminal(
                        TitleExecutionState.RuntimeFailure,
                        snap,
                        segments,
                        engine.Name,
                        "CPU_EXCEPTION",
                        $"The guest raised a CPU exception at PC 0x{snap.PC:X8}.");

                case RecompilerIrTerminationReason.UnsupportedInstruction:
                case RecompilerIrTerminationReason.UnsupportedIr:
                case RecompilerIrTerminationReason.UnsupportedMemory:
                case RecompilerIrTerminationReason.UnsupportedMmio:
                case RecompilerIrTerminationReason.StateMismatch:
                    // The engine could not execute the guest at that point. That
                    // is a failed run (the recompiled path lacks coverage for it),
                    // not a clean transfer to an uncompiled PC — so RuntimeFailure,
                    // never UnsupportedTransfer.
                    return Terminal(
                        TitleExecutionState.RuntimeFailure,
                        snap,
                        segments,
                        engine.Name,
                        snap.Termination.ToString(),
                        $"The execution engine could not execute at PC 0x{snap.PC:X8}: {snap.Termination}.");

                default:
                    return Terminal(
                        TitleExecutionState.InvalidState,
                        snap,
                        segments,
                        engine.Name,
                        "UNEXPECTED_TERMINATION",
                        $"The execution engine reported an unexpected termination reason {snap.Termination}.");
            }
        }
    }

    private static TitleExecutionResult Terminal(
        TitleExecutionState state,
        RecompilerStateSnapshot? snapshot,
        uint segmentsRetired,
        string engineName,
        string? diagnosticCode,
        string? diagnosticMessage) =>
        new(state, snapshot, segmentsRetired, engineName, diagnosticCode, diagnosticMessage);
}