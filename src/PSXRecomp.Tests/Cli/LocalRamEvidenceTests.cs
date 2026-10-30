using System.Diagnostics;
using System.Text;
using FluentAssertions;
using PSXRecomp.Core;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Tests.Recompiler;
using Xunit;

#pragma warning disable AARC003 // the test fixtures create real files, links and processes; the rule guards production code
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
    private const string WorkTreeRefusal = "outside any Git work tree";

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

        LocalRamEvidence.RefusalFor(inside, mustBeNew: true).Should().Contain(WorkTreeRefusal);
        LocalRamEvidence.RefusalFor(inside, mustBeNew: false).Should().Contain(WorkTreeRefusal);
    }

    [Fact]
    public void RefusalFor_RefusesAPathInsideAWorkTree_ThatIsASubdirectoryOfTheRepository()
    {
        using var dir = new TempDirectory();
        dir.CreateSubdirectory(Path.Combine("repo", ".git"));
        dir.CreateSubdirectory(Path.Combine("repo", "reports"));
        var inside = Path.Combine(dir.FullPath, "repo", "reports", "ram.json");

        LocalRamEvidence.RefusalFor(inside, mustBeNew: true).Should().Contain(WorkTreeRefusal);
    }

    [Fact]
    public void WriteNew_InsideAWorkTree_IsRefused_AndCreatesNothing()
    {
        using var dir = new TempDirectory();
        dir.CreateSubdirectory(Path.Combine("repo", ".git"));
        dir.CreateSubdirectory(Path.Combine("repo", "reports"));
        var inside = Path.Combine(dir.FullPath, "repo", "reports", "ram.json");

        LocalRamEvidence.WriteNew(inside, "{}").Should().Contain(WorkTreeRefusal);
        File.Exists(inside).Should().BeFalse("a refused write creates nothing");
    }

    [Fact]
    public void RefusalFor_RefusesAPathInsideALinkedWorktree_WhoseGitIsAFile()
    {
        using var dir = new TempDirectory();
        dir.CreateSubdirectory("linked");
        dir.WriteFile(Path.Combine("linked", ".git"), Encoding.ASCII.GetBytes("gitdir: C:/somewhere/.git/worktrees/linked\n"));
        dir.CreateSubdirectory(Path.Combine("linked", "reports"));

        LocalRamEvidence.RefusalFor(Path.Combine(dir.FullPath, "linked", "reports", "ram.json"), mustBeNew: true).Should().Contain(WorkTreeRefusal);
        LocalRamEvidence.RefusalFor(Path.Combine(dir.FullPath, "linked", "deeper", "ram.json"), mustBeNew: true).Should().Contain(WorkTreeRefusal);
    }

    [SkippableFact]
    public void Junction_IntoAWorkTree_IsRefused_ForWriteAndRead()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "NOT RUN: junctions are a Windows reparse point; this test is Windows-only");
        using var dir = new TempDirectory();
        dir.CreateSubdirectory(Path.Combine("repo", ".git"));
        dir.CreateSubdirectory(Path.Combine("repo", "reports"));
        var existing = dir.WriteFile(Path.Combine("repo", "reports", "existing.json"), Encoding.UTF8.GetBytes("{}"));
        var junction = Path.Combine(dir.FullPath, "outside-looking");
        Skip.IfNot(TryCreateJunction(junction, Path.Combine(dir.FullPath, "repo", "reports"), out var error),
            $"NOT RUN: mklink /J failed in this environment: {error}");

        LocalRamEvidence.RefusalFor(Path.Combine(junction, "ram.json"), mustBeNew: true).Should().Contain(WorkTreeRefusal);
        LocalRamEvidence.WriteNew(Path.Combine(junction, "ram.json"), "{}").Should().Contain(WorkTreeRefusal);
        File.Exists(Path.Combine(dir.FullPath, "repo", "reports", "ram.json")).Should().BeFalse("no RAM word may be written into the worktree");
        LocalRamEvidence.ReadText(Path.Combine(junction, "existing.json"), out var text).Should().Contain(WorkTreeRefusal);
        text.Should().BeNull();
    }

    [SkippableFact]
    public void Symlink_ToASubdirectoryOfAWorkTree_IsRefused()
    {
        using var dir = new TempDirectory();
        dir.CreateSubdirectory(Path.Combine("repo", ".git"));
        dir.CreateSubdirectory(Path.Combine("repo", "reports"));
        var link = Path.Combine(dir.FullPath, "linked-subdir");
        Skip.IfNot(TryCreateDirectorySymlink(link, Path.Combine(dir.FullPath, "repo", "reports"), out var error),
            $"NOT RUN: symbolic link creation needs Developer Mode or elevation in this environment: {error}");

        LocalRamEvidence.RefusalFor(Path.Combine(link, "ram.json"), mustBeNew: true).Should().Contain(WorkTreeRefusal);
        LocalRamEvidence.WriteNew(Path.Combine(link, "ram.json"), "{}").Should().Contain(WorkTreeRefusal);
        File.Exists(Path.Combine(dir.FullPath, "repo", "reports", "ram.json")).Should().BeFalse();
    }

    // Symbolic-link resolution without the privilege: the real target of an existing outside-looking directory is supplied by the test.
    // It exercises the decision on a resolved target, including the fail-closed case where the target cannot be resolved.
    [Fact]
    public void ResolvedTargetInsideAWorkTree_IsRefused_AndAnUnresolvableTargetFailsClosed()
    {
        using var dir = new TempDirectory();
        dir.CreateSubdirectory(Path.Combine("repo", ".git"));
        var repoReports = dir.CreateSubdirectory(Path.Combine("repo", "reports"));
        var outsideLooking = dir.CreateSubdirectory("outside");
        var probe = Path.Combine(outsideLooking, "ram.json");

        LocalRamEvidence.RefusalFor(probe, mustBeNew: true, resolveExisting: _ => repoReports).Should().Contain(WorkTreeRefusal);
        LocalRamEvidence.RefusalFor(probe, mustBeNew: false, resolveExisting: _ => repoReports).Should().Contain(WorkTreeRefusal);
        LocalRamEvidence.RefusalFor(probe, mustBeNew: true, resolveExisting: _ => null).Should().NotBeNull("an unresolved target is refused");
    }

    [Fact]
    public void CanonicalPathOutsideTheRepository_IsAllowed_AndWritesOnceAndReadsBack()
    {
        using var dir = new TempDirectory();
        dir.CreateSubdirectory(Path.Combine("repo", ".git"));
        var outside = Path.Combine(dir.FullPath, "fresh.json");

        LocalRamEvidence.RefusalFor(outside, mustBeNew: true).Should().BeNull();
        LocalRamEvidence.WriteNew(outside, "{\"format\":\"fallback-ram-evidence/v1\"}").Should().BeNull();
        LocalRamEvidence.ReadText(outside, out var text).Should().BeNull();
        text.Should().Be("{\"format\":\"fallback-ram-evidence/v1\"}");
    }

    [Fact]
    public void OverwritingAnExistingFile_IsRefused_AndLeavesItUnchanged()
    {
        using var dir = new TempDirectory();
        var existing = dir.WriteFile("existing.json", Encoding.UTF8.GetBytes("{}"));

        LocalRamEvidence.WriteNew(existing, "{\"replaced\":true}").Should().Contain("already exists");
        File.ReadAllText(existing).Should().Be("{}");
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

    // Finding 1 (PR #839 re-review): a volume with no drive letter resolves to \\?\Volume{GUID}\..., which is not an absolute location. Stripping
    // its prefix would leave a relative path, anchored to the working directory, so the Git check would look at the wrong tree. Refused.
    [Fact]
    public void ResolvedVolumeGuidPath_WithoutADriveLetter_IsRefused_NotAnchoredToTheWorkingDirectory()
    {
        using var dir = new TempDirectory();
        var outsideLooking = dir.CreateSubdirectory("outside");
        const string VolumeForm = "Volume{8c0c2e4f-0000-0000-0000-100000000000}\\repo\\reports";

        LocalRamEvidence.RefusalFor(Path.Combine(outsideLooking, "ram.json"), mustBeNew: true, resolveExisting: _ => VolumeForm)
            .Should().Contain("cannot be determined");
        LocalRamEvidence.RefusalFor(Path.Combine(outsideLooking, "ram.json"), mustBeNew: false, resolveExisting: _ => VolumeForm)
            .Should().Contain("cannot be determined");
    }

    // PR #839 re-review item 3: a write failure with a non-IO exception is a refusal, not an exception that escapes. Deterministic: the create
    // seam hands back a read-only handle, which RandomAccess.Write cannot write to.
    [Fact]
    public void WriteNew_WhenTheWriteFails_ReturnsARefusal_AndDoesNotThrow()
    {
        using var dir = new TempDirectory();
        var readOnly = dir.WriteFile("readonly.bin", [1, 2, 3]);
        var target = Path.Combine(dir.FullPath, "ram.bin");

        var refusal = LocalRamEvidence.WriteNewChecked(target, [9, 9], RealPathInterop.TryGetRealPath, resolveHandle: null, beforeCreate: null,
            create: _ => File.OpenHandle(readOnly, FileMode.Open, FileAccess.Read));

        refusal.Should().Contain("the write failed");
    }

    // PR #839 MAJOR follow-up item 2: a NotSupportedException from the path resolution is a refusal, not an exception that escapes. Deterministic
    // through the resolver seam (the real EntryExists path that can throw it is not reproducible here).
    [Fact]
    public void Resolve_WhenTheLocationQueryThrowsNotSupported_IsARefusal()
    {
        using var dir = new TempDirectory();
        var outside = dir.CreateSubdirectory("outside");

        var refusal = LocalRamEvidence.RefusalFor(Path.Combine(outside, "ram.json"), mustBeNew: true,
            resolveExisting: _ => throw new NotSupportedException("the path format is not supported"));

        refusal.Should().Contain("could not be verified");
    }

    // PR #839 MAJOR follow-up item 2: the create's exceptions (on POSIX, File.OpenHandle) are refusals too.
    [Fact]
    public void WriteNew_WhenTheCreateThrowsNotSupported_IsARefusal()
    {
        using var dir = new TempDirectory();
        var target = Path.Combine(dir.FullPath, "ram.bin");

        var refusal = LocalRamEvidence.WriteNewChecked(target, [1], RealPathInterop.TryGetRealPath, resolveHandle: null, beforeCreate: null,
            create: _ => throw new NotSupportedException("handle type not supported"));

        refusal.Should().Contain("could not be created exclusively");
        File.Exists(target).Should().BeFalse();
    }

    // PR #839 MINOR item 2: a path beyond the Windows limit makes GetFullPath throw PathTooLongException (an IOException); the probe's pre-check
    // must get a refusal, not an exception that escapes.
    [SkippableFact]
    public void RefusalFor_APathBeyondTheWindowsLimit_IsARefusal_NotAnException()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "NOT RUN: the path-length limit exercised here is Windows' (32767 characters)");
        var tooLong = Path.Combine(Path.GetTempPath(), new string('a', 40000) + ".json");

        LocalRamEvidence.RefusalFor(tooLong, mustBeNew: true).Should().Contain("is not a valid path");
    }

    [Fact]
    public void NormalizeFinalPath_KeepsOnlyDriveAbsoluteAndUncLocations()
    {
        RealPathInterop.NormalizeFinalPath(@"\\?\C:\repo\reports").Should().Be(@"C:\repo\reports");
        RealPathInterop.NormalizeFinalPath(@"\\?\UNC\server\share\reports").Should().Be(@"\\server\share\reports");
        RealPathInterop.NormalizeFinalPath(@"\\?\Volume{8c0c2e4f-0000-0000-0000-100000000000}\repo").Should().BeNull("a volume with no drive letter is not a location");
        RealPathInterop.NormalizeFinalPath(@"C:\repo").Should().BeNull("a path without the final-path prefix is not a verified location");
    }

    // Finding 3 and PR #839 re-review item 1: the created file is checked through its handle before any byte is written. A file that is not
    // where it was verified to be receives nothing, and the empty file this call created is deleted through its handle (never by path).
    [SkippableFact]
    public void WriteNew_WhenTheCreatedFileIsNotAtTheVerifiedLocation_WritesNothing_AndDeletesItsEmptyFile()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "NOT RUN: the handle check and deletion through the handle are Windows only");
        using var dir = new TempDirectory();
        var target = Path.Combine(dir.FullPath, "ram.bin");

        var refusal = LocalRamEvidence.WriteNewChecked(target, [1, 2, 3], RealPathInterop.TryGetRealPath,
            _ => Path.Combine(dir.FullPath, "elsewhere.bin"), beforeCreate: null);

        refusal.Should().Contain("nothing was written").And.Contain("deleted through its handle");
        File.Exists(target).Should().BeFalse("the empty file this call created is removed, so no empty evidence file stays behind");
        File.Exists(Path.Combine(dir.FullPath, "elsewhere.bin")).Should().BeFalse();
    }

    [SkippableFact]
    public void WriteNew_WhenTheCreatedFileCannotBeConfirmed_WritesNothing_AndDeletesItsEmptyFile()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "NOT RUN: the handle check and deletion through the handle are Windows only");
        using var dir = new TempDirectory();
        var target = Path.Combine(dir.FullPath, "ram.bin");

        LocalRamEvidence.WriteNewChecked(target, [1, 2, 3], RealPathInterop.TryGetRealPath, _ => null, beforeCreate: null)
            .Should().Contain("nothing was written");
        File.Exists(target).Should().BeFalse();
    }

    // PR #839 re-review item 5: a real directory-junction swap in the create window, made deterministic by a hook that runs after the last
    // check and before the create. The create makes an empty file in the work tree before the handle check deletes it, so the transient
    // file is not observed; this test asserts the end state only (no file remains in the work tree, and the write is refused).
    [SkippableFact]
    public void CreateWindow_DirectorySwappedForAJunction_IsRefusedByTheHandleCheck_AndNoFileRemainsInTheWorkTree()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "NOT RUN: directory junctions and the handle check are Windows only");
        using var dir = new TempDirectory();
        dir.CreateSubdirectory(Path.Combine("repo", ".git"));
        var reports = dir.CreateSubdirectory(Path.Combine("repo", "reports"));
        var outside = dir.CreateSubdirectory("outside");
        var target = Path.Combine(outside, "ram.bin");
        var inWorkTree = Path.Combine(reports, "ram.bin");
        var swapped = false;

        var refusal = LocalRamEvidence.WriteNewChecked(target, [1, 2, 3], RealPathInterop.TryGetRealPath, RealPathInterop.TryGetRealPath, beforeCreate: () =>
        {
            Directory.Delete(outside);
            Skip.IfNot(TryCreateJunction(outside, reports, out var error), $"NOT RUN: the junction could not be created in the create window: {error}");
            swapped = true;
        });

        swapped.Should().BeTrue("the swap ran in the create window");
        refusal.Should().Contain("resolves to").And.Contain("nothing was written");
        File.Exists(inWorkTree).Should().BeFalse("no file may be created in the work tree through the swapped directory");
    }

    [Fact]
    public void WriteNewBytes_OutsideTheRepository_WritesTheBytesExactly()
    {
        using var dir = new TempDirectory();
        var target = Path.Combine(dir.FullPath, "ram.bin");

        LocalRamEvidence.WriteNewBytes(target, [0x00, 0xFF, 0x10]).Should().BeNull();
        File.ReadAllBytes(target).Should().Equal(0x00, 0xFF, 0x10);
    }

    private static bool TryCreateJunction(string link, string target, out string error) =>
        TryRun("cmd.exe", ["/c", "mklink", "/J", link, target], out error);

    private static bool TryCreateDirectorySymlink(string link, string target, out string error)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryRun(string program, string[] arguments, out string error)
    {
        var info = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        error = output.Trim();
        return process.ExitCode == 0;
    }
}

#pragma warning restore AARC003
