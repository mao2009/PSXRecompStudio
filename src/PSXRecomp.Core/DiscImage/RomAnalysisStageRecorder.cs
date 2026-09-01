using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Accumulates <see cref="RomAnalysisStageResult"/> entries for one real-ROM
/// analysis run and tracks the last successful stage plus the failing stage.
///
/// Stages must be recorded in strictly increasing <see cref="RomAnalysisStage"/>
/// order, and nothing may be recorded after a failure. Both rules are enforced
/// so that "last successful stage" is always well-defined.
/// </summary>
[Domain]
public sealed class RomAnalysisStageRecorder
{
    private readonly List<RomAnalysisStageResult> _results = [];

    /// <summary>Stage results in execution order.</summary>
    public IReadOnlyList<RomAnalysisStageResult> Results => _results;

    /// <summary>The most recent stage recorded as passed, or <c>null</c> if none passed yet.</summary>
    public RomAnalysisStage? LastSuccessfulStage { get; private set; }

    /// <summary>The stage that failed, or <c>null</c> while the run is still healthy.</summary>
    public RomAnalysisStage? FailedStage { get; private set; }

    /// <summary>Failure reason of <see cref="FailedStage"/>.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>Machine-readable classification of <see cref="FailedStage"/>.</summary>
    public string? FailureKind { get; private set; }

    /// <summary>The exception that caused the failure, when the failure originated from one.</summary>
    public Exception? FailureException { get; private set; }

    public bool HasFailed => FailedStage is not null;

    /// <summary>Records a successful stage.</summary>
    public void Pass(RomAnalysisStage stage, string detail)
    {
        Append(stage, RomAnalysisStageStatus.Passed, detail, failureKind: null);
        LastSuccessfulStage = stage;
    }

    /// <summary>Records a stage that was deliberately not executed.</summary>
    public void Skip(RomAnalysisStage stage, string reason)
    {
        Append(stage, RomAnalysisStageStatus.Skipped, reason, failureKind: null);
    }

    /// <summary>Records a stage failure from an explicit reason.</summary>
    public void Fail(RomAnalysisStage stage, string failureKind, string reason)
    {
        Append(stage, RomAnalysisStageStatus.Failed, reason, failureKind);
        FailedStage = stage;
        FailureKind = failureKind;
        FailureReason = reason;
    }

    /// <summary>
    /// Records a stage failure caused by an exception. The exception is retained so
    /// callers that need the original error (rather than a classified result) can
    /// rethrow it without losing its type.
    /// </summary>
    public void Fail(RomAnalysisStage stage, string failureKind, Exception exception)
    {
        Fail(stage, failureKind, $"{exception.GetType().Name}: {exception.Message}");
        FailureException = exception;
    }

    private void Append(RomAnalysisStage stage, RomAnalysisStageStatus status, string detail, string? failureKind)
    {
        if (HasFailed)
        {
            throw new InvalidOperationException(
                $"Cannot record stage {stage} after the run already failed at {FailedStage}.");
        }

        if (_results.Count > 0 && stage <= _results[^1].Stage)
        {
            throw new InvalidOperationException(
                $"Stage {stage} must be recorded after {_results[^1].Stage}; stages are strictly ordered.");
        }

        _results.Add(new RomAnalysisStageResult
        {
            Stage = stage,
            Status = status,
            Detail = detail,
            FailureKind = failureKind,
        });
    }
}
