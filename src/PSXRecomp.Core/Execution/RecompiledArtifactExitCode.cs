using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Execution;

/// <summary>
/// The three-way classification a runnable recompiled artifact's execution
/// reduces to for process-exit and CLI purposes (Issue #459). Deliberately
/// coarser than <see cref="TitleExecutionState"/>: this is the stable contract
/// #460's CLI consumes, while <see cref="TitleExecutionState"/> remains the
/// full-fidelity classification carried in the machine-readable result.
/// </summary>
[Domain]
public enum RecompiledArtifactOutcome : byte
{
    /// <summary>The guest reached a classified, legitimate end.</summary>
    Success,

    /// <summary>Execution stopped at an explicit unsupported/blocked boundary —
    /// a Runtime/BIOS diagnostic, an unresolved transfer, or an exhausted
    /// budget. Not a defect: a valid, diagnosable stopping point.</summary>
    Blocked,

    /// <summary>Execution failed for a reason other than an unsupported
    /// boundary: an engine mechanism failure or a contract violation.</summary>
    Failure,
}

/// <summary>
/// Maps the full-fidelity <see cref="TitleExecutionState"/> an artifact run
/// produced onto the stable three-way <see cref="RecompiledArtifactOutcome"/>
/// and its process exit code. The numeric exit codes are this milestone's own
/// choice (Issue #459 leaves the exact values to the implementation) and, once
/// established, must not change without a documented compatibility break —
/// #460's CLI is the first consumer.
/// </summary>
[Domain]
public static class RecompiledArtifactExitCode
{
    /// <summary>Process exit code for <see cref="RecompiledArtifactOutcome.Success"/>.</summary>
    public const int Success = 0;

    /// <summary>Process exit code for <see cref="RecompiledArtifactOutcome.Failure"/>.</summary>
    public const int Failure = 1;

    /// <summary>Process exit code for <see cref="RecompiledArtifactOutcome.Blocked"/>.</summary>
    public const int Blocked = 2;

    /// <summary>Classifies a full-title execution outcome into the three-way contract.</summary>
    public static RecompiledArtifactOutcome Classify(TitleExecutionState state) => state switch
    {
        TitleExecutionState.Completed => RecompiledArtifactOutcome.Success,
        TitleExecutionState.Returned => RecompiledArtifactOutcome.Success,
        TitleExecutionState.RuntimeHandoff => RecompiledArtifactOutcome.Blocked,
        TitleExecutionState.BudgetExhausted => RecompiledArtifactOutcome.Blocked,
        TitleExecutionState.UnsupportedTransfer => RecompiledArtifactOutcome.Blocked,
        TitleExecutionState.RuntimeFailure => RecompiledArtifactOutcome.Failure,
        TitleExecutionState.InvalidState => RecompiledArtifactOutcome.Failure,
        _ => RecompiledArtifactOutcome.Failure,
    };

    /// <summary>The stable process exit code for a full-title execution outcome.</summary>
    public static int ExitCodeFor(TitleExecutionState state) => Classify(state) switch
    {
        RecompiledArtifactOutcome.Success => Success,
        RecompiledArtifactOutcome.Blocked => Blocked,
        RecompiledArtifactOutcome.Failure => Failure,
        _ => Failure,
    };
}
