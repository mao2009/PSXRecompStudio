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

    /// <summary>The segment ended because the engine's diagnostic <c>StopRequested</c> fired (a probe's stop PC), so
    /// the configured instruction budget was not exhausted. Instructions retired before the stop are still counted in
    /// the outcome; only an exhaustion claim is withheld.</summary>
    public const string StoppedOnRequest = "ARTIFACT_FALLBACK_STOP_REQUESTED";

    /// <summary>The run made more artifact-to-interpreter handoffs than its transition budget.</summary>
    public const string TransitionBudgetExhausted = "ARTIFACT_FALLBACK_TRANSITION_BUDGET_EXHAUSTED";

    /// <summary>The RAM copy-sync failed its integrity check; no side was left half-updated.</summary>
    public const string SyncFailed = "ARTIFACT_FALLBACK_SYNC_FAILED";

    /// <summary>Static eligibility: the transfer target is not a 4-byte-aligned PC (Issue #763).</summary>
    public const string IneligibleUnaligned = "ARTIFACT_FALLBACK_INELIGIBLE_UNALIGNED";

    /// <summary>Static eligibility: the target is outside the executable image and the run is not guest firmware (Issue #763).</summary>
    public const string IneligibleOutOfImage = "ARTIFACT_FALLBACK_INELIGIBLE_OUT_OF_IMAGE";

    /// <summary>Static eligibility: the target already has a compiled block, so a fallback must not claim it (Issue #763).</summary>
    public const string IneligibleHasBlock = "ARTIFACT_FALLBACK_INELIGIBLE_HAS_BLOCK";

    /// <summary>The artifact refused the fallback query (unsupported protocol version) (Issue #763).</summary>
    public const string IneligibleQueryRefused = "ARTIFACT_FALLBACK_INELIGIBLE_QUERY_REFUSED";

    /// <summary>The transfer did not come from JR/JALR: a statically-known target the build should have compiled (Issue #763).</summary>
    public const string IneligibleNotIndirect = "ARTIFACT_FALLBACK_INELIGIBLE_NOT_INDIRECT";
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
/// <param name="LastEntryPc">The target of the most recent handoff that ran to the end of its segment, or null when none did (Issue #763).</param>
/// <param name="LastReturnPc">Where that segment returned to a compiled block, or null when it did not return.</param>
[Domain]
public sealed record MixedFallbackEvidence(
    uint Transitions,
    uint Returns,
    ulong FallbackInstructions,
    ulong PagesToInterpreter,
    ulong PagesToArtifact,
    IReadOnlyList<MixedFallbackTarget> Targets,
    uint? LastEntryPc = null,
    uint? LastReturnPc = null);

/// <summary>
/// The guest MMIO access the host last relayed to the device graph (Issue #763; measurement only). When a run is stopped from
/// outside (a timeout) the guest's pc is unknowable, but the register it last touched says what it was waiting on.
/// </summary>
/// <param name="PhysicalAddress">The physical register address.</param>
/// <param name="Width">Access width in bytes (1, 2 or 4).</param>
/// <param name="IsWrite">Whether it was a store.</param>
[Domain]
public sealed record HostMmioAccess(uint PhysicalAddress, int Width, bool IsWrite);

/// <summary>
/// One artifact-to-interpreter handoff as it ended (Issue #732): what a measurement layer needs to classify why it
/// happened and what it cost. Observation only; reported once per transition, after its segment ran.
/// </summary>
/// <param name="EntryPc">The PC the artifact had no block for.</param>
/// <param name="Indirect">The transfer came from JR/JALR (its target was not known when the image was built).</param>
/// <param name="Status">How the segment ended.</param>
/// <param name="ExitPc">The PC the segment ended at: the compiled block entry it returned to, or where it stopped.</param>
/// <param name="RetiredInstructions">Guest instructions the interpreter retired in the segment.</param>
/// <param name="DiagnosticCode">The stop's code; null when the segment returned.</param>
/// <param name="AotClass">Why no AOT code ran here, one of <see cref="MixedFallbackAotClass"/>, when the code that made the
/// handoff knows it (an AOT image table does); null lets the measurement layer derive it from the address alone.</param>
[Domain]
public sealed record MixedFallbackTransition(
    uint EntryPc, bool Indirect, FallbackSegmentStatus Status, uint ExitPc, ulong RetiredInstructions, string? DiagnosticCode,
    string? AotClass = null,
    FallbackEntryAttribution? Attribution = null);

/// <summary>
/// Why one fallback entry had no native code, decided on entry from the loaded-code table and the RAM the artifact held
/// (measurement only; computed only when a transition observer is installed).
/// </summary>
/// <param name="RegisteredVersions">How many guarded versions of RAM-placed code are registered at the entry PC (0: none).</param>
/// <param name="MatchedVersion">Index of a registered version whose words equal RAM, or -1 when none does. A match here
/// would be an anomaly: the artifact only offers a PC when no version matched.</param>
/// <param name="PreviousReturnPc">Where the previous fallback segment returned to a compiled block, or null for the first / after a non-return.</param>
[Domain]
public sealed record FallbackEntryAttribution(int RegisteredVersions, int MatchedVersion, uint? PreviousReturnPc);

/// <summary>
/// Why a PC that reached the interpreter fallback had no AOT code (Issue #732): the taxonomy a fallback is measured by.
/// Every class except <see cref="RuntimeGenerated"/> is code that could in principle be compiled ahead of time.
/// </summary>
[Domain]
public static class MixedFallbackAotClass
{
    /// <summary>The PC lies in a statically known code image that has no block for it (yet).</summary>
    public const string KnownNotYetAot = "known-not-yet-aot";

    /// <summary>No AOT image covers the PC: statically unknown to this build.</summary>
    public const string NotInAnyImage = "not-in-any-aot-image";

    /// <summary>An AOT image covers the PC but its content hash or generation differs from guest memory.</summary>
    public const string ImageStale = "image-stale";

    /// <summary>The range was overwritten by another image than the one compiled for it.</summary>
    public const string RegionOverwritten = "region-overwritten";

    /// <summary>Class C: run-time generated or unanalysable self-modifying code; not pre-determinable.</summary>
    public const string RuntimeGenerated = "runtime-generated";
}

/// <summary>Wall-clock costs of one run's mixed execution. Measurement only: never part of a deterministic document.</summary>
/// <param name="TransferMilliseconds">Time spent in the RAM/CPU copy-sync protocol (both directions, including pipe I/O).</param>
/// <param name="FallbackMilliseconds">Time spent executing interpreter segments.</param>
[Domain]
public sealed record MixedFallbackTimings(double TransferMilliseconds, double FallbackMilliseconds);

/// <summary>
/// The last control transfer the host declined to claim (Issue #763): the transfer the artifact then treated as unsupported.
/// Observation only, captured host-side: it carries the target and whether the mixed-execution fallback was consulted, not
/// the transfer's source or opcode (those are not on the artifact's wire protocol).
/// </summary>
/// <param name="TargetPc">The unresolved PC the artifact offered; the run's stop PC when a decline ends it.</param>
/// <param name="FallbackAttempted">Whether mixing was enabled and the fallback was asked about this target.</param>
/// <param name="RejectReason">Why the fallback declined, one of <see cref="MixedFallbackDiagnostics"/>'s "ineligible" codes;
/// null when mixing was off (not asked) or the fallback stopped the run instead of declining.</param>
[Domain]
public sealed record FallbackDeclinedTransfer(uint TargetPc, bool FallbackAttempted, string? RejectReason);
