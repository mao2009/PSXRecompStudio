using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Writes the reusable real-ROM analysis artifacts to disk so multiple disc images
/// can be compared. Layout (fixture is a human-friendly alias; SHA-256 is the
/// formal input identity held inside the deterministic manifest):
///
///   reports/real-rom/&lt;fixture&gt;/manifest.json   - deterministic AnalysisSnapshot
///   reports/real-rom/&lt;fixture&gt;/report.json     - existing DiscImageAnalysisReport JSON
///   logs/real-rom/&lt;fixture&gt;/analysis.log.jsonl - execution log (may have timestamps)
///
/// These are local analysis artifacts and are not required to be committed.
/// </summary>
[Test]
public static class AnalysisSnapshotWriter
{
    /// <summary>
    /// Runs the pipeline for the given CHD and writes all artifacts, then returns
    /// the deterministic snapshot (available for assertions).
    /// </summary>
    public static AnalysisSnapshot AnalyzeAndWrite(
        string chdPath,
        string fixtureName,
        string reportDirectory,
        string logDirectory,
        int? instructionCount = null)
    {
        var (snapshot, log) = RealRomAnalyzer.Analyze(chdPath, fixtureName, instructionCount);
        var report = BuildReport(chdPath);

#pragma warning disable PSXR005
        var fixtureReportDir = Path.Combine(reportDirectory, fixtureName);
        var fixtureLogDir = Path.Combine(logDirectory, fixtureName);
        Directory.CreateDirectory(fixtureReportDir);
        Directory.CreateDirectory(fixtureLogDir);

        File.WriteAllText(Path.Combine(fixtureReportDir, "manifest.json"), snapshot.ToJson());
        File.WriteAllText(Path.Combine(fixtureReportDir, "report.json"), report.ToJson());
        ExecutionLogWriter.Write(Path.Combine(fixtureLogDir, "analysis.log.jsonl"), log);
#pragma warning restore PSXR005

        return snapshot;
    }

    private static DiscImageAnalysisReport BuildReport(string chdPath)
    {
#pragma warning disable PSXR005
        var bytes = File.ReadAllBytes(chdPath);
#pragma warning restore PSXR005
        var sha256 = RealRomAnalyzer.ComputeSha256ForTest(bytes);
        var report = DiscImageAnalyzer.Analyze(bytes, sha256);
        return report;
    }
}
