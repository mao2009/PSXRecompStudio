using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.DiscImage.AnalysisArtifacts;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Issue #410: recompilation-coverage measurement, separate from proof-candidate
/// selection.
///
/// Every test here runs on synthetic input, so classification, aggregation and
/// determinism are verified on every CI run rather than only on a machine that owns a
/// disc image. The real-ROM path is exercised by the fixture-gated tests; it produces the
/// same document through the same code.
/// </summary>
[Test]
public class RealRomCoverageAnalyzerTests
{
    private const string FixtureId = "fixture-a";
    private const uint EntryPoint = 0x80010000;

    // ------------------------------------------------------------ classification

    [Fact]
    public void EveryAnalyzedUnitIsClassifiedExactlyOnce()
    {
        var report = CreateReport();

        var coverage = Analyze(report);

        var classified = coverage.Classes.Sum(entry => entry.InstructionCount);
        classified.Should().Be(
            coverage.Totals.DecodedInstructions
            + coverage.Totals.DecodeFailures
            + coverage.Totals.NotAnalyzedInstructions,
            "the classes must partition the corpus: no unit may be counted twice or dropped");
    }

    [Fact]
    public void ALowerableInstructionIsDecidedByTheLoweringStageItself()
    {
        var report = CreateReport();

        var coverage = Analyze(report);

        // lui, addiu, beq, nop, jal, nop, nop lower; the jr does not (indirect).
        Count(coverage, RealRomCoverageClass.Lowerable).Should().Be(7);
        coverage.Totals.LowerableInstructions.Should().Be(7);
    }

    [Fact]
    public void IndirectControlFlowIsCountedSeparatelyFromUnsupportedInstructions()
    {
        var report = CreateReport();

        var coverage = Analyze(report);

        Count(coverage, RealRomCoverageClass.IndirectControlFlow).Should().Be(1);
        Count(coverage, RealRomCoverageClass.UnsupportedInstruction).Should().Be(0);
        Reason(coverage, RealRomCoverageClass.IndirectControlFlow, "jr").Should().Be(1);
    }

    [Fact]
    public void AnUnsupportedInstructionIsNamedRatherThanSilentlyRejected()
    {
        // MULT is decoded by the CPU model but not lowered by the current contract.
        var report = CreateReport(extraWords: [0x00220018]);

        var coverage = Analyze(report);

        Count(coverage, RealRomCoverageClass.UnsupportedInstruction).Should().Be(1);
        Reason(coverage, RealRomCoverageClass.UnsupportedInstruction, "mult").Should().Be(1,
            "a rejection must carry an explicit, machine-readable reason, never a generic bucket");
    }

    [Fact]
    public void ADecodeFailureIsClassifiedAsMalformedWithTheDecoderSOwnReason()
    {
        var report = CreateReport();

        var coverage = Analyze(report);

        Count(coverage, RealRomCoverageClass.MalformedOrUndecodable).Should().Be(1);
        Reason(coverage, RealRomCoverageClass.MalformedOrUndecodable, "Address outside text segment bounds")
            .Should().Be(1);
    }

    [Fact]
    public void AnInstructionOutsideTheBasicBlockPartitionIsRecordedAsUncertainty()
    {
        var full = CreateReport();
        var report = full with { BasicBlocks = [full.BasicBlocks[0]] };

        var coverage = Analyze(report);

        Count(coverage, RealRomCoverageClass.AnalysisUncertainty).Should().Be(4,
            "nothing may be claimed about a word the analyzer's own block partition does not cover");
        Reason(coverage, RealRomCoverageClass.AnalysisUncertainty, "outside-basic-block-partition").Should().Be(4);
    }

    [Fact]
    public void ABiosCallSiteIsClassifiedFromRecognizerEvidenceNotGuesswork()
    {
        var report = SyntheticAnalysisReports.CreateBiosCallReport(
            SyntheticAnalysisReports.Sha256Of("disc-bios"), EntryPoint);

        var coverage = Analyze(report);

        Count(coverage, RealRomCoverageClass.BiosDependency).Should().Be(report.BiosCalls!.Sites.Count);
        Reason(coverage, RealRomCoverageClass.BiosDependency, "A0:3C").Should().Be(2);
        Reason(coverage, RealRomCoverageClass.BiosDependency, "A0:unresolved").Should().Be(1,
            "an unresolved BIOS identity is recorded as a dependency, not dropped");

        // The subroutine's own `jr $ra` is not a BIOS transfer and must not be absorbed.
        Reason(coverage, RealRomCoverageClass.IndirectControlFlow, "jr").Should().Be(1);
    }

    // -------------------------------------------------------------- denominator

    [Fact]
    public void TextWordsTheBoundedDecodeWindowNeverReachedAreNotCountedAsRejected()
    {
        var report = CreateReport();

        var coverage = Analyze(report);

        coverage.Totals.TextInstructionSlots.Should().Be(report.TextSize / 4);
        coverage.Totals.NotAnalyzedInstructions.Should().Be(
            coverage.Totals.TextInstructionSlots
            - coverage.Totals.DecodedInstructions
            - coverage.Totals.DecodeFailures);
        coverage.Totals.RejectedInstructions.Should().Be(
            coverage.Totals.DecodedInstructions + coverage.Totals.DecodeFailures - coverage.Totals.LowerableInstructions,
            "a word that was never examined has not been rejected");
    }

    [Fact]
    public void BasicBlocksArePartitionedIntoFullyLowerablePartialAndRejected()
    {
        var report = CreateReport();

        var coverage = Analyze(report);

        var totals = coverage.Totals;
        (totals.FullyLowerableBasicBlocks + totals.PartiallyLowerableBasicBlocks + totals.RejectedBasicBlocks)
            .Should().Be(totals.BasicBlocks);
        totals.FullyLowerableBasicBlocks.Should().Be(1);
        totals.PartiallyLowerableBasicBlocks.Should().Be(1, "the second block ends in an indirect jump");
        totals.RejectedBasicBlocks.Should().Be(0);
    }

    // ----------------------------------------------- lowerable vs. proven coverage

    [Fact]
    public void DifferentialCoverageIsEmptyUntilADifferentialActuallyRan()
    {
        var coverage = Analyze(CreateReport());

        coverage.Differential.AttemptedWindows.Should().Be(0);
        coverage.Differential.MatchedInstructions.Should().Be(0);
        coverage.Differential.Windows.Should().BeEmpty();
        coverage.Totals.LowerableInstructions.Should().BePositive(
            "lowerable coverage must be reported even when nothing has been proven, and the two must stay distinct");
    }

    [Fact]
    public void DifferentialCoverageCountsOnlyWindowsThatWereActuallyExecuted()
    {
        var coverage = Analyze(CreateReport(), validations:
        [
            new RealRomCoverageValidation { StartAddress = EntryPoint + 0x10, InstructionCount = 6, Matched = false },
            new RealRomCoverageValidation { StartAddress = EntryPoint, InstructionCount = 4, Matched = true },
        ]);

        coverage.Differential.AttemptedWindows.Should().Be(2);
        coverage.Differential.MatchedWindows.Should().Be(1);
        coverage.Differential.MismatchedWindows.Should().Be(1);
        coverage.Differential.MatchedInstructions.Should().Be(4);
        coverage.Differential.MismatchedInstructions.Should().Be(6);
        coverage.Differential.Windows.Select(window => window.StartAddress)
            .Should().Equal("0x80010000", "0x80010010");
        coverage.Differential.MatchedInstructions.Should().BeLessThan(coverage.Totals.LowerableInstructions,
            "proven coverage is strictly narrower than lowerable coverage and must never be conflated with it");
    }

    [Fact]
    public void LowerableCoverageIsAnUpperBoundOnWhatTheConservativeSelectorAccepts()
    {
        var report = CreateReport();

        var coverage = Analyze(report);
        var candidate = RealRomCandidateSelector.SelectBest(report);

        candidate.Should().NotBeNull();
        coverage.Totals.LowerableInstructions.Should().BeGreaterThanOrEqualTo(candidate!.InstructionCount,
            "the selector only takes a contiguous, self-contained window, so it can never exceed the corpus-wide count");
    }

    // ------------------------------------------------------------- determinism

    [Fact]
    public void TheSameAnalysisProducesByteIdenticalCoverageText()
    {
        var first = Analyze(CreateReport()).ToCanonicalJson();
        var second = Analyze(CreateReport()).ToCanonicalJson();

        second.Should().Be(first);
    }

    [Fact]
    public void DiscoveryOrderOfTheInputDoesNotChangeTheOutput()
    {
        var report = CreateReport();
        var canonical = Analyze(report).ToCanonicalJson();

        var shuffled = report with
        {
            DecodedInstructions = report.DecodedInstructions.Reverse().ToList(),
            BasicBlocks = report.BasicBlocks.Reverse().ToList(),
            DecodeFailures = report.DecodeFailures.Reverse().ToList(),
        };

        Analyze(shuffled).ToCanonicalJson().Should().Be(canonical,
            "every array must be explicitly ordered, never left in discovery order");
    }

    [Fact]
    public void TheDocumentCarriesNoTimestampPathOrEnvironmentData()
    {
        var text = Analyze(CreateReport()).ToCanonicalJson();

#pragma warning disable AARC003
        var machineName = Environment.MachineName;
        var userName = Environment.UserName;
#pragma warning restore AARC003

        text.Should().NotContain(machineName, "a host name would break cross-machine reproducibility");
        text.Should().NotContain(userName);
        text.Should().NotContain(RealRomFixtures.RepositoryRoot.Replace("\\", "\\\\", StringComparison.Ordinal),
            "a local filesystem path must never reach a deterministic artifact");
        foreach (var forbidden in new[] { "elapsed", "timestamp", "createdAt", "durationMs", "hostname" })
        {
            text.Should().NotContainEquivalentOf(forbidden,
                "execution metadata belongs in the log, never in a deterministic artifact");
        }

        text.Should().NotContain("\r", "artifacts use LF so Windows and Linux runs agree byte-for-byte");
        text.Should().EndWith("\n");
    }

    [Fact]
    public void EveryClassIsPresentEvenAtZeroSoTwoFixturesDiffRowForRow()
    {
        var coverage = Analyze(CreateReport());

        coverage.Classes.Select(entry => entry.Class).Should().Equal(
            Enum.GetValues<RealRomCoverageClass>().OrderBy(value => (byte)value).Select(value => value.ToString()));
        coverage.Unit.Should().Be("instruction");
        coverage.UnitRationale.Should().Contain("Function-level coverage is not reported");
    }

    // ------------------------------------------------------- artifact integration

    [Fact]
    public void CoverageIsPersistedAsAFifthArtifactAndIndexedByTheManifest()
    {
        var report = CreateReport();
        var input = SyntheticAnalysisReports.CreateInput(FixtureId, report);
        var coverage = RealRomCoverageAnalyzer.Analyze(
            report, DeterministicArtifactBuilder.BuildFixtureIdentity(input));

        var artifacts = DeterministicArtifactBuilder.Build(input with { Coverage = coverage });

        artifacts.Files.Select(file => file.FileName).Should().Equal(
            "cfg.json", "coverage.json", "instructions.json", "manifest.json", "report.json");

        var entry = artifacts.Manifest.Documents.Single(
            document => document.FileName == AnalysisArtifactSchema.CoverageFileName);
        entry.ArtifactKind.Should().Be(AnalysisArtifactSchema.CoverageArtifactKind);
        entry.SchemaVersion.Should().Be(AnalysisArtifactSchema.CoverageSchemaVersion);

        var bytes = artifacts.Files.Single(file => file.FileName == AnalysisArtifactSchema.CoverageFileName).ToUtf8Bytes();
        entry.Sha256.Should().Be(ArtifactJson.Sha256Hex(bytes));
        entry.SizeBytes.Should().Be(bytes.Length);
    }

    [Fact]
    public void CoverageIsOptionalSoAnAnalysisWithoutItIsUnchanged()
    {
        var input = SyntheticAnalysisReports.CreateInput(FixtureId, CreateReport());

        var artifacts = DeterministicArtifactBuilder.Build(input);

        artifacts.Coverage.Should().BeNull();
        artifacts.Files.Select(file => file.FileName).Should().Equal(
            "cfg.json", "instructions.json", "manifest.json", "report.json");
    }

    [Fact]
    public void ThePersistedCoverageDocumentIsAttributedToTheSameInputAsItsSiblings()
    {
        var report = CreateReport();
        var input = SyntheticAnalysisReports.CreateInput(FixtureId, report);
        var foreign = RealRomCoverageAnalyzer.Analyze(report, new ArtifactFixtureIdentity
        {
            FixtureId = "someone-else",
            DiscImageFormat = "CHD",
            DiscImageSha256 = new string('0', 64),
            DiscImageSizeBytes = 1,
            ExecutableFileName = "OTHER.EXE",
            ExecutableSerial = "OTHER.EXE",
            ExecutableSizeBytes = 1,
            ExecutableSha256 = new string('0', 64),
        });

        var artifacts = DeterministicArtifactBuilder.Build(input with { Coverage = foreign });

        artifacts.Coverage!.Fixture.Should().Be(artifacts.Manifest.Fixture,
            "all documents in a fixture directory must attribute themselves to one input");
    }

    // ------------------------------------------------------------------ helpers

    private static RealRomCoverageDocument Analyze(
        DiscImageAnalysisReport report, IReadOnlyList<RealRomCoverageValidation>? validations = null)
    {
        var input = SyntheticAnalysisReports.CreateInput(FixtureId, report);
        return RealRomCoverageAnalyzer.Analyze(
            report, DeterministicArtifactBuilder.BuildFixtureIdentity(input), validations);
    }

    private static long Count(RealRomCoverageDocument coverage, RealRomCoverageClass coverageClass) =>
        coverage.Classes.Single(entry => entry.Class == coverageClass.ToString()).InstructionCount;

    private static long Reason(RealRomCoverageDocument coverage, RealRomCoverageClass coverageClass, string detail) =>
        coverage.Reasons
            .Where(entry => entry.Class == coverageClass.ToString() && entry.Detail == detail)
            .Sum(entry => entry.InstructionCount);

    /// <summary>
    /// The shared synthetic analysis, optionally extended with extra raw words appended to
    /// the final basic block so one specific instruction shape can be exercised.
    /// </summary>
    private static DiscImageAnalysisReport CreateReport(IReadOnlyList<uint>? extraWords = null)
    {
        var report = SyntheticAnalysisReports.CreateReport(SyntheticAnalysisReports.Sha256Of("disc-a"), entryPoint: EntryPoint);
        if (extraWords is null || extraWords.Count == 0)
        {
            return report;
        }

        var instructions = report.DecodedInstructions.ToList();
        var nextAddress = instructions[^1].Address + 4;
        foreach (var word in extraWords)
        {
            var decoded = PSXRecomp.Core.Cpu.R3000aDecoder.Decode(word);
            instructions.Add(new DecodedInstruction
            {
                Address = nextAddress,
                RawWord = word,
                Mnemonic = MipsInstructionFormatter.FormatMnemonic(decoded.Opcode),
                Operands = MipsInstructionFormatter.FormatOperands(decoded),
                Format = decoded.Format.ToString(),
                ControlFlow = decoded.ControlFlow.ToString(),
            });
            nextAddress += 4;
        }

        var blocks = report.BasicBlocks.ToList();
        var last = blocks[^1];
        blocks[^1] = last with
        {
            EndAddress = instructions[^1].Address,
            InstructionCount = last.InstructionCount + extraWords.Count,
        };

        return report with
        {
            DecodedInstructions = instructions,
            DecodedInstructionCount = instructions.Count,
            BasicBlocks = blocks,
            // The extra words displace the decode failure, which is anchored to the old end.
            DecodeFailures = [new DecodeFailure { Address = nextAddress, Reason = "Address outside text segment bounds" }],
        };
    }
}
