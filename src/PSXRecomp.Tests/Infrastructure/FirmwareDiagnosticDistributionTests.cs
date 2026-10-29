using System.Diagnostics;
using System.Text.Json.Nodes;
using FluentAssertions;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Infrastructure.Standalone;
using System.ComponentModel;
using System.Text.RegularExpressions;
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
#pragma warning disable AARC003 // Justified: this line calls File.Copy (temp file) and Environment.SystemDirectory (system path read); it creates, checks, moves or removes an entry in this test's own temporary directory.
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
        using var bystander = new TempDirectory();
        var target = bystander.FullPath;
#pragma warning disable AARC003 // Justified: this line calls File.WriteAllText (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        File.WriteAllText(Path.Combine(target, "keep.txt"), "x");
#pragma warning restore AARC003
        var output = dir.Combine("measure");
        using var junction = JunctionFixture.Create(dir, output, target);
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
        using var junction = JunctionFixture.Create(dir, output, target);
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

        JunctionFixture.SkipUnlessAvailable(dir);
        using var links = new JunctionFixtureSet();
        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, (phase, path) =>
        {
            if (phase == "staged")
            {
                // A same-user process swaps the empty staging directory for a junction to a directory it controls.
                Directory.Delete(path);
                links.Add(JunctionFixture.Create(dir, path, elsewhere));
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
        JunctionFixture.SkipUnlessAvailable(dir);
        using var links = new JunctionFixtureSet();

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, (phase, path) =>
        {
            var name = Path.GetFileName(path);
            if (phase == "file" && !swapped && (name == "rom.roots" || name == "images.txt"))
            {
                swapped = true;
                var firmware = Path.GetDirectoryName(path)!;
                Directory.Move(firmware, firmware + "-aside");
                links.Add(JunctionFixture.Create(dir, firmware, foreign));
            }
        });

        act.Should().Throw<IOException>().WithMessage("*A link was found*",
            "the write is refused because the firmware directory is now a link (the chain check), not for another reason");
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
        using var junction = JunctionFixture.Create(dir, dir.Combine("host", "locale", "nested"), outside);
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
        using var junction = JunctionFixture.Create(dir, linkedHost, realHost);
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
        using var junction = JunctionFixture.Create(dir, dir.Combine("host", "nested"), outside);
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
    public void Junction_DisposeRemovesOnlyTheLink()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        using var dir = new TempDirectory();
        var target = dir.CreateSubdirectory("target");
        dir.WriteFile("target/keep.txt", "target content"u8.ToArray());
        var link = dir.Combine("link");
        var fixture = JunctionFixture.Create(dir, link, target);

        fixture.Dispose();

        Path.Exists(link).Should().BeFalse("the link is removed");
#pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (fixture read); it reads a fixture or a file the build wrote in this test's temporary directory, to assert its contents.
        File.ReadAllText(Path.Combine(target, "keep.txt")).Should().Be("target content", "the link's target is never removed with it");
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void Junction_DisposeOfTheTestDirectoryWithALeftLink_DoesNotDeleteThroughTheLink()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // A junction into a directory outside the test-owned one is left in place, and the owned directory is then disposed.
        using var bystander = new TempDirectory();
        bystander.WriteFile("keep.txt", "outside the owned directory"u8.ToArray());
        var dir = new TempDirectory();
        JunctionFixture.SkipUnlessAvailable(dir);
        using var leftLink = JunctionFixture.Create(dir, dir.Combine("link"), bystander.FullPath);

        dir.Dispose();

#pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (fixture read); it reads a fixture or a file the build wrote in this test's temporary directory, to assert its contents.
        File.ReadAllText(bystander.Combine("keep.txt")).Should().Be("outside the owned directory",
#pragma warning restore AARC003
            "disposing the owned directory never recursively deletes through a junction into another directory");
    }

    [SkippableFact]
    public void Junction_PathWithADoubleQuote_IsRefusedWithAReasonBeforeAnyLaunch()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // Windows file names cannot hold a double quote, so the path can only be refused, never created. The refusal returns a reason.
        using var dir = new TempDirectory();
        var target = dir.CreateSubdirectory("target");
        var quotedLink = dir.Combine("a\"b");

        var fixture = JunctionFixture.TryCreate(dir, quotedLink, target, out var reason);

        fixture.Should().BeNull("a path that cannot be quoted is refused");
        reason.Should().Contain("cannot be quoted for cmd.exe", "the reason says why no launch was made");
    }

    [SkippableFact]
    public void Junction_PathWithParentheses_CreatesAJunction()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // A real profile path such as "John (Work)" must not be refused: the path is quoted, and the parentheses are inside the quotes.
        using var dir = new TempDirectory();
        var target = dir.CreateSubdirectory("target (work)");
        dir.WriteFile("target (work)/keep.txt", "through the link"u8.ToArray());
        using var fixture = JunctionFixture.Create(dir, dir.Combine("link (work) & it's+#1"), target);

#pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (fixture read); it reads a fixture or a file the build wrote in this test's temporary directory, to assert its contents.
        File.ReadAllText(Path.Combine(fixture.Link, "keep.txt")).Should().Be("through the link", "the junction resolves to its target");
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void Junction_PathWithNonAsciiCharacters_CreatesAJunction()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // A Japanese profile name must not be refused either.
        using var dir = new TempDirectory();
        var target = dir.CreateSubdirectory("ターゲット");
        dir.WriteFile("ターゲット/keep.txt", "through the link"u8.ToArray());
        using var fixture = JunctionFixture.Create(dir, dir.Combine("リンク"), target);

#pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (fixture read); it reads a fixture or a file the build wrote in this test's temporary directory, to assert its contents.
        File.ReadAllText(Path.Combine(fixture.Link, "keep.txt")).Should().Be("through the link", "the junction resolves to its target");
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void Junction_PathWithAmpersandBangParenthesesAndJapanese_CreatesAJunction()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // The characters & ( ) ! are passed literally inside the double quotes of the mklink command line, and delayed expansion is off (/v:off),
        // so none of them is read as syntax. The path is valid and must create a junction that resolves to its target.
        using var dir = new TempDirectory();
        var target = dir.CreateSubdirectory("対象 (作業) & !x");
        dir.WriteFile("対象 (作業) & !x/keep.txt", "through the link"u8.ToArray());
        var link = dir.Combine("リンク (作業) & !y");

        var launches = 0;
        using var fixture = JunctionFixture.TryCreate(dir, link, target, out var reason, launching: () => launches++);
        launches.Should().Be(1, "a valid path reaches the launch exactly once, so the seam is wired to the real launch");
        // A refusal of a valid path fails the test. A machine that cannot create a junction at all skips the test with its reason.
        (fixture is null && reason is not null && reason.Contains("so no launch is made")).Should().BeFalse(
            $"a valid path with these characters is accepted: {reason}");
        Skip.If(fixture is null, reason ?? "the junction could not be created");

#pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (fixture read); it reads a file this test wrote in its own temporary directory, through the junction it created.
        File.ReadAllText(Path.Combine(fixture!.Link, "keep.txt")).Should().Be("through the link", "the junction resolves to its target");
#pragma warning restore AARC003
    }

    [SkippableTheory]
    [InlineData("inj%COMSPEC%")]
    [InlineData("inj^echo pwned")]
    [InlineData("inj\" & echo pwned > marker & \"")]
    public void Junction_CommandInjectionThroughTheName_IsRefusedWithAReasonAndNothingIsLaunched(string name)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // Each name would end the quoted argument or expand a variable if it reached cmd.exe. The fixture refuses it before any launch, so
        // no process runs and no marker file appears in the test's own directory.
        using var dir = new TempDirectory();
        var target = dir.CreateSubdirectory("target");

        var launches = 0;
        var fixture = JunctionFixture.TryCreate(dir, dir.Combine(name), target, out var reason, launching: () => launches++);

        fixture.Should().BeNull("a name that cmd.exe would read as syntax is refused");
        reason.Should().Contain("so no launch is made", "the refusal is made before any process is started, not by the command that would have run");
        launches.Should().Be(0, "the launch seam counts zero process launches for a refused name");
#pragma warning disable AARC003 // Justified: this line calls File.Exists (fixture check); it reads the test's own temporary directory to show the refused command wrote no marker file.
        File.Exists(dir.Combine("marker")).Should().BeFalse("no command ran, so no marker file was written");
#pragma warning restore AARC003
    }

    [SkippableTheory]
    [InlineData("link\\", "target")]
    [InlineData("link", "target\\")]
    [InlineData("link/", "target")]
    public void Junction_PathEndingInASeparator_IsRefusedBeforeAnyLaunch(string linkName, string targetName)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // A trailing backslash before the closing quote changes how the argument is read (the quote is escaped). The path is refused with a
        // reason and the launch seam counts no launch.
        using var dir = new TempDirectory();
        dir.CreateSubdirectory("target");
        var launches = 0;

        var fixture = JunctionFixture.TryCreate(dir, dir.Combine(linkName), dir.Combine(targetName), out var reason, launching: () => launches++);

        fixture.Should().BeNull("a path that ends in a separator is refused");
        reason.Should().Contain("so no launch is made");
        launches.Should().Be(0, "no process is started for a refused path");
    }

    [SkippableFact]
    public void Junction_CreationFailures_ReturnAReasonAndNeverThrow()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // A missing target and an existing link are creation failures: each returns no fixture and a reason, and throws nothing.
        using var dir = new TempDirectory();
        var missingTarget = dir.Combine("no-such-target");

        var missing = JunctionFixture.TryCreate(dir, dir.Combine("link-a"), missingTarget, out var missingReason);
        missing.Should().BeNull();
        missingReason.Should().Contain("does not exist");

        var existing = dir.CreateSubdirectory("existing-link");
        var target = dir.CreateSubdirectory("target-b");
        var clash = JunctionFixture.TryCreate(dir, existing, target, out var clashReason);
        clash.Should().BeNull();
        clashReason.Should().Contain("already exists");
    }

    [SkippableFact]
    public void Junction_LinkOutsideTheOwnedDirectory_IsRefused()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        using var dir = new TempDirectory();
        using var bystander = new TempDirectory();
        var target = dir.CreateSubdirectory("target");
        var outsideLink = bystander.Combine("link");

        ((Action)(() => JunctionFixture.TryCreate(dir, outsideLink, target, out _)))
            .Should().Throw<InvalidOperationException>().WithMessage("*inside the test-owned directory*");
        Path.Exists(outsideLink).Should().BeFalse("no link is created outside the test-owned directory");
    }

    [SkippableFact]
    public void Build_HardLinkCreatedAfterThePrecheck_IsRefusedBeforeTheCopyAndPublishesNothing()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // Deterministic seam: the observation point "provision" runs after the host pre-check and before the host copy begins. At that point this
        // test creates, inside the host directory, a hard link to a file outside it. The pre-check cannot see the link; the copy must read the count.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        dir.WriteFile("prebuilt/measure.exe", "artifact bytes"u8.ToArray());
        var hostDir = dir.CreateSubdirectory("host");
        dir.WriteFile("host/PSXRecomp.RuntimeHost.exe", "host bytes"u8.ToArray());
        var external = dir.WriteFile("outside/secret.bin", "external secret"u8.ToArray());
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n",
            artifactSource: dir.Combine("prebuilt", "measure.exe"), hostSource: hostDir);
        var linkCreated = false;

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, (phase, path) =>
        {
            if (phase == "provision" && !linkCreated)
            {
                linkCreated = true;
                FirmwareDiagnosticDistribution.LinkDiscInPlace(external, dir.Combine("host", "late.bin"));
            }
        });

        act.Should().Throw<InvalidDataException>().WithMessage("*late.bin has 2 hard links*Guarantee*",
            "the copy reads the link count again and refuses the file that gained a second link");
        linkCreated.Should().BeTrue("the seam ran between the pre-check and the copy");
        Directory.Exists(output).Should().BeFalse("a refused copy publishes nothing and creates no output directory");
#pragma warning disable AARC003 // Justified: this line calls Directory.GetDirectories (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        Directory.GetDirectories(dir.FullPath, ".measure.staging-*").Should().BeEmpty("no staging directory is left on disk");
#pragma warning restore AARC003
#pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (fixture read); it reads a fixture or a file the build wrote in this test's temporary directory, to assert its contents.
        File.ReadAllText(external).Should().Be("external secret", "the external target is not modified");
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_SecondHardLinkAddedAtTheFileSeam_IsRefusedFromTheHandleAndPublishesNothing()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // The file seam for the host file "late.bin" runs after the path-based pre-check and before the copy opens the file. At that point this
        // test adds a second link to the host file, outside the host directory. The copy must count the links on the handle it reads from.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        dir.WriteFile("prebuilt/measure.exe", "artifact bytes"u8.ToArray());
        var hostDir = dir.CreateSubdirectory("host");
        dir.WriteFile("host/PSXRecomp.RuntimeHost.exe", "host bytes"u8.ToArray());
        var hostLate = dir.WriteFile("host/late.bin", "host content"u8.ToArray());
        var outside = dir.CreateSubdirectory("outside");
        var alias = dir.Combine("outside", "alias.bin");
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n",
            artifactSource: dir.Combine("prebuilt", "measure.exe"), hostSource: hostDir);
        var linked = false;

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, (phase, path) =>
        {
            if (phase == "file" && !linked && Path.GetFileName(path) == "late.bin")
            {
                linked = true;
                FirmwareDiagnosticDistribution.LinkDiscInPlace(hostLate, alias);
            }
        });

        act.Should().Throw<InvalidDataException>().WithMessage("*late.bin has 2 hard links*Guarantee*",
            "the copy counts the links on the handle it reads and refuses the file that gained a second link");
        linked.Should().BeTrue("the seam ran between the pre-check and the open");
        Directory.Exists(output).Should().BeFalse("a refused copy creates no output directory");
        Directory.GetDirectories(dir.FullPath, ".measure.staging-*").Should().BeEmpty("no staging directory is left on disk");
        File.ReadAllText(hostLate).Should().Be("host content", "the host file is not modified");
#pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (test fixture read); it reads a file this test created inside its own temporary directory and does not change it.
        File.ReadAllText(alias).Should().Be("host content", "the external target is not modified");
#pragma warning restore AARC003
#pragma warning disable AARC003 // Justified: this line calls Directory.Exists (test fixture check); it reads a directory this test created inside its own temporary directory and does not change it.
        Directory.Exists(outside).Should().BeTrue("the external directory is left as it was");
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_OwnedEntriesReplacedBeforeCleanup_AreLeftInPlaceAsResiduals()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // Cleanup removes an entry this build created only while it is still that entry (its identity is compared with the one read at
        // creation). The seam, placed where the copy of a subdirectory's file begins, replaces the build's own file and its own subdirectory
        // with other content, as another process could, and then fails the step. Cleanup leaves each replacement and reports it.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        dir.WriteFile("prebuilt/measure.exe", "artifact bytes"u8.ToArray());
        var hostDir = dir.CreateSubdirectory("host");
        dir.WriteFile("host/PSXRecomp.RuntimeHost.exe", "host bytes"u8.ToArray());
        dir.WriteFile("host/a.bin", "host content a"u8.ToArray());
        dir.WriteFile("host/sub/inner.bin", "host content inner"u8.ToArray());
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n",
            artifactSource: dir.Combine("prebuilt", "measure.exe"), hostSource: hostDir);
        string? runtime = null;
        var replaced = false;

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, (phase, path) =>
        {
            if (phase != "file" || replaced || Path.GetFileName(path) != "inner.bin")
            {
                return;
            }

            replaced = true;
            var subDirectory = Path.GetDirectoryName(path)!; // runtime/sub: created by this build, still empty here
            runtime = Path.GetDirectoryName(subDirectory)!;
            var ownFile = Path.Combine(runtime, "a.bin");
            File.Delete(ownFile);
            File.WriteAllText(ownFile, "foreign a");
            Directory.Delete(subDirectory);
            Directory.CreateDirectory(subDirectory);
            File.WriteAllText(Path.Combine(subDirectory, "foreign.txt"), "foreign sub");
            throw new InvalidOperationException("the step fails after the replacement");
        });

        var thrown = act.Should().Throw<IOException>().Which;
        thrown.Message.Should().Contain("a.bin: it is not the entry this build created",
            "the replaced file is reported, not removed");
        thrown.Message.Should().Contain("sub: it is not the entry this build created",
            "the replaced subdirectory is reported, not removed");
        replaced.Should().BeTrue("the seam ran");
        File.ReadAllText(Path.Combine(runtime!, "a.bin")).Should().Be("foreign a", "another process's file is never removed by cleanup");
#pragma warning disable AARC003 // Justified: these lines call File.ReadAllText and Directory.Exists (fixture reads of the entries the seam placed in this test's temporary directory).
        File.ReadAllText(Path.Combine(runtime!, "sub", "foreign.txt")).Should().Be("foreign sub",
            "another process's directory is never removed by cleanup");
        Directory.Exists(output).Should().BeFalse("the failed build publishes nothing");
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void ReleaseEmptyDirectory_SwappedBeforeTheDelete_IsNotDeletedAndIsReported()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // The publish directory is empty when cleanup reaches it. Another process swaps it for an empty directory of its own. The identity check
        // runs immediately before the delete: the foreign directory is not this build's, so it is kept and reported.
        using var dir = new TempDirectory();
        var area = new FirmwareDiagnosticDistribution.StagingArea(dir.Combine("measure"), null);
        area.Open("measure");
        var publish = area.CreateDirectory(Path.Combine(area.Root, "publish"));
        Directory.Delete(publish);
        Directory.CreateDirectory(publish);

        var act = () => area.ReleaseEmptyDirectory(publish);

        act.Should().Throw<IOException>("a directory this build did not create is not deleted");
        area.Residuals.Should().ContainSingle(residual => residual.Contains("it is not the directory this build created"),
            "the kept directory is reported as a residual");
#pragma warning disable AARC003 // Justified: this line calls Directory.Exists (fixture check); it reads the test's own temporary directory, to show the foreign directory survived.
        Directory.Exists(publish).Should().BeTrue("the foreign directory is not removed");
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void MoveNew_SourceIsALink_IsRefusedBeforeTheMove()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // The file move takes the same check: a link at the source is refused and nothing is moved.
        using var dir = new TempDirectory();
        var area = new FirmwareDiagnosticDistribution.StagingArea(dir.Combine("measure"), null);
        area.Open("measure");
        var publish = area.CreateDirectory(Path.Combine(area.Root, "publish"));
        using var bystander = new TempDirectory();
        var source = Path.Combine(publish, "linked-entry");
        using var link = JunctionFixture.Create(dir, source, bystander.FullPath);
        var destination = Path.Combine(area.Root, "runtime-entry");

        var act = () => area.MoveNew(source, destination);

        act.Should().Throw<IOException>().WithMessage("*A link was found*", "the link is refused before the move");
#pragma warning disable AARC003 // Justified: this line calls Directory.Exists (fixture check); it reads the test's own temporary directory to show nothing was moved.
        Directory.Exists(destination).Should().BeFalse("nothing is moved to the destination");
#pragma warning restore AARC003
    }

    [SkippableFact]
    public void MoveNew_SourceNotOwned_IsRefusedAndNeverAdopted()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // A file in the publish directory that this build did not create or adopt is never moved, and the destination is never adopted.
        using var dir = new TempDirectory();
        var area = new FirmwareDiagnosticDistribution.StagingArea(dir.Combine("measure"), null);
        area.Open("measure");
        var publish = area.CreateDirectory(Path.Combine(area.Root, "publish"));
        var source = Path.Combine(publish, "stranger.txt");
        File.WriteAllText(source, "not ours");
        var destination = Path.Combine(area.Root, "stranger.txt");

        var act = () => area.MoveNew(source, destination);

        act.Should().Throw<IOException>().WithMessage("*did not create*", "a source that is not owned is refused");
        File.Exists(source).Should().BeTrue("the source is not moved");
#pragma warning disable AARC003 // Justified: this line calls File.Exists (fixture check); it reads the test's own temporary directory to show the destination was not created.
        File.Exists(destination).Should().BeFalse();
#pragma warning restore AARC003
        area.Residuals.Should().ContainSingle(residual => residual.Contains("not created or adopted"), "the refusal is reported as a residual");
    }

    // The publish step is replaced by a stub that writes the files a publish would write, so the publish path runs without a toolchain.
    private static Action<string> PublishStub(params string[] names) => directory =>
    {
        foreach (var name in names)
        {
            #pragma warning disable AARC003 // Justified: this line calls File.WriteAllText (test fixture I/O); it writes only the test's own temporary directory, to assert the outcome of the build.
            File.WriteAllText(Path.Combine(directory, name), "published " + name);
            #pragma warning restore AARC003
        }
    };

    private static Action<string> AllowListPublish() => PublishStub(FirmwareDiagnosticDistribution.RuntimePublishAllowList(true));

    private static FirmwareDiagnosticRequest PublishRequest(TempDirectory dir, string output)
    {
        dir.WriteFile("prebuilt/measure.exe", "artifact bytes"u8.ToArray());
        return ValidRequest(dir, output, roots: "0xBFC00000\n", artifactSource: dir.Combine("prebuilt", "measure.exe"));
    }

    [SkippableFact]
    public void Build_UnownedFileInsideStaging_IsNotPublishedOrDeleted()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // Another process places a file in the staging area after it is created. The commit refuses the staging area, so the file is never
        // published, and cleanup does not delete it.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = PublishRequest(dir, output);
        const string foreign = "placed by another process in the staging area";

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, observe: (phase, path) =>
        {
            if (phase == "provision")
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "foreign.txt"), foreign);
            }
        }, compiler: null, publisher: AllowListPublish());

        act.Should().Throw<IOException>().WithMessage("*foreign.txt*", "the unowned entry is named, and nothing is published");
        Directory.Exists(output).Should().BeFalse("the staging area holding an unowned entry is never published");
        #pragma warning disable AARC003 // Justified: this line calls Directory.GetDirectories (test fixture I/O); it lists only the test's own temporary directory, to assert the outcome of the build.
        var staging = Directory.GetDirectories(dir.FullPath, ".measure.staging-*").Single();
        #pragma warning restore AARC003
        #pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (test fixture I/O); it reads only the test's own temporary directory, to assert the outcome of the build.
        File.ReadAllText(Path.Combine(staging, "foreign.txt")).Should().Be(foreign, "an unowned file is never deleted");
        #pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_UnownedFileInThePublishDestination_IsNotOverwritten()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // A file another process placed at a name the publish would copy into runtime/ is never overwritten: the copy creates a new file.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = PublishRequest(dir, output);
        const string foreign = "someone else's core library";
        string? runtime = null;

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, observe: (phase, path) =>
        {
            if (phase == "provision")
            {
                runtime = path;
                File.WriteAllText(Path.Combine(path, "PSXRecomp.Core.dll"), foreign);
            }
        }, compiler: null, publisher: AllowListPublish());

        act.Should().Throw<IOException>();
        #pragma warning disable AARC003 // Justified: this line calls Directory.Exists (test fixture I/O); it checks only the test's own temporary directory, to assert the outcome of the build.
        Directory.Exists(output).Should().BeFalse("nothing is published");
        #pragma warning restore AARC003
        #pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (test fixture I/O); it reads only the test's own temporary directory, to assert the outcome of the build.
        File.ReadAllText(Path.Combine(runtime!, "PSXRecomp.Core.dll")).Should().Be(foreign, "the file another process placed is never overwritten");
        #pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_PublishLinkNamedAsAListedFile_IsRefusedAndTheTargetIsNeverRead()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // A junction in the publish output, named as a listed file, points at a directory outside the staging area. It is refused before any
        // copy, and the file behind it is neither copied nor changed.
        using var dir = new TempDirectory();
        using var bystander = new TempDirectory();
        bystander.WriteFile("PSXRecomp.Core.dll", "outside the staging area"u8.ToArray());
        var output = dir.Combine("measure");
        var request = PublishRequest(dir, output);
        JunctionFixture? link = null;

        try
        {
            var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, observe: null, compiler: null, publisher: publishDir =>
            {
                link = JunctionFixture.Create(dir, Path.Combine(publishDir, "PSXRecomp.Core.dll"), bystander.FullPath);
            });

            act.Should().Throw<IOException>().WithMessage("*A link was found in the publish output*");
            #pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (test fixture I/O); it reads only the test's own temporary directory, to assert the outcome of the build.
            File.ReadAllText(Path.Combine(bystander.FullPath, "PSXRecomp.Core.dll")).Should().Be("outside the staging area");
            #pragma warning restore AARC003
            #pragma warning disable AARC003 // Justified: this line calls Directory.Exists (test fixture I/O); it checks only the test's own temporary directory, to assert the outcome of the build.
            Directory.Exists(output).Should().BeFalse();
            #pragma warning restore AARC003
        }
        finally
        {
            link?.Dispose();
        }
    }

    [SkippableFact]
    public void Build_FailureDuringAWrite_RemovesOnlyTheOwnedOutput()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // The second listed file's copy fails. The files already copied, the publish output's listed files and the directories are removed,
        // because this build created them. Nothing is published.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = PublishRequest(dir, output);

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, observe: (phase, path) =>
        {
            if (phase == "file" && Path.GetFileName(path) == "PSXRecomp.Infrastructure.dll")
            {
                throw new InvalidOperationException("the write failed");
            }
        }, compiler: null, publisher: AllowListPublish());

        act.Should().Throw<InvalidOperationException>().WithMessage("the write failed");
        Directory.Exists(output).Should().BeFalse("a failed build publishes nothing");
        #pragma warning disable AARC003 // Justified: this line calls Directory.GetDirectories (test fixture I/O); it lists only the test's own temporary directory, to assert the outcome of the build.
        Directory.GetDirectories(dir.FullPath, ".measure.staging-*").Should().BeEmpty("the staging area the build created is removed");
        #pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_FailureAtTheCommit_PublishesNothing()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = PublishRequest(dir, output);

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, observe: (phase, _) =>
        {
            if (phase == "commit")
            {
                throw new InvalidOperationException("the commit failed");
            }
        }, compiler: null, publisher: AllowListPublish());

        act.Should().Throw<InvalidOperationException>().WithMessage("the commit failed");
        Directory.Exists(output).Should().BeFalse("the rename never ran, so nothing is published");
        #pragma warning disable AARC003 // Justified: this line calls Directory.GetDirectories (test fixture I/O); it lists only the test's own temporary directory, to assert the outcome of the build.
        Directory.GetDirectories(dir.FullPath, ".measure.staging-*").Should().BeEmpty("the staging area is removed");
        #pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_ExistingPublishedOutput_IsPreservedOnFailure()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // A good output appears at the output name between the last write and the commit. The commit refuses it: the earlier output is not
        // replaced, and the failed build publishes nothing.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = PublishRequest(dir, output);
        const string earlier = "earlier good output";

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, observe: (phase, _) =>
        {
            if (phase == "commit")
            {
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "previous.txt"), earlier);
            }
        }, compiler: null, publisher: AllowListPublish());

        act.Should().Throw<IOException>();
        #pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (test fixture I/O); it reads only the test's own temporary directory, to assert the outcome of the build.
        File.ReadAllText(Path.Combine(output, "previous.txt")).Should().Be(earlier, "the existing output is not replaced or changed");
        #pragma warning restore AARC003
        Directory.GetFileSystemEntries(output).Select(Path.GetFileName).Should().BeEquivalentTo(["previous.txt"]);
        #pragma warning disable AARC003 // Justified: this line calls Directory.GetDirectories (test fixture I/O); it lists only the test's own temporary directory, to assert the outcome of the build.
        Directory.GetDirectories(dir.FullPath, ".measure.staging-*").Should().BeEmpty("the failed build leaves no staging area");
        #pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_ExtraEntryNotOnTheAllowList_IsRefused()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // The publish output holds an entry that is not on the allow-list. The step is refused before anything is copied.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = PublishRequest(dir, output);
        var names = FirmwareDiagnosticDistribution.RuntimePublishAllowList(true).Append("extra.dll").ToArray();
        var copied = new List<string>();

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, observe: (phase, path) =>
        {
            if (phase == "file")
            {
                copied.Add(Path.GetFileName(path));
            }
        }, compiler: null, publisher: PublishStub(names));

        act.Should().Throw<IOException>().WithMessage("*not on the allow-list*extra.dll*");
        copied.Should().NotContain("extra.dll", "an entry that is not on the list is never copied");
        #pragma warning disable AARC003 // Justified: this line calls Directory.Exists (test fixture I/O); it checks only the test's own temporary directory, to assert the outcome of the build.
        Directory.Exists(output).Should().BeFalse();
        #pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_NormalGeneration_CopiesTheAllowListAndPublishesOnlyIt()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = PublishRequest(dir, output);

        var build = FirmwareDiagnosticDistribution.Build(request, windows: true, observe: null, compiler: null, publisher: AllowListPublish());

        Directory.Exists(output).Should().BeTrue("the completed measurement is published");
        foreach (var name in FirmwareDiagnosticDistribution.RuntimePublishAllowList(true))
        {
            File.ReadAllText(Path.Combine(output, "runtime", name)).Should().Be("published " + name, "each listed file is copied with its bytes");
        }

        #pragma warning disable AARC003 // Justified: this line calls Directory.Exists (test fixture I/O); it checks only the test's own temporary directory, to assert the outcome of the build.
        Directory.Exists(Path.Combine(output, "runtime", "publish")).Should().BeFalse("the publish directory is removed once it is emptied");
        #pragma warning restore AARC003
        #pragma warning disable AARC003 // Justified: this line calls File.Exists (test fixture I/O); it checks only the test's own temporary directory, to assert the outcome of the build.
        File.Exists(build.HostPath).Should().BeTrue();
        #pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_FailureAfterAnOwnedCopy_CleanupRemovesOnlyOwnedEntries()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // An unowned file is placed in the staging area, and a later copy fails. Cleanup removes the copies and the publish output files this
        // build created, and leaves the unowned file where it is.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = PublishRequest(dir, output);
        string? staging = null;
        const string foreign = "not this build's";

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, observe: (phase, path) =>
        {
            if (phase == "staged")
            {
                staging = path;
                File.WriteAllText(Path.Combine(path, "foreign.txt"), foreign);
            }

            if (phase == "file" && Path.GetFileName(path) == "PSXRecomp.Infrastructure.dll")
            {
                throw new InvalidOperationException("the write failed");
            }
        }, compiler: null, publisher: AllowListPublish());

        act.Should().Throw<IOException>().WithMessage("*was kept at*");
        Directory.Exists(output).Should().BeFalse();
        #pragma warning disable AARC003 // Justified: this line calls Directory.GetFileSystemEntries (test fixture I/O); it lists only the test's own temporary directory, to assert the outcome of the build.
        Directory.GetFileSystemEntries(staging!).Select(Path.GetFileName).Should().BeEquivalentTo(["foreign.txt"],
        #pragma warning restore AARC003
            "the owned copies and publish output files are removed, and the unowned file is kept");
        #pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (test fixture I/O); it reads only the test's own temporary directory, to assert the outcome of the build.
        File.ReadAllText(Path.Combine(staging!, "foreign.txt")).Should().Be(foreign);
        #pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_PublishedFiles_AreCopiedNotMoved()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // Regression for the move-and-adopt path: the publish output is still in place when its files are copied. A move would have removed them
        // from the publish directory first.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = PublishRequest(dir, output);
        var listed = FirmwareDiagnosticDistribution.RuntimePublishAllowList(true);
        var seenWhileCopied = false;
        var sourcesPresent = false;

        FirmwareDiagnosticDistribution.Build(request, windows: true, observe: (phase, path) =>
        {
            if (phase == "publish-copied")
            {
                seenWhileCopied = true;
                #pragma warning disable AARC003 // Justified: this line calls File.Exists (test fixture I/O); it checks only the test's own temporary directory, to assert the outcome of the build.
                sourcesPresent = listed.All(name => File.Exists(Path.Combine(path, name)));
                #pragma warning restore AARC003
            }
        }, compiler: null, publisher: AllowListPublish());

        seenWhileCopied.Should().BeTrue("the copy step reached the point after its copies");
        sourcesPresent.Should().BeTrue("each listed source is still in the publish directory when the copies are made, so nothing was moved");
    }

    [Fact]
    public void StagingArea_HasNoMoveAndAdoptPublishPath()
    {
        // Regression: the move-and-adopt publish path (adopting each publish entry and moving it into runtime/) must not return.
        typeof(FirmwareDiagnosticDistribution.StagingArea).GetMethod("AdoptPublished").Should().BeNull("publish output is copied, never adopted");
        typeof(FirmwareDiagnosticDistribution.StagingArea).GetMethod("MoveNewDirectory").Should().BeNull("a publish directory is never moved");
    }

    /// <summary>
    /// The allow-list must equal the set the project files declare for the framework-dependent host publish: the host's own files, the assembly of
    /// each project it references (transitively), each runtime package reference (not PrivateAssets=all), and the native library named by the
    /// Windows NativeLibName of PSXRecomp.Core.csproj. This test reads the project files; it does not run a publish. The real publish is checked
    /// by Build_RealRuntimeHostPublish_PublishesExactlyTheAllowList.
    /// </summary>
    [Fact]
    public void RuntimePublishAllowList_MatchesTheSetTheProjectFilesDeclareForThePublish()
    {
        var root = FindRepositoryRoot();
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            RuntimeHostLayout.HostFileName(true),
            RuntimeHostLayout.HostBinaryName + ".dll",
            RuntimeHostLayout.HostBinaryName + ".deps.json",
            RuntimeHostLayout.HostBinaryName + ".runtimeconfig.json",
        };
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string projectPath)
        {
            var full = Path.GetFullPath(projectPath);
            if (!visited.Add(full))
            {
                return;
            }

            var xml = System.Xml.Linq.XDocument.Load(full);
            var assembly = xml.Descendants("AssemblyName").Select(static e => e.Value.Trim()).FirstOrDefault()
                ?? Path.GetFileNameWithoutExtension(full);
            expected.Add(assembly + ".dll");
            foreach (var reference in xml.Descendants("ProjectReference"))
            {
                Visit(Path.Combine(Path.GetDirectoryName(full)!, reference.Attribute("Include")!.Value));
            }

            foreach (var package in xml.Descendants("PackageReference"))
            {
                if (!string.Equals(package.Attribute("PrivateAssets")?.Value, "all", StringComparison.OrdinalIgnoreCase))
                {
                    expected.Add(package.Attribute("Include")!.Value + ".dll");
                }
            }

            if (assembly == "PSXRecomp.Core")
            {
                var windowsNative = xml.Descendants("NativeLibName")
                    .First(static e => e.Attribute("Condition")?.Value.Contains("Windows_NT", StringComparison.Ordinal) == true).Value.Trim();
                expected.Add(windowsNative);
            }
        }

        Visit(Path.Combine(root, "src", "PSXRecomp.RuntimeHost", "PSXRecomp.RuntimeHost.csproj"));

        FirmwareDiagnosticDistribution.RuntimePublishAllowList(true).Should().BeEquivalentTo(expected,
            "a dependency or native library the publish produces must be on the allow-list, or the real publish is refused");
    }

    [SkippableFact]
    public void Build_RealRuntimeHostPublish_PublishesExactlyTheAllowList()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // The real framework-dependent publish of the Runtime host (no stub). The output must pass the allow-list, and the listed files are copied.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = PublishRequest(dir, output);

        var build = FirmwareDiagnosticDistribution.Build(request, windows: true, observe: null, compiler: null, publisher: null);

        var runtime = Path.Combine(output, "runtime");
        var allowList = FirmwareDiagnosticDistribution.RuntimePublishAllowList(true);
        foreach (var name in allowList)
        {
            File.Exists(Path.Combine(runtime, name)).Should().BeTrue($"the real publish produced {name}, which is copied into runtime/");
        }

        var topLevelPublished = Directory.GetFiles(runtime)
            .Select(Path.GetFileName)
            .Where(static name => name!.EndsWith(".dll", StringComparison.Ordinal) || name.EndsWith(".exe", StringComparison.Ordinal)
                || name.EndsWith(".deps.json", StringComparison.Ordinal) || name.EndsWith(".runtimeconfig.json", StringComparison.Ordinal))
            .ToArray();
        topLevelPublished.Should().BeEquivalentTo(allowList, "the runtime holds exactly the published files the allow-list names");
        #pragma warning disable AARC003 // Justified: this line calls Directory.Exists (test fixture check); it reads only the test's own temporary directory, to assert the outcome of the build.
        Directory.Exists(Path.Combine(runtime, "publish")).Should().BeFalse();
        #pragma warning restore AARC003
        #pragma warning disable AARC003 // Justified: this line calls File.Exists (test fixture check); it reads only the test's own temporary directory, to assert the outcome of the build.
        File.Exists(build.HostPath).Should().BeTrue();
        #pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_PublishOutputWithASecondHardLink_IsRefusedBeforeAnyCopy()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // A publish entry that has a second hard link is refused before any copy: its bytes could come from another name.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = PublishRequest(dir, output);
        var listed = FirmwareDiagnosticDistribution.RuntimePublishAllowList(true).Where(static name => name != "SharpCompress.dll").ToArray();

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, observe: null, compiler: null, publisher: publishDir =>
        {
            PublishStub(listed)(publishDir);
            FirmwareDiagnosticDistribution.LinkDiscInPlace(Path.Combine(publishDir, "PSXRecomp.Core.dll"), Path.Combine(publishDir, "SharpCompress.dll"));
        });

        act.Should().Throw<InvalidDataException>().WithMessage("*hard link*", "a publish entry with a second link is not copied");
        #pragma warning disable AARC003 // Justified: this line calls Directory.Exists (test fixture check); it reads only the test's own temporary directory, to assert the outcome of the build.
        Directory.Exists(output).Should().BeFalse("nothing is published");
        #pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_FailingPublish_RemovesOnlyTheOwnedPublishOutput()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // The publish writes the allow-listed files and then fails. Cleanup removes those files and the publish directory, because this run created
        // them, and nothing is published.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = PublishRequest(dir, output);
        var listed = FirmwareDiagnosticDistribution.RuntimePublishAllowList(true);

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, observe: null, compiler: null, publisher: publishDir =>
        {
            PublishStub(listed)(publishDir);
            throw new InvalidOperationException("the publish failed");
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("the publish failed");
        Directory.Exists(output).Should().BeFalse("a failed publish publishes nothing");
        #pragma warning disable AARC003 // Justified: this line calls Directory.GetDirectories (test fixture check); it reads only the test's own temporary directory, to assert the outcome of the build.
        Directory.GetDirectories(dir.FullPath, ".measure.staging-*").Should().BeEmpty("the owned publish output is removed with the staging area");
        #pragma warning restore AARC003
    }

    [SkippableFact]
    public void Build_ExtraPublishEntry_RemovesTheOwnedFilesAndKeepsTheUnlistedEntry()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // The publish output holds an entry that is not on the list. The allow-listed files are this run's and are removed; the unlisted entry
        // is not this run's and is kept.
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = PublishRequest(dir, output);
        var listed = FirmwareDiagnosticDistribution.RuntimePublishAllowList(true).Append("extra.dll").ToArray();

        var act = () => FirmwareDiagnosticDistribution.Build(request, windows: true, observe: null, compiler: null, publisher: PublishStub(listed));

        act.Should().Throw<IOException>().WithMessage("*not on the allow-list*extra.dll*");
        var staging = Directory.GetDirectories(dir.FullPath, ".measure.staging-*").Single();
        Directory.GetFileSystemEntries(Path.Combine(staging, "publish")).Select(Path.GetFileName).Should().BeEquivalentTo(["extra.dll"],
            "the allow-listed files are removed as owned; the unlisted entry is kept");
        #pragma warning disable AARC003 // Justified: this line calls Directory.Exists (test fixture check); it reads only the test's own temporary directory, to assert the outcome of the build.
        Directory.Exists(output).Should().BeFalse();
        #pragma warning restore AARC003
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        #pragma warning disable AARC003 // Justified: this line calls File.Exists (test fixture check); it reads only the test's own temporary directory, to assert the outcome of the build.
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "PSXRecomp.RuntimeHost", "PSXRecomp.RuntimeHost.csproj")))
        #pragma warning restore AARC003
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("the repository root was not found from the test output directory");
    }

    [SkippableFact]
    public void Measure_WaitLoopThrows_TheLaunchedHostIsKilled()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // A copy of cmd.exe stands in for the host and waits on its stdin. The test makes the guarded wait fail right after the launch. The
        // launched host must then be killed and awaited, not left running.
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("openbios.bin", new byte[OpenBiosFirmware.ImageSize]);
        var disc = dir.WriteFile("disc/game.chd", new byte[16]);
        dir.WriteFile("aot/rom.roots", Array.Empty<byte>());
        dir.WriteFile("aot/images.txt", Array.Empty<byte>());
        var aot = dir.Combine("aot");
        var outDir = dir.Combine("out", "measure");
        var runtime = dir.CreateSubdirectory(Path.Combine("out", "measure", "runtime"));
        var host = Path.Combine(runtime, "PSXRecomp.RuntimeHost.exe");
#pragma warning disable AARC003 // Justified: this line calls File.Copy and Environment.SystemDirectory (test fixture copy); it copies the system cmd.exe into this test's own temporary directory as the stand-in host, and the copy is never run outside the test.
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
        bool? stillRunningBeforeRelease = null;

        // The measurement reports, after its finally block and before it releases the host's Process object, whether the host still runs.
        var act = () => FirmwareDiagnosticDistribution.Measure(build, request, TimeSpan.FromMilliseconds(500),
            afterLaunch: _ => throw new InvalidOperationException("the guarded wait failed"),
            beforeRelease: running => stillRunningBeforeRelease = running);

        act.Should().Throw<InvalidOperationException>().WithMessage("the guarded wait failed");
        stillRunningBeforeRelease.Should().NotBeNull("the release point was reached after the wait failed");
        stillRunningBeforeRelease.Should().BeFalse(
            "the launched host was killed and awaited in the finally block, before its Process object was released, so it is not left running");
    }

    [SkippableFact]
    public void Junction_LinkNameWithAPipe_IsRefusedWithAReason()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // A pipe is a character a Windows path cannot hold. The fixture returns a reason; it does not throw.
        using var dir = new TempDirectory();
        var target = dir.CreateSubdirectory("target");

        var fixture = JunctionFixture.TryCreate(dir, dir.Combine("a|b"), target, out var reason);

        fixture.Should().BeNull("a name Windows cannot hold is refused");
        reason.Should().Contain("cannot hold", "the reason says why no launch was made");
    }

    [SkippableFact]
    public void Junction_InsideTheOwnedDirectoryPointingOutside_BlocksALinkOutsideIt()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), WindowsOnlyReason);

        // "out-link" lies inside the owned directory but is a junction to a directory outside it. A link created through it lies outside.
        using var dir = new TempDirectory();
        using var bystander = new TempDirectory();
        using var outLink = JunctionFixture.Create(dir, dir.Combine("out-link"), bystander.FullPath);
        var target = dir.CreateSubdirectory("target");

        ((Action)(() => JunctionFixture.TryCreate(dir, dir.Combine("out-link", "newlink"), target, out _)))
            .Should().Throw<InvalidOperationException>().WithMessage("*inside the test-owned directory*");
#pragma warning disable AARC003 // Justified: this line calls Directory.Exists (test fixture check); it reads the bystander directory this test created and changes nothing.
        Directory.Exists(Path.Combine(bystander.FullPath, "newlink")).Should().BeFalse("no link is created outside the owned directory");
#pragma warning restore AARC003
    }

    [Fact]
    public void WriteNewMeasurementFile_RefusesAnExistingFileAndKeepsIt()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteFile("measurement.json", "previous evidence"u8.ToArray());

        ((Action)(() => FirmwareDiagnosticDistribution.WriteNewMeasurementFile(path, "new measurement")))
            .Should().Throw<IOException>("a measurement file that exists is never overwritten");
#pragma warning disable AARC003 // Justified: this line calls File.ReadAllText (test fixture read); it reads the file this test created inside its own temporary directory and does not change it.
        File.ReadAllText(path).Should().Be("previous evidence");
#pragma warning restore AARC003
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

/// <summary>
/// A junction fixture for the junction-refusal regression tests. Its one process launch (cmd.exe running mklink /J) is the test-only
/// exception that the architecture matrix's rule 6 grants for a junction fixture (PR #846 owner ruling). No production code path uses it.
/// The guarantees it keeps:
/// the link is created only inside the caller's test-owned temporary directory; each path is quoted for cmd.exe, and only a path that cannot be
/// quoted safely is refused, so no path can inject a command; the only process it waits for or kills is the Process object that this
/// fixture started, never a process found by PID; a launch that outlives its bound is killed and awaited; and disposal removes only the link,
/// never the link's target.
/// Failure model: every creation failure (a refused path, a missing target, an existing link, a failed or timed-out launch, a non-zero exit,
/// an unverified result) returns no fixture with its reason, and <see cref="Create"/> turns that into a skip. The only throw is for a link
/// outside the caller's owned directory, which is a bug in the test, not an environment condition.
/// </summary>
[Test]
internal sealed class JunctionFixture : IDisposable
{
    private static readonly TimeSpan LaunchBound = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KillBound = TimeSpan.FromSeconds(10);

    // A path is quoted for cmd.exe, so the characters that cmd.exe reads as syntax inside double quotes are harmless. Four are not: a double
    // quote ends the quoting, a percent sign expands even inside quotes, a caret would need quoting of its own, and control characters or line
    // breaks split the command. Everything else (parentheses, apostrophes, ampersands, plus, hash, exclamation marks, non-ASCII) is allowed.
    // The launch passes /v:off, so exclamation marks are never expanded either. The end is anchored with \z, so a trailing newline fails.
    private static readonly Regex QuotablePath = new(@"^[^""%^\x00-\x1F\x7F]+\z", RegexOptions.CultureInvariant);

    private JunctionFixture(string link)
    {
        Link = link;
    }

    /// <summary>The junction's path.</summary>
    public string Link { get; }

    /// <summary>Creates the junction, or skips the calling test (NOT_RUN, with the reason) when this environment cannot create and verify one.</summary>
    public static JunctionFixture Create(TempDirectory owner, string link, string target)
    {
        var fixture = TryCreate(owner, link, target, out var reason);
        Skip.If(fixture is null, $"NOT_RUN: no junction fixture can be created in this environment: {reason}");
        return fixture!;
    }

    /// <summary>Skips the calling test unless a junction can be created and verified here. The probe junction is removed again.</summary>
    public static void SkipUnlessAvailable(TempDirectory owner)
    {
        var probeTarget = owner.CreateSubdirectory("junction-probe-target");
        using var probe = TryCreate(owner, owner.Combine("junction-probe-link"), probeTarget, out var reason);
        Skip.If(probe is null, $"NOT_RUN: no junction fixture can be created in this environment: {reason}");
    }

    /// <summary>
    /// Creates a junction at <paramref name="link"/> to <paramref name="target"/>. Returns null, with <paramref name="reason"/>, for every
    /// creation failure; it does not throw for one. Throws only when the link is outside the caller's owned directory (a test bug).
    /// </summary>
    public static JunctionFixture? TryCreate(TempDirectory owner, string link, string target, out string reason, Action? launching = null)
    {
        // A name that cannot be used is refused first, with a reason. It is checked before any path API that could throw on it.
        var nameRefusal = NameRefusal(link, target);
        if (nameRefusal is not null)
        {
            reason = $"{nameRefusal}: {link} / {target}";
            return null;
        }

        RequireInsideOwner(owner, link);

        var refusal = RefusalFor(link, target);
        if (refusal is not null)
        {
            reason = refusal;
            return null;
        }

        // Each path is wrapped in double quotes. RefusalFor has already refused any path that contains a double quote.
        launching?.Invoke(); // the seam counts launches: a refused name never reaches this line
        return Launch($"/d /v:off /c mklink /J \"{link}\" \"{target}\"", link, out reason);
    }

    /// <summary>Removes the link only. A junction is removed without entering its target.</summary>
    public void Dispose()
    {
        if (IsReparsePoint(Link))
        {
#pragma warning disable AARC003 // Justified: this line calls Directory.Delete (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
            Directory.Delete(Link);
#pragma warning restore AARC003
        }
    }

    private static string? RefusalFor(string link, string target)
    {
        if (!Path.IsPathFullyQualified(link) || !Path.IsPathFullyQualified(target))
        {
            return "the junction paths must be fully qualified";
        }

        if (!QuotablePath.IsMatch(link))
        {
            return $"the link path has a character that cannot be quoted for cmd.exe (a double quote, a percent sign, a caret, or a control character), so no launch is made: {link}";
        }

        if (!QuotablePath.IsMatch(target))
        {
            return $"the target path has a character that cannot be quoted for cmd.exe (a double quote, a percent sign, a caret, or a control character), so no launch is made: {target}";
        }

#pragma warning disable AARC003 // Justified: this line calls Directory.Exists (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
        if (!Directory.Exists(target))
#pragma warning restore AARC003
        {
            return $"the junction target does not exist: {target}";
        }

        if (Path.Exists(link) || IsReparsePoint(link))
        {
            return $"the junction link already exists: {link}";
        }

        return null;
    }

    private static JunctionFixture? Launch(string arguments, string link, out string reason)
    {
        Process process;
        try
        {
            process = StartMklink(arguments);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            reason = $"cmd.exe did not start: {exception.Message}";
            return null;
        }

        JunctionFixture? created = null;
        string? failure = null;
        var survived = false;
        try
        {
            if (!WaitOwned(process, LaunchBound))
            {
                failure = $"mklink did not finish within {LaunchBound.TotalSeconds} seconds; the cmd.exe process this test started is killed";
            }
            else
            {
                var exitCode = ExitCodeOf(process);
                if (exitCode != 0)
                {
                    failure = $"mklink exited with code {exitCode}";
                }
#pragma warning disable AARC003 // Justified: this line calls Directory.Exists (temp file); it creates, checks, moves or removes an entry in this test's own temporary directory.
                else if (!IsReparsePoint(link) || !Directory.Exists(link))
#pragma warning restore AARC003
                {
                    failure = "mklink exited with code 0, but the link is not a junction that resolves to a directory";
                }
                else
                {
                    created = new JunctionFixture(link);
                }
            }
        }
        finally
        {
            // Abnormal-exit cleanup. A process still running here is the one this method started: it is killed through its own object and awaited.
            // A Kill that races a natural exit may throw; the exception is recorded, and it never replaces the exception already in flight.
            if (!WaitOwned(process, TimeSpan.Zero))
            {
                try
                {
                    KillOwnedTree(process);
                }
                catch (Exception exception)
                {
                    failure ??= $"the cmd.exe process could not be killed: {exception.Message}";
                }

                if (!WaitOwned(process, KillBound))
                {
                    survived = true;
                    failure ??= "the cmd.exe process this test started did not exit after it was killed; the fixture is refused";
                }
            }

            process.Dispose();
        }

        if (created is not null && failure is null && !survived)
        {
            reason = string.Empty;
            return created;
        }

        reason = failure ?? "junction creation failed";
        return null;
    }

    private static bool EndsWithSeparator(string path) => path.EndsWith('\\') || path.EndsWith('/');

    /// <summary>The reason a name cannot be used, or null. It never throws: a name Windows cannot hold is a refusal, not an exception.</summary>
    private static string? NameRefusal(string link, string target)
    {
        if (!QuotablePath.IsMatch(link) || !QuotablePath.IsMatch(target))
        {
            return "a path has a character that cannot be quoted for cmd.exe (a double quote, a percent sign, a caret, or a control character), so no launch is made";
        }

        if (link.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || target.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return "a path has a character that a Windows path cannot hold (for example a pipe), so no launch is made";
        }

        // A trailing separator would sit before the closing quote; the Windows argument parser reads a backslash before a quote as an escaped quote.
        if (EndsWithSeparator(link) || EndsWithSeparator(target))
        {
            return "a path ends with a backslash or a slash, which would change how the closing quote is read, so no launch is made";
        }

        try
        {
            _ = Path.GetFullPath(link);
            _ = Path.GetFullPath(target);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"Windows cannot resolve a path ({exception.Message}), so no launch is made";
        }

        return null;
    }

    /// <summary>
    /// Refuses a link whose parent, after following every junction on the way, is outside the owned directory. This is the one exception the
    /// fixture throws: a link outside the owned directory is a bug in the test, not an environment condition.
    /// </summary>
    private static void RequireInsideOwner(TempDirectory owner, string path)
    {
        var root = ResolvedDirectory(owner.FullPath);
        var parent = ResolvedDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var inside = string.Equals(parent, root, StringComparison.OrdinalIgnoreCase)
            || parent.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!inside)
        {
            throw new InvalidOperationException($"A junction fixture must be created inside the test-owned directory: {path}");
        }
    }

    /// <summary>The real location of a directory: every junction or symbolic link on the way is followed to its target.</summary>
    private static string ResolvedDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        var info = new DirectoryInfo(full);
        try
        {
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            return Path.GetFullPath((target ?? info).FullName).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (IOException)
        {
            return full.TrimEnd(Path.DirectorySeparatorChar);
        }
    }

    private static bool IsReparsePoint(string path)
    {
        var attributes = (int)new DirectoryInfo(path).Attributes;
        return attributes != -1 && (attributes & (int)FileAttributes.ReparsePoint) != 0;
    }

    private static Process StartMklink(string arguments) =>
#pragma warning disable AARC003 // Justified: this line calls Process.Start (process launch); it is the one test-only process launch of the matrix rule 6 junction-fixture exception (PR #846): cmd.exe runs mklink /J inside a test-owned temporary directory, the Process object is the one this fixture started, and it is waited for, or killed, only through that object.
        Process.Start(new ProcessStartInfo("cmd.exe", arguments) { UseShellExecute = false, CreateNoWindow = true })
#pragma warning restore AARC003
            ?? throw new InvalidOperationException("cmd.exe did not start.");

#pragma warning disable AARC003 // Justified: this line calls Process.WaitForExit (instance) (process launch); it is the one test-only process launch of the matrix rule 6 junction-fixture exception (PR #846): cmd.exe runs mklink /J inside a test-owned temporary directory, the Process object is the one this fixture started, and it is waited for, or killed, only through that object.
    private static bool WaitOwned(Process process, TimeSpan bound) => process.WaitForExit((int)bound.TotalMilliseconds);
#pragma warning restore AARC003

#pragma warning disable AARC003 // Justified: this line calls Process.Kill (instance) (process launch); it is the one test-only process launch of the matrix rule 6 junction-fixture exception (PR #846): cmd.exe runs mklink /J inside a test-owned temporary directory, the Process object is the one this fixture started, and it is waited for, or killed, only through that object.
    private static void KillOwnedTree(Process process) => process.Kill(entireProcessTree: true);
#pragma warning restore AARC003

#pragma warning disable AARC003 // Justified: this line calls Process.ExitCode (instance) (process launch); it is the one test-only process launch of the matrix rule 6 junction-fixture exception (PR #846): cmd.exe runs mklink /J inside a test-owned temporary directory, the Process object is the one this fixture started, and it is waited for, or killed, only through that object.
    private static int ExitCodeOf(Process process) => process.ExitCode;
#pragma warning restore AARC003
}

/// <summary>Junction fixtures created during one test, removed in reverse order when the set is disposed.</summary>
[Test]
internal sealed class JunctionFixtureSet : IDisposable
{
    private readonly List<JunctionFixture> _links = [];

    /// <summary>Adds a fixture to the set.</summary>
    public void Add(JunctionFixture fixture) => _links.Add(fixture);

    /// <summary>Removes every fixture's link, last created first.</summary>
    public void Dispose()
    {
        for (var index = _links.Count - 1; index >= 0; index--)
        {
            _links[index].Dispose();
        }
    }
}

