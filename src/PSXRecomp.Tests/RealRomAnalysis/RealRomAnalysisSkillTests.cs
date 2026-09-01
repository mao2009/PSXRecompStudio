using System.Text.Json;
using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// The CI-facing entry point of the real-ROM analysis skill.
///
/// It analyzes whatever the user has placed under <c>rom/</c> and asserts each run
/// reaches COMPLETE. When no fixture is present — the normal case in CI — the test
/// skips explicitly with a reason instead of failing, so the disc-image requirement
/// never breaks the existing pipeline.
/// </summary>
[Test]
public class RealRomAnalysisSkillTests
{
    /// <summary>Instructions decoded per fixture; enough for basic blocks, small enough to stay quick.</summary>
    private const int InstructionCount = 128;

#pragma warning disable PSXR005
    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(Path.Combine(RomFixtureLocator.RepositoryRoot, relativePath));
#pragma warning restore PSXR005

    [SkippableFact]
    public void EveryLocalFixture_CompletesTheAnalysisFlow()
    {
        var fixtures = RomFixtureLocator.Discover();
        Skip.If(fixtures.Count == 0,
            $"skipped: no real-ROM fixture under '{RomFixtureLocator.DefaultRomDirectory}' " +
            "(supported: " + string.Join(", ", RomFixtureLocator.SupportedExtensions) + ")");

        foreach (var fixture in fixtures)
        {
            var result = RealRomAnalysisFlow.Run(
                fixture,
                RealRomAnalysisFlow.DefaultReportDirectory,
                RealRomAnalysisFlow.DefaultLogDirectory,
                InstructionCount);

            result.Outcome.Status.Should().Be(RomAnalysisStatus.Pass,
                $"fixture '{fixture.Name}' failed at {result.Outcome.FailedStage} " +
                $"({result.Outcome.FailureKind}): {result.Outcome.FailureReason}");
            result.Outcome.LastSuccessfulStage.Should().Be(RomAnalysisStage.Complete);
        }
    }

    [Fact]
    public void SkipConditionIsDrivenByFixturePresenceOnly()
    {
        using var temp = new TempDirectory();

        RomFixtureLocator.Discover(temp.Combine("rom")).Should().BeEmpty(
            "an environment without a rom/ directory yields no fixtures, which the skill reports as SKIP");
    }

    /// <summary>
    /// The skill relies on the repository artifact policy (SSOT for the CI contamination
    /// gate) to keep disc images out of Git. Assert that contract still holds, so the
    /// skill cannot silently start operating without that protection.
    /// </summary>
    [Fact]
    public void ArtifactPolicyForbidsDiscImagesAndTheRomDirectory()
    {
        using var policy = JsonDocument.Parse(ReadRepositoryFile(Path.Combine("config", "artifact-policy.json")));

        var segments = policy.RootElement.GetProperty("forbiddenPathSegments")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        var extensions = policy.RootElement.GetProperty("forbiddenExtensions")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        segments.Should().Contain("rom");
        extensions.Should().Contain(RomFixtureLocator.SupportedExtensions,
            "every disc image format the skill accepts must be rejected by the contamination gate");
    }

    /// <summary>
    /// The skill's artifact trees must stay untracked; otherwise a passing run would
    /// commit analysis output derived from a user-owned disc image.
    /// </summary>
    [Fact]
    public void ArtifactDirectoriesAreGitIgnored()
    {
        var gitignore = ReadRepositoryFile(".gitignore")
            .Split('\n')
            .Select(line => line.Trim())
            .ToList();

        gitignore.Should().Contain("reports/").And.Contain("logs/");
        gitignore.Should().Contain(line => line.StartsWith("rom/", StringComparison.Ordinal));
    }
}
