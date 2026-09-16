using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Execution;
using PSXRecompStudio.Services;

namespace PSXRecompStudio.Tests;

// Issue #409: with a legal user-supplied real fixture, the analyzed PS-X EXE enters
// the production execution path and produces a classified result — proving the
// bridge connects the existing disc analysis to TitleExecutionService / Orchestrator /
// InterpreterTitleExecutionEngine. Skipped in CI when no fixtures exist.
[Test]
public class RealRomProductionExecutionTests
{
    private static string RepositoryRoot { get; } = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private const string NoFixtureSkipReason =
        "skipped: no real-ROM fixture found under rom/*.chd (disc images are never committed)";

    [SkippableFact]
    public void EveryLocalFixture_RunsThroughProductionService_ClassifiedEnd()
    {
#pragma warning disable AARC003
        var romDir = Path.Combine(RepositoryRoot, "rom");
        var chdFiles = Directory.Exists(romDir)
            ? Directory.GetFiles(romDir, "*.chd", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();
#pragma warning restore AARC003

        Skip.If(chdFiles.Length == 0, NoFixtureSkipReason);

        var executedCount = 0;

        foreach (var chdPath in chdFiles)
        {
            var outcome = AnalyzeFixture(chdPath);
            if (outcome.Status != RomAnalysisStatus.Pass || outcome.Executable is null)
            {
                continue; // Analysis failed or produced no EXE — not a failure of this test.
            }

            executedCount++;

            var exe = outcome.Executable;
            var run = new TitleExecutionService().Run(exe, outerBudget: 64, segmentBudget: 4096);

            // A real title must reach one of the classified guest outcomes with a
            // snapshot, not a caller/handoff contract violation (InvalidState) and
            // not a mechanism failure with no snapshot.
            run.Result.FinalSnapshot.Should().NotBeNull(
                $"{chdPath}: {run.Result.DiagnosticCode} {run.Result.DiagnosticMessage}");
            run.Result.State.Should().NotBe(
                TitleExecutionState.InvalidState,
                $"{chdPath}: {run.Result.DiagnosticCode} {run.Result.DiagnosticMessage}");
            run.Result.SegmentsRetired.Should().BeGreaterThan(0, chdPath);
        }

        executedCount.Should().BeGreaterThan(0,
            "at least one fixture must produce an EXE that reaches the production execution path");
    }

    private static RomAnalysisOutcome AnalyzeFixture(string chdPath)
    {
        string sha256;
#pragma warning disable AARC003
        using (var hashStream = File.OpenRead(chdPath))
#pragma warning restore AARC003
        {
            sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(hashStream)).ToLowerInvariant();
        }

#pragma warning disable AARC003
        using var runStream = File.OpenRead(chdPath);
#pragma warning restore AARC003
        return RomAnalysisPipeline.RunFromChd(runStream, sha256);
    }
}
