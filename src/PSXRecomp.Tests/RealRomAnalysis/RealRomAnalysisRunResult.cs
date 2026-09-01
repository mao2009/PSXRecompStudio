using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.DiscImage.AnalysisArtifacts;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// In-memory result of one real-ROM analysis run as driven by the Issue #213
/// orchestration (analyze → persist → classify).
///
/// This is a test-layer result object only, never a persisted schema: the only things
/// written to disk are the #215 deterministic artifacts and the execution log (see
/// <see cref="RealRomArtifactWriter"/>). No competing "run-summary.json" is produced.
///
/// Persistence is best-effort and isolated per fixture: <see cref="ReportPath"/> and
/// <see cref="LogPath"/> are <c>null</c> when that artifact could not be written, and the
/// failure is classified as <see cref="RealRomAnalyzer.ArtifactPersistenceFailure"/> at
/// the MANIFEST stage on <see cref="Outcome"/>. Callers must not assume a summary or log
/// always exists.
/// </summary>
[Test]
public sealed record RealRomAnalysisRunResult
{
    /// <summary>Collision-free fixture alias (see <see cref="AnalysisArtifactSchema.DisambiguateFixtureIds"/>).</summary>
    public required string FixtureId { get; init; }

    /// <summary>Stage-aware outcome, including MANIFEST / COMPLETE recorded by the orchestration layer.</summary>
    public required RomAnalysisOutcome Outcome { get; init; }

    /// <summary>Deterministic artifacts; <c>null</c> when analysis failed before the REPORT stage.</summary>
    public RealRomAnalysisArtifacts? Artifacts { get; init; }

    /// <summary>Persisted artifact directory; <c>null</c> when the run failed before REPORT or persistence failed.</summary>
    public string? ReportPath { get; init; }

    /// <summary>Persisted execution log path; <c>null</c> when the log could not be written.</summary>
    public string? LogPath { get; init; }

    /// <summary>True when at least one artifact (report or log) was successfully persisted.</summary>
    public bool AnyArtifactAvailable => ReportPath is not null || LogPath is not null;
}
