using System.Text;
using PSXRecomp.Core.DiscImage.Artifacts;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Persists one fixture's analysis output to the local artifact layout:
///
/// <code>
/// reports/real-rom/&lt;fixture&gt;/manifest.json       deterministic, safe to persist and diff
/// reports/real-rom/&lt;fixture&gt;/report.json         deterministic, safe to persist and diff
/// reports/real-rom/&lt;fixture&gt;/instructions.json   deterministic, safe to persist and diff
/// reports/real-rom/&lt;fixture&gt;/cfg.json            deterministic, safe to persist and diff
/// logs/real-rom/&lt;fixture&gt;/analysis.log.jsonl     execution log, local-only (carries timing)
/// </code>
///
/// Both roots are git-ignored. The writer makes no formatting decisions of its own: it
/// writes the canonical bytes the artifact builder produced, so what lands on disk is
/// exactly what determinism was asserted on.
/// </summary>
[Test]
public static class RealRomArtifactWriter
{
    /// <summary>
    /// Writes the deterministic artifacts under <paramref name="reportRoot"/> and,
    /// when <paramref name="log"/> is supplied, the execution log under
    /// <paramref name="logRoot"/>. Returns the fixture's artifact directory.
    ///
    /// Existing files are overwritten, so re-running an analysis on an unchanged disc
    /// image leaves the artifact bytes untouched.
    /// </summary>
    public static string Write(
        RealRomAnalysisArtifacts artifacts,
        string reportRoot,
        string? logRoot = null,
        IReadOnlyList<ExecutionLogEntry>? log = null)
    {
        ArgumentNullException.ThrowIfNull(artifacts);

        var fixtureId = artifacts.Manifest.Fixture.FixtureId;

#pragma warning disable PSXR005
        var fixtureReportDirectory = Path.Combine(reportRoot, fixtureId);
        Directory.CreateDirectory(fixtureReportDirectory);

        foreach (var file in artifacts.Files)
        {
            // Written as raw bytes rather than text so no encoding, BOM or newline
            // translation can be introduced between the asserted content and the file.
            File.WriteAllBytes(Path.Combine(fixtureReportDirectory, file.FileName), file.ToUtf8Bytes());
        }

        if (logRoot is not null && log is not null)
        {
            var fixtureLogDirectory = Path.Combine(logRoot, fixtureId);
            Directory.CreateDirectory(fixtureLogDirectory);
            ExecutionLogWriter.Write(Path.Combine(fixtureLogDirectory, "analysis.log.jsonl"), log);
        }
#pragma warning restore PSXR005

        return fixtureReportDirectory;
    }

    /// <summary>
    /// Analyzes a disc image and writes every artifact, returning the artifact set for
    /// assertions. Convenience wrapper over <see cref="RealRomAnalyzer.Analyze"/> plus
    /// <see cref="Write"/>.
    /// </summary>
    public static RealRomAnalysisArtifacts AnalyzeAndWrite(
        string discImagePath,
        string fixtureId,
        string reportRoot,
        string logRoot,
        int? instructionCount = null)
    {
        var (artifacts, log) = RealRomAnalyzer.Analyze(discImagePath, fixtureId, instructionCount);
        Write(artifacts, reportRoot, logRoot, log);
        return artifacts;
    }

    /// <summary>
    /// Reads back a previously written artifact file as raw bytes, for byte-for-byte
    /// comparison against a freshly built artifact.
    /// </summary>
    public static byte[] ReadArtifactBytes(string fixtureReportDirectory, string fileName)
    {
#pragma warning disable PSXR005
        return File.ReadAllBytes(Path.Combine(fixtureReportDirectory, fileName));
#pragma warning restore PSXR005
    }

    /// <summary>UTF-8 text of a previously written artifact file.</summary>
    public static string ReadArtifactText(string fixtureReportDirectory, string fileName)
    {
        return Encoding.UTF8.GetString(ReadArtifactBytes(fixtureReportDirectory, fileName));
    }
}
