using System.Diagnostics;
using System.Security.Cryptography;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.DiscImage.AnalysisArtifacts;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Runs the Issue #213 staged pipeline against a local disc image and hands the result to
/// the deterministic serialization layer, producing two clearly separated outputs:
///
/// <list type="bullet">
///   <item><b>Deterministic artifacts</b> (<see cref="RealRomAnalysisArtifacts"/>) —
///   identical byte-for-byte for identical input. Safe to persist and diff.</item>
///   <item><b>Execution log</b> (<see cref="ExecutionLogEntry"/>) — stage/progress
///   records that intentionally carry elapsed time and are therefore local-only.</item>
/// </list>
///
/// The two must never mix: nothing from the log reaches an artifact, and the artifact
/// builder cannot read the clock even if asked to (Domain-layer PSXR005).
///
/// Analysis itself is <em>not</em> re-implemented here: <see cref="RomAnalysisPipeline"/>
/// owns the stage sequence (START → … → REPORT) and returns a classified
/// <see cref="RomAnalysisOutcome"/>, and <see cref="DiscImageAnalyzer.CreateIsoReader"/>
/// remains the single CHD→ISO reader. This type only reads the file, records the I/O-side
/// MANIFEST / COMPLETE stages, and serializes.
///
/// The disc image is read streaming (never buffered whole into memory): the file is hashed
/// with a streaming SHA-256 and passed to <see cref="RomAnalysisPipeline.RunFromChd(Stream, string, int?, RomAnalysisStageRecorder?)"/>
/// as a caller-owned, seekable stream. The pipeline manages only the <see cref="ChdReader"/>
/// lifetime and never disposes the caller's stream.
/// </summary>
[Test]
public static class RealRomAnalyzer
{
    private const string DiscImageFormat = "CHD";

    /// <summary>Stable classification for any artifact/log write failure at the MANIFEST stage.</summary>
    public const string ArtifactPersistenceFailure = "ArtifactPersistenceFailure";

    /// <summary>Stable classification when the post-REPORT CHD/ISO metadata pass cannot read the disc.</summary>
    public const string DiscMetadataUnreadable = "DiscMetadataUnreadable";

    /// <summary>
    /// Runs the pipeline against the local disc image and returns the deterministic
    /// artifact set together with the detailed execution log. The staged outcome and
    /// shared recorder are also returned so the orchestration layer can append the
    /// MANIFEST / COMPLETE stages after persistence.
    /// </summary>
    /// <param name="fixtureId">
    /// Canonical fixture alias (see <see cref="AnalysisArtifactSchema.NormalizeFixtureId"/>).
    /// Used for the artifact directory name only; the formal identity is the disc SHA-256.
    /// </param>
    public static (RealRomAnalysisArtifacts? Artifacts, RomAnalysisOutcome Outcome,
        RomAnalysisStageRecorder Recorder, List<ExecutionLogEntry> Log) AnalyzeStaged(
        string discImagePath,
        string fixtureId,
        int? instructionCount = null)
    {
        return AnalyzeStagedCore(discImagePath, fixtureId, capture: null, instructionCount);
    }

    /// <summary>
    /// Core of <see cref="AnalyzeStaged"/>. The post-REPORT metadata/stats capture is
    /// injected via <paramref name="capture"/> (null uses the real
    /// <see cref="CaptureChdMetadata"/>) so the failure boundary around it can be exercised
    /// deterministically in tests without a real disc. The boundary classifies any expected
    /// analysis/file-access read failure as <see cref="DiscMetadataUnreadable"/> at MANIFEST
    /// and never lets it escape to the caller; <see cref="RunAll"/> therefore stays isolated
    /// per fixture.
    /// </summary>
    internal static (RealRomAnalysisArtifacts? Artifacts, RomAnalysisOutcome Outcome,
        RomAnalysisStageRecorder Recorder, List<ExecutionLogEntry> Log) AnalyzeStagedCore(
        string discImagePath,
        string fixtureId,
        Func<string, (ChdMapStatistics Chd, IsoVolumeStatistics Iso)>? capture,
        int? instructionCount = null)
    {
        ArgumentNullException.ThrowIfNull(discImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureId);

        var recorder = new RomAnalysisStageRecorder();
        var log = new List<ExecutionLogEntry>();
        var watch = Stopwatch.StartNew();

        void Record(string stage, string status, string message) =>
            log.Add(new ExecutionLogEntry
            {
                Stage = stage,
                Status = status,
                Message = message,
                ElapsedMs = Math.Round(watch.Elapsed.TotalMilliseconds, 3),
            });

        long sizeBytes = 0;
        string sha256 = string.Empty;
        try
        {
#pragma warning disable PSXR005
            sizeBytes = new FileInfo(discImagePath).Length;
            using (var hashStream = File.OpenRead(discImagePath))
            {
                sha256 = ComputeSha256(hashStream);
            }
#pragma warning restore PSXR005

            Record("INPUT", "PASS", $"Read {sizeBytes} bytes; SHA-256 {sha256}");
        }
        catch (Exception ex) when (IsFileAccessFailure(ex))
        {
            Record("INPUT", "FAILED", ex.Message);
            recorder.Fail(RomAnalysisStage.Input, "FixtureUnreadable", ex);
            return (null, RomAnalysisOutcome.From(recorder), recorder, log);
        }

        RomAnalysisOutcome outcome;
#pragma warning disable PSXR005
        using (var runStream = File.OpenRead(discImagePath))
#pragma warning restore PSXR005
        {
            outcome = RomAnalysisPipeline.RunFromChd(runStream, sha256, instructionCount, recorder);
        }

        foreach (var stage in outcome.Stages)
        {
            Record(stage.Stage.ToString(), StatusLabel(stage.Status),
                stage.Detail + (stage.FailureKind is not null ? $" [{stage.FailureKind}]" : string.Empty));
        }

        if (outcome.Report is null)
        {
            // Pipeline failed before REPORT; no deterministic artifacts can be built.
            return (null, outcome, recorder, log);
        }

        capture ??= CaptureChdMetadata;

        ChdMapStatistics chdStats;
        IsoVolumeStatistics isoStats;
        try
        {
            (chdStats, isoStats) = capture(discImagePath);
        }
        catch (Exception ex) when (IsPostReportMetadataFailure(ex))
        {
            // REPORT already passed; the failure is at MANIFEST (the CHD/ISO metadata could
            // not be read to build the deterministic artifacts). It is classified and
            // returned — never thrown — so a single fixture's read failure cannot stop
            // RunAll from processing the remaining fixtures, and COMPLETE is not reached.
            if (!recorder.HasFailed)
            {
                recorder.Fail(RomAnalysisStage.Manifest, DiscMetadataUnreadable, ex);
            }

            return (null, RomAnalysisOutcome.From(recorder, outcome.Report, outcome.DecodeFailureCount), recorder, log);
        }

        Record("CHD_META", "PASS",
            $"V{chdStats.Version} hunks={chdStats.TotalHunks} cdlz={chdStats.CdlzCount} cdzl={chdStats.CdzlCount}");

        var artifacts = DeterministicArtifactBuilder.Build(new DeterministicArtifactInput
        {
            FixtureId = fixtureId,
            DiscImageFormat = DiscImageFormat,
            DiscImageSha256 = sha256,
            DiscImageSizeBytes = sizeBytes,
            Chd = chdStats,
            Iso = isoStats,
            Report = outcome.Report,
        });

        Record("ARTIFACTS", "PASS",
            $"Built {artifacts.Files.Count} deterministic artifacts (manifest schema v{artifacts.Manifest.SchemaVersion})");

        return (artifacts, outcome, recorder, log);
    }

    /// <summary>
    /// Runs the pipeline and returns the deterministic artifact set plus the execution log.
    /// Kept as the #215 entry point; it throws if analysis failed before the REPORT stage,
    /// because the deterministic-artifact contract only applies to a successful analysis.
    /// </summary>
    public static (RealRomAnalysisArtifacts Artifacts, List<ExecutionLogEntry> Log) Analyze(
        string discImagePath,
        string fixtureId,
        int? instructionCount = null)
    {
        var staged = AnalyzeStaged(discImagePath, fixtureId, instructionCount);
        if (staged.Artifacts is null)
        {
            throw new InvalidDataException(
                $"Fixture '{fixtureId}' produced no deterministic artifacts ({staged.Outcome.FailureKind}: {staged.Outcome.FailureReason}); " +
                "the #215 artifact contract only applies when REPORT and the metadata pass succeed.");
        }

        return (staged.Artifacts, staged.Log);
    }

    /// <summary>
    /// Orchestrates one fixture end to end: analyze, persist artifacts + log, record the
    /// MANIFEST / COMPLETE stages. A persistence failure is classified as
    /// <see cref="ArtifactPersistenceFailure"/> at MANIFEST and returned as a result (with
    /// nullable artifact paths), never thrown. COMPLETE is only reached when the pipeline
    /// AND every artifact write succeeded.
    /// </summary>
    public static RealRomAnalysisRunResult AnalyzeAndPersist(
        string discImagePath,
        string fixtureId,
        string reportRoot,
        string logRoot,
        int? instructionCount = null)
    {
        return AnalyzeAndPersistCore(discImagePath, fixtureId, reportRoot, logRoot, capture: null, instructionCount);
    }

    /// <summary>Core of <see cref="AnalyzeAndPersist"/>; see <see cref="AnalyzeStagedCore"/> for the capture seam.</summary>
    internal static RealRomAnalysisRunResult AnalyzeAndPersistCore(
        string discImagePath,
        string fixtureId,
        string reportRoot,
        string logRoot,
        Func<string, (ChdMapStatistics Chd, IsoVolumeStatistics Iso)>? capture,
        int? instructionCount = null)
    {
        var staged = AnalyzeStagedCore(discImagePath, fixtureId, capture, instructionCount);
        var recorder = staged.Recorder;

        string? reportPath = null;
        string? logPath = null;
        try
        {
            if (staged.Artifacts is not null)
            {
                reportPath = RealRomArtifactWriter.Write(staged.Artifacts, reportRoot, logRoot, staged.Log);
                logPath = Path.Combine(logRoot, fixtureId, "analysis.log.jsonl");
            }
        }
        catch (Exception ex) when (IsFileAccessFailure(ex))
        {
            reportPath = null;
            logPath = null;
            if (!recorder.HasFailed)
            {
                recorder.Fail(RomAnalysisStage.Manifest, ArtifactPersistenceFailure, ex);
            }
        }

        if (!recorder.HasFailed && reportPath is not null)
        {
            recorder.Pass(RomAnalysisStage.Manifest, "Analysis artifacts persisted");
            recorder.Pass(RomAnalysisStage.Complete, "Real-ROM analysis flow completed");
        }

        var outcome = RomAnalysisOutcome.From(recorder, staged.Outcome.Report, staged.Outcome.DecodeFailureCount);
        return new RealRomAnalysisRunResult
        {
            FixtureId = fixtureId,
            Outcome = outcome,
            Artifacts = staged.Artifacts,
            ReportPath = reportPath,
            LogPath = logPath,
        };
    }

    /// <summary>
    /// Analyzes and persists every locally discovered fixture. Each fixture is isolated: a
    /// persistence or analysis failure for one fixture is returned in that fixture's result
    /// and never stops the remaining fixtures from running.
    /// </summary>
    public static IReadOnlyList<RealRomAnalysisRunResult> RunAll(
        string reportRoot,
        string logRoot,
        int? instructionCount = null)
    {
        return RunAllCore(reportRoot, logRoot, capture: null, instructionCount);
    }

    /// <summary>Core of <see cref="RunAll"/>; see <see cref="AnalyzeStagedCore"/> for the capture seam.</summary>
    internal static IReadOnlyList<RealRomAnalysisRunResult> RunAllCore(
        string reportRoot,
        string logRoot,
        Func<string, (ChdMapStatistics Chd, IsoVolumeStatistics Iso)>? capture,
        int? instructionCount = null)
    {
        return RealRomFixtures.Discover()
            .Select(fixture => AnalyzeAndPersistCore(fixture.DiscImagePath, fixture.FixtureId, reportRoot, logRoot, capture, instructionCount))
            .ToList();
    }

    /// <summary>Computes the lowercase hex SHA-256 of a byte buffer. Shared for tests.</summary>
    public static string ComputeSha256ForTest(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string ComputeSha256(Stream stream) => Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

    /// <summary>
    /// Re-opens the disc image and captures the CHD map statistics plus ISO 9660 volume
    /// statistics consumed by <see cref="DeterministicArtifactBuilder"/>. This is the
    /// post-REPORT metadata pass: it may throw the expected analysis/read exceptions, which
    /// <see cref="AnalyzeStagedCore"/> classifies as <see cref="DiscMetadataUnreadable"/> at
    /// MANIFEST rather than letting them escape.
    /// </summary>
    internal static (ChdMapStatistics Chd, IsoVolumeStatistics Iso) CaptureChdMetadata(string discImagePath)
    {
#pragma warning disable PSXR005
        using var statsStream = File.OpenRead(discImagePath);
#pragma warning restore PSXR005
        using var chd = ChdReader.Open(statsStream);
        var iso = DiscImageAnalyzer.CreateIsoReader(chd);
        iso.Initialize();
        return (chd.ComputeMapStatistics(), iso.ComputeVolumeStatistics());
    }

    private static string StatusLabel(RomAnalysisStageStatus status) => status switch
    {
        RomAnalysisStageStatus.Passed => "PASS",
        RomAnalysisStageStatus.Failed => "FAILED",
        RomAnalysisStageStatus.Skipped => "SKIP",
        _ => "?",
    };

    /// <summary>Class of failures that surface as a classified persistence failure.</summary>
    private static bool IsFileAccessFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or PathTooLongException or NotSupportedException
            or System.Security.SecurityException;

    /// <summary>
    /// Expected analysis/file-access failures for the post-REPORT metadata pass. This is
    /// deliberately narrower than catch-anything: process-level failures are not swallowed.
    /// </summary>
    private static bool IsPostReportMetadataFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or PathTooLongException
            or NotSupportedException or System.Security.SecurityException or InvalidDataException;
}
