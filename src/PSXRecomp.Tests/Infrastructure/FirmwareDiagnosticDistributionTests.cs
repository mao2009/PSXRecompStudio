using System.Diagnostics;
using System.Text.Json.Nodes;
using FluentAssertions;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

#pragma warning disable AARC003 // The test asserts on local files it stages itself, as TempDirectory does.

/// <summary>
/// Issue #846: the developer-only measurement distribution references the local ROM, disc and AOT inputs in place. These
/// tests pin the refusals that must hold before anything is generated, and that the disc is linked, never copied.
/// </summary>
[Test]
public sealed class FirmwareDiagnosticDistributionTests
{
    private static readonly string NoSha = new('0', 64);

    [Fact]
    public void Build_RomDigestMismatch_RefusesBeforeAnyOutputIsCreated()
    {
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("openbios.bin", new byte[OpenBiosFirmware.ImageSize]);
        var output = dir.Combine("out", "measure");

        var act = () => FirmwareDiagnosticDistribution.Build(
            Request(dir, romPath: rom, romSha256: NoSha, outputDirectory: output), windows: true);

        act.Should().Throw<InvalidDataException>().WithMessage("*does not match*");
        Directory.Exists(output).Should().BeFalse("a refused input must leave no output behind");
    }

    [Fact]
    public void Build_ExistingOutputDirectory_IsRefusedAndLeftAsIs()
    {
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("openbios.bin", new byte[OpenBiosFirmware.ImageSize]);
        var output = dir.Combine("out", "measure");
        dir.WriteFile("out/measure/keep.txt", "previous evidence"u8.ToArray());

        var act = () => FirmwareDiagnosticDistribution.Build(
            Request(dir, romPath: rom, romSha256: NoSha, outputDirectory: output), windows: true);

        act.Should().Throw<IOException>().WithMessage("*already exists*");
        File.ReadAllText(Path.Combine(output, "keep.txt")).Should().Be("previous evidence");
    }

    [Fact]
    public void LinkDiscInPlace_ProducesAHardLinkNotACopy_AndLeavesTheOriginalUnchanged()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDirectory();
        var original = dir.WriteFile("disc/game.chd", new byte[4096]);
        var before = OpenBiosRomInput.Sha256HexOfFile(original);
        var linked = dir.Combine("runtime", "disc", "game.chd");
        Directory.CreateDirectory(Path.GetDirectoryName(linked)!);

        FirmwareDiagnosticDistribution.LinkDiscInPlace(original, linked);

        File.Exists(linked).Should().BeTrue();
        new FileInfo(linked).LinkTarget.Should().BeNull("a hard link is an entry of the same file, not a symbolic link");
        FirmwareDiagnosticDistribution.HardLinkCount(linked).Should().Be(2, "the link shares the original's file (no copy)");
        OpenBiosRomInput.Sha256HexOfFile(original).Should().Be(before);
    }

    [Fact]
    public void Measure_HostPastTheBound_IsStoppedAndTheStopIsRecorded()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A copy of cmd.exe stands in for the host: with its stdin pipe open it waits, so it is still running at the bound.
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("openbios.bin", new byte[OpenBiosFirmware.ImageSize]);
        var disc = dir.WriteFile("disc/game.chd", new byte[16]);
        dir.WriteFile("aot/rom.roots", Array.Empty<byte>());
        dir.WriteFile("aot/images.txt", Array.Empty<byte>());
        var aot = dir.Combine("aot");
        var outDir = dir.Combine("out", "measure");
        Directory.CreateDirectory(outDir);
        var runtime = Directory.CreateDirectory(dir.Combine("out", "measure", "runtime")).FullName;
        var host = Path.Combine(runtime, "PSXRecomp.RuntimeHost.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), host);
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
        var pid = stop["processId"]!.GetValue<int>();
        pid.Should().BeGreaterThan(0);
        var gone = () => Process.GetProcessById(pid);
        gone.Should().Throw<ArgumentException>("the stopped host must no longer be running");
    }

    [Fact]
    public void ResolveRuntimeDiscPath_WithoutTheMarker_IsTheProductPath()
    {
        // Default off: no marker means the product's runtime/disc path, unchanged.
        using var dir = new TempDirectory();
        var runtime = Directory.CreateDirectory(dir.Combine("runtime")).FullName;

        FirmwareDiagnosticDistribution.ResolveRuntimeDiscPath(runtime, "disc/game.chd")
            .Should().Be(Path.Combine(runtime, "disc/game.chd"));
    }

    [Fact]
    public void ResolveRuntimeDiscPath_WithTheMarker_NamesTheOriginalDiscAndNothingIsCreatedInRuntime()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDirectory();
        var original = dir.WriteFile("source/game.chd", new byte[4096]);
        var runtime = Directory.CreateDirectory(dir.Combine("runtime", "disc")).FullName;
        var before = OpenBiosRomInput.Sha256HexOfFile(original);
        File.WriteAllText(Path.Combine(dir.Combine("runtime"), "diagnostic-disc.json"),
            "{\"schema\":\"psxrecomp.diagnostic-disc.v1\",\"path\":\"" + original.Replace("\\", "\\\\") + "\",\"sha256\":\"" + before + "\",\"access\":\"read-only\"}");

        var resolved = FirmwareDiagnosticDistribution.ResolveRuntimeDiscPath(dir.Combine("runtime"), "disc/game.chd");

        resolved.Should().Be(Path.GetFullPath(original));
        Directory.EnumerateFileSystemEntries(runtime).Should().BeEmpty("the disc is referenced, never linked or copied into runtime/");
        OpenBiosRomInput.Sha256HexOfFile(original).Should().Be(before);
        new FileInfo(original).LinkTarget.Should().BeNull();
    }

    [Fact]
    public void ResolveRuntimeDiscPath_MarkerWithAnUnknownSchema_IsRefused()
    {
        using var dir = new TempDirectory();
        var runtime = Directory.CreateDirectory(dir.Combine("runtime")).FullName;
        var disc = dir.WriteFile("source/game.chd", new byte[16]);
        File.WriteAllText(Path.Combine(runtime, "diagnostic-disc.json"),
            "{\"schema\":\"other\",\"path\":\"" + disc.Replace("\\", "\\\\") + "\"}");

        var act = () => FirmwareDiagnosticDistribution.ResolveRuntimeDiscPath(runtime, "disc/game.chd");

        act.Should().Throw<InvalidDataException>().WithMessage("*unknown schema*");
    }

    [Fact]
    public void DiscOpener_ReadOnlyFile_OpensWithoutWriteAccessAndLeavesTheBytesUnchanged()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

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
            File.SetAttributes(disc, FileAttributes.Normal);
        }
    }

    [Fact]
    public void RequireInputsUnchanged_AnyDigestChange_InvalidatesTheMeasurement()
    {
        var before = new Dictionary<string, string> { ["rom"] = "aa", ["disc"] = "bb" };
        var after = new Dictionary<string, string> { ["rom"] = "aa", ["disc"] = "cc" };

        FirmwareDiagnosticDistribution.RequireInputsUnchanged(before, before);
        var act = () => FirmwareDiagnosticDistribution.RequireInputsUnchanged(before, after);

        act.Should().Throw<InvalidDataException>().WithMessage("*disc*changed*");
    }

    [Fact]
    public void Build_DirectoryCreatedAfterTheExistenceCheck_IsPreservedAndNeverBuiltInto()
    {
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
        File.ReadAllText(Path.Combine(output, "foreign.txt")).Should().Be(foreign, "a directory this build did not create is never deleted");
        Directory.GetFileSystemEntries(output).Select(Path.GetFileName).Should().BeEquivalentTo(["foreign.txt"],
            "the build writes nothing into a directory it did not create");
    }

    [Fact]
    public void Build_FailureWithAForeignFileInStaging_DeletesOnlyItsOwnOutput()
    {
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
        File.ReadAllText(Path.Combine(staged!, "foreign.txt")).Should().Be("someone else's file",
            "content another process placed in the staging area is not deleted");
        Directory.Exists(Path.Combine(staged!, "runtime")).Should().BeFalse("the build's own runtime directory is removed");
        Directory.Exists(output).Should().BeFalse();
    }

    [Fact]
    public void Build_FailureMidBuild_RemovesOnlyItsOwnTemporaryOutput()
    {
        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = ValidRequest(dir, output, roots: "nope\n");

        ((Action)(() => FirmwareDiagnosticDistribution.Build(request, windows: true))).Should().Throw<Exception>();

        Directory.Exists(output).Should().BeFalse();
        Directory.GetDirectories(dir.FullPath, "." + Path.GetFileName(output) + ".staging-*").Should().BeEmpty(
            "the staging directory the build created is removed");
        Directory.GetFileSystemEntries(dir.FullPath).Select(Path.GetFileName).Should().BeEquivalentTo(
            ["openbios.bin", "game.bin", "aot"], "only the inputs the caller named remain");
    }

    [Fact]
    public void Build_OutputPathThatIsAnExistingJunction_IsRefusedAndTheTargetIsNeverWritten()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDirectory();
        var target = dir.CreateSubdirectory("elsewhere");
        File.WriteAllText(Path.Combine(target, "keep.txt"), "x");
        var output = dir.Combine("measure");
        MakeJunction(output, target);
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n");

        ((Action)(() => FirmwareDiagnosticDistribution.Build(request, windows: true))).Should().Throw<IOException>();

        Directory.GetFileSystemEntries(target).Select(Path.GetFileName).Should().BeEquivalentTo(["keep.txt"],
            "a junction at the output path is never followed");
    }

    [Fact]
    public void Build_DanglingJunctionAtTheOutputPath_IsNotFollowed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDirectory();
        var target = dir.CreateSubdirectory("gone");
        var output = dir.Combine("measure");
        MakeJunction(output, target);
        Directory.Delete(target);
        var request = ValidRequest(dir, output, roots: "nope\n");

        // The refusal of a link at the output name is the guard that must fire, not a later failure.
        ((Action)(() => FirmwareDiagnosticDistribution.Build(request, windows: true))).Should().Throw<IOException>()
            .WithMessage("*already exists*");

        Directory.Exists(target).Should().BeFalse("the build never creates the directory a dangling junction names");
    }

    [Fact]
    public void Build_ForeignFileWrittenDuringAStep_SurvivesTheFailureCleanup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

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

        act.Should().Throw<InvalidDataException>("the host directory holds no runtime host");
        runtime.Should().NotBeNull("the host step ran");
        File.ReadAllText(Path.Combine(runtime!, "foreign.txt")).Should().Be("written by another process during the step",
            "content another process writes during a step is never deleted by the failure cleanup");
        Directory.GetFileSystemEntries(runtime!).Select(Path.GetFileName).Should().BeEquivalentTo(["foreign.txt"],
            "the build's own entries under runtime/ are removed");
        Directory.GetFileSystemEntries(Path.GetDirectoryName(runtime!)!).Select(Path.GetFileName).Should().BeEquivalentTo(["runtime"],
            "the claimed artifact copy is removed; the staging directory stays because it still holds foreign content");
        Directory.Exists(output).Should().BeFalse();
    }

    [Fact]
    public void Build_StagingDirectoryReplacedByAJunction_IsNeitherWrittenNorDeleted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDirectory();
        var output = dir.Combine("measure");
        var request = ValidRequest(dir, output, roots: "0xBFC00000\n");
        var elsewhere = dir.CreateSubdirectory("elsewhere");
        File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "x");

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
        Directory.Exists(output).Should().BeFalse();
    }

    [Fact]
    public void Build_Success_ManifestAndExecutableReferencesAreConsistent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

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

        var identity = JsonNode.Parse(File.ReadAllText(Path.Combine(output, FirmwareDiagnosticDistribution.IdentityFileName)))!.AsObject();
        identity["artifact"]!["file"]!.GetValue<string>().Should().Be("measure.exe");
        identity["artifact"]!["sha256"]!.GetValue<string>().Should().Be(OpenBiosRomInput.Sha256HexOfFile(build.ArtifactPath));
        identity["host"]!["file"]!.GetValue<string>().Should().Be("PSXRecomp.RuntimeHost.exe");
        Directory.GetDirectories(dir.FullPath, ".measure.staging-*").Should().BeEmpty("a committed build leaves no staging directory");
    }

    [Fact]
    public void Build_OutputCreatedBeforeTheCommit_IsNotReplaced()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

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
        File.ReadAllText(Path.Combine(output, "foreign.txt")).Should().Be("someone else's directory");
        Directory.GetFileSystemEntries(output).Select(Path.GetFileName).Should().BeEquivalentTo(["foreign.txt"]);
        Directory.Exists(staged!).Should().BeFalse("the staging directory the build created is removed");
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
        process.ExitCode.Should().Be(0, "the junction is needed for the redirection test");
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

#pragma warning restore AARC003
