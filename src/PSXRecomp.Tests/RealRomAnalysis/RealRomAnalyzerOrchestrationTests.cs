using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Tests for the Issue #213 orchestration layer (<see cref="RealRomAnalyzer.AnalyzeAndPersist"/>
/// / <see cref="RealRomAnalyzer.RunAll"/>) and the execution-log persistence boundary.
///
/// These replace the behaviors originally exercised by the (removed) second flow: a
/// persistence failure is classified as <c>ArtifactPersistenceFailure</c> at MANIFEST and
/// returned with nullable artifact paths rather than throwing; one fixture's failure never
/// stops the rest; and the persisted log never leaks an absolute local path.
///
/// Tests that require a disc image reaching the REPORT stage are gated on the locally
/// discovered <c>rom/*.chd</c> fixtures and skip in CI, exactly like
/// <see cref="RealRomAnalysisTests"/>. Tests needing no disc image always run.
/// </summary>
[Test]
public class RealRomAnalyzerOrchestrationTests
{
    private static IReadOnlyList<RealRomFixture> Fixtures => RealRomFixtures.Discover();

    // ---------------------------------------------------------------- persistence isolation (always run)

    [Fact]
    public void AnalyzeAndPersist_MissingFixture_ReturnsClassifiedInputFailureWithoutThrowing()
    {
        using var temp = new TempDirectory();

        var result = RealRomAnalyzer.AnalyzeAndPersist(
            temp.Combine("rom", "ghost.chd"), "ghost", temp.Combine("reports"), temp.Combine("logs"));

        result.Outcome.Status.Should().Be(RomAnalysisStatus.Fail);
        result.Outcome.FailedStage.Should().Be(RomAnalysisStage.Input);
        result.Outcome.FailureKind.Should().Be("FixtureUnreadable");
        result.Outcome.Report.Should().BeNull();
        result.Artifacts.Should().BeNull();
        result.ReportPath.Should().BeNull("a run that never reached REPORT has no artifacts to persist");
        result.LogPath.Should().BeNull();
        result.AnyArtifactAvailable.Should().BeFalse();
    }

    [Fact]
    public void ExecutionLogWriter_RedactsAbsoluteWindowsPathsAtThePersistenceBoundary()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("logs", "analysis.log.jsonl");
        var leaked = @"C:\Users\someone\rom\game.chd";

        ExecutionLogWriter.Write(path,
        [
            new ExecutionLogEntry
            {
                Stage = "INPUT",
                Status = "FAILED",
                Message = $"Could not find file '{leaked}'.",
                ElapsedMs = 0,
            },
        ]);

#pragma warning disable PSXR005
        var text = File.ReadAllText(path);
#pragma warning restore PSXR005
        text.Should().Contain("redacted", "the JSON escapes angle brackets, so match the unescaped word");
        text.Should().NotContain(leaked, "absolute paths must never be persisted into the execution log");
    }

    [Fact]
    public void ExecutionLogWriter_RedactsAbsolutePosixPathsAtThePersistenceBoundary()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("logs", "analysis.log.jsonl");
        var leaked = "/home/someone/rom/game.chd could not be read";

        ExecutionLogWriter.Write(path,
        [
            new ExecutionLogEntry
            {
                Stage = "INPUT",
                Status = "FAILED",
                Message = leaked,
                ElapsedMs = 0,
            },
        ]);

#pragma warning disable PSXR005
        var text = File.ReadAllText(path);
#pragma warning restore PSXR005
        text.Should().Contain("redacted", "the JSON escapes angle brackets, so match the unescaped word");
        text.Should().NotContain("/home/someone", "POSIX roots must never be persisted into the execution log");
    }

    // ---------------------------------------------------------------- persistence failure classification (needs REPORT)

    /// <summary>
    /// A persistence failure is classified as <c>ArtifactPersistenceFailure</c> at MANIFEST
    /// and returned with unavailable (null) artifact paths rather than thrown. COMPLETE is
    /// not reached. Requires a disc image that reaches the REPORT stage, so it is skippable.
    /// </summary>
    [SkippableFact]
    public void PersistenceFailure_IsClassifiedAtManifest_WithUnavailableArtifacts()
    {
        var fixtures = Fixtures;
        Skip.If(fixtures.Count == 0, RealRomFixtures.NoFixtureSkipReason);

        using var temp = new TempDirectory();
        var blockingReportRoot = temp.WriteFile("blocker", [1, 2, 3]);
        var blocker = Path.Combine(temp.FullPath, "blocker");

        var result = RealRomAnalyzer.AnalyzeAndPersist(
            fixtures[0].DiscImagePath, fixtures[0].FixtureId, blockingReportRoot, blocker);

        result.Outcome.Status.Should().Be(RomAnalysisStatus.Fail);
        result.Outcome.FailedStage.Should().Be(RomAnalysisStage.Manifest);
        result.Outcome.FailureKind.Should().Be(RealRomAnalyzer.ArtifactPersistenceFailure);
        result.ReportPath.Should().BeNull("an unavailable artifact is represented by a null path, never a claim it exists");
        result.LogPath.Should().BeNull();
        result.AnyArtifactAvailable.Should().BeFalse();
        result.Outcome.LastSuccessfulStage.Should().NotBe(RomAnalysisStage.Complete,
            "COMPLETE is only reached when every artifact write succeeds");
    }

    /// <summary>
    /// One fixture's (even every fixture's) persistence failure never stops the loop: a
    /// <see cref="RealRomAnalyzer.RunAll"/> with a failing root still returns one result per
    /// fixture, each classified, instead of throwing partway. Requires real fixtures.
    /// </summary>
    [SkippableFact]
    public void RunAll_ContinuesPastPerFixtureFailure()
    {
        var fixtures = Fixtures;
        Skip.If(fixtures.Count == 0, RealRomFixtures.NoFixtureSkipReason);

        using var temp = new TempDirectory();
        var blockingReportRoot = temp.WriteFile("reports", [1]);

        var results = RealRomAnalyzer.RunAll(
            blockingReportRoot, Path.Combine(temp.FullPath, "logs"), instructionCount: 128);

        results.Should().HaveCount(fixtures.Count,
            "the loop must run every fixture and return a result for each, even when persistence fails");
        results.Should().OnlyContain(r => r.Outcome.FailedStage == RomAnalysisStage.Manifest);
    }

    // ---------------------------------------------------------------- successful persistence (needs REPORT)

    /// <summary>
    /// A fully persisted run records MANIFEST then COMPLETE, and leaves both the artifact
    /// directory and the execution log on disk. Requires real fixtures.
    /// </summary>
    [SkippableFact]
    public void Run_PersistsArtifactsAndReachesComplete()
    {
        var fixtures = Fixtures;
        Skip.If(fixtures.Count == 0, RealRomFixtures.NoFixtureSkipReason);

        var result = RealRomAnalyzer.AnalyzeAndPersist(
            fixtures[0].DiscImagePath, fixtures[0].FixtureId,
            RealRomFixtures.ReportRoot, RealRomFixtures.LogRoot, instructionCount: 128);

        result.Outcome.Status.Should().Be(RomAnalysisStatus.Pass);
        result.Outcome.LastSuccessfulStage.Should().Be(RomAnalysisStage.Complete);
        result.Outcome.Stages.Select(s => s.Stage).Should()
            .Contain(RomAnalysisStage.Manifest).And.Contain(RomAnalysisStage.Complete);
        result.AnyArtifactAvailable.Should().BeTrue();
    }
}
