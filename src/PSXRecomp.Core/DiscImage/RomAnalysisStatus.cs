namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Outcome of a single <see cref="RomAnalysisStage"/>.
/// </summary>
public enum RomAnalysisStageStatus
{
    /// <summary>The stage completed successfully.</summary>
    Passed = 0,

    /// <summary>The stage was deliberately not executed (for example, a non-CHD input skips CHD_OPEN).</summary>
    Skipped = 1,

    /// <summary>The stage failed; the run stops here.</summary>
    Failed = 2,
}

/// <summary>
/// Overall classification of a real-ROM analysis run.
/// </summary>
public enum RomAnalysisStatus
{
    /// <summary>Every executed stage passed.</summary>
    Pass = 0,

    /// <summary>A stage failed; <c>FailedStage</c> and <c>FailureReason</c> identify it.</summary>
    Fail = 1,

    /// <summary>The run was not executed at all, typically because no fixture was available.</summary>
    Skip = 2,
}
