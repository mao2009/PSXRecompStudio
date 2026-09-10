using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.DiscImage.AnalysisArtifacts;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// End-to-end tests over whatever real disc images exist locally under <c>rom/</c>.
///
/// No title is named here: every test iterates the discovered fixtures, so adding or
/// removing a disc image changes coverage without changing code. Disc images are never
/// committed, so on CI  Eand on any machine without fixtures  Ethese tests skip
/// explicitly with a reason rather than passing vacuously. The format-level guarantees
/// they exercise are additionally covered on synthetic input by
/// <see cref="DeterministicArtifactTests"/>, which always runs.
/// </summary>
[Test]
[Collection("RealRom")]
public class RealRomAnalysisTests
{
    private static IReadOnlyList<RealRomFixture> Fixtures => RealRomFixtures.Discover();

    /// <summary>
    /// The core requirement of Issue #215: analyzing the same disc image twice must
    /// produce byte-for-byte identical deterministic artifacts. The execution log is
    /// deliberately excluded  Eit carries timing and is expected to differ.
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

#pragma warning disable AARC003
            File.Exists(Path.Combine(RealRomFixtures.LogRoot, fixture.FixtureId, "analysis.log.jsonl"))
                .Should().BeTrue("the execution log is written alongside, but separately from, the artifacts");
#pragma warning restore AARC003
        }
    }

    /// <summary>
    /// Issue #11 / #279: real-ROM analysis must yield BIOS call evidence, so the next HLE
    /// service is chosen from what titles actually request rather than from a static
    /// candidate table.
    ///
    /// The assertion is deliberately shaped as "at least one local fixture requests the
    /// BIOS, and every fixture's evidence is internally consistent". Pinning exact call
    /// counts would bind the test to one particular disc image, which is precisely the
    /// title-specific coupling this repository forbids.
    /// </summary>
    [SkippableFact]
    public void RealRomAnalysis_ProducesBiosCallEvidence()
    {
        var fixtures = Fixtures;
        Skip.If(fixtures.Count == 0, RealRomFixtures.NoFixtureSkipReason);

        var fixturesWithBiosCalls = 0;

        foreach (var fixture in fixtures)
        {
#pragma warning disable AARC003
            var bytes = File.ReadAllBytes(fixture.DiscImagePath);
#pragma warning restore AARC003
            var report = DiscImageAnalyzer.Analyze(
                bytes,
                RealRomAnalyzer.ComputeSha256ForTest(bytes),
                BiosEvidenceInstructionCount);

            var evidence = report.BiosCalls;
            var because = $"fixture '{fixture.FixtureId}'";

            evidence.Should().NotBeNull(because);
            evidence!.Sites.Select(site => site.GuestPc).Should().BeInAscendingOrder(because);
            evidence.Sites.Should().OnlyHaveUniqueItems(because);

            foreach (var site in evidence.Sites)
            {
                Enum.IsDefined(site.Family).Should().BeTrue(because);

                // A service name is only ever attached to a resolved function number, and
                // only when the identity table verifies it — never guessed from proximity.
                if (site.ServiceName is not null)
                {
                    site.FunctionNumber.Should().NotBeNull(because);
                    BiosCallNames.TryResolve(site.Family, site.FunctionNumber!.Value, out var documented)
                        .Should().BeTrue(because);
                    site.ServiceName.Should().Be(documented, because);
                }

                if (site.FunctionNumber is null)
                {
                    site.Resolution.Should().Be(BiosCallResolution.Unresolved, because);
                }
            }

            // The aggregation must account for every site exactly once.
            evidence.Summary.Sum(entry => entry.CallSiteCount).Should().Be(evidence.Sites.Count, because);
            evidence.Summary.Select(entry => (entry.Family, entry.FunctionNumber))
                .Should().OnlyHaveUniqueItems(because);

            if (evidence.Sites.Count > 0)
            {
                fixturesWithBiosCalls++;
            }
        }

        fixturesWithBiosCalls.Should().BePositive(
            "at least one local disc image must request the BIOS, otherwise the recognizer "
            + "produces no evidence to drive HLE service selection from");
    }

    /// <summary>
    /// Decode window used for BIOS evidence. The pipeline's own default
    /// (<see cref="RomAnalysisPipeline.DefaultInstructionCount"/>) is a small probe around
    /// the entry point; BIOS stubs live throughout the text segment, so this test asks for
    /// a window wide enough to cover it. Decoding stops at the end of the text segment, so
    /// a fixture smaller than this simply decodes less.
    /// </summary>
    private const int BiosEvidenceInstructionCount = 200_000;

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
#pragma warning disable AARC003
            var bytes = File.ReadAllBytes(fixture.DiscImagePath);
#pragma warning restore AARC003
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
