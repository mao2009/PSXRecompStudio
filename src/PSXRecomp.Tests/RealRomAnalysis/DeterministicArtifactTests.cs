using System.Text;
using System.Text.Json;
using PSXRecomp.Core.Analysis.Contracts;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.DiscImage.AnalysisArtifacts;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Contract tests for the deterministic real-ROM analysis artifact format.
///
/// Every test here runs on synthetic input, so the guarantees the format claims —
/// stable schema, byte-for-byte reproducibility, canonical ordering, SHA-256 identity,
/// multi-fixture support, and freedom from environment contamination — are verified on
/// every CI run rather than only on a machine that happens to own a disc image.
/// </summary>
[Test]
public class DeterministicArtifactTests
{
    private const string FixtureA = "fixture-a";
    private const string FixtureB = "fixture-b";

    // ---------------------------------------------------------------- schema

    [Fact]
    public void Build_ProducesTheFourCanonicalDocumentsInFileNameOrder()
    {
        var artifacts = BuildArtifacts(FixtureA, "disc-a");

        artifacts.Files.Select(file => file.FileName).Should().Equal(
            "cfg.json", "instructions.json", "manifest.json", "report.json");
    }

    [Fact]
    public void Documents_DeclareTheirSchemaVersionAndArtifactKind()
    {
        var artifacts = BuildArtifacts(FixtureA, "disc-a");

        artifacts.Manifest.SchemaVersion.Should().Be(AnalysisArtifactSchema.ManifestSchemaVersion);
        artifacts.Manifest.ArtifactKind.Should().Be(AnalysisArtifactSchema.ManifestArtifactKind);
        artifacts.Report.SchemaVersion.Should().Be(AnalysisArtifactSchema.ReportSchemaVersion);
        artifacts.Report.ArtifactKind.Should().Be(AnalysisArtifactSchema.ReportArtifactKind);
        artifacts.Instructions.SchemaVersion.Should().Be(AnalysisArtifactSchema.InstructionsSchemaVersion);
        artifacts.Instructions.ArtifactKind.Should().Be(AnalysisArtifactSchema.InstructionsArtifactKind);
        artifacts.Cfg.SchemaVersion.Should().Be(AnalysisArtifactSchema.CfgSchemaVersion);
        artifacts.Cfg.ArtifactKind.Should().Be(AnalysisArtifactSchema.CfgArtifactKind);
    }

    [Fact]
    public void Manifest_ReferencesEverySiblingDocumentByContentHash()
    {
        var artifacts = BuildArtifacts(FixtureA, "disc-a");
        var contentByName = artifacts.Files.ToDictionary(file => file.FileName, file => file.ToUtf8Bytes());

        artifacts.Manifest.Documents.Select(document => document.FileName).Should().Equal(
            "cfg.json", "instructions.json", "report.json");

        foreach (var document in artifacts.Manifest.Documents)
        {
            var bytes = contentByName[document.FileName];
            document.Sha256.Should().Be(ArtifactJson.Sha256Hex(bytes),
                "the manifest must address each sibling document by the hash of its persisted bytes");
            document.SizeBytes.Should().Be(bytes.Length);
        }
    }

    [Fact]
    public void Manifest_DoesNotHashItself()
    {
        var artifacts = BuildArtifacts(FixtureA, "disc-a");

        artifacts.Manifest.Documents.Should().NotContain(
            document => document.FileName == AnalysisArtifactSchema.ManifestFileName,
            "a self-referential hash cannot be computed and must never be attempted");
    }

    [Fact]
    public void Build_RejectsANonCanonicalFixtureId()
    {
        var input = SyntheticAnalysisReports.CreateInput(
            "Not A Fixture Id", SyntheticAnalysisReports.CreateReport(SyntheticAnalysisReports.Sha256Of("disc-a")));

        var act = () => DeterministicArtifactBuilder.Build(input);

        act.Should().Throw<ArgumentException>();
    }

    // ----------------------------------------------------------- determinism

    [Fact]
    public void Build_IsByteForByteIdenticalAcrossRepeatedRuns()
    {
        var first = BuildArtifacts(FixtureA, "disc-a");
        var second = BuildArtifacts(FixtureA, "disc-a");

        first.Files.Should().HaveCount(second.Files.Count);
        for (int index = 0; index < first.Files.Count; index++)
        {
            first.Files[index].FileName.Should().Be(second.Files[index].FileName);
            first.Files[index].ToUtf8Bytes().Should().Equal(second.Files[index].ToUtf8Bytes(),
                $"'{first.Files[index].FileName}' must be byte-for-byte identical across runs");
        }
    }

    [Fact]
    public void Serialization_UsesLfLineEndingsAndNoByteOrderMark()
    {
        var artifacts = BuildArtifacts(FixtureA, "disc-a");

        foreach (var file in artifacts.Files)
        {
            file.Content.Should().NotContain("\r",
                $"'{file.FileName}' must use LF endings so Windows and Linux runs agree byte-for-byte");
            file.Content.Should().EndWith("\n");

            var bytes = file.ToUtf8Bytes();
            bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "artifacts must carry no BOM");
        }
    }

    [Fact]
    public void Artifacts_ContainNoTimestampPathOrEnvironmentData()
    {
        var artifacts = BuildArtifacts(FixtureA, "disc-a");

#pragma warning disable PSXR005
        var machineName = Environment.MachineName;
        var userName = Environment.UserName;
#pragma warning restore PSXR005
        var repositoryRoot = RealRomFixtures.RepositoryRoot;

        foreach (var file in artifacts.Files)
        {
            var content = file.Content;

            foreach (var forbidden in new[] { "elapsed", "timestamp", "createdAt", "updatedAt", "durationMs", "hostname" })
            {
                content.Should().NotContainEquivalentOf(forbidden,
                    $"'{file.FileName}' is a deterministic artifact; execution metadata belongs in the log");
            }

            content.Should().NotContain(repositoryRoot.Replace("\\", "\\\\", StringComparison.Ordinal),
                $"'{file.FileName}' must not embed a local filesystem path");
            content.Should().NotContain("/rom/", $"'{file.FileName}' must not embed a local filesystem path");

            // Guarded: an empty machine/user name would make "does not contain" vacuously
            // false in the assertion library rather than trivially true.
            if (machineName.Length > 0)
            {
                content.Should().NotContainEquivalentOf(machineName, $"'{file.FileName}' must not embed the host name");
            }

            if (userName.Length > 0)
            {
                content.Should().NotContainEquivalentOf(userName, $"'{file.FileName}' must not embed the user name");
            }
        }
    }

    /// <summary>
    /// Property-style determinism check: the artifact must describe the analysis, not the
    /// order in which the analyzer happened to emit it. Feeding the same instructions,
    /// blocks and edges in many different permutations must produce one single output.
    /// </summary>
    [Fact]
    public void Build_IsIndependentOfInputCollectionOrder()
    {
        var canonical = BuildArtifacts(FixtureA, "disc-a");
        var expected = ConcatenatedContent(canonical);

        for (uint seed = 1; seed <= 25; seed++)
        {
            var report = SyntheticAnalysisReports.CreateReport(SyntheticAnalysisReports.Sha256Of("disc-a"));
            var shuffled = report with
            {
                DecodedInstructions = Shuffle(report.DecodedInstructions, seed),
                BasicBlocks = Shuffle(report.BasicBlocks, seed * 7),
                CfgEdges = Shuffle(report.CfgEdges, seed * 13),
                DecodeFailures = Shuffle(report.DecodeFailures, seed * 31),
            };

            var artifacts = DeterministicArtifactBuilder.Build(
                SyntheticAnalysisReports.CreateInput(FixtureA, shuffled));

            ConcatenatedContent(artifacts).Should().Be(expected,
                $"permutation {seed} of the same analysis must serialize identically");
        }
    }

    // -------------------------------------------------------------- identity

    [Fact]
    public void Identity_IsCarriedByEveryDocument()
    {
        var artifacts = BuildArtifacts(FixtureA, "disc-a");
        var identity = artifacts.Manifest.Fixture;

        identity.DiscImageSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        identity.ExecutableSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        artifacts.Report.Fixture.Should().Be(identity);
        artifacts.Instructions.Fixture.Should().Be(identity);
        artifacts.Cfg.Fixture.Should().Be(identity);
    }

    [Fact]
    public void DifferentDiscSha256_YieldsDifferentIdentityAndDifferentArtifacts()
    {
        var first = BuildArtifacts(FixtureA, "disc-a");
        var second = BuildArtifacts(FixtureA, "disc-b");

        second.Manifest.Fixture.DiscImageSha256.Should().NotBe(first.Manifest.Fixture.DiscImageSha256);
        ConcatenatedContent(second).Should().NotBe(ConcatenatedContent(first));
    }

    [Fact]
    public void FixtureId_IsAnAliasWhileTheDiscHashIsTheIdentity()
    {
        var underAliasA = BuildArtifacts(FixtureA, "disc-a");
        var underAliasB = BuildArtifacts(FixtureB, "disc-a");

        underAliasB.Manifest.Fixture.DiscImageSha256.Should().Be(underAliasA.Manifest.Fixture.DiscImageSha256,
            "the same disc image keeps its identity regardless of the local directory alias");
        underAliasB.Manifest.Fixture.FixtureId.Should().Be(FixtureB);
    }

    [Theory]
    [InlineData("SLPS_012.34;1", "SLPS-01234")]
    [InlineData("SLPS_012.34", "SLPS-01234")]
    [InlineData("slus_987.65;1", "SLUS-98765")]
    [InlineData("PSX.EXE;1", "PSX.EXE")]
    [InlineData("", "")]
    public void DeriveExecutableSerial_IsPureAndTitleAgnostic(string fileName, string expected)
    {
        AnalysisArtifactSchema.DeriveExecutableSerial(fileName).Should().Be(expected);
    }

    [Theory]
    [InlineData("PERSONA", "persona")]
    [InlineData("Some Game (USA)", "some-game-usa")]
    [InlineData("disc_01.track", "disc_01.track")]
    [InlineData("///", "unnamed")]
    [InlineData("", "unnamed")]
    public void NormalizeFixtureId_ProducesACanonicalAlias(string label, string expected)
    {
        var normalized = AnalysisArtifactSchema.NormalizeFixtureId(label);

        normalized.Should().Be(expected);
        AnalysisArtifactSchema.IsValidFixtureId(normalized).Should().BeTrue();
    }

    [Theory]
    [InlineData("Fixture")]
    [InlineData("-leading-dash")]
    [InlineData("has space")]
    [InlineData("")]
    public void IsValidFixtureId_RejectsNonCanonicalAliases(string candidate)
    {
        AnalysisArtifactSchema.IsValidFixtureId(candidate).Should().BeFalse();
    }

    // --------------------------------------------------- instruction artifact

    [Fact]
    public void InstructionArtifact_IsOrderedByAddressAndCarriesEveryRequiredField()
    {
        var artifacts = BuildArtifacts(FixtureA, "disc-a");
        var instructions = artifacts.Instructions;

        instructions.Ordering.Should().Be(AnalysisArtifactSchema.InstructionOrdering);
        instructions.Count.Should().Be(instructions.Instructions.Count);
        instructions.Instructions.Select(instruction => instruction.Address)
            .Should().BeInAscendingOrder(StringComparer.Ordinal);

        var first = instructions.Instructions[0];
        first.Address.Should().MatchRegex("^0x[0-9A-F]{8}$");
        first.RawWord.Should().MatchRegex("^0x[0-9A-F]{8}$");
        first.Mnemonic.Should().NotBeNullOrEmpty();
        first.Operands.Should().NotBeNull();
        first.Format.Should().NotBeNullOrEmpty();
        first.ControlFlow.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void InstructionArtifact_HasThePinnedCanonicalEncoding()
    {
        var artifacts = BuildMinimalArtifacts();

        Content(artifacts, AnalysisArtifactSchema.InstructionsFileName).Should().Be(
            """
            {
              "schemaVersion": 1,
              "artifactKind": "psxrecomp.real-rom-analysis.instructions",
              "fixture": {
                "fixtureId": "fixture-a",
                "discImageFormat": "CHD",
                "discImageSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "discImageSizeBytes": 512345678,
                "executableFileName": "SLPS_012.34;1",
                "executableSerial": "SLPS-01234",
                "executableSizeBytes": 2064,
                "executableSha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
              },
              "ordering": "address-ascending",
              "count": 1,
              "instructions": [
                {
                  "address": "0x80010000",
                  "rawWord": "0x03E00008",
                  "mnemonic": "jr",
                  "operands": "$ra",
                  "format": "RType",
                  "controlFlow": "JumpRegister"
                }
              ]
            }

            """.ReplaceLineEndings("\n"));
    }

    // ---------------------------------------------------------- cfg artifact

    [Fact]
    public void CfgArtifact_IsOrderedAndCarriesEveryRequiredField()
    {
        var artifacts = BuildArtifacts(FixtureA, "disc-a");
        var cfg = artifacts.Cfg;

        cfg.BlockOrdering.Should().Be(AnalysisArtifactSchema.BasicBlockOrdering);
        cfg.EdgeOrdering.Should().Be(AnalysisArtifactSchema.CfgEdgeOrdering);
        cfg.BasicBlockCount.Should().Be(cfg.BasicBlocks.Count);
        cfg.EdgeCount.Should().Be(cfg.Edges.Count);

        cfg.BasicBlocks.Select(block => block.StartAddress).Should().BeInAscendingOrder(StringComparer.Ordinal);
        cfg.Edges.Select(edge => edge.SourceAddress + edge.TargetAddress + edge.Kind)
            .Should().BeInAscendingOrder(StringComparer.Ordinal);

        cfg.BasicBlocks[0].InstructionCount.Should().BePositive();
        cfg.Edges.Select(edge => edge.Kind).Should().Contain(new[] { "branch", "fallthrough", "jump", "indirect" });
    }

    [Fact]
    public void CfgArtifact_HasThePinnedCanonicalEncoding()
    {
        var artifacts = BuildMinimalArtifacts();

        Content(artifacts, AnalysisArtifactSchema.CfgFileName).Should().Be(
            """
            {
              "schemaVersion": 1,
              "artifactKind": "psxrecomp.real-rom-analysis.cfg",
              "fixture": {
                "fixtureId": "fixture-a",
                "discImageFormat": "CHD",
                "discImageSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "discImageSizeBytes": 512345678,
                "executableFileName": "SLPS_012.34;1",
                "executableSerial": "SLPS-01234",
                "executableSizeBytes": 2064,
                "executableSha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
              },
              "blockOrdering": "start-address-ascending,end-address-ascending",
              "edgeOrdering": "source-address-ascending,target-address-ascending,kind-ordinal",
              "basicBlockCount": 1,
              "edgeCount": 1,
              "basicBlocks": [
                {
                  "startAddress": "0x80010000",
                  "endAddress": "0x80010000",
                  "instructionCount": 1
                }
              ],
              "edges": [
                {
                  "sourceAddress": "0x80010000",
                  "targetAddress": "0x00000000",
                  "kind": "indirect"
                }
              ]
            }

            """.ReplaceLineEndings("\n"));
    }

    // ------------------------------------------------------------- report

    [Fact]
    public void Report_SummarizesEveryPipelineStageAndDistribution()
    {
        var artifacts = BuildArtifacts(FixtureA, "disc-a");
        var report = artifacts.Report;

        report.Chd.FormatVersion.Should().Be(5);
        report.Chd.TotalHunks.Should().BePositive();
        report.Iso.VolumeIdentifier.Should().NotBeNullOrEmpty();
        report.Iso.SystemCnfPresent.Should().BeTrue();
        report.SystemCnf.BootPath.Should().NotBeNullOrEmpty();
        report.Executable.EntryPoint.Should().Be("0x80010000");
        report.Executable.TextEnd.Should().Be("0x80030000");
        report.Decode.StartAddress.Should().Be("0x80010000");
        report.Decode.FailureCount.Should().Be(report.Decode.Failures.Count);

        report.Decode.MnemonicMix.Select(bucket => bucket.Name).Should().BeInAscendingOrder(StringComparer.Ordinal);
        report.Decode.FormatMix.Select(bucket => bucket.Name).Should().BeInAscendingOrder(StringComparer.Ordinal);
        report.Decode.ControlFlowMix.Select(bucket => bucket.Name).Should().BeInAscendingOrder(StringComparer.Ordinal);
        report.ControlFlow.EdgeKindMix.Select(bucket => bucket.Name).Should().BeInAscendingOrder(StringComparer.Ordinal);

        report.Decode.MnemonicMix.Sum(bucket => bucket.Count).Should().Be(report.Decode.InstructionCount);
        report.ControlFlow.EdgeKindMix.Sum(bucket => bucket.Count).Should().Be(report.ControlFlow.EdgeCount);
    }

    [Fact]
    public void Manifest_CountsMatchTheUnderlyingRuntimeReport()
    {
        var runtimeReport = SyntheticAnalysisReports.CreateReport(SyntheticAnalysisReports.Sha256Of("disc-a"));
        var artifacts = DeterministicArtifactBuilder.Build(
            SyntheticAnalysisReports.CreateInput(FixtureA, runtimeReport));

        var counts = artifacts.Manifest.Counts;
        counts.DecodedInstructions.Should().Be(runtimeReport.DecodedInstructionCount);
        counts.DecodeFailures.Should().Be(runtimeReport.DecodeFailures.Count);
        counts.BasicBlocks.Should().Be(runtimeReport.BasicBlocks.Count);
        counts.CfgEdges.Should().Be(runtimeReport.CfgEdges.Count);
        counts.CallCandidates.Should().Be(runtimeReport.CallCandidateCount);
        counts.ReturnCandidates.Should().Be(runtimeReport.ReturnCandidateCount);
        counts.Branches.Should().Be(2, "one conditional branch and one link branch are present");
        counts.Jumps.Should().Be(1, "one jump-register instruction is present");
    }

    [Fact]
    public void EveryDocument_IsValidJson()
    {
        var artifacts = BuildArtifacts(FixtureA, "disc-a");

        foreach (var file in artifacts.Files)
        {
            var act = () => JsonDocument.Parse(file.Content);
            act.Should().NotThrow($"'{file.FileName}' must be parseable JSON");
        }
    }

    // ------------------------------------------------------ multiple fixtures

    [Fact]
    public void MultipleFixtures_AreIndependentlyIdentifiableAndWrittenSideBySide()
    {
        var artifactsA = BuildArtifacts(FixtureA, "disc-a");
        var artifactsB = DeterministicArtifactBuilder.Build(SyntheticAnalysisReports.CreateInput(
            FixtureB,
            SyntheticAnalysisReports.CreateReport(
                SyntheticAnalysisReports.Sha256Of("disc-b"),
                SyntheticAnalysisReports.AlternateExecutableName,
                entryPoint: 0x80020000),
            volumeIdentifier: "SYNTHETIC_VOLUME_B"));

        artifactsA.Manifest.Fixture.ExecutableSerial.Should().Be("SLPS-01234");
        artifactsB.Manifest.Fixture.ExecutableSerial.Should().Be("SLUS-98765");
        artifactsB.Manifest.Fixture.DiscImageSha256.Should().NotBe(artifactsA.Manifest.Fixture.DiscImageSha256);

        var root = CreateTemporaryDirectory();
        try
        {
            var directoryA = RealRomArtifactWriter.Write(artifactsA, root);
            var directoryB = RealRomArtifactWriter.Write(artifactsB, root);

            directoryA.Should().NotBe(directoryB, "each fixture owns its own artifact directory");
            foreach (var file in artifactsA.Files)
            {
                RealRomArtifactWriter.ReadArtifactBytes(directoryA, file.FileName)
                    .Should().Equal(file.ToUtf8Bytes(), $"'{file.FileName}' must be persisted verbatim");
            }

            RealRomArtifactWriter.ReadArtifactText(directoryB, AnalysisArtifactSchema.ManifestFileName)
                .Should().Contain("SLUS-98765");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void RewritingAnUnchangedAnalysis_LeavesTheArtifactBytesUntouched()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var directory = RealRomArtifactWriter.Write(BuildArtifacts(FixtureA, "disc-a"), root);
            var before = artifactBytes(directory);

            RealRomArtifactWriter.Write(BuildArtifacts(FixtureA, "disc-a"), root);
            var after = artifactBytes(directory);

            after.Should().BeEquivalentTo(before,
                "a second analysis of the same input must not produce a diff");
        }
        finally
        {
            DeleteDirectory(root);
        }

        static Dictionary<string, byte[]> artifactBytes(string directory)
        {
            return new[]
            {
                AnalysisArtifactSchema.ManifestFileName,
                AnalysisArtifactSchema.ReportFileName,
                AnalysisArtifactSchema.InstructionsFileName,
                AnalysisArtifactSchema.CfgFileName,
            }.ToDictionary(name => name, name => RealRomArtifactWriter.ReadArtifactBytes(directory, name));
        }
    }

    // --------------------------------------------------------------- helpers

    private static RealRomAnalysisArtifacts BuildArtifacts(string fixtureId, string discSeed)
    {
        var report = SyntheticAnalysisReports.CreateReport(SyntheticAnalysisReports.Sha256Of(discSeed));
        return DeterministicArtifactBuilder.Build(SyntheticAnalysisReports.CreateInput(fixtureId, report));
    }

    private static RealRomAnalysisArtifacts BuildMinimalArtifacts()
    {
        var report = SyntheticAnalysisReports.CreateMinimalReport(new string('a', 64));
        return DeterministicArtifactBuilder.Build(SyntheticAnalysisReports.CreateInput(FixtureA, report));
    }

    private static string Content(RealRomAnalysisArtifacts artifacts, string fileName)
    {
        return artifacts.Files.Single(file => file.FileName == fileName).Content;
    }

    private static string ConcatenatedContent(RealRomAnalysisArtifacts artifacts)
    {
        var builder = new StringBuilder();
        foreach (var file in artifacts.Files)
        {
            builder.Append(file.FileName).Append('\n').Append(file.Content);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Deterministic Fisher-Yates shuffle driven by a small linear congruential generator.
    /// A seeded, self-contained generator keeps the permutation reproducible, so a failing
    /// seed can be re-run exactly.
    /// </summary>
    private static List<T> Shuffle<T>(IReadOnlyList<T> items, uint seed)
    {
        var shuffled = items.ToList();
        var state = seed == 0 ? 1u : seed;

        for (int index = shuffled.Count - 1; index > 0; index--)
        {
            state = unchecked((state * 1664525u) + 1013904223u);
            var swapWith = (int)(state % (uint)(index + 1));
            (shuffled[index], shuffled[swapWith]) = (shuffled[swapWith], shuffled[index]);
        }

        return shuffled;
    }

    private static string CreateTemporaryDirectory()
    {
#pragma warning disable PSXR005
        return Directory.CreateTempSubdirectory("psxr-artifacts-").FullName;
#pragma warning restore PSXR005
    }

    private static void DeleteDirectory(string path)
    {
#pragma warning disable PSXR005
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
#pragma warning restore PSXR005
    }
}
