using System.Globalization;
using System.Text;
using System.Text.Json;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.DiscImage.AnalysisArtifacts;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Contract tests for the #215 selected-function provenance document: the metadata-only
/// way to identify which guest range of which executable a downstream consumer selected,
/// without embedding the executable's bytes.
///
/// Every test runs on synthetic blocks, so the guarantees — a stable versioned schema,
/// byte-for-byte reproducibility, canonical ordering that ignores insertion order,
/// range/identity distinction, and the absence of raw instruction data — are verified on
/// every CI run with no copyrighted content present.
/// </summary>
[Test]
public class SelectedFunctionProvenanceTests
{
    private const uint StartAddress = 0x80010000;
    private const uint EndAddress = 0x8001001C;

    // ---------------------------------------------------------------- schema

    [Fact]
    public void Schema_OwnsTheCanonicalFileNameAndVersion()
    {
        AnalysisArtifactSchema.FunctionProvenanceFileName.Should().Be("function-provenance.json");
        AnalysisArtifactSchema.FunctionProvenanceSchemaVersion.Should().Be(1);
    }

    [Fact]
    public void Build_CarriesEveryRequiredFieldInCanonicalForm()
    {
        var provenance = BuildProvenance();

        provenance.SchemaVersion.Should().Be(AnalysisArtifactSchema.FunctionProvenanceSchemaVersion);
        provenance.ArtifactKind.Should().Be(AnalysisArtifactSchema.FunctionProvenanceArtifactKind);
        provenance.Fixture.ExecutableSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        provenance.Fixture.DiscImageSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        provenance.StartAddress.Should().Be("0x80010000");
        provenance.EndAddress.Should().Be("0x8001001C");
        provenance.InstructionCount.Should().Be(8);
        provenance.SelectionRule.Should().NotBeNullOrWhiteSpace();
        provenance.BlockOrdering.Should().Be(AnalysisArtifactSchema.BasicBlockOrdering);

        provenance.BasicBlocks.Select(block => block.StartAddress)
            .Should().BeInAscendingOrder(StringComparer.Ordinal);
        provenance.BasicBlocks.Should().AllSatisfy(block =>
        {
            block.StartAddress.Should().MatchRegex("^0x[0-9A-F]{8}$");
            block.EndAddress.Should().MatchRegex("^0x[0-9A-F]{8}$");
            block.InstructionCount.Should().BePositive();
        });

        provenance.BlockIdentitySha256.Should().MatchRegex("^[0-9a-f]{64}$");
        provenance.BlockIdentitySha256.Should().Be(
            FunctionProvenanceBuilder.ComputeBlockIdentity(provenance.BasicBlocks),
            "the recorded CFG identity must be recomputable from the ordered blocks alone");

        provenance.SubsetOrdering.Should().Be(AnalysisArtifactSchema.FunctionProvenanceSubsetOrdering);
        provenance.RequiredInstructionSubset.Should().BeInAscendingOrder(StringComparer.Ordinal);
        provenance.RequiredInstructionSubset.Should().OnlyHaveUniqueItems();

        provenance.FlagsOrdering.Should().Be(AnalysisArtifactSchema.FunctionProvenanceFlagsOrdering);
        provenance.UnresolvedFlags.Should().BeInAscendingOrder(StringComparer.Ordinal);
        provenance.UnresolvedFlags.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Build_IsByteForByteIdenticalAcrossRepeatedRuns()
    {
        var first = BuildProvenance();
        var second = BuildProvenance();

        Encoding.UTF8.GetBytes(first.ToCanonicalJson())
            .Should().Equal(Encoding.UTF8.GetBytes(second.ToCanonicalJson()));
    }

    /// <summary>
    /// Strong regression: the document must describe the selected range, not the order the
    /// blocks were handed in. The same blocks in a different insertion order must serialize
    /// to identical bytes.
    /// </summary>
    [Fact]
    public void Build_IsIndependentOfBlockInsertionOrder()
    {
        var blocks = Report().BasicBlocks;
        var forward = BuildProvenance(blocks);
        var reversed = BuildProvenance(blocks.Reverse().ToArray());

        reversed.ToCanonicalJson().Should().Be(forward.ToCanonicalJson());
        reversed.BlockIdentitySha256.Should().Be(forward.BlockIdentitySha256);
    }

    [Fact]
    public void Build_IsByteForByteIdenticalUnderEveryCulture()
    {
        var baseline = BuildProvenance().ToCanonicalJson();

        AssertIdenticalUnderCulture(CultureInfo.GetCultureInfo("de-DE"), baseline);
        AssertIdenticalUnderCulture(CultureInfo.GetCultureInfo("tr-TR"), baseline);

        void AssertIdenticalUnderCulture(CultureInfo culture, string expected)
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = culture;
                BuildProvenance().ToCanonicalJson().Should().Be(expected,
                    $"provenance must be byte-for-byte identical under culture '{culture.Name}'");
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }
    }

    // -------------------------------------------------------------- identity

    [Fact]
    public void Identity_IsStableForTheSameRangeAndDistinctForADifferentRange()
    {
        var sameRangeAgain = BuildProvenance();
        var otherRange = BuildProvenance(Report().BasicBlocks.Take(1).ToArray(), endAddress: 0x8001000C);

        sameRangeAgain.BlockIdentitySha256.Should().Be(BuildProvenance().BlockIdentitySha256);
        otherRange.BlockIdentitySha256.Should().NotBe(sameRangeAgain.BlockIdentitySha256);
        otherRange.ToCanonicalJson().Should().NotBe(sameRangeAgain.ToCanonicalJson());
    }

    [Fact]
    public void DifferentFixtures_ProduceDifferentArtifactsForTheSameRange()
    {
        var first = BuildProvenance();
        var second = BuildProvenance(fixtureId: "fixture-b", discSeed: "disc-b");

        second.Fixture.DiscImageSha256.Should().NotBe(first.Fixture.DiscImageSha256);
        second.ToCanonicalJson().Should().NotBe(first.ToCanonicalJson());
    }

    // ------------------------------------------------ no executable payload

    [Fact]
    public void Artifact_ContainsNoRawInstructionWordsOrExecutableBytes()
    {
        var content = BuildProvenance().ToCanonicalJson();

        // The document is a metadata contract: it identifies a range by addresses, hashes,
        // and opcode names, never by the instructions themselves.
        content.Should().NotContain("rawWord", "the provenance document must not carry raw instruction words");
        content.Should().NotContain("encodedInstructions", "the provenance document must not carry encoded words");

        var document = JsonDocument.Parse(content);
        document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .Should().NotContain(new[] { "rawWord", "encodedInstructions", "instructions" });
    }

    // -------------------------------------------------- negative boundaries

    [Fact]
    public void Build_RejectsAnEmptyBlockList()
    {
        var act = () => BuildProvenance(Array.Empty<BasicBlock>());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Build_RejectsAnEndAddressBeforeTheStart()
    {
        var act = () => BuildProvenance(Report().BasicBlocks, startAddress: 0x80010010, endAddress: 0x80010000);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_RejectsAMissingSelectionRule(string rule)
    {
        var act = () => BuildProvenance(selectionRule: rule);

        act.Should().Throw<ArgumentException>();
    }

    // --------------------------------------------------------------- helpers

    private static DiscImageAnalysisReport Report() =>
        SyntheticAnalysisReports.CreateReport(SyntheticAnalysisReports.Sha256Of("disc-a"));

    private static SelectedFunctionProvenanceDocument BuildProvenance(
        IReadOnlyList<BasicBlock>? blocks = null,
        string fixtureId = "fixture-a",
        string discSeed = "disc-a",
        uint startAddress = StartAddress,
        uint endAddress = EndAddress,
        string selectionRule = "entry-point-reachability")
    {
        var report = SyntheticAnalysisReports.CreateReport(
            SyntheticAnalysisReports.Sha256Of(discSeed), entryPoint: StartAddress);
        var fixture = DeterministicArtifactBuilder.BuildFixtureIdentity(
            SyntheticAnalysisReports.CreateInput(fixtureId, report));

        return FunctionProvenanceBuilder.Build(
            fixture,
            startAddress,
            endAddress,
            blocks ?? report.BasicBlocks,
            selectionRule,
            requiredInstructionSubset: new[] { "lui", "addiu", "beq", "jal", "jr" },
            unresolvedFlags: new[] { "indirect-control-flow" });
    }
}