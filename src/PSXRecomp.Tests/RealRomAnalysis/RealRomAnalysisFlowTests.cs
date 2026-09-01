using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// End-to-end tests for <see cref="RealRomAnalysisFlow"/> driven by synthetic disc
/// images: successful runs, failing runs, multi-fixture handling, artifact separation
/// (detailed log vs. summary report), and the no-fixture SKIP condition.
/// </summary>
[Test]
public class RealRomAnalysisFlowTests
{
    private const string BootValue = @"cdrom:\SLPS_TEST.01;1";
    private const string ExeIsoName = "SLPS_TEST.01;1";

    private static byte[] ValidIsoImage(int instructionCount = 16) =>
        new SyntheticIsoImageBuilder()
            .AddSystemCnf(BootValue)
            .AddFile(ExeIsoName, SyntheticPsxExeBuilder.BuildValid(instructionCount))
            .Build();

    private static byte[] BrokenIsoImage() =>
        new SyntheticIsoImageBuilder()
            .WithoutPrimaryVolumeDescriptor()
            .AddSystemCnf(BootValue)
            .Build();

#pragma warning disable PSXR005
    private static bool Exists(string path) => File.Exists(path);

    private static string ReadText(string path) => File.ReadAllText(path);
#pragma warning restore PSXR005

    [Fact]
    public void Run_ValidFixture_PassesEveryStageAndWritesAllArtifacts()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(Path.Combine("rom", "sample.iso"), ValidIsoImage());

        var results = RealRomAnalysisFlow.RunAll(
            temp.Combine("rom"), temp.Combine("reports"), temp.Combine("logs"), instructionCount: 16);

        var result = results.Should().ContainSingle().Subject;
        result.Outcome.Status.Should().Be(RomAnalysisStatus.Pass);
        result.Outcome.LastSuccessfulStage.Should().Be(RomAnalysisStage.Complete,
            "a fully persisted run ends at COMPLETE, past MANIFEST");

        result.Outcome.Stages.Select(s => s.Stage).Should()
            .Contain(RomAnalysisStage.Manifest)
            .And.Contain(RomAnalysisStage.Complete);

        Exists(result.SummaryPath).Should().BeTrue();
        Exists(result.LogPath).Should().BeTrue();
        Exists(result.ReportPath!).Should().BeTrue();
    }

    [Fact]
    public void Run_ValidFixture_SummaryRecordsPassAndAggregateCounts()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(Path.Combine("rom", "sample.iso"), ValidIsoImage());

        var result = RealRomAnalysisFlow.RunAll(
            temp.Combine("rom"), temp.Combine("reports"), temp.Combine("logs"), 16).Single();

        result.Summary.Status.Should().Be("PASS");
        result.Summary.Fixture.Should().Be("sample");
        result.Summary.Format.Should().Be("ISO");
        result.Summary.DiscImageSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        result.Summary.FailedStage.Should().BeNull();

        result.Summary.Counts.Should().NotBeNull();
        var counts = result.Summary.Counts!;
        counts.DecodedInstructionCount.Should().Be(16);
        counts.DecodeFailureCount.Should().Be(0);
        counts.BasicBlockCount.Should().BeGreaterThan(0);
        counts.ExecutableSha256.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Run_FailingFixture_RecordsFailureStageAndReasonAndSkipsTheReport()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(Path.Combine("rom", "broken.iso"), BrokenIsoImage());

        var result = RealRomAnalysisFlow.RunAll(
            temp.Combine("rom"), temp.Combine("reports"), temp.Combine("logs")).Single();

        result.Outcome.Status.Should().Be(RomAnalysisStatus.Fail);
        result.Summary.Status.Should().Be("FAIL");
        result.Summary.FailedStage.Should().Be(nameof(RomAnalysisStage.Filesystem));
        result.Summary.FailureKind.Should().Be("FilesystemFailure");
        result.Summary.FailureReason.Should().NotBeNullOrWhiteSpace();
        result.Summary.LastSuccessfulStage.Should().Be(nameof(RomAnalysisStage.Input));
        result.Summary.Counts.Should().BeNull();

        result.ReportPath.Should().BeNull("a run that never reached REPORT has no report to persist");
    }

    [Fact]
    public void Run_FailingFixture_StillWritesTheSummaryAndTheDetailedLog()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(Path.Combine("rom", "broken.iso"), BrokenIsoImage());

        var result = RealRomAnalysisFlow.RunAll(
            temp.Combine("rom"), temp.Combine("reports"), temp.Combine("logs")).Single();

        Exists(result.SummaryPath).Should().BeTrue("failures must leave the same evidence trail as successes");
        Exists(result.LogPath).Should().BeTrue();

        var logLines = ReadText(result.LogPath).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        logLines.Should().HaveCount(result.Outcome.Stages.Count);
        logLines[^1].Should().Contain("\"Status\":\"FAILED\"").And.Contain("FilesystemFailure");
    }

    [Fact]
    public void Run_SeparatesTheDetailedLogFromTheSummaryReport()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(Path.Combine("rom", "sample.iso"), ValidIsoImage());

        var result = RealRomAnalysisFlow.RunAll(
            temp.Combine("rom"), temp.Combine("reports"), temp.Combine("logs"), 16).Single();

        Path.GetDirectoryName(result.LogPath).Should().NotBe(Path.GetDirectoryName(result.SummaryPath));

        var summary = ReadText(result.SummaryPath);
        summary.Should().NotContain("Mnemonic").And.NotContain("RawWord",
            "the summary carries aggregate counts only, never decoded ROM content");

        var log = ReadText(result.LogPath);
        log.Should().Contain("\"Stage\":\"MipsDecode\"", "per-stage detail belongs to the log");
    }

    [Fact]
    public void RunAll_MultipleFixtures_AnalyzesEachIntoItsOwnArtifactDirectory()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(Path.Combine("rom", "first.iso"), ValidIsoImage(16));
        temp.WriteFile(Path.Combine("rom", "second", "disc.iso"), ValidIsoImage(24));
        temp.WriteFile(Path.Combine("rom", "third.iso"), BrokenIsoImage());

        var results = RealRomAnalysisFlow.RunAll(
            temp.Combine("rom"), temp.Combine("reports"), temp.Combine("logs"));

        results.Select(r => r.Fixture.Name).Should().Equal("first", "second", "third");
        results.Select(r => r.Summary.Status).Should().Equal("PASS", "PASS", "FAIL");

        foreach (var result in results)
        {
            Path.GetFileName(Path.GetDirectoryName(result.SummaryPath)).Should().Be(result.Fixture.Name);
            Exists(result.SummaryPath).Should().BeTrue();
        }
    }

    [Fact]
    public void RunAll_WithoutAnyFixture_ReturnsNoResults()
    {
        using var temp = new TempDirectory();

        RealRomAnalysisFlow.RunAll(temp.Combine("rom"), temp.Combine("reports"), temp.Combine("logs"))
            .Should().BeEmpty("callers turn an empty result into an explicit SKIP");
    }

    [Fact]
    public void Run_SameFixtureTwice_ProducesAnIdenticalSummary()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(Path.Combine("rom", "sample.iso"), ValidIsoImage());

        var first = RealRomAnalysisFlow.RunAll(
            temp.Combine("rom"), temp.Combine("reports"), temp.Combine("logs"), 16).Single();
        var second = RealRomAnalysisFlow.RunAll(
            temp.Combine("rom"), temp.Combine("reports"), temp.Combine("logs"), 16).Single();

        second.Summary.ToJson().Should().Be(first.Summary.ToJson(),
            "the summary contains no timestamps, paths, or environment data");
    }

    [Fact]
    public void Run_UnreadableFixture_FailsAtInputWithoutThrowing()
    {
        using var temp = new TempDirectory();

        var fixture = new RomFixture
        {
            Name = "ghost",
            ImagePath = temp.Combine("rom", "ghost.iso"),
            Format = RomFixtureFormat.Iso,
        };

        var result = RealRomAnalysisFlow.Run(fixture, temp.Combine("reports"), temp.Combine("logs"));

        result.Outcome.Status.Should().Be(RomAnalysisStatus.Fail);
        result.Summary.FailedStage.Should().Be(nameof(RomAnalysisStage.Input));
        result.Summary.FailureKind.Should().Be("FixtureUnreadable");
        Exists(result.SummaryPath).Should().BeTrue();
    }
}
