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
