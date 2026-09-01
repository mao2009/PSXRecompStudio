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

    [Fact]
    public void ExecutionLogWriter_PlainMessage_IsPersistedUnchanged()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("logs", "analysis.log.jsonl");
        const string message = "INPUT PASS Read 4096 bytes; SHA-256 0123456789abcdef";

        ExecutionLogWriter.Write(path,
        [
            new ExecutionLogEntry { Stage = "INPUT", Status = "PASS", Message = message, ElapsedMs = 0 },
        ]);

#pragma warning disable PSXR005
        var text = File.ReadAllText(path);
#pragma warning restore PSXR005
        text.Should().Contain(message,
            "a message that needs no redaction must be persisted verbatim, without rewriting it");
    }

    [Fact]
    public void ExecutionLogWriter_Message_IsNeverNullAcrossRedactionAndPlain()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("logs", "analysis.log.jsonl");

        ExecutionLogWriter.Write(path,
        [
            new ExecutionLogEntry { Stage = "INPUT", Status = "FAILED", Message = @"Could not find file 'C:\Users\someone\rom\game.chd'.", ElapsedMs = 0 },
            new ExecutionLogEntry { Stage = "INPUT", Status = "FAILED", Message = "/home/someone/rom/game.chd could not be read", ElapsedMs = 0 },
            new ExecutionLogEntry { Stage = "MIPS_DECODE", Status = "PASS", Message = "Decoded 16 instruction(s)", ElapsedMs = 0 },
        ]);

#pragma warning disable PSXR005
        var lines = File.ReadAllLines(path);
#pragma warning restore PSXR005
        lines.Should().HaveCount(3);
        foreach (var line in lines)
        {
            var entry = System.Text.Json.JsonSerializer.Deserialize<ExecutionLogEntry>(line);
            entry.Should().NotBeNull();
            entry!.Message.Should().NotBeNull("redacted or not, the persisted Message must never be null");
        }
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

    // ------------------------------------------------- post-REPORT metadata failure classification (needs REPORT)

    /// <summary>
    /// Issue #213 BLOCKER: the post-REPORT metadata/statistics pass (open + map stats +
    /// ISO volume stats) may throw an expected analysis/read exception. It must be
    /// classified as <see cref="RealRomAnalyzer.DiscMetadataUnreadable"/> at MANIFEST and
    /// returned, never thrown. Requires real fixtures so the pipeline reaches REPORT.
    /// </summary>
    [SkippableFact]
    public void AnalyzeStagedCore_MetadataFailure_IsClassifiedAtManifestWithoutThrowing()
    {
        var fixtures = Fixtures;
        Skip.If(fixtures.Count == 0, RealRomFixtures.NoFixtureSkipReason);

        var (artifacts, outcome, _, _) = RealRomAnalyzer.AnalyzeStagedCore(
            fixtures[0].DiscImagePath, fixtures[0].FixtureId,
            capture: _ => throw new InvalidDataException("simulated metadata read failure"));

        outcome.Status.Should().Be(RomAnalysisStatus.Fail);
        outcome.FailedStage.Should().Be(RomAnalysisStage.Manifest);
        outcome.FailureKind.Should().Be(RealRomAnalyzer.DiscMetadataUnreadable);
        outcome.LastSuccessfulStage.Should().Be(RomAnalysisStage.Report,
            "REPORT already succeeded; only the downstream metadata pass failed");
        outcome.Report.Should().NotBeNull();
        outcome.Stages.Select(s => s.Stage).Should()
            .NotContain(RomAnalysisStage.Complete, "COMPLETE must not be reached after a MANIFEST failure");
        artifacts.Should().BeNull();
    }

    [SkippableFact]
    public void AnalyzeAndPersistCore_MetadataFailure_DoesNotThrowAndReturnsUnavailableArtifacts()
    {
        var fixtures = Fixtures;
        Skip.If(fixtures.Count == 0, RealRomFixtures.NoFixtureSkipReason);

        using var temp = new TempDirectory();
        var result = RealRomAnalyzer.AnalyzeAndPersistCore(
            fixtures[0].DiscImagePath, fixtures[0].FixtureId, temp.Combine("reports"), temp.Combine("logs"),
            capture: _ => throw new IOException("simulated CHD read failure"));

        result.Outcome.Status.Should().Be(RomAnalysisStatus.Fail);
        result.Outcome.FailedStage.Should().Be(RomAnalysisStage.Manifest);
        result.Outcome.FailureKind.Should().Be(RealRomAnalyzer.DiscMetadataUnreadable);
        result.Artifacts.Should().BeNull();
        result.ReportPath.Should().BeNull();
        result.LogPath.Should().BeNull();
        result.AnyArtifactAvailable.Should().BeFalse();
    }

    [SkippableFact]
    public void RunAllCore_MetadataFailure_ContinuesPastTheFailingFixture()
    {
        var fixtures = Fixtures;
        Skip.If(fixtures.Count < 2, RealRomFixtures.NoFixtureSkipReason);

        using var temp = new TempDirectory();
        var failingPath = fixtures[0].DiscImagePath;
        var results = RealRomAnalyzer.RunAllCore(
            temp.Combine("reports"), temp.Combine("logs"),
            capture: path => path == failingPath
                ? throw new IOException($"simulated read failure for {failingPath}")
                : RealRomAnalyzer.CaptureChdMetadata(path));

        results.Should().HaveCount(fixtures.Count,
            "a metadata read failure for one fixture must not stop the remaining fixtures from running");
        results[0].Outcome.FailureKind.Should().Be(RealRomAnalyzer.DiscMetadataUnreadable);
        results[0].Outcome.FailedStage.Should().Be(RomAnalysisStage.Manifest);
        results.Skip(1).Should().OnlyContain(r => r.Outcome.Status == RomAnalysisStatus.Pass,
            "the non-failing fixtures must still complete despite an earlier fixture's failure");
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
