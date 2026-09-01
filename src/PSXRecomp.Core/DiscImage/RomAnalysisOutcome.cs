using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Classified result of one real-ROM analysis run.
///
/// Both success and failure are first-class: a failed run still reports the last
/// successful stage, the failing stage, a machine-readable failure kind, and the
/// failure reason, so a partial run remains diagnosable.
/// </summary>
[Domain]
public sealed record RomAnalysisOutcome
{
    public required RomAnalysisStatus Status { get; init; }

    /// <summary>The furthest stage that completed successfully, or <c>null</c> if none did.</summary>
    public required RomAnalysisStage? LastSuccessfulStage { get; init; }

    /// <summary>The stage that failed; <c>null</c> for a passing or skipped run.</summary>
    public RomAnalysisStage? FailedStage { get; init; }

    /// <summary>Stable machine-readable failure classification; <c>null</c> unless failed.</summary>
    public string? FailureKind { get; init; }

    /// <summary>Failure reason text; <c>null</c> unless failed.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Every recorded stage, in execution order.</summary>
    public required IReadOnlyList<RomAnalysisStageResult> Stages { get; init; }

    /// <summary>The analysis report, present only when the REPORT stage completed.</summary>
    public DiscImageAnalysisReport? Report { get; init; }

    /// <summary>
    /// Number of addresses the linear decoder could not decode. A run can pass with
    /// a non-zero count (partial decode); zero decoded instructions is a MIPS_DECODE failure.
    /// </summary>
    public int DecodeFailureCount { get; init; }

    /// <summary>
    /// The original exception behind <see cref="FailureReason"/>, when the failure came
    /// from one. Retained so callers that want to propagate the original error rather
    /// than a classified result can rethrow it with its type intact.
    /// </summary>
    internal Exception? FailureException { get; init; }

    /// <summary>
    /// Builds an outcome from a recorder and the (optional) report produced by the run.
    /// </summary>
    public static RomAnalysisOutcome From(
        RomAnalysisStageRecorder recorder,
        DiscImageAnalysisReport? report = null,
        int decodeFailureCount = 0)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        return new RomAnalysisOutcome
        {
            Status = recorder.HasFailed ? RomAnalysisStatus.Fail : RomAnalysisStatus.Pass,
            LastSuccessfulStage = recorder.LastSuccessfulStage,
            FailedStage = recorder.FailedStage,
            FailureKind = recorder.FailureKind,
            FailureReason = recorder.FailureReason,
            Stages = recorder.Results,
            Report = report,
            DecodeFailureCount = decodeFailureCount,
            FailureException = recorder.FailureException,
        };
    }

    /// <summary>
    /// Builds a skipped outcome, used when no fixture is available to analyze.
    /// </summary>
    public static RomAnalysisOutcome Skipped(string reason)
    {
        return new RomAnalysisOutcome
        {
            Status = RomAnalysisStatus.Skip,
            LastSuccessfulStage = null,
            Stages =
            [
                new RomAnalysisStageResult
                {
                    Stage = RomAnalysisStage.Start,
                    Status = RomAnalysisStageStatus.Skipped,
                    Detail = reason,
                },
            ],
        };
    }
}
