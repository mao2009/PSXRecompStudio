using System.Globalization;
using PSXRecomp.Core.Analysis.Contracts;

namespace PSXRecomp.Tests.Analysis;

[Test]
public class ArtifactContractTests
{
    [Fact]
    public void AnalysisArtifact_CanBeConstructed_WithEvidenceConfidenceProvenanceAndUnresolvedItems()
    {
        var evidence = EvidenceReference.Create(
            EvidenceType.Screenshot,
            "capture://memory/0x80010000.png",
            "screenshot of main entrypoint",
            1700000000,
            new Dictionary<string, string> { ["resolution"] = "320x240" });

        var artifact = new AnalysisArtifact
        {
            Version = AnalysisArtifact.CurrentVersion,
            Id = "art-slus-00594-0001",
            ArtifactKind = "function-discovery",
            TitleId = "SLUS-00594",
            RegionCode = "US",
            Description = "First pass over the main overlay.",
            Status = ValidationStatus.PendingHumanReview,
            Confidence = new Confidence(ConfidenceLevel.High, "consistent trace evidence", 0.91),
            Provenance = new Provenance("psx-recomp", "0.4.1", "auto-an-7", "janedoe"),
            CreatedUnixSeconds = 1700000000,
            UpdatedUnixSeconds = 1700000100,
            EvidenceReferences = new[] { evidence },
            Functions =
            [
                new FunctionInfo(
                    "Entrypoint",
                    new FunctionBoundary(0x80010000, 0x80010100),
                    [new MnemonicRef(0x80010000, "addiu", "sp, sp, -0x20")],
                    [new CfgEdge(0x80010000, 0x80010028, "fallthrough")],
                    "overlay_main"),
            ],
            DynamicCode =
            [
                new DynamicCodeCapture(0x80020000, 512, "writes into ram region"),
            ],
            MmioFindings =
            [
                new MmioFinding(0x1F801070, "timer", "CTC 0 retrigger observed", new Confidence(ConfidenceLevel.Certain, "captured on trace", 1.0), new[] { evidence }),
            ],
            TitleWorkarounds =
            [
                new WorkaroundNote("laggy transfer", "insert delay before DMA chain", "hardware", new[] { evidence }),
            ],
            UnresolvedItems =
            [
                new UnresolvedItem("MMIO at 0x1f801040 unknown", UnresolvedItemKind.UnresolvedMmio, new[] { evidence }, ValidationStatus.Accepted),
            ],
        };

        artifact.IsValid().Should().BeTrue();
        artifact.Version.Should().Be(1);
        artifact.TitleId.Should().Be("SLUS-00594");
        artifact.RegionCode.Should().Be("US");
        artifact.EvidenceReferences.Should().HaveCount(1);
        artifact.EvidenceReferences![0].Id.Should().Be(evidence.Id);
        artifact.Functions.Should().HaveCount(1);
        artifact.UnresolvedItems.Should().HaveCount(1);
        artifact.UnresolvedItems![0].Kind.Should().Be(UnresolvedItemKind.UnresolvedMmio);
        artifact.UnresolvedItems![0].Status.Should().Be(ValidationStatus.Accepted);
    }

    [Theory]
    [InlineData(ValidationStatus.Unverified)]
    [InlineData(ValidationStatus.PendingHumanReview)]
    [InlineData(ValidationStatus.Accepted)]
    [InlineData(ValidationStatus.Rejected)]
    [InlineData(ValidationStatus.Superseded)]
    public void ValidationStatus_DefinesExpectedMembers(ValidationStatus status)
    {
        Enum.IsDefined(status).Should().BeTrue();
    }

    [Theory]
    [InlineData(ConfidenceLevel.Unspecified)]
    [InlineData(ConfidenceLevel.Low)]
    [InlineData(ConfidenceLevel.Medium)]
    [InlineData(ConfidenceLevel.High)]
    [InlineData(ConfidenceLevel.Certain)]
    public void ConfidenceLevel_DefinesExpectedMembers(ConfidenceLevel level)
    {
        Enum.IsDefined(level).Should().BeTrue();
    }

    [Fact]
    public void EvidenceReference_SameContent_ProducesSameDeterministicId()
    {
        var referenceA = EvidenceReference.Create(
            EvidenceType.Disassembly,
            "disasm://0x80010000",
            "addiu sp, sp, -0x20",
            1700000000);
        var referenceB = EvidenceReference.Create(
            EvidenceType.Disassembly,
            "disasm://0x80010000",
            "addiu sp, sp, -0x20",
            1700000000);

        referenceA.Id.Should().Be(referenceB.Id);
        referenceA.Id.Should().HaveLength(16);
    }

    [Fact]
    public void EvidenceReference_DifferentContent_ProducesDifferentId()
    {
        var lower = EvidenceReference.Create(
            EvidenceType.Disassembly,
            "disasm://0x80010000",
            "addiu sp, sp, -0x20",
            1700000000);
        var upper = EvidenceReference.Create(
            EvidenceType.Disassembly,
            "disasm://0x80010000",
            "ADDIU sp, sp, -0x20",
            1700000000);

        lower.Id.Should().NotBe(upper.Id);
    }

    [Fact]
    public void Confidence_ScoreInsideUnitRange_IsValidAndRationaleIsKept()
    {
        new Confidence(ConfidenceLevel.High, "consistent with trace", 0.5).IsValid().Should().BeTrue();
        new Confidence(ConfidenceLevel.Certain, null, 1.0).IsValid().Should().BeTrue();
    }

    [Fact]
    public void Confidence_ScoreOutsideUnitRange_IsInvalid()
    {
        new Confidence(ConfidenceLevel.Medium, null, 1.0001).IsValid().Should().BeFalse();
        new Confidence(ConfidenceLevel.Medium, null, -0.1).IsValid().Should().BeFalse();
    }

    [Fact]
    public void Artifact_ToTokenString_IsDeterministic()
    {
        var first = BuildSampleArtifact();
        var second = BuildSampleArtifact();

        first.ToTokenString().Should().Be(second.ToTokenString());
    }

    [Fact]
    public void Artifact_ToTokenString_ChangesWhenContentChanges()
    {
        var artifact = BuildSampleArtifact() with { Description = "changed" };

        artifact.ToTokenString().Should().NotBe(BuildSampleArtifact().ToTokenString());
    }

    [Fact]
    public void Artifact_ToTokenString_ScoreIsCultureInvariant()
    {
        var artifact = BuildSampleArtifact();
        var updated = artifact with
        {
            Confidence = new Confidence(ConfidenceLevel.Medium, null, 0.10),
            UpdatedUnixSeconds = 1700000200,
        };

        updated.ToTokenString().Should().Contain(@"confidence=level\=Medium\;rationale\=\;score\=0.1");
    }

    [Fact]
    public void Records_AreImmutablyEqualByContent()
    {
        new Confidence(ConfidenceLevel.High, "observed", 0.9).Should().Be(new Confidence(ConfidenceLevel.High, "observed", 0.9));
        new FunctionBoundary(0x80010000, 0x80010100).Should().Be(new FunctionBoundary(0x80010000, 0x80010100));
        new Provenance("t", "1", "a").Should().Be(new Provenance("t", "1", "a"));
    }

    [Fact]
    public void Records_WithExpression_PreservesOriginal()
    {
        var artifact = BuildSampleArtifact() with { TitleId = "PBPX-95010" };

        BuildSampleArtifact().TitleId.Should().Be("SLUS-00594");
        artifact.TitleId.Should().Be("PBPX-95010");
    }

    [Fact]
    public void Artifact_WithoutBinary_IsValid()
    {
        var artifact = new AnalysisArtifact
        {
            Version = AnalysisArtifact.CurrentVersion,
            Id = "art-x",
            ArtifactKind = "overlay-analysis",
            TitleId = "SLUS-00594",
            RegionCode = "US",
        };

        artifact.IsValid().Should().BeTrue();
    }

    private static AnalysisArtifact BuildSampleArtifact()
    {
        var evidence = EvidenceReference.Create(
            EvidenceType.Screenshot,
            "capture://memory/0x80010000.png",
            "screenshot of main entrypoint",
            1700000000,
            new Dictionary<string, string> { ["resolution"] = "320x240" });

        return new AnalysisArtifact
        {
            Version = AnalysisArtifact.CurrentVersion,
            Id = "art-slus-00594-0001",
            ArtifactKind = "function-discovery",
            TitleId = "SLUS-00594",
            RegionCode = "US",
            Description = "First pass over the main overlay.",
            Status = ValidationStatus.PendingHumanReview,
            Confidence = new Confidence(ConfidenceLevel.High, "consistent trace evidence", 0.91),
            Provenance = new Provenance("psx-recomp", "0.4.1", "auto-an-7", "janedoe"),
            CreatedUnixSeconds = 1700000000,
            UpdatedUnixSeconds = 1700000100,
            EvidenceReferences = new[] { evidence },
            Functions =
            [
                new FunctionInfo(
                    "Entrypoint",
                    new FunctionBoundary(0x80010000, 0x80010100),
                    [new MnemonicRef(0x80010000, "addiu", "sp, sp, -0x20")],
                    [new CfgEdge(0x80010000, 0x80010028, "fallthrough")],
                    "overlay_main"),
            ],
            DynamicCode =
            [
                new DynamicCodeCapture(0x80020000, 512, "writes into ram region"),
            ],
            MmioFindings =
            [
                new MmioFinding(0x1F801070, "timer", "CTC 0 retrigger observed", new Confidence(ConfidenceLevel.Certain, "captured on trace", 1.0), new[] { evidence }),
            ],
            TitleWorkarounds =
            [
                new WorkaroundNote("laggy transfer", "insert delay before DMA chain", "hardware", new[] { evidence }),
            ],
            UnresolvedItems =
            [
                new UnresolvedItem("MMIO at 0x1f801040 unknown", UnresolvedItemKind.UnresolvedMmio, new[] { evidence }, ValidationStatus.Accepted),
            ],
        };
    }
}