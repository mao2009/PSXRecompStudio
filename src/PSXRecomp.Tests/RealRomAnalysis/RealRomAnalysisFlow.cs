using System.Security.Cryptography;
using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Result of driving the real-ROM analysis flow for one fixture, including the
/// paths of the artifacts that were written.
/// </summary>
[Test]
public sealed record RomAnalysisRunResult
{
    public required RomFixture Fixture { get; init; }

    public required RomAnalysisOutcome Outcome { get; init; }

    public required RomAnalysisRunSummary Summary { get; init; }

    /// <summary>Path of the summary report; always written.</summary>
    public required string SummaryPath { get; init; }

    /// <summary>Path of the detailed per-stage log; always written.</summary>
    public required string LogPath { get; init; }

    /// <summary>Path of the analysis report; written only when the run reached REPORT.</summary>
    public string? ReportPath { get; init; }
}

/// <summary>
/// Drives the reusable real-ROM analysis flow end to end for one or more fixtures:
/// input detection → staged analysis → artifact persistence → result classification.
///
/// The analysis itself belongs to <see cref="RomAnalysisPipeline"/> (domain, pure);
/// this driver owns only what the domain layer must not do — reading the disc image,
/// writing artifacts, and recording the MANIFEST and COMPLETE stages that follow a
/// successful analysis.
///
/// Artifact layout (both trees are git-ignored; ROM/EXE payload never leaves the machine):
/// <code>
///   reports/real-rom/&lt;fixture&gt;/report.json       - DiscImageAnalysisReport (PASS only)
///   reports/real-rom/&lt;fixture&gt;/run-summary.json  - PASS/FAIL verdict, stage table, counts
///   logs/real-rom/&lt;fixture&gt;/analysis.log.jsonl   - detailed per-stage log
/// </code>
///
/// The deterministic snapshot/manifest schema itself is owned by Issue #215; this flow
/// deliberately reuses the existing <see cref="DiscImageAnalysisReport"/> format rather
/// than defining a competing one.
/// </summary>
[Test]
public static class RealRomAnalysisFlow
{
    /// <summary>Artifact sub-tree shared by the report and log directories.</summary>
    public const string ArtifactSubdirectory = "real-rom";

    public static string DefaultReportDirectory =>
        Path.Combine(RomFixtureLocator.RepositoryRoot, "reports", ArtifactSubdirectory);

    public static string DefaultLogDirectory =>
        Path.Combine(RomFixtureLocator.RepositoryRoot, "logs", ArtifactSubdirectory);

    /// <summary>
    /// Runs the flow for every fixture discovered under <paramref name="romDirectory"/>.
    /// An empty result means no fixture was available, which callers report as SKIP.
    /// </summary>
    public static IReadOnlyList<RomAnalysisRunResult> RunAll(
        string romDirectory,
        string reportDirectory,
        string logDirectory,
        int? instructionCount = null)
    {
        return RomFixtureLocator.Discover(romDirectory)
            .Select(fixture => Run(fixture, reportDirectory, logDirectory, instructionCount))
            .ToList();
    }

    /// <summary>
    /// Runs the flow for a single fixture. A failure at any stage is returned as a
    /// classified result — never swallowed and never thrown away.
    /// </summary>
    public static RomAnalysisRunResult Run(
        RomFixture fixture,
        string reportDirectory,
        string logDirectory,
        int? instructionCount = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        var recorder = new RomAnalysisStageRecorder();

        byte[] imageBytes;
        try
        {
#pragma warning disable PSXR005
            imageBytes = File.ReadAllBytes(fixture.ImagePath);
#pragma warning restore PSXR005
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            recorder.Pass(RomAnalysisStage.Start, "Real-ROM analysis flow started");
            recorder.Fail(RomAnalysisStage.Input, "FixtureUnreadable", ex);
            return Persist(fixture, string.Empty, 0, RomAnalysisOutcome.From(recorder),
                reportDirectory, logDirectory);
        }

        var sha256 = ComputeSha256(imageBytes);

        var outcome = fixture.Format == RomFixtureFormat.Chd
            ? RomAnalysisPipeline.RunFromChd(imageBytes, sha256, instructionCount, recorder)
            : RomAnalysisPipeline.RunFromIsoImage(imageBytes, sha256, instructionCount, recorder);

        string? reportPath = null;
        if (!recorder.HasFailed && outcome.Report is not null)
        {
            try
            {
                reportPath = WriteReport(reportDirectory, fixture.Name, outcome.Report);
                recorder.Pass(RomAnalysisStage.Manifest, $"Analysis report persisted as '{Path.GetFileName(reportPath)}'");
                recorder.Pass(RomAnalysisStage.Complete, "Real-ROM analysis flow completed");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                recorder.Fail(RomAnalysisStage.Manifest, "ArtifactPersistenceFailure", ex);
            }
        }

        var finalOutcome = RomAnalysisOutcome.From(recorder, outcome.Report, outcome.DecodeFailureCount);
        return Persist(fixture, sha256, imageBytes.Length, finalOutcome, reportDirectory, logDirectory, reportPath);
    }

    /// <summary>
    /// Writes the summary report and the detailed log. Both are written for every run,
    /// so a failed run leaves the same evidence trail as a successful one.
    /// </summary>
    private static RomAnalysisRunResult Persist(
        RomFixture fixture,
        string sha256,
        long sizeBytes,
        RomAnalysisOutcome outcome,
        string reportDirectory,
        string logDirectory,
        string? reportPath = null)
    {
        var summary = RomAnalysisRunSummary.From(fixture.Name, fixture.Format, sha256, sizeBytes, outcome);

        var fixtureReportDirectory = Path.Combine(reportDirectory, fixture.Name);
        var summaryPath = Path.Combine(fixtureReportDirectory, "run-summary.json");
        var logPath = Path.Combine(logDirectory, fixture.Name, "analysis.log.jsonl");

#pragma warning disable PSXR005
        Directory.CreateDirectory(fixtureReportDirectory);
        File.WriteAllText(summaryPath, summary.ToJson());
#pragma warning restore PSXR005
        RomAnalysisLogWriter.Write(logPath, outcome);

        return new RomAnalysisRunResult
        {
            Fixture = fixture,
            Outcome = outcome,
            Summary = summary,
            SummaryPath = summaryPath,
            LogPath = logPath,
            ReportPath = reportPath,
        };
    }

    private static string WriteReport(string reportDirectory, string fixtureName, DiscImageAnalysisReport report)
    {
        var directory = Path.Combine(reportDirectory, fixtureName);
        var path = Path.Combine(directory, "report.json");

#pragma warning disable PSXR005
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, report.ToJson());
#pragma warning restore PSXR005

        return path;
    }

    private static string ComputeSha256(byte[] data)
    {
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(data)).ToLowerInvariant();
    }
}
