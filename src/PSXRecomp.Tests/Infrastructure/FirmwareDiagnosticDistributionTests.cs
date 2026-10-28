using System.Diagnostics;
using System.Text.Json.Nodes;
using FluentAssertions;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.E2E;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;


/// <summary>
/// Issue #846: the developer-only measurement distribution references the local ROM, disc and AOT inputs in place. These
/// tests pin the refusals that must hold before anything is generated, and that the disc is linked, never copied.
/// </summary>
[Test]
public sealed class FirmwareDiagnosticDistributionTests
{
    private static readonly string NoSha = new('0', 64);

    private const string WindowsOnlyReason = "NOT RUN on this OS: the test drives the Windows-only measurement (hard links, junctions, CreateDirectoryW).";

    [SkippableFact]
    public void Build_RomDigestMismatch_RefusesBeforeAnyOutputIsCreated()
    {
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("openbios.bin", new byte[OpenBiosFirmware.ImageSize]);
        var output = dir.Combine("out", "measure");

        var act = () => FirmwareDiagnosticDistribution.Build(
            Request(dir, romPath: rom, romSha256: NoSha, outputDirectory: output), windows: true);

        act.Should().Throw<InvalidDataException>().WithMessage("*does not match*");
#pragma warning disable AARC003 // Justified: this line calls Directory.Exists (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.Exists(output).Should().BeFalse("a refused input must leave no output behind");
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_ExistingOutputDirectory_IsRefusedAndLeftAsIs()
    {
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("openbios.bin", new byte[OpenBiosFirmware.ImageSize]);
        var output = dir.Combine("out", "measure");
        dir.WriteFile("out/measure/keep.txt", "previous evidence"u8.ToArray());

        var act = () => FirmwareDiagnosticDistribution.Build(
            Request(dir, romPath: rom, romSha256: NoSha, outputDirectory: output), windows: true);

        act.Should().Throw<IOException>().WithMessage("*already exists*");
#pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (fixture read); it reads a fixture or a file the build wrote in this test's temporary directory, to assert its contents.
        File.ReadAllText(Path.Combine(output, "keep.txt")).Should().Be("previous evidence");
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void LinkDiscInPlace_ProducesAHardLinkNotACopy_AndLeavesTheOriginalUnchanged()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        using var dir = new TempDirectory();
        var original = dir.WriteFile("disc/game.chd", new byte[4096]);
        var before = OpenBiosRomInput.Sha256HexOfFile(original);
        var linked = dir.Combine("runtime", "disc", "game.chd");
#pragma warning disable AARC003 // Justified: this line calls Directory.CreateDirectory (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.CreateDirectory(Path.GetDirectoryName(linked)!);
#pragma warning restore AARC003

        FirmwareDiagnosticDistribution.LinkDiscInPlace(original, linked);

#pragma warning disable AARC003 // Justified: this line calls File.Exists (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        File.Exists(linked).Should().BeTrue();
#pragma warning restore AARC003
        new FileInfo(linked).LinkTarget.Should().BeNull("a hard link is an entry of the same file, not a symbolic link");
        FirmwareDiagnosticDistribution.HardLinkCount(linked).Should().Be(2, "the link shares the original's file (no copy)");
        OpenBiosRomInput.Sha256HexOfFile(original).Should().Be(before);
    }

    [SkippableFact]
    public void Measure_HostPastTheBound_IsStoppedAndTheStopIsRecorded()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // A copy of cmd.exe stands in for the host: with its stdin pipe open it waits, so it is still running at the bound.
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("openbios.bin", new byte[OpenBiosFirmware.ImageSize]);
        var disc = dir.WriteFile("disc/game.chd", new byte[16]);
        dir.WriteFile("aot/rom.roots", Array.Empty<byte>());
        dir.WriteFile("aot/images.txt", Array.Empty<byte>());
        var aot = dir.Combine("aot");
        var outDir = dir.Combine("out", "measure");
        Directory.CreateDirectory(outDir);
#pragma warning disable AARC003 // Justified: this line calls Directory.CreateDirectory (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        var runtime = Directory.CreateDirectory(dir.Combine("out", "measure", "runtime")).FullName;
#pragma warning restore AARC003
        var host = Path.Combine(runtime, "PSXRecomp.RuntimeHost.exe");
#pragma warning disable AARC003 // Justified: this line calls File.Copy (temp file) and Environment.SystemDirectory (system path read); it copies the system cmd.exe into this test's own temporary directory as a stand-in host process, which is a process fixture, not a compiler launch.
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), host);
#pragma warning restore AARC003
        var digests = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["rom"] = OpenBiosRomInput.Sha256HexOfFile(rom),
            ["disc"] = OpenBiosRomInput.Sha256HexOfFile(disc),
        };
        var request = new FirmwareDiagnosticRequest(
            RomPath: rom, RomSha256: digests["rom"], DiscPath: disc, DiscSha256: digests["disc"], AotDirectory: aot,
            OutputDirectory: outDir, CodeBytes: 64, InstructionBudget: uint.MaxValue, RunTimeoutMs: 100);
        var build = new FirmwareDiagnosticBuild(outDir, Path.Combine(outDir, "artifact.exe"), host,
            Path.Combine(runtime, RuntimeHostLayout.ManifestFileName), digests);

        var result = FirmwareDiagnosticDistribution.Measure(build, request, exitGrace: TimeSpan.FromMilliseconds(500));

        result["status"]!.GetValue<string>().Should().Be("timed-out-stopped");
        result["valid"]!.GetValue<bool>().Should().BeFalse("a run that was stopped at the bound is not a valid measurement");
        var stop = result["stop"]!.AsObject();
        stop["confirmed"]!.GetValue<bool>().Should().BeTrue();
        stop["refused"].Should().BeNull();
        stop["residuals"]!.AsArray().Should().BeEmpty("cmd.exe starts no descendant, so no residual is left");
        // The host was launched by the measurement itself, not by this test. The stop is confirmed by the measurement's own record above
        // (it holds the Process object it started), so the test does not look the PID up in the process table.
        stop["processId"]!.GetValue<int>().Should().BeGreaterThan(0);
    }

    [SkippableFact]
    public void ResolveRuntimeDiscPath_WithoutTheMarker_IsTheProductPath()
    {
        // Default off: no marker means the product's runtime/disc path, unchanged.
        using var dir = new TempDirectory();
#pragma warning disable AARC003 // Justified: this line calls Directory.CreateDirectory (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        var runtime = Directory.CreateDirectory(dir.Combine("runtime")).FullName;
#pragma warning restore AARC003

        FirmwareDiagnosticDistribution.ResolveRuntimeDiscPath(runtime, "disc/game.chd")
            .Should().Be(Path.Combine(runtime, "disc/game.chd"));
    }

    [SkippableFact]
    public void ResolveRuntimeDiscPath_WithTheMarker_NamesTheOriginalDiscAndNothingIsCreatedInRuntime()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        using var dir = new TempDirectory();
        var original = dir.WriteFile("source/game.chd", new byte[4096]);
        var runtime = Directory.CreateDirectory(dir.Combine("runtime", "disc")).FullName;
        var before = OpenBiosRomInput.Sha256HexOfFile(original);
#pragma warning disable AARC003 // Justified: this line calls File.WriteAllText (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        File.WriteAllText(Path.Combine(dir.Combine("runtime"), "diagnostic-disc.json"),
#pragma warning restore AARC003
            "{\"schema\":\"psxrecomp.diagnostic-disc.v1\",\"path\":\"" + original.Replace("\\", "\\\\") + "\",\"sha256\":\"" + before + "\",\"access\":\"read-only\"}");

        var resolved = FirmwareDiagnosticDistribution.ResolveRuntimeDiscPath(dir.Combine("runtime"), "disc/game.chd");

        resolved.Should().Be(Path.GetFullPath(original));
#pragma warning disable AARC003 // Justified: this line calls Directory.GetFileSystemEntries (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.GetFileSystemEntries(runtime).Should().BeEmpty("the disc is referenced, never linked or copied into runtime/");
#pragma warning restore AARC003
        OpenBiosRomInput.Sha256HexOfFile(original).Should().Be(before);
        new FileInfo(original).LinkTarget.Should().BeNull();
    }

    [SkippableFact]
    public void ResolveRuntimeDiscPath_MarkerWithAnUnknownSchema_IsRefused()
    {
        using var dir = new TempDirectory();
#pragma warning disable AARC003 // Justified: this line calls Directory.CreateDirectory (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        var runtime = Directory.CreateDirectory(dir.Combine("runtime")).FullName;
#pragma warning restore AARC003
        var disc = dir.WriteFile("source/game.chd", new byte[16]);
#pragma warning disable AARC003 // Justified: this line calls File.WriteAllText (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        File.WriteAllText(Path.Combine(runtime, "diagnostic-disc.json"),
#pragma warning restore AARC003
            "{\"schema\":\"other\",\"path\":\"" + disc.Replace("\\", "\\\\") + "\"}");

        var act = () => FirmwareDiagnosticDistribution.ResolveRuntimeDiscPath(runtime, "disc/game.chd");

        act.Should().Throw<InvalidDataException>().WithMessage("*unknown schema*");
    }

    [SkippableFact]
    public void DiscOpener_ReadOnlyFile_OpensWithoutWriteAccessAndLeavesTheBytesUnchanged()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        using var dir = new TempDirectory();
        var disc = dir.WriteFile("source/game.bin", new byte[2352 * 2]);
        var before = OpenBiosRomInput.Sha256HexOfFile(disc);
        File.SetAttributes(disc, FileAttributes.ReadOnly);
        try
        {
            using (var opened = DiscSectorSourceOpener.Open(disc))
            {
                opened.Source.Should().NotBeNull();
            }

            OpenBiosRomInput.Sha256HexOfFile(disc).Should().Be(before, "the read-only open does not change the disc");
        }
        finally
        {
#pragma warning disable AARC003 // Justified: this line calls File.SetAttributes (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
            File.SetAttributes(disc, FileAttributes.Normal);
#pragma warning restore AARC003
        }
    }

    [SkippableFact]
    public void RequireInputsUnchanged_AnyDigestChange_InvalidatesTheMeasurement()
    {
        var before = new Dictionary<string, string> { ["rom"] = "aa", ["disc"] = "bb" };
        var after = new Dictionary<string, string> { ["rom"] = "aa", ["disc"] = "cc" };

        FirmwareDiagnosticDistribution.RequireInputsUnchanged(before, before);
        var act = () => FirmwareDiagnosticDistribution.RequireInputsUnchanged(before, after);

        act.Should().Throw<InvalidDataException>().WithMessage("*disc*changed*");
    }

    [SkippableFact]
    public void Build_OutputDirectoryCreatedBeforeTheBuildWrites_IsLeftUntouched()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // The race: another process creates the output directory between the existence check and the build's own creation.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = ValidRequest(dir, output, roots: "nope\n");
        const string foreign = "someone else's evidence";

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, (phase, path) =>
        {
            if (phase == "claim")
            {
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "foreign.txt"), foreign);
            }
        });

        act.Should().Throw<Exception>("the roots file is invalid, so the build fails after it has started");
#pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (fixture read); it reads a fixture or a file the build wrote in this test's temporary directory, to assert its contents.
        File.ReadAllText(Path.Combine(output, "foreign.txt")).Should().Be(foreign, "a directory this build did not create is never deleted");
#pragma warning restore AARC003
#pragma warning disable AARC003 // Justified: this line calls Directory.GetFileSystemEntries (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.GetFileSystemEntries(output).Select(Path.GetFileName).Should().BeEquivalentTo(["foreign.txt"],
#pragma warning restore AARC003
            "the build writes nothing into a directory it did not create");
    }

    [SkippableFact]
    public void Build_FailureWithAForeignFileInStaging_DeletesOnlyItsOwnOutput()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = ValidRequest(dir, output, roots: "nope\n");
        string? staged = null;

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, (phase, path) =>
        {
            if (phase == "staged")
            {
                staged = path;
                File.WriteAllText(Path.Combine(path, "foreign.txt"), "someone else's file");
            }
        });

        act.Should().Throw<Exception>();
        staged.Should().NotBeNull("the build stages its output in a sibling directory before it fails");
#pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (fixture read); it reads a fixture or a file the build wrote in this test's temporary directory, to assert its contents.
        File.ReadAllText(Path.Combine(staged!, "foreign.txt")).Should().Be("someone else's file",
#pragma warning restore AARC003
            "content another process placed in the staging area is not deleted");
        Directory.Exists(Path.Combine(staged!, "runtime")).Should().BeFalse("the build's own runtime directory is removed");
#pragma warning disable AARC003 // Justified: this line calls Directory.Exists (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.Exists(output).Should().BeFalse();
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_FailureMidBuild_RemovesOnlyItsOwnTemporaryOutput()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = ValidRequest(dir, output, roots: "nope\n");

        ((Action)(() => FirmwareDiagnosticDistribution.Build(request, windows: true))).Should().Throw<Exception>();

        Directory.Exists(output).Should().BeFalse();
        Directory.GetDirectories(dir.FullPath, "." + Path.GetFileName(output) + ".staging-*").Should().BeEmpty(
            "the staging directory the build created is removed");
#pragma warning disable AARC003 // Justified: this line calls Directory.GetFileSystemEntries (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.GetFileSystemEntries(dir.FullPath).Select(Path.GetFileName).Should().BeEquivalentTo(
#pragma warning restore AARC003
            ["openbios.bin", "game.bin", "aot"], "only the inputs the caller named remain");
    }

    [SkippableFact]
    public void Build_OutputPathThatIsAnExistingJunction_IsRefusedAndTheTargetIsNeverWritten()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        using var dir = new TempDirectory();
        var target = dir.CreateSubdirectory("elsewhere");
#pragma warning disable AARC003 // Justified: this line calls File.WriteAllText (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        File.WriteAllText(Path.Combine(target, "keep.txt"), "x");
#pragma warning restore AARC003
        var output = dir.Combine("measure");
        MakeJunction(output, target);
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n");

        ((Action)(() => FirmwareDiagnosticDistribution.Build(request, windows: true))).Should().Throw<IOException>();

#pragma warning disable AARC003 // Justified: this line calls Directory.GetFileSystemEntries (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.GetFileSystemEntries(target).Select(Path.GetFileName).Should().BeEquivalentTo(["keep.txt"],
#pragma warning restore AARC003
            "a junction at the output path is never followed");
    }

    [SkippableFact]
    public void Build_DanglingJunctionAtTheOutputPath_IsNotFollowed()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        using var dir = new TempDirectory();
        var target = dir.CreateSubdirectory("gone");
        var output = dir.Combine("measure");
        MakeJunction(output, target);
        Directory.Delete(target);
        var request = ValidRequest(dir, output, roots: "nope\n");

        // The refusal of a link at the output name is the guard that must fire, not a later failure.
        ((Action)(() => FirmwareDiagnosticDistribution.Build(request, windows: true))).Should().Throw<IOException>()
            .WithMessage("*already exists*");

#pragma warning disable AARC003 // Justified: this line calls Directory.Exists (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.Exists(target).Should().BeFalse("the build never creates the directory a dangling junction names");
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_ForeignFileWrittenDuringAStep_SurvivesTheFailureCleanup()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // The foreign file is written inside the host step, after the step has started and before its first write of its own.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        dir.WriteFile("prebuilt/measure.exe", "artifact bytes"u8.ToArray());
        var hostDir = dir.CreateSubdirectory("host"); // holds no runtime host, so the step fails after the hook
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n",
            artifactSource: dir.Combine("prebuilt", "measure.exe"), hostSource: hostDir);
        string? runtime = null;

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, (phase, path) =>
        {
            if (phase == "provision")
            {
                runtime = path;
                File.WriteAllText(Path.Combine(path, "foreign.txt"), "written by another process during the step");
            }
        });

        act.Should().Throw<IOException>().WithMessage("*kept at*does not hold*",
            "the failure is reported with the kept staging directory and the host directory's refusal");
        runtime.Should().NotBeNull("the host step ran");
#pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (fixture read); it reads a fixture or a file the build wrote in this test's temporary directory, to assert its contents.
        File.ReadAllText(Path.Combine(runtime!, "foreign.txt")).Should().Be("written by another process during the step",
#pragma warning restore AARC003
            "content another process writes during a step is never deleted by the failure cleanup");
        Directory.GetFileSystemEntries(runtime!).Select(Path.GetFileName).Should().BeEquivalentTo(["foreign.txt"],
            "the build's own entries under runtime/ are removed");
        Directory.GetFileSystemEntries(Path.GetDirectoryName(runtime!)!).Select(Path.GetFileName).Should().BeEquivalentTo(["runtime"],
            "the claimed artifact copy is removed; the staging directory stays because it still holds foreign content");
#pragma warning disable AARC003 // Justified: this line calls Directory.Exists (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.Exists(output).Should().BeFalse();
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_StagingDirectoryReplacedByAJunction_IsNeitherWrittenNorDeleted()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n");
        var elsewhere = dir.CreateSubdirectory("elsewhere");
#pragma warning disable AARC003 // Justified: this line calls File.WriteAllText (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "x");
#pragma warning restore AARC003

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, (phase, path) =>
        {
            if (phase == "staged")
            {
                // A same-user process swaps the empty staging directory for a junction to a directory it controls.
                Directory.Delete(path);
                MakeJunction(path, elsewhere);
            }
        });

        act.Should().Throw<IOException>().WithMessage("*no longer a real directory*");
        Directory.GetFileSystemEntries(elsewhere).Select(Path.GetFileName).Should().BeEquivalentTo(["keep.txt"],
            "nothing is written through the junction, and the cleanup does not delete through it");
#pragma warning disable AARC003 // Justified: this line calls Directory.Exists (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.Exists(output).Should().BeFalse();
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_OnNonWindows_IsRefusedBeforeAnyOutputIsCreated()
    {
        Skip.If(OperatingSystem.IsWindows(), "the refusal is checked on non-Windows hosts; on Windows the Windows tests run instead");

        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n");

        ((Action)(() => FirmwareDiagnosticDistribution.Build(request, windows: false)))
            .Should().Throw<PlatformNotSupportedException>();
        Directory.Exists(output).Should().BeFalse();
#pragma warning disable AARC003 // Justified: this line calls Directory.GetFileSystemEntries (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.GetFileSystemEntries(dir.FullPath).Select(Path.GetFileName).Should().BeEquivalentTo(["openbios.bin", "game.bin", "aot"],
#pragma warning restore AARC003
            "the refused build leaves nothing behind, not even a staging directory");
    }

    [SkippableFact]
    public void Build_ForeignFileAppearingAtACopyTarget_IsRefusedAndNeverDeleted()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // The foreign file appears between the claim check and the copy: the copy must refuse it, and the cleanup must keep it.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = ValidRequest(dir, output, roots: "nope\n");
        const string foreign = "someone else's roots";
        string? target = null;

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, (phase, path) =>
        {
            if (phase == "file" && Path.GetFileName(path) == "rom.roots")
            {
                target = path;
                File.WriteAllText(path, foreign);
            }
        });

        act.Should().Throw<Exception>();
        target.Should().NotBeNull("the copy of rom.roots was reached");
#pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (fixture read); it reads a fixture or a file the build wrote in this test's temporary directory, to assert its contents.
        File.ReadAllText(target!).Should().Be(foreign, "a file another process created at a name the build writes is never deleted");
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_ForeignFileAppearingAtTheBootManifest_IsNeverOverwritten()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        dir.WriteFile("prebuilt/measure.exe", "artifact bytes"u8.ToArray());
        var hostDir = dir.CreateSubdirectory("host");
        dir.WriteFile("host/PSXRecomp.RuntimeHost.exe", "host bytes"u8.ToArray());
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n",
            artifactSource: dir.Combine("prebuilt", "measure.exe"), hostSource: hostDir);
        string? target = null;
        const string foreign = "someone else's boot manifest";

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, (phase, path) =>
        {
            if (phase == "file" && Path.GetFileName(path) == FirmwareBootManifest.FileName)
            {
                target = path;
                File.WriteAllText(path, foreign); // another process creates the name between the claim and the write
            }
        });

        act.Should().Throw<Exception>("the boot manifest's name is taken by another process");
        target.Should().NotBeNull("the boot manifest was reached");
#pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (fixture read); it reads a fixture or a file the build wrote in this test's temporary directory, to assert its contents.
        File.ReadAllText(target!).Should().Be(foreign, "a file another process created is never overwritten");
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_DirectoryInjectedIntoTheAdoptionWindow_IsNotRemoved()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // Another process creates the runtime/firmware directory after the build has decided to create it and before the creation.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = ValidRequest(dir, output, roots: "nope\n");
        string? injected = null;

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, (phase, path) =>
        {
            if (phase == "directory" && Path.GetFileName(path) == FirmwareBootManifest.FirmwareDirectory)
            {
                injected = path;
                Directory.CreateDirectory(path);
            }
        });

        act.Should().Throw<IOException>().WithMessage("*already exists at*" + FirmwareBootManifest.FirmwareDirectory + "*",
            "the exclusive create refuses the directory another process created, and the refusal is the failure reported");
        injected.Should().NotBeNull("the directory creation was reached");
#pragma warning disable AARC003 // Justified: this line calls Directory.Exists (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.Exists(injected!).Should().BeTrue("a directory this build did not create is never removed");
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_JunctionSwappedUnderRuntimeFirmware_IsNeverWrittenThrough()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // The firmware directory is replaced by a junction after it was created and before its first file is written.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = ValidRequest(dir, output, roots: "nope\n");
        var foreign = dir.CreateSubdirectory("foreign");
#pragma warning disable AARC003 // Justified: this line calls File.WriteAllText (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        File.WriteAllText(Path.Combine(foreign, "keep.txt"), "x");
#pragma warning restore AARC003
        var swapped = false;

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, (phase, path) =>
        {
            var name = Path.GetFileName(path);
            if (phase == "file" && !swapped && (name == "rom.roots" || name == "images.txt"))
            {
                swapped = true;
                var firmware = Path.GetDirectoryName(path)!;
                Directory.Move(firmware, firmware + "-aside");
                MakeJunction(firmware, foreign);
            }
        });

        act.Should().Throw<Exception>();
        swapped.Should().BeTrue("the swap happened before the first file write");
#pragma warning disable AARC003 // Justified: this line calls Directory.GetFileSystemEntries (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.GetFileSystemEntries(foreign).Select(Path.GetFileName).Should().BeEquivalentTo(["keep.txt"],
#pragma warning restore AARC003
            "nothing is written through the junction");
    }

    [SkippableFact]
    public void Build_AncestorDirectoryInjectedIntoTheOpenWindow_IsRefusedAndNotRemoved()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // The output's parent ("missing") does not exist, so Open creates it. Another process creates it in the window.
        using var dir = new TempDirectory();
        var output = dir.Combine("missing", "measure");
        var request = ValidRequest(dir, output, roots: "nope\n");
        string? injected = null;

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, (phase, path) =>
        {
            if (phase == "directory" && Path.GetFileName(path) == "missing")
            {
                injected = path;
                Directory.CreateDirectory(path);
            }
        });

        act.Should().Throw<IOException>().WithMessage("*already exists at*missing*",
            "Open refuses the ancestor another process created rather than adopting it");
        injected.Should().NotBeNull("the ancestor creation was reached");
#pragma warning disable AARC003 // Justified: this line calls Directory.Exists (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.Exists(injected!).Should().BeTrue("an ancestor this build did not create is never removed");
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_GeneratedArtifactPath_WithAStubToolchain_PublishesTheLinkedArtifact()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // No roots-and-artifact shortcut: the artifact is generated and the stub toolchain stands in for gcc. The stub writes its
        // "-o" output, so the compile and link steps succeed without a C compiler; the generated path's claims, reservations and
        // moves are all exercised.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var stub = BuildStubToolchain(dir);
        var hostDir = dir.CreateSubdirectory("host");
        dir.WriteFile("host/PSXRecomp.RuntimeHost.exe", "host bytes"u8.ToArray());
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n", hostSource: hostDir);

        var build = FirmwareDiagnosticDistribution.Build(request, windows: true, observe: null, compiler: stub);

        AssertGeneratedMeasurementIsConsistent(dir, output, build, expectedArtifactContent: "stub linked output");
    }

    [SkippableFact]
    public void Build_GeneratedArtifactPath_WithGcc_PublishesTheCompiledArtifact()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // NOT RUN where gcc is absent: RequireGcc skips with its reason rather than passing.
        RuntimeHostPublishE2E.RequireGcc();

        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var hostDir = dir.CreateSubdirectory("host");
        dir.WriteFile("host/PSXRecomp.RuntimeHost.exe", "host bytes"u8.ToArray());
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n", hostSource: hostDir);

        var build = FirmwareDiagnosticDistribution.Build(request, windows: true);

        AssertGeneratedMeasurementIsConsistent(dir, output, build, expectedArtifactContent: null);
    }

    private static void AssertGeneratedMeasurementIsConsistent(
        TempDirectory dir, string output, FirmwareDiagnosticBuild build, string? expectedArtifactContent)
    {
        build.ArtifactPath.Should().Be(Path.Combine(output, "measure.exe"));
        File.Exists(build.ArtifactPath).Should().BeTrue();
        if (expectedArtifactContent is not null)
        {
            File.ReadAllText(build.ArtifactPath).Should().Be(expectedArtifactContent,
                "the artifact is the binary the link step wrote, moved under the measurement's name");
        }

        Directory.GetFileSystemEntries(output).Select(Path.GetFileName).Should().BeEquivalentTo(
            ["diagnostic-identity.json", "measure.exe", "measure.exe.c", "runtime"],
            "the toolchain's random-named outputs are moved to the measurement's names or removed; the object file is removed");

        var manifest = RuntimeHostManifestJson.Parse(File.ReadAllText(build.ManifestPath));
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(build.ManifestPath)!, manifest.Artifact))
            .Should().Be(Path.GetFullPath(build.ArtifactPath), "the manifest names the artifact that was published");

#pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (fixture read); it reads a fixture or a file the build wrote in this test's temporary directory, to assert its contents.
        var identity = JsonNode.Parse(File.ReadAllText(Path.Combine(output, FirmwareDiagnosticDistribution.IdentityFileName)))!.AsObject();
#pragma warning restore AARC003
        identity["artifact"]!["file"]!.GetValue<string>().Should().Be("measure.exe");
        identity["artifact"]!["sha256"]!.GetValue<string>().Should().Be(OpenBiosRomInput.Sha256HexOfFile(build.ArtifactPath));
#pragma warning disable AARC003 // Justified: this line calls Directory.GetDirectories (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.GetDirectories(dir.FullPath, ".measure.staging-*").Should().BeEmpty("a committed build leaves no staging directory");
#pragma warning restore AARC003
    }

    /// <summary>
    /// Builds a tiny console program that stands in for the C toolchain: with "-o &lt;path&gt;" it writes that file and exits 0, and it
    /// records whether the call was a compile ("-c") or a link. Built with the SDK at test time, so no C compiler is needed.
    /// </summary>
    private static string BuildStubToolchain(TempDirectory dir)
    {
        var project = dir.Combine("stub-toolchain");
#pragma warning disable AARC003 // Justified: this line calls Directory.CreateDirectory (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.CreateDirectory(project);
#pragma warning restore AARC003
        var projectFile = Path.Combine(project, "stub-toolchain.csproj");
        File.WriteAllText(projectFile, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>disable</ImplicitUsings>
                <Nullable>disable</Nullable>
              </PropertyGroup>
            </Project>
            """);
#pragma warning disable AARC003 // Justified: this line calls File.WriteAllText (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        File.WriteAllText(Path.Combine(project, "Program.cs"), """
            public static class Program
            {
                public static int Main(string[] args)
                {
                    var output = System.Array.IndexOf(args, "-o");
                    if (output < 0 || output + 1 >= args.Length)
                    {
                        return 2;
                    }

                    var kind = System.Array.IndexOf(args, "-c") >= 0 ? "object" : "linked";
                    System.IO.File.WriteAllText(args[output + 1], "stub " + kind + " output");
                    return 0;
                }
            }
            """);
#pragma warning restore AARC003

        var outDir = dir.Combine("stub-toolchain-out");
        // The child build runs with node reuse and the shared compiler server off, so the MSBuild and compiler processes it starts do not
        // outlive it. These switches apply to this child process only; no other build server is shut down.
        var startInfo = new ProcessStartInfo("dotnet",
            $"build \"{projectFile}\" -c Release -o \"{outDir}\" -nologo -nodeReuse:false -p:UseSharedCompilation=false -p:UseRazorBuildServer=false")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using var build = Process.Start(startInfo) ?? throw new InvalidOperationException("dotnet did not start.");
        if (!build.WaitForExit(180000))
        {
            // A build that hangs is killed with its child processes, so the test does not leave a dotnet tree running.
            build.Kill(entireProcessTree: true);
            throw new TimeoutException("The dotnet build of the stub toolchain did not finish within 180 seconds and was killed.");
        }

#pragma warning disable AARC003 // Justified: this line calls Process.ExitCode (instance) (compiler launch); it starts or waits for the dotnet SDK that builds the stub toolchain, in this test's temporary directory.
        build.ExitCode.Should().Be(0, "the stub toolchain must build");
#pragma warning restore AARC003
        return Path.Combine(outDir, "stub-toolchain.exe");
    }

    [SkippableFact]
    public void Build_WindowsFalse_IsRefusedWithADiagnostic()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n");

        ((Action)(() => FirmwareDiagnosticDistribution.Build(request, windows: false)))
            .Should().Throw<NotSupportedException>().WithMessage("*windows: false*");
#pragma warning disable AARC003 // Justified: this line calls Directory.Exists (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.Exists(output).Should().BeFalse("a refused build creates nothing");
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_OutputPathOverTheStagingLimit_IsRefusedWithTheLimitNamed()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // The staging name adds about 42 characters to the output path; this output path is over the limit with the staging name.
        using var dir = new TempDirectory();
        var output = dir.Combine(new string('m', 210));
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n");

        ((Action)(() => FirmwareDiagnosticDistribution.Build(request, windows: true)))
            .Should().Throw<IOException>().WithMessage("*247-character limit*", "the refusal names the limit and what to do");
        Directory.Exists(output).Should().BeFalse();
#pragma warning disable AARC003 // Justified: this line calls Directory.GetFileSystemEntries (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.GetFileSystemEntries(dir.FullPath).Select(Path.GetFileName).Should().BeEquivalentTo(["openbios.bin", "game.bin", "aot"],
#pragma warning restore AARC003
            "nothing is created before the limit is checked");
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("{\"schema\": 7, \"path\": \"x\"}")]
    [InlineData("null")]
    [InlineData("{\"schema\": \"psxrecomp.diagnostic-disc.v1\", \"path\": 5}")]
    public void ResolveRuntimeDiscPath_MalformedMarker_IsRefusedAsInvalidData(string markerText)
    {
        using var dir = new TempDirectory();
#pragma warning disable AARC003 // Justified: this line calls Directory.CreateDirectory (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        var runtime = Directory.CreateDirectory(dir.Combine("runtime")).FullName;
#pragma warning restore AARC003
#pragma warning disable AARC003 // Justified: this line calls File.WriteAllText (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        File.WriteAllText(Path.Combine(runtime, "diagnostic-disc.json"), markerText);
#pragma warning restore AARC003

        var act = () => FirmwareDiagnosticDistribution.ResolveRuntimeDiscPath(runtime, "disc/game.chd");

        act.Should().Throw<InvalidDataException>("a malformed marker is refused with the same type as the other marker checks");
    }

    [SkippableFact]
    public void Build_ToolchainNamesOverTheLimit_AreRefusedBeforeTheToolchainRuns()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // The staging root is 210 characters: the writes before the toolchain are under the limit, but the toolchain's reserved name
        // (root plus 44 characters) is not. The refusal names the limit and comes before any toolchain process starts.
        using var dir = new TempDirectory();
        var output = dir.Combine(new string('m', 210 - dir.FullPath.Length - 43));
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n");

        ((Action)(() => FirmwareDiagnosticDistribution.Build(request, windows: true)))
            .Should().Throw<IOException>().WithMessage("*247-character limit*host-*161 characters*",
                "the refusal names the limit, the reserved name, and the output path limit that applies to a generated artifact");
#pragma warning disable AARC003 // Justified: this line calls Directory.Exists (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.Exists(output).Should().BeFalse();
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_RefusedOutputPath_CreatesNoAncestorDirectory()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        using var dir = new TempDirectory();
        var parent = dir.Combine("missing-parent");
        var output = Path.Combine(parent, new string('m', 260));
        var request = ValidRequest(dir, output, roots: "nope\n");

        ((Action)(() => FirmwareDiagnosticDistribution.Build(request, windows: true)))
            .Should().Throw<IOException>().WithMessage("*247-character limit*");
#pragma warning disable AARC003 // Justified: this line calls Directory.Exists (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.Exists(parent).Should().BeFalse("the limit is checked before any ancestor directory is created");
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_HostDirectoryWithSubdirectories_CopiesTheWholeTree()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        dir.WriteFile("prebuilt/measure.exe", "artifact bytes"u8.ToArray());
        var hostDir = dir.CreateSubdirectory("host");
        dir.WriteFile("host/PSXRecomp.RuntimeHost.exe", "host bytes"u8.ToArray());
        dir.WriteFile("host/locale/strings.txt", "subdirectory content"u8.ToArray());
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n",
            artifactSource: dir.Combine("prebuilt", "measure.exe"), hostSource: hostDir);

        var build = FirmwareDiagnosticDistribution.Build(request, windows: true);

        File.ReadAllText(Path.Combine(output, "runtime", "locale", "strings.txt")).Should().Be("subdirectory content",
            "a subdirectory of the published host is copied, not silently left out");
#pragma warning disable AARC003 // Justified: this line calls File.Exists (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        File.Exists(build.HostPath).Should().BeTrue();
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void ResolveRuntimeDiscPath_LockedMarker_IsRefusedAsInvalidData()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        using var dir = new TempDirectory();
#pragma warning disable AARC003 // Justified: this line calls Directory.CreateDirectory (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        var runtime = Directory.CreateDirectory(dir.Combine("runtime")).FullName;
#pragma warning restore AARC003
        var marker = Path.Combine(runtime, "diagnostic-disc.json");
#pragma warning disable AARC003 // Justified: this line calls File.WriteAllText (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        File.WriteAllText(marker, "{}");
#pragma warning restore AARC003
        using var locked = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None);

        var act = () => FirmwareDiagnosticDistribution.ResolveRuntimeDiscPath(runtime, "disc/game.chd");

        act.Should().Throw<InvalidDataException>().WithMessage("*could not be read*");
    }

    [Fact]
    public void Run_UnreadableRom_ReturnsExitCodeOneWithADiagnostic()
    {
        // The CLI maps a refused or failed build to the same diagnostic and exit code 1 on every platform.
        using var dir = new TempDirectory();
        var disc = dir.WriteFile("game.bin", new byte[2352]);
        var error = new StringWriter();

        var exit = FirmwareDiagnosticCommand.Run(
            ["--rom", dir.Combine("missing-openbios.bin"), "--rom-sha256", NoSha, "--disc", disc, "--disc-sha256", NoSha,
             "--aot-dir", dir.Combine("aot"), "--out", dir.Combine("out")],
            TextWriter.Null, error);

        exit.Should().Be(1);
        error.ToString().Should().Contain("psxrecomp firmware-diagnostic:");
    }

    [SkippableFact]
    public void Build_LinkInsideAHostSubdirectory_IsRefusedAndNothingIsCopiedThroughIt()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // A junction sits inside a subdirectory of the host, and its target holds a file that must not be copied.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        dir.WriteFile("prebuilt/measure.exe", "artifact bytes"u8.ToArray());
        var hostDir = dir.CreateSubdirectory("host");
        dir.WriteFile("host/PSXRecomp.RuntimeHost.exe", "host bytes"u8.ToArray());
        dir.CreateSubdirectory("host/locale");
        var outside = dir.CreateSubdirectory("outside");
        dir.WriteFile("outside/secret.txt", "outside content"u8.ToArray());
        MakeJunction(dir.Combine("host", "locale", "nested"), outside);
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n",
            artifactSource: dir.Combine("prebuilt", "measure.exe"), hostSource: hostDir);

        ((Action)(() => FirmwareDiagnosticDistribution.Build(request, windows: true)))
            .Should().Throw<InvalidDataException>().WithMessage("*holds a link*nested*", "the copy refuses the link by name");
#pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (fixture read); it reads a fixture or a file the build wrote in this test's temporary directory, to assert its contents.
        File.ReadAllText(Path.Combine(outside, "secret.txt")).Should().Be("outside content");
#pragma warning restore AARC003
        Directory.GetFileSystemEntries(outside).Select(Path.GetFileName).Should().BeEquivalentTo(["secret.txt"],
            "nothing is copied through the link");
#pragma warning disable AARC003 // Justified: this line calls Directory.Exists (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.Exists(output).Should().BeFalse();
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_HostDirectoryItselfALink_IsRefused()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        dir.WriteFile("prebuilt/measure.exe", "artifact bytes"u8.ToArray());
        var realHost = dir.CreateSubdirectory("host-real");
        dir.WriteFile("host-real/PSXRecomp.RuntimeHost.exe", "host bytes"u8.ToArray());
        var linkedHost = dir.Combine("host-link");
        MakeJunction(linkedHost, realHost);
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n",
            artifactSource: dir.Combine("prebuilt", "measure.exe"), hostSource: linkedHost);

        ((Action)(() => FirmwareDiagnosticDistribution.Build(request, windows: true)))
            .Should().Throw<InvalidDataException>().WithMessage("*host directory is a link*", "a junction at the host directory is refused");
#pragma warning disable AARC003 // Justified: this line calls Directory.Exists (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.Exists(output).Should().BeFalse();
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_HostLinkIsRefusedBeforeAnyWrite()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // The link sits in the host, and the build also has an artifact to copy. Every observation point records its phase: a refusal
        // before the first write means no staging, file, directory or publish phase is ever reached.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        dir.WriteFile("prebuilt/measure.exe", "artifact bytes"u8.ToArray());
        var hostDir = dir.CreateSubdirectory("host");
        dir.WriteFile("host/PSXRecomp.RuntimeHost.exe", "host bytes"u8.ToArray());
        var outside = dir.CreateSubdirectory("outside");
        MakeJunction(dir.Combine("host", "nested"), outside);
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n",
            artifactSource: dir.Combine("prebuilt", "measure.exe"), hostSource: hostDir);
        var phases = new List<string>();

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, (phase, path) => phases.Add(phase));

        act.Should().Throw<InvalidDataException>().WithMessage("*holds a link*nested*");
        phases.Should().NotContain(phase => phase == "staged" || phase == "file" || phase == "directory" || phase == "provision",
            "the link is refused before the staging directory or any file is written");
        Directory.Exists(output).Should().BeFalse();
#pragma warning disable AARC003 // Justified: this line calls Directory.GetDirectories (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.GetDirectories(dir.FullPath, ".measure.staging-*").Should().BeEmpty(
#pragma warning restore AARC003
            "no staging directory exists on disk after the refusal");
    }

    [SkippableFact]
    public void Build_HostWithoutHardLinks_IsPublished()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // Positive: a host whose files each have one link is copied and published as before.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        dir.WriteFile("prebuilt/measure.exe", "artifact bytes"u8.ToArray());
        var hostDir = dir.CreateSubdirectory("host");
        dir.WriteFile("host/PSXRecomp.RuntimeHost.exe", "host bytes"u8.ToArray());
        dir.WriteFile("host/locale/strings.txt", "subdirectory content"u8.ToArray());
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n",
            artifactSource: dir.Combine("prebuilt", "measure.exe"), hostSource: hostDir);

        var build = FirmwareDiagnosticDistribution.Build(request, windows: true);

#pragma warning disable AARC003 // Justified: this line calls Directory.Exists (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.Exists(output).Should().BeTrue("a host with no hard links is published");
#pragma warning restore AARC003
#pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (fixture read); it reads a fixture or a file the build wrote in this test's temporary directory, to assert its contents.
        File.ReadAllText(Path.Combine(output, "runtime", "locale", "strings.txt")).Should().Be("subdirectory content");
#pragma warning restore AARC003
        FirmwareDiagnosticDistribution.HardLinkCount(build.HostPath).Should().Be(1, "the published host file is a plain copy");
    }

    [SkippableFact]
    public void Build_HostFileHardLinkedOutsideTheHost_IsRefusedWithNoOutput()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // Negative: a file in the host directory is a second directory entry for a file outside it. The copy must refuse it, publish nothing,
        // and not copy the external bytes.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        dir.WriteFile("prebuilt/measure.exe", "artifact bytes"u8.ToArray());
        var hostDir = dir.CreateSubdirectory("host");
        dir.WriteFile("host/PSXRecomp.RuntimeHost.exe", "host bytes"u8.ToArray());
        var external = dir.WriteFile("outside/secret.bin", "external secret"u8.ToArray());
        FirmwareDiagnosticDistribution.LinkDiscInPlace(external, dir.Combine("host", "data.bin"));
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n",
            artifactSource: dir.Combine("prebuilt", "measure.exe"), hostSource: hostDir);

        ((Action)(() => FirmwareDiagnosticDistribution.Build(request, windows: true)))
            .Should().Throw<InvalidDataException>().WithMessage("*data.bin has 2 hard links*Guarantee*",
                "the copy refuses a host file with a second link, and states the guarantee");
        Directory.Exists(output).Should().BeFalse("a refused host publishes nothing and creates no output directory");
#pragma warning disable AARC003 // Justified: this line calls Directory.GetDirectories (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.GetDirectories(dir.FullPath, ".measure.staging-*").Should().BeEmpty("no staging directory is left on disk");
#pragma warning restore AARC003
#pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (fixture read); it reads a fixture or a file the build wrote in this test's temporary directory, to assert its contents.
        File.ReadAllText(external).Should().Be("external secret", "the external target is not modified");
#pragma warning restore AARC003
        FirmwareDiagnosticDistribution.HardLinkCount(external).Should().Be(2, "the refusal leaves the link as it was");
    }

    [SkippableFact]
    public void Build_HostFileWhoseLinkCountCannotBeRead_IsRefusedFailClosed()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // A host file held open without sharing cannot be opened to read its link count. The copy fails closed.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        dir.WriteFile("prebuilt/measure.exe", "artifact bytes"u8.ToArray());
        var hostDir = dir.CreateSubdirectory("host");
        var hostFile = dir.WriteFile("host/PSXRecomp.RuntimeHost.exe", "host bytes"u8.ToArray());
        using var held = new FileStream(hostFile, FileMode.Open, FileAccess.Read, FileShare.None);
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n",
            artifactSource: dir.Combine("prebuilt", "measure.exe"), hostSource: hostDir);

        ((Action)(() => FirmwareDiagnosticDistribution.Build(request, windows: true)))
            .Should().Throw<InvalidDataException>().WithMessage("*could not be read*Guarantee*",
                "an unreadable link count is refused, never silently allowed");
#pragma warning disable AARC003 // Justified: this line calls Directory.Exists (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.Exists(output).Should().BeFalse("a refused host publishes nothing");
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_ConflictingOutputDirectory_IsNotDestroyedByARefusal()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // The output path already exists and holds evidence. The build is refused for that conflict, and the evidence stays.
        using var dir = new TempDirectory();
        var output = dir.CreateSubdirectory("measure");
        dir.WriteFile("measure/keep.txt", "previous evidence"u8.ToArray());
        dir.WriteFile("prebuilt/measure.exe", "artifact bytes"u8.ToArray());
        var hostDir = dir.CreateSubdirectory("host");
        dir.WriteFile("host/PSXRecomp.RuntimeHost.exe", "host bytes"u8.ToArray());
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n",
            artifactSource: dir.Combine("prebuilt", "measure.exe"), hostSource: hostDir);

        ((Action)(() => FirmwareDiagnosticDistribution.Build(request, windows: true)))
            .Should().Throw<IOException>().WithMessage("*already exists*");
#pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (fixture read); it reads a fixture or a file the build wrote in this test's temporary directory, to assert its contents.
        File.ReadAllText(Path.Combine(output, "keep.txt")).Should().Be("previous evidence");
#pragma warning restore AARC003
#pragma warning disable AARC003 // Justified: this line calls Directory.GetFileSystemEntries (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.GetFileSystemEntries(output).Select(Path.GetFileName).Should().BeEquivalentTo(["keep.txt"],
#pragma warning restore AARC003
            "the refused build leaves the existing output exactly as it was");
    }

    [SkippableFact]
    public void Build_Success_ManifestAndExecutableReferencesAreConsistent()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var artifact = dir.WriteFile("prebuilt/measure.exe", "artifact bytes"u8.ToArray());
        var hostDir = dir.CreateSubdirectory("host");
        dir.WriteFile("host/PSXRecomp.RuntimeHost.exe", "host bytes"u8.ToArray());
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n", artifactSource: artifact, hostSource: hostDir);

        var build = FirmwareDiagnosticDistribution.Build(request, windows: true);

        build.Root.Should().Be(output);
        build.ArtifactPath.Should().Be(Path.Combine(output, "measure.exe"));
        build.HostPath.Should().Be(Path.Combine(output, "runtime", "PSXRecomp.RuntimeHost.exe"));
        build.ManifestPath.Should().Be(Path.Combine(output, "runtime", RuntimeHostLayout.ManifestFileName));
        File.Exists(build.HostPath).Should().BeTrue();

        var manifest = RuntimeHostManifestJson.Parse(File.ReadAllText(build.ManifestPath));
        var referenced = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(build.ManifestPath)!, manifest.Artifact));
        referenced.Should().Be(Path.GetFullPath(build.ArtifactPath), "the manifest names the artifact that was published");
        File.ReadAllText(referenced).Should().Be("artifact bytes");

#pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (fixture read); it reads a fixture or a file the build wrote in this test's temporary directory, to assert its contents.
        var identity = JsonNode.Parse(File.ReadAllText(Path.Combine(output, FirmwareDiagnosticDistribution.IdentityFileName)))!.AsObject();
#pragma warning restore AARC003
        identity["artifact"]!["file"]!.GetValue<string>().Should().Be("measure.exe");
        identity["artifact"]!["sha256"]!.GetValue<string>().Should().Be(OpenBiosRomInput.Sha256HexOfFile(build.ArtifactPath));
        identity["host"]!["file"]!.GetValue<string>().Should().Be("PSXRecomp.RuntimeHost.exe");
#pragma warning disable AARC003 // Justified: this line calls Directory.GetDirectories (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.GetDirectories(dir.FullPath, ".measure.staging-*").Should().BeEmpty("a committed build leaves no staging directory");
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_OutputCreatedBeforeTheCommit_IsNotReplaced()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        dir.WriteFile("prebuilt/measure.exe", "artifact bytes"u8.ToArray());
        var hostDir = dir.CreateSubdirectory("host");
        dir.WriteFile("host/PSXRecomp.RuntimeHost.exe", "host bytes"u8.ToArray());
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n", artifactSource: dir.Combine("prebuilt", "measure.exe"), hostSource: hostDir);
        string? staged = null;

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, (phase, path) =>
        {
            if (phase == "staged")
            {
                staged = path;
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "foreign.txt"), "someone else's directory");
            }
        });

        act.Should().Throw<IOException>("the output name is taken when the build commits");
        staged.Should().NotBeNull("the build staged its output before the commit");
#pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (fixture read); it reads a fixture or a file the build wrote in this test's temporary directory, to assert its contents.
        File.ReadAllText(Path.Combine(output, "foreign.txt")).Should().Be("someone else's directory");
#pragma warning restore AARC003
        Directory.GetFileSystemEntries(output).Select(Path.GetFileName).Should().BeEquivalentTo(["foreign.txt"]);
#pragma warning disable AARC003 // Justified: this line calls Directory.Exists (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.Exists(staged!).Should().BeFalse("the staging directory the build created is removed");
#pragma warning restore AARC003
    }

    /// <summary>Valid inputs up to the AOT plan. The roots decide whether the build fails there or completes.</summary>
    private static FirmwareDiagnosticRequest ValidRequest(
        TempDirectory dir, string outputDirectory, string roots, string? artifactSource = null, string? hostSource = null)
    {
        var rom = dir.WriteFile("openbios.bin", new byte[OpenBiosFirmware.ImageSize]);
        var disc = dir.WriteFile("game.bin", new byte[2352]);
        dir.WriteFile("aot/rom.roots", System.Text.Encoding.ASCII.GetBytes(roots));
        dir.WriteFile("aot/images.txt", System.Text.Encoding.ASCII.GetBytes("# none\n"));
        return new FirmwareDiagnosticRequest(
            RomPath: rom,
            RomSha256: OpenBiosRomInput.Sha256HexOfFile(rom),
            DiscPath: disc,
            DiscSha256: OpenBiosRomInput.Sha256HexOfFile(disc),
            AotDirectory: dir.Combine("aot"),
            OutputDirectory: outputDirectory,
            CodeBytes: 64,
            InstructionBudget: uint.MaxValue,
            RunTimeoutMs: 1000,
            HostSourceDirectory: hostSource,
            ArtifactSourcePath: artifactSource);
    }

    private static void MakeJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("mklink did not start.");
        process.WaitForExit();
#pragma warning disable AARC003 // Justified: this line calls Process.ExitCode (instance) (process launch); it starts a process this test launches to create a fixture (cmd.exe mklink /J, a junction), which is not a compiler launch and not a temporary-file call.
        process.ExitCode.Should().Be(0, "the junction is needed for the redirection test");
#pragma warning restore AARC003
    }

    private static FirmwareDiagnosticRequest Request(TempDirectory dir, string romPath, string romSha256, string outputDirectory) =>
        new(
            RomPath: romPath,
            RomSha256: romSha256,
            DiscPath: dir.WriteFile("disc/game.chd", new byte[16]),
            DiscSha256: NoSha,
            AotDirectory: dir.Combine("aot"),
            OutputDirectory: outputDirectory,
            CodeBytes: 64,
            InstructionBudget: uint.MaxValue,
            RunTimeoutMs: 1000);
}

