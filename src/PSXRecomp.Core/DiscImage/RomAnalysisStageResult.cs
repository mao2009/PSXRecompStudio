using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Result of one stage of a real-ROM analysis run.
///
/// Deliberately carries no timing or wall-clock information so a sequence of
/// stage results is deterministic and diff-comparable; timing belongs to the
/// detailed execution log, not to the summary.
/// </summary>
[Domain]
public sealed record RomAnalysisStageResult
{
    public required RomAnalysisStage Stage { get; init; }

    public required RomAnalysisStageStatus Status { get; init; }

    /// <summary>Human-readable stage detail; for a failure, the failure reason.</summary>
    public required string Detail { get; init; }

    /// <summary>
    /// Stable machine-readable failure classification (for example
    /// <c>SystemCnfMissing</c>), set only when <see cref="Status"/> is
    /// <see cref="RomAnalysisStageStatus.Failed"/>.
    /// </summary>
    public string? FailureKind { get; init; }
}
