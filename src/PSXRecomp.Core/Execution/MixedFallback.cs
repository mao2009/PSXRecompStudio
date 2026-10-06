using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Execution;

/// <summary>
/// Opt-in configuration of mixed execution (Issue #693, ADR-012/014/015/016/025 amendments): the generated-host
/// artifact may hand a guest control transfer it has no compiled block for to the interpreter and take control back
/// at a clean compiled block entry. A null options value anywhere it is accepted means the pre-existing behavior:
/// such a transfer stops the run as <c>UNRESOLVED_TRANSFER[_IN_IMAGE]</c>.
/// </summary>
/// <param name="SegmentInstructionBudget">The most guest instructions one fallback segment may retire. Exhausting it
/// stops the run with <see cref="MixedFallbackDiagnostics.SegmentBudgetExhausted"/>.</param>
/// <param name="MaxTransitions">The most artifact-to-interpreter handoffs one run may make. Exceeding it stops the
/// run with <see cref="MixedFallbackDiagnostics.TransitionBudgetExhausted"/>. A repeated target is not an error (a
/// legitimate callback is entered every frame), so there is deliberately no per-PC limit.</param>
[Domain]
public sealed record MixedFallbackOptions(
    uint SegmentInstructionBudget = MixedFallbackOptions.DefaultSegmentInstructionBudget,
    uint MaxTransitions = MixedFallbackOptions.DefaultMaxTransitions)
{
    /// <summary>The default per-segment instruction cap.</summary>
    public const uint DefaultSegmentInstructionBudget = 1_000_000u;

    /// <summary>The default per-run transition cap.</summary>
    public const uint DefaultMaxTransitions = 100_000u;

    /// <summary>Whether both budgets are positive.</summary>
    public bool IsValid => SegmentInstructionBudget != 0 && MaxTransitions != 0;
}

/// <summary>Stable diagnostic codes of the mixed-execution fail-closed boundaries (Issue #693).</summary>
[Domain]
public static class MixedFallbackDiagnostics
{
    /// <summary>The fallback reached a CPU/pipeline/region state that cannot be written back to the artifact.</summary>
    public const string UnsupportedState = "ARTIFACT_FALLBACK_UNSUPPORTED_STATE";

    /// <summary>The fallback raised an exception the interpreter loop does not service (not INT, not SYSCALL).</summary>
    public const string ExceptionUnsupported = "ARTIFACT_FALLBACK_EXCEPTION_UNSUPPORTED";

    /// <summary>One fallback segment retired its whole instruction budget without reaching a clean block entry.</summary>
    public const string SegmentBudgetExhausted = "ARTIFACT_FALLBACK_BUDGET_EXHAUSTED";

    /// <summary>The run made more artifact-to-interpreter handoffs than its transition budget.</summary>
    public const string TransitionBudgetExhausted = "ARTIFACT_FALLBACK_TRANSITION_BUDGET_EXHAUSTED";

    /// <summary>The RAM copy-sync failed its integrity check; no side was left half-updated.</summary>
    public const string SyncFailed = "ARTIFACT_FALLBACK_SYNC_FAILED";
}

/// <summary>
/// The CPU state a fallback segment starts from and ends with: everything the artifact owns that the interpreter
/// needs (Issue #693). COP0 beyond SR/CAUSE/EPC is not modelled by the artifact; the interpreter core keeps it
/// across segments because the core is the host-owned graph's own, never rebuilt.
/// </summary>
/// <param name="Gpr">Exactly 32 values; <c>$zero</c> is read as 0.</param>
/// <param name="Hi">HI.</param>
/// <param name="Lo">LO.</param>
/// <param name="Pc">The next instruction's PC.</param>
/// <param name="Sr">COP0 SR.</param>
/// <param name="Cause">COP0 CAUSE.</param>
/// <param name="Epc">COP0 EPC.</param>
[Domain]
public sealed record FallbackCpuState(IReadOnlyList<uint> Gpr, uint Hi, uint Lo, uint Pc, uint Sr, uint Cause, uint Epc);

/// <summary>How one fallback segment ended.</summary>
[Domain]
public enum FallbackSegmentStatus
{
    /// <summary>It stopped before executing a compiled block entry at a clean boundary: the artifact may resume there.</summary>
    Returned,

    /// <summary>It retired its whole instruction budget without such a boundary.</summary>
    BudgetExhausted,

    /// <summary>It stopped at an explicit, diagnosed boundary (a BIOS/kernel stop, an unserviced exception, left the image).</summary>
    Stopped,
}

/// <summary>The result of one fallback segment.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="State">The CPU state at that point.</param>
/// <param name="RetiredInstructions">Guest instructions retired by the interpreter during the segment.</param>
/// <param name="DiagnosticCode">The stop's stable code, or null for <see cref="FallbackSegmentStatus.Returned"/>.</param>
/// <param name="DiagnosticMessage">The stop's message, or null.</param>
[Domain]
public sealed record FallbackSegmentOutcome(
    FallbackSegmentStatus Status,
    FallbackCpuState State,
    ulong RetiredInstructions,
    string? DiagnosticCode,
    string? DiagnosticMessage);

/// <summary>One distinct fallback target and what the run spent there (Issue #693).</summary>
/// <param name="Target">The in-image PC the artifact had no block for.</param>
/// <param name="Entries">How many times control was handed to the interpreter at it.</param>
/// <param name="Instructions">Guest instructions the interpreter retired in those segments.</param>
/// <param name="LastReturnPc">The compiled block entry the most recent segment returned at.</param>
[Domain]
public sealed record MixedFallbackTarget(uint Target, ulong Entries, ulong Instructions, uint LastReturnPc);

/// <summary>
/// Deterministic evidence of one run's mixed execution (Issue #693): counts only, so two runs of the same input produce
/// the same document (<see cref="MixedFallbackTimings"/> carries the non-deterministic costs separately).
/// </summary>
/// <param name="Transitions">Artifact-to-interpreter handoffs.</param>
/// <param name="Returns">Handoffs that ended with a clean return to a compiled block.</param>
/// <param name="FallbackInstructions">Guest instructions the interpreter retired in total.</param>
/// <param name="PagesToInterpreter">RAM pages copied artifact to interpreter (4096 bytes each).</param>
/// <param name="PagesToArtifact">RAM pages copied interpreter to artifact.</param>
/// <param name="Targets">Per distinct target, ascending by target.</param>
[Domain]
public sealed record MixedFallbackEvidence(
    uint Transitions,
    uint Returns,
    ulong FallbackInstructions,
    ulong PagesToInterpreter,
    ulong PagesToArtifact,
    IReadOnlyList<MixedFallbackTarget> Targets);

/// <summary>Wall-clock costs of one run's mixed execution. Measurement only: never part of a deterministic document.</summary>
/// <param name="TransferMilliseconds">Time spent in the RAM/CPU copy-sync protocol (both directions, including pipe I/O).</param>
/// <param name="FallbackMilliseconds">Time spent executing interpreter segments.</param>
[Domain]
public sealed record MixedFallbackTimings(double TransferMilliseconds, double FallbackMilliseconds);
