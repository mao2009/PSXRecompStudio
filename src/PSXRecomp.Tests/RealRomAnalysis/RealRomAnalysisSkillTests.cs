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
///
/// Fixture discovery is <see cref="RealRomFixtures"/> (the single SSOT); persistence is
/// the #215 deterministic artifact set written by <see cref="RealRomArtifactWriter"/>,
/// orchestrated by <see cref="RealRomAnalyzer.AnalyzeAndPersist"/>. No competing
/// artifact schema or second flow exists here.
/// </summary>
[Test]
public class RealRomAnalysisSkillTests
{
    /// <summary>Instructions decoded per fixture; enough for basic blocks, small enough to stay quick.</summary>
    private const int InstructionCount = 128;

#pragma warning disable PSXR005
    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(Path.Combine(RealRomFixtures.RepositoryRoot, relativePath));
#pragma warning restore PSXR005

    [SkippableFact]
    public void EveryLocalFixture_CompletesTheAnalysisFlow()
    {
        var fixtures = RealRomFixtures.Discover();
        Skip.If(fixtures.Count == 0, RealRomFixtures.NoFixtureSkipReason);

        var results = RealRomAnalyzer.RunAll(
            RealRomFixtures.ReportRoot, RealRomFixtures.LogRoot, InstructionCount);

        results.Should().HaveCount(fixtures.Count);
        foreach (var result in results)
        {
            result.Outcome.Status.Should().Be(RomAnalysisStatus.Pass,
                $"fixture '{result.FixtureId}' failed at {result.Outcome.FailedStage} " +
                $"({result.Outcome.FailureKind}): {result.Outcome.FailureReason}");
            result.Outcome.LastSuccessfulStage.Should().Be(RomAnalysisStage.Complete,
                "a fully persisted run ends at COMPLETE, past MANIFEST");
            result.AnyArtifactAvailable.Should().BeTrue();
        }
    }

    [Fact]
    public void SkipConditionIsDrivenByFixturePresenceOnly()
    {
        var fixtures = RealRomFixtures.Discover();

        if (fixtures.Count == 0)
        {
            // With no fixtures the skill must report SKIP, never fail. The RunAll loop
            // over an empty discovery is vacuously empty and callers turn that into SKIP.
            RealRomAnalyzer.RunAll(RealRomFixtures.ReportRoot, RealRomFixtures.LogRoot, InstructionCount)
                .Should().BeEmpty();
        }
        else
        {
            fixtures.Select(f => f.FixtureId).Should().OnlyHaveUniqueItems(
                "collision-free fixture ids come from AnalysisArtifactSchema.DisambiguateFixtureIds");
        }
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
        extensions.Should().Contain(".chd", "every disc image format the skill accepts must be rejected by the contamination gate");
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
