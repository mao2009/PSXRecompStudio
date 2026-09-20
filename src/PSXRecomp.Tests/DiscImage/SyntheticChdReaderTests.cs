using PSXRecomp.Core.DiscImage;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.DiscImageTests;

/// <summary>
/// Pins <see cref="SyntheticChdBuilder"/>'s fixture to the production reader: this is
/// where the synthetic disc is proven to be a genuine CHD that <see cref="ChdReader"/>
/// opens, whose sectors decode back to the original ISO 9660 user data, and whose boot
/// executable reaches the pipeline's REPORT stage. The CLI's CHD tests in
/// <see cref="PSXRecomp.Tests.Cli.CliRunTests"/> then rely on that proven disc — no
/// copyrighted image anywhere.
/// </summary>
[Test]
public sealed class SyntheticChdReaderTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static byte[] BuildWrappedDisc(out byte[] iso)
    {
        var exe = SyntheticPsxExeBuilder.BuildValid(8);
        iso = new SyntheticIsoImageBuilder()
            .AddSystemCnf(SyntheticChdBuilder.BootValue)
            .AddFile(SyntheticChdBuilder.ExeIsoName, exe)
            .Build();
        return SyntheticChdBuilder.WrapInChd(iso);
    }

    [Fact]
    public void Open_ProductionReaderOpensItAndEverySectorDecodesToTheIso()
    {
        var chdBytes = BuildWrappedDisc(out var iso);
        int sectorCount = iso.Length / Iso9660Reader.SectorSize;

        using var chd = ChdReader.Open(new MemoryStream(chdBytes));

        chd.Header.Version.Should().Be(5);
        chd.FramesPerHunk.Should().Be(1);

        for (int sector = 0; sector < sectorCount; sector++)
        {
            var raw = chd.ReadSector(sector);
            raw[15].Should().Be(1, "every frame is written as RAW Mode-1");

            int isoOffset = sector * Iso9660Reader.SectorSize;
            raw.Skip(16).Take(Iso9660Reader.SectorSize)
                .Should().Equal(iso.Skip(isoOffset).Take(Iso9660Reader.SectorSize),
                    $"sector {sector} must carry the original ISO user data");
        }
    }

    [Fact]
    public void RunFromChd_ReachesReportAndCarriesTheBootExecutable()
    {
        var chdBytes = BuildWrappedDisc(out _);

        var outcome = RomAnalysisPipeline.RunFromChd(new MemoryStream(chdBytes), Sha);

        outcome.Status.Should().Be(RomAnalysisStatus.Pass);
        outcome.FailedStage.Should().BeNull();
        outcome.Report.Should().NotBeNull();
        outcome.Executable.Should().NotBeNull();
        outcome.Executable!.FileName.Should().Be("SLPS_TEST.01");
    }

    [Fact]
    public void RunFromChd_WithoutSystemCnf_FailsClassifiedAtTheFilesystemLayer()
    {
        var iso = new SyntheticIsoImageBuilder()
            .AddFile("OTHER.TXT;1", System.Text.Encoding.ASCII.GetBytes("not a boot disc"))
            .Build();

        var outcome = RomAnalysisPipeline.RunFromChd(
            new MemoryStream(SyntheticChdBuilder.WrapInChd(iso)), Sha);

        outcome.Status.Should().Be(RomAnalysisStatus.Fail);
        outcome.FailedStage.Should().Be(RomAnalysisStage.SystemCnf);
        outcome.FailureKind.Should().Be("SystemCnfMissing");
        outcome.Executable.Should().BeNull();
    }
}