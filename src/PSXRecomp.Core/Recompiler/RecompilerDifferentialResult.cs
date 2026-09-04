using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Recompiler;

/// <summary>
/// The result of running one fixture through both executors and comparing their
/// state snapshots.
/// </summary>
[Domain]
public sealed record RecompilerDifferentialResult(
    RecompilerDifferentialFixture Fixture,
    RecompilerExecutionResult Reference,
    RecompilerExecutionResult Actual,
    RecompilerStateDiffResult? Diff)
{
    /// <summary>True when both executors completed and a state comparison exists.</summary>
    public bool BothCompleted =>
        Reference.Status == RecompilerExecutionStatus.Completed &&
        Actual.Status == RecompilerExecutionStatus.Completed &&
        Diff is not null;

    /// <summary>True when both completed and the state snapshots match.</summary>
    public bool IsMatch => BothCompleted && Diff!.IsMatch;
}

/// <summary>
/// Orchestrates a single differential run: executes the fixture on the reference
/// (interpreter) executor and the actual (recompiled) executor, then compares
/// their state snapshots. Pure orchestration — it does not itself perform
/// host compiles, file I/O or process control.
/// </summary>
[Domain]
public static class RecompilerDifferentialRunner
{
    public static RecompilerDifferentialResult Run(
        RecompilerDifferentialFixture fixture,
        IRecompilerExecutor reference,
        IRecompilerExecutor actual)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(actual);

        var referenceResult = reference.Execute(fixture);
        var actualResult = actual.Execute(fixture);

        RecompilerStateDiffResult? diff = referenceResult.Snapshot is not null && actualResult.Snapshot is not null
            ? RecompilerStateDiff.Compare(referenceResult.Snapshot, actualResult.Snapshot)
            : null;

        return new RecompilerDifferentialResult(fixture, referenceResult, actualResult, diff);
    }
}
