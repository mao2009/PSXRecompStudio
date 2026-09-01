using System.Text;
using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Stage-model tests for <see cref="RomAnalysisPipeline"/>.
///
/// Every test drives the pipeline with a synthetic, in-memory disc image, so the whole
/// flow — including each meaningful failure — is verified without any copyrighted ROM
/// and runs identically in CI.
/// </summary>
[Test]
public class RomAnalysisPipelineTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string BootValue = @"cdrom:\SLPS_TEST.01;1";
    private const string ExeIsoName = "SLPS_TEST.01;1";

    /// <summary>Stages a successful pipeline run records, in order.</summary>
    private static readonly RomAnalysisStage[] ExpectedStages =
    [
        RomAnalysisStage.Start,
        RomAnalysisStage.Input,
        RomAnalysisStage.ChdOpen,
        RomAnalysisStage.Filesystem,
        RomAnalysisStage.SystemCnf,
        RomAnalysisStage.BootExecutable,
        RomAnalysisStage.PsxExe,
        RomAnalysisStage.ExeHeader,
        RomAnalysisStage.EntryPoint,
        RomAnalysisStage.TextRegion,
        RomAnalysisStage.MipsDecode,
        RomAnalysisStage.BasicBlock,
        RomAnalysisStage.Report,
    ];

    private static byte[] BuildDisc(byte[] exeBytes) =>
        new SyntheticIsoImageBuilder()
            .AddSystemCnf(BootValue)
            .AddFile(ExeIsoName, exeBytes)
            .Build();

    private static byte[] BuildValidDisc(int instructionCount = 16) =>
        BuildDisc(SyntheticPsxExeBuilder.BuildValid(instructionCount));

    // ---------------------------------------------------------------- success path

    [Fact]
    public void RunFromIsoImage_ValidDisc_ReachesReportStage()
    {
        var outcome = RomAnalysisPipeline.RunFromIsoImage(BuildValidDisc(), Sha, instructionCount: 16);

        outcome.Status.Should().Be(RomAnalysisStatus.Pass);
        outcome.LastSuccessfulStage.Should().Be(RomAnalysisStage.Report);
        outcome.FailedStage.Should().BeNull();
        outcome.FailureReason.Should().BeNull();
        outcome.Report.Should().NotBeNull();
    }

    [Fact]
    public void RunFromIsoImage_ValidDisc_RecordsEveryStageInOrder()
    {
        var outcome = RomAnalysisPipeline.RunFromIsoImage(BuildValidDisc(), Sha, instructionCount: 16);

        outcome.Stages.Select(s => s.Stage).Should().Equal(ExpectedStages);
    }

    [Fact]
    public void RunFromIsoImage_ValidDisc_SkipsChdOpenAndPassesEveryOtherStage()
    {
        var outcome = RomAnalysisPipeline.RunFromIsoImage(BuildValidDisc(), Sha, instructionCount: 16);

        outcome.Stages.Single(s => s.Stage == RomAnalysisStage.ChdOpen)
            .Status.Should().Be(RomAnalysisStageStatus.Skipped,
                "a plain ISO image has no CHD container to open");

        outcome.Stages.Where(s => s.Stage != RomAnalysisStage.ChdOpen)
            .Should().OnlyContain(s => s.Status == RomAnalysisStageStatus.Passed);
    }

    [Fact]
    public void RunFromIsoImage_ValidDisc_ReportIsDerivedFromTheDiscNotHardCoded()
    {
        var outcome = RomAnalysisPipeline.RunFromIsoImage(BuildValidDisc(), Sha, instructionCount: 16);

        var report = outcome.Report!;
        report.DiscImageSha256.Should().Be(Sha);
        report.SystemCnfBootPath.Should().Be(BootValue);
        report.ExecutableFileName.Should().Be("SLPS_TEST.01");
        report.EntryPoint.Should().Be(SyntheticPsxExeBuilder.DefaultTextStart);
        report.DecodedInstructionCount.Should().Be(16);
        report.DecodeFailures.Should().BeEmpty();
        report.BasicBlocks.Should().NotBeEmpty();
    }

    [Fact]
    public void RunFromIsoImage_SameInput_ProducesIdenticalStagesAndReport()
    {
        var image = BuildValidDisc();

        var first = RomAnalysisPipeline.RunFromIsoImage(image, Sha, instructionCount: 16);
        var second = RomAnalysisPipeline.RunFromIsoImage(image, Sha, instructionCount: 16);

        second.Stages.Should().Equal(first.Stages, "stage results carry no timing or environment data");
        second.Report!.ToTokenString().Should().Be(first.Report!.ToTokenString());
    }

    [Fact]
    public void RunFromIsoImage_DistinctDiscs_ProduceDistinctReports()
    {
        var a = RomAnalysisPipeline.RunFromIsoImage(BuildValidDisc(instructionCount: 16), Sha, 16);
        var b = RomAnalysisPipeline.RunFromIsoImage(BuildValidDisc(instructionCount: 32), Sha, 32);

        b.Report!.ToTokenString().Should().NotBe(a.Report!.ToTokenString(),
            "the flow is title-agnostic and reflects whatever disc it was given");
    }

    // ---------------------------------------------------------------- INPUT

    [Fact]
    public void RunFromChd_EmptyImage_FailsAtInput()
    {
        var outcome = RomAnalysisPipeline.RunFromChd([], Sha);

        AssertFailure(outcome, RomAnalysisStage.Input, "EmptyInput", RomAnalysisStage.Start);
    }

    [Fact]
    public void RunFromIsoImage_MissingSha256_FailsAtInput()
    {
        var outcome = RomAnalysisPipeline.RunFromIsoImage(BuildValidDisc(), "   ");

        AssertFailure(outcome, RomAnalysisStage.Input, "MissingInputIdentity", RomAnalysisStage.Start);
    }

    // ---------------------------------------------------------------- CHD_OPEN

    [Fact]
    public void RunFromChd_NotAChdContainer_FailsAtChdOpen()
    {
        var outcome = RomAnalysisPipeline.RunFromChd(Encoding.ASCII.GetBytes("not a CHD file at all"), Sha);

        AssertFailure(outcome, RomAnalysisStage.ChdOpen, "ChdOpenFailure", RomAnalysisStage.Input);
    }

    // ---------------------------------------------------------------- FILESYSTEM

    [Fact]
    public void RunFromIsoImage_WithoutPrimaryVolumeDescriptor_FailsAtFilesystem()
    {
        var image = new SyntheticIsoImageBuilder()
            .WithoutPrimaryVolumeDescriptor()
            .AddSystemCnf(BootValue)
            .Build();

        var outcome = RomAnalysisPipeline.RunFromIsoImage(image, Sha);

        AssertFailure(outcome, RomAnalysisStage.Filesystem, "FilesystemFailure", RomAnalysisStage.Input);
    }

    // ---------------------------------------------------------------- SYSTEM_CNF

    [Fact]
    public void RunFromIsoImage_WithoutSystemCnf_FailsAtSystemCnf()
    {
        var image = new SyntheticIsoImageBuilder()
            .AddFile(ExeIsoName, SyntheticPsxExeBuilder.BuildValid())
            .Build();

        var outcome = RomAnalysisPipeline.RunFromIsoImage(image, Sha);

        AssertFailure(outcome, RomAnalysisStage.SystemCnf, "SystemCnfMissing", RomAnalysisStage.Filesystem);
    }

    [Fact]
    public void RunFromIsoImage_SystemCnfWithoutBootEntry_FailsAtSystemCnf()
    {
        var image = new SyntheticIsoImageBuilder()
            .AddSystemCnfWithoutBoot()
            .AddFile(ExeIsoName, SyntheticPsxExeBuilder.BuildValid())
            .Build();

        var outcome = RomAnalysisPipeline.RunFromIsoImage(image, Sha);

        AssertFailure(outcome, RomAnalysisStage.SystemCnf, "SystemCnfInvalid", RomAnalysisStage.Filesystem);
    }

    // ---------------------------------------------------------------- BOOT_EXECUTABLE

    [Fact]
    public void RunFromIsoImage_BootExecutableAbsentFromDisc_FailsAtBootExecutable()
    {
        var image = new SyntheticIsoImageBuilder()
            .AddSystemCnf(BootValue)
            .Build();

        var outcome = RomAnalysisPipeline.RunFromIsoImage(image, Sha);

        AssertFailure(outcome, RomAnalysisStage.BootExecutable, "BootExecutableMissing", RomAnalysisStage.SystemCnf);
    }

    // ---------------------------------------------------------------- PSX_EXE

    [Fact]
    public void RunFromIsoImage_BootExecutableWithWrongMagic_FailsAtPsxExe()
    {
        var exe = SyntheticPsxExeBuilder.Build(
            entryPoint: SyntheticPsxExeBuilder.DefaultTextStart,
            textStart: SyntheticPsxExeBuilder.DefaultTextStart,
            textSize: 64,
            storedTextBytes: SyntheticPsxExeBuilder.BuildText(16),
            magic: 0x1122334455667788);

        var outcome = RomAnalysisPipeline.RunFromIsoImage(BuildDisc(exe), Sha);

        AssertFailure(outcome, RomAnalysisStage.PsxExe, "InvalidPsxExe", RomAnalysisStage.BootExecutable);
    }

    [Fact]
    public void RunFromIsoImage_BootExecutableShorterThanHeader_FailsAtPsxExe()
    {
        var outcome = RomAnalysisPipeline.RunFromIsoImage(BuildDisc(new byte[64]), Sha);

        AssertFailure(outcome, RomAnalysisStage.PsxExe, "InvalidPsxExe", RomAnalysisStage.BootExecutable);
        outcome.FailureReason.Should().Contain("64 bytes");
    }

    // ---------------------------------------------------------------- EXE_HEADER

    [Fact]
    public void RunFromIsoImage_HeaderWithZeroTextStart_FailsAtExeHeader()
    {
        var exe = SyntheticPsxExeBuilder.Build(
            entryPoint: 0,
            textStart: 0,
            textSize: 64,
            storedTextBytes: SyntheticPsxExeBuilder.BuildText(16));

        var outcome = RomAnalysisPipeline.RunFromIsoImage(BuildDisc(exe), Sha);

        AssertFailure(outcome, RomAnalysisStage.ExeHeader, "InvalidExeHeader", RomAnalysisStage.PsxExe);
    }

    // ---------------------------------------------------------------- ENTRY_POINT

    [Fact]
    public void RunFromIsoImage_EntryPointOutsideTextRegion_FailsAtEntryPoint()
    {
        var exe = SyntheticPsxExeBuilder.Build(
            entryPoint: 0x80020000,
            textStart: SyntheticPsxExeBuilder.DefaultTextStart,
            textSize: 64,
            storedTextBytes: SyntheticPsxExeBuilder.BuildText(16));

        var outcome = RomAnalysisPipeline.RunFromIsoImage(BuildDisc(exe), Sha);

        AssertFailure(outcome, RomAnalysisStage.EntryPoint, "InvalidEntryPoint", RomAnalysisStage.ExeHeader);
    }

    [Fact]
    public void RunFromIsoImage_UnalignedEntryPoint_FailsAtEntryPoint()
    {
        var exe = SyntheticPsxExeBuilder.Build(
            entryPoint: SyntheticPsxExeBuilder.DefaultTextStart + 2,
            textStart: SyntheticPsxExeBuilder.DefaultTextStart,
            textSize: 64,
            storedTextBytes: SyntheticPsxExeBuilder.BuildText(16));

        var outcome = RomAnalysisPipeline.RunFromIsoImage(BuildDisc(exe), Sha);

        AssertFailure(outcome, RomAnalysisStage.EntryPoint, "InvalidEntryPoint", RomAnalysisStage.ExeHeader);
        outcome.FailureReason.Should().Contain("aligned");
    }

    // ---------------------------------------------------------------- TEXT_REGION

    [Fact]
    public void RunFromIsoImage_TextRegionDeclaredButAbsent_FailsAtTextRegion()
    {
        var exe = SyntheticPsxExeBuilder.Build(
            entryPoint: SyntheticPsxExeBuilder.DefaultTextStart,
            textStart: SyntheticPsxExeBuilder.DefaultTextStart,
            textSize: 0x100,
            storedTextBytes: []);

        var outcome = RomAnalysisPipeline.RunFromIsoImage(BuildDisc(exe), Sha);

        AssertFailure(outcome, RomAnalysisStage.TextRegion, "TextRegionUnavailable", RomAnalysisStage.EntryPoint);
    }

    // ---------------------------------------------------------------- MIPS_DECODE

    [Fact]
    public void RunFromIsoImage_EntryPointBeyondStoredText_FailsAtMipsDecode()
    {
        var exe = SyntheticPsxExeBuilder.Build(
            entryPoint: SyntheticPsxExeBuilder.DefaultTextStart + 0x800,
            textStart: SyntheticPsxExeBuilder.DefaultTextStart,
            textSize: 0x1000,
            storedTextBytes: SyntheticPsxExeBuilder.BuildText(2));

        var outcome = RomAnalysisPipeline.RunFromIsoImage(BuildDisc(exe), Sha);

        AssertFailure(outcome, RomAnalysisStage.MipsDecode, "DecodeFailure", RomAnalysisStage.TextRegion);
        outcome.FailureReason.Should().Contain("0x80010800");
    }

    // ---------------------------------------------------------------- failure contract

    [Fact]
    public void FailedRun_PreservesLastSuccessfulStageAndReason()
    {
        var image = new SyntheticIsoImageBuilder()
            .AddSystemCnf(BootValue)
            .Build();

        var outcome = RomAnalysisPipeline.RunFromIsoImage(image, Sha);

        outcome.Status.Should().Be(RomAnalysisStatus.Fail);
        outcome.LastSuccessfulStage.Should().Be(RomAnalysisStage.SystemCnf);
        outcome.FailedStage.Should().Be(RomAnalysisStage.BootExecutable);
        outcome.FailureReason.Should().NotBeNullOrWhiteSpace();
        outcome.Report.Should().BeNull("no report exists for a run that never reached REPORT");

        var failing = outcome.Stages.Single(s => s.Status == RomAnalysisStageStatus.Failed);
        failing.Stage.Should().Be(RomAnalysisStage.BootExecutable);
        failing.Detail.Should().Be(outcome.FailureReason);
    }

    [Fact]
    public void FailedRun_StopsAtTheFailingStage()
    {
        var outcome = RomAnalysisPipeline.RunFromChd(Encoding.ASCII.GetBytes("garbage"), Sha);

        outcome.Stages[^1].Stage.Should().Be(RomAnalysisStage.ChdOpen);
        outcome.Stages.Should().NotContain(s => s.Stage > RomAnalysisStage.ChdOpen,
            "no stage may be recorded after the failing one");
    }

    [Fact]
    public void EveryRun_RecordsStagesInStrictlyIncreasingOrder()
    {
        RomAnalysisOutcome[] outcomes =
        [
            RomAnalysisPipeline.RunFromIsoImage(BuildValidDisc(), Sha, 16),
            RomAnalysisPipeline.RunFromIsoImage(new SyntheticIsoImageBuilder().AddSystemCnf(BootValue).Build(), Sha),
            RomAnalysisPipeline.RunFromChd(Encoding.ASCII.GetBytes("garbage"), Sha),
            RomAnalysisPipeline.RunFromChd([], Sha),
        ];

        foreach (var outcome in outcomes)
        {
            var stages = outcome.Stages.Select(s => (int)s.Stage).ToList();
            stages.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
            outcome.Stages[0].Stage.Should().Be(RomAnalysisStage.Start);
        }
    }

    private static void AssertFailure(
        RomAnalysisOutcome outcome,
        RomAnalysisStage expectedFailedStage,
        string expectedFailureKind,
        RomAnalysisStage expectedLastSuccessfulStage)
    {
        outcome.Status.Should().Be(RomAnalysisStatus.Fail);
        outcome.FailedStage.Should().Be(expectedFailedStage);
        outcome.FailureKind.Should().Be(expectedFailureKind);
        outcome.LastSuccessfulStage.Should().Be(expectedLastSuccessfulStage);
        outcome.FailureReason.Should().NotBeNullOrWhiteSpace();
        outcome.Report.Should().BeNull();
    }
}
