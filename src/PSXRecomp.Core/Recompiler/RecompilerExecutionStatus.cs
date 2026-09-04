using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Recompiler;

/// <summary>
/// Classifies whether an executor mechanism itself produced a usable state
/// snapshot for a fixture. This is distinct from the CPU-level termination
/// reason carried inside the snapshot (Success, UnsupportedInstruction,
/// ExecutionBudgetExceeded, ...): <see cref="RecompilerExecutionStatus"/>
/// describes the executor, while <see cref="RecompilerStateSnapshot.Termination"/>
/// describes the simulated CPU.
/// </summary>
[Domain]
public enum RecompilerExecutionStatus : byte
{
    /// <summary>The executor produced a valid snapshot (Completed).</summary>
    Completed,

    /// <summary>The host source generation step failed.</summary>
    GenerationFailed,

    /// <summary>The generated host source failed to compile with the fixed recipe.</summary>
    BuildFailed,

    /// <summary>The host process failed to start or terminated without producing a snapshot.</summary>
    ExecutionFailed,

    /// <summary>The host process exceeded the bounded execution budget.</summary>
    TimedOut,

    /// <summary>The host process produced output that could not be parsed into a snapshot.</summary>
    MalformedResult,
}

/// <summary>
/// The outcome of running one executor against one fixture.
/// </summary>
[Domain]
public sealed record RecompilerExecutionResult(
    RecompilerExecutionStatus Status,
    RecompilerStateSnapshot? Snapshot,
    string? DiagnosticCode,
    string? DiagnosticMessage)
{
    /// <summary>Convenience: an execution that produced a valid snapshot.</summary>
    public static RecompilerExecutionResult Completed(RecompilerStateSnapshot snapshot) =>
        new(RecompilerExecutionStatus.Completed, snapshot, null, null);

    /// <summary>Convenience: an executor mechanism failure with a machine-readable code.</summary>
    public static RecompilerExecutionResult Failed(
        RecompilerExecutionStatus status, string diagnosticCode, string diagnosticMessage) =>
        new(status, null, diagnosticCode, diagnosticMessage);
}
