using System.Text;
using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Tests.Recompiler;
using Xunit;

namespace PSXRecomp.Tests.Cli;

/// <summary>
/// The ruling on #839: a RAM word is never in a public output. The public fallback-targets export carries no word; the word goes only to
/// the explicit local evidence, which covers RAM PCs and is bound to one export.
/// </summary>
[Test]
public sealed class LocalRamEvidenceTests
{
    private const uint RamPc = ObservedRootsFixtures.Callee; // 0x80010010: a RAM PC
    private const uint RomPc = 0xBFC00000u;                  // the BIOS ROM range: never a RAM word
    private const string ExportSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void PublicExport_CarriesNoRamWord()
    {
        using var dir = new TempDirectory();
        var romBytes = new byte[OpenBiosFirmware.ImageSize];
        var rom = dir.WriteFile("openbios.bin", romBytes);
        dir.WriteFile("test.exe", ObservedRootsFixtures.Exe(ObservedRootsFixtures.Text));
        var manifestPath = dir.WriteFile("images.txt", Encoding.ASCII.GetBytes("exe test test.exe\n"));
        var manifest = LoadImageManifest.Read(manifestPath, OpenBiosFirmware.FromBytes(romBytes), disc: null);
        var evidence = new MixedFallbackEvidence(1, 0, 40, 0, 0, [new MixedFallbackTarget(RamPc, 1, 40, ObservedRootsFixtures.Dest)]);

        var json = OpenBiosProbeCommand.BuildFallbackTargetsDocument(
            evidence, ObservedRootsFixtures.Sha256Hex(romBytes), manifest, _ => "unknown-code", TitleExecutionState.Completed, null, 0, [], null);

        json.Should().NotBeNull();
        json!.Should().NotContain("ramWord", "the public export never carries a RAM word");
        ObservedRootPlanner.ReadExport(json).Targets.Single().RamWord.Should().BeNull();
    }

    [Fact]
    public void LocalEvidence_CarriesTheWordsOfRamPcsOnly_AndReadsBackBoundToItsExport()
    {
        var json = OpenBiosProbeCommand.BuildRamEvidenceDocument(ExportSha, [RamPc, RomPc], _ => 0x24020BADu);

        json.Should().NotContain("BFC00000", "a PC outside RAM has no RAM word to record");
        var words = ObservedRootPlanner.ReadRamEvidence(json, ExportSha);
        words.Should().ContainKey(RamPc).WhoseValue.Should().Be(0x24020BADu);
        words.Should().NotContainKey(RomPc);
    }
    [Fact]
    public void RefusalFor_RefusesAPathInsideAGitWorkTree()
    {
        using var dir = new TempDirectory();
        dir.CreateSubdirectory(Path.Combine("repo", ".git"));
        var inside = Path.Combine(dir.FullPath, "repo", "ram-evidence.json");

        LocalRamEvidence.RefusalFor(inside, mustBeNew: true).Should().Contain("outside any Git work tree");
        LocalRamEvidence.RefusalFor(inside, mustBeNew: false).Should().Contain("outside any Git work tree");
    }

    [Fact]
    public void RefusalFor_RefusesReplacingAnExistingFile_WhenItIsToBeWritten()
    {
        using var dir = new TempDirectory();
        var existing = dir.WriteFile("existing.json", Encoding.UTF8.GetBytes("{}"));

        LocalRamEvidence.RefusalFor(existing, mustBeNew: true).Should().Contain("already exists");
        LocalRamEvidence.RefusalFor(existing, mustBeNew: false).Should().BeNull("reading an existing file is allowed");
    }

    [Fact]
    public void RefusalFor_AcceptsANewPathOutsideAGitWorkTree()
    {
        using var dir = new TempDirectory();
        LocalRamEvidence.RefusalFor(Path.Combine(dir.FullPath, "fresh.json"), mustBeNew: true).Should().BeNull();
    }
}
