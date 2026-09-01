using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.DiscImage.AnalysisArtifacts;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// End-to-end tests over whatever real disc images exist locally under <c>rom/</c>.
///
/// No title is named here: every test iterates the discovered fixtures, so adding or
/// removing a disc image changes coverage without changing code. Disc images are never
/// committed, so on CI — and on any machine without fixtures — these tests skip
/// explicitly with a reason rather than passing vacuously. The format-level guarantees
/// they exercise are additionally covered on synthetic input by
/// <see cref="DeterministicArtifactTests"/>, which always runs.
/// </summary>
[Test]
public class RealRomAnalysisTests
{
    private static IReadOnlyList<RealRomFixture> Fixtures => RealRomFixtures.Discover();

    /// <summary>
    /// The core requirement of Issue #215: analyzing the same disc image twice must
    /// produce byte-for-byte identical deterministic artifacts. The execution log is
    /// deliberately excluded — it carries timing and is expected to differ.
    /// </summary>
    [SkippableFact]
    public void RepeatedAnalysis_ProducesByteIdenticalArtifacts()
    {
        var fixtures = Fixtures;
        Skip.If(fixtures.Count == 0, RealRomFixtures.NoFixtureSkipReason);

        foreach (var fixture in fixtures)
        {
            var (first, _) = RealRomAnalyzer.Analyze(fixture.DiscImagePath, fixture.FixtureId);
            var (second, _) = RealRomAnalyzer.Analyze(fixture.DiscImagePath, fixture.FixtureId);

            for (int index = 0; index < first.Files.Count; index++)
            {
                first.Files[index].ToUtf8Bytes().Should().Equal(second.Files[index].ToUtf8Bytes(),
                    $"fixture '{fixture.FixtureId}': '{first.Files[index].FileName}' must be reproducible");
            }
        }
    }

    /// <summary>
    /// Writes the full artifact set for every local fixture and verifies the documented
    /// layout, the SHA-256 identity fields, and that the whole #212 pipeline ran:
    /// CHD container, ISO 9660 volume, SYSTEM.CNF, PS-X EXE, decode, basic blocks, CFG.
    /// </summary>
    [SkippableFact]
    public void AnalyzeAndWrite_ProducesTheDocumentedArtifactLayout()
    {
        var fixtures = Fixtures;
        Skip.If(fixtures.Count == 0, RealRomFixtures.NoFixtureSkipReason);

        foreach (var fixture in fixtures)
        {
            var artifacts = RealRomArtifactWriter.AnalyzeAndWrite(
                fixture.DiscImagePath, fixture.FixtureId, RealRomFixtures.ReportRoot, RealRomFixtures.LogRoot);

            var identity = artifacts.Manifest.Fixture;
            identity.FixtureId.Should().Be(fixture.FixtureId);
            identity.DiscImageFormat.Should().Be("CHD");
            identity.DiscImageSha256.Should().MatchRegex("^[0-9a-f]{64}$");
            identity.DiscImageSizeBytes.Should().BePositive();
            identity.ExecutableSha256.Should().MatchRegex("^[0-9a-f]{64}$");
            identity.ExecutableSerial.Should().NotBeNullOrEmpty();

            artifacts.Report.Chd.TotalHunks.Should().BePositive();
            artifacts.Report.Chd.DataRegionBytes.Should().BePositive();
            artifacts.Report.Iso.SystemCnfPresent.Should().BeTrue();
            artifacts.Report.Iso.FileCount.Should().BePositive();
            artifacts.Report.SystemCnf.BootPath.Should().NotBeNullOrEmpty();
            artifacts.Report.Decode.InstructionCount.Should().BePositive();
            artifacts.Report.ControlFlow.BasicBlockCount.Should().BePositive();

            artifacts.Instructions.Instructions.Should().NotBeEmpty();
            artifacts.Cfg.BasicBlocks.Should().NotBeEmpty();

            var directory = Path.Combine(RealRomFixtures.ReportRoot, fixture.FixtureId);
            foreach (var file in artifacts.Files)
            {
                RealRomArtifactWriter.ReadArtifactBytes(directory, file.FileName)
                    .Should().Equal(file.ToUtf8Bytes(),
                        $"fixture '{fixture.FixtureId}': '{file.FileName}' must be persisted verbatim");
            }

#pragma warning disable PSXR005
            File.Exists(Path.Combine(RealRomFixtures.LogRoot, fixture.FixtureId, "analysis.log.jsonl"))
                .Should().BeTrue("the execution log is written alongside, but separately from, the artifacts");
#pragma warning restore PSXR005
        }
    }

    /// <summary>
    /// Distinct disc images must be independently identifiable by their SHA-256, so
    /// multi-title comparison never depends on the local directory alias.
    /// </summary>
    [SkippableFact]
    public void DistinctFixtures_HaveDistinctIdentities()
    {
        var fixtures = Fixtures;
        Skip.If(fixtures.Count < 2, "skipped: fewer than two real-ROM fixtures are present locally");

        var identities = fixtures
            .Select(fixture => RealRomAnalyzer.Analyze(fixture.DiscImagePath, fixture.FixtureId).Artifacts)
            .Select(artifacts => artifacts.Manifest.Fixture.DiscImageSha256)
            .ToList();

        identities.Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// The existing Issue #212 runtime report remains the single producer of analysis
    /// results; the artifact layer only serializes it. This pins that relationship.
    /// </summary>
    [SkippableFact]
    public void ArtifactsAgreeWithTheRuntimeAnalysisReport()
    {
        var fixtures = Fixtures;
        Skip.If(fixtures.Count == 0, RealRomFixtures.NoFixtureSkipReason);

        foreach (var fixture in fixtures)
        {
#pragma warning disable PSXR005
            var bytes = File.ReadAllBytes(fixture.DiscImagePath);
#pragma warning restore PSXR005
            var sha256 = RealRomAnalyzer.ComputeSha256ForTest(bytes);
            var report = DiscImageAnalyzer.Analyze(bytes, sha256);

            var (artifacts, _) = RealRomAnalyzer.Analyze(fixture.DiscImagePath, fixture.FixtureId);

            artifacts.Manifest.Fixture.DiscImageSha256.Should().Be(report.DiscImageSha256);
            artifacts.Manifest.Counts.DecodedInstructions.Should().Be(report.DecodedInstructionCount);
            artifacts.Manifest.Counts.BasicBlocks.Should().Be(report.BasicBlocks.Count);
            artifacts.Manifest.Counts.CfgEdges.Should().Be(report.CfgEdges.Count);
            artifacts.Report.Executable.EntryPoint.Should().Be(AnalysisArtifactSchema.FormatWord32(report.EntryPoint));
        }
    }
}
