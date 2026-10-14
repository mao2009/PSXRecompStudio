using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Cli;

/// <summary>
/// Tests for OpenBiosProbeCommand option validation (Issue #772 m1).
/// </summary>
[Test]
public sealed class OpenBiosProbeValidationTests
{
    private const uint RomBase = OpenBiosFirmware.ResetVector;

    private static uint I(uint opcode, uint rs, uint rt, uint imm) => opcode << 26 | rs << 21 | rt << 16 | (imm & 0xFFFFu);

    private static byte[] SyntheticRom()
    {
        uint[] words =
        [
            I(0x0F, 0, 8, 0x8001),
            I(0x0D, 0, 9, 0x1234),
            I(0x2B, 8, 9, 0x100),
            0,
            0x08000000u | ((RomBase + 0x10u & 0x0FFFFFFFu) >> 2),
            0,
        ];
        var bytes = new byte[OpenBiosFirmware.ImageSize];
        for (var i = 0; i < words.Length; i++) BitConverter.GetBytes(words[i]).CopyTo(bytes, i * 4);
        return bytes;
    }

    private static int RunProbe(string[] args, out string output, out string error)
    {
        var outputWriter = new StringWriter();
        var errorWriter = new StringWriter();
        var exit = OpenBiosProbeCommand.Run(args, outputWriter, errorWriter);
        output = outputWriter.ToString();
        error = errorWriter.ToString();
        return exit;
    }

    private static string GetTempPath(TempDirectory dir, string name) => dir.Combine(name);

    // ===== m1: OpenBIOS Probe option validation (Issue #772) =====

    [Fact]
    public void Run_EngineGeneratedHostWithDumpRam_Rejected()
    {
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("openbios.bin", SyntheticRom());
        var roots = dir.WriteFile("roots.txt", []);
        var dumpRam = GetTempPath(dir, "ram.bin");

        var exit = RunProbe([
            rom, "--engine", "generated-host", "--roots", roots,
            "--dump-ram", dumpRam, "--json"
        ], out _, out var error);

        exit.Should().Be(1);
        error.Should().Contain("--dump-ram is not supported with --engine generated-host");
    }

    [Fact]
    public void Run_EngineGeneratedHostWithDumpFrame_Rejected()
    {
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("openbios.bin", SyntheticRom());
        var roots = dir.WriteFile("roots.txt", []);
        var dumpFrame = GetTempPath(dir, "frame.ppm");

        var exit = RunProbe([
            rom, "--engine", "generated-host", "--roots", roots,
            "--dump-frame", dumpFrame, "--json"
        ], out _, out var error);

        exit.Should().Be(1);
        error.Should().Contain("--dump-frame is not supported with --engine generated-host");
    }

    [Fact]
    public void Run_EngineGeneratedHostWithPcHistogram_Rejected()
    {
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("openbios.bin", SyntheticRom());
        var roots = dir.WriteFile("roots.txt", []);
        var histogram = GetTempPath(dir, "hist.json");

        var exit = RunProbe([
            rom, "--engine", "generated-host", "--roots", roots,
            "--pc-histogram", histogram, "--json"
        ], out _, out var error);

        exit.Should().Be(1);
        error.Should().Contain("--pc-histogram is not supported with --engine generated-host");
    }

    [Fact]
    public void Run_DumpRamWithoutCaptureAt_Rejected()
    {
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("openbios.bin", SyntheticRom());
        var dumpRam = GetTempPath(dir, "ram.bin");

        var exit = RunProbe([
            rom, "--engine", "interpreter",
            "--dump-ram", dumpRam, "--json"
        ], out _, out var error);

        exit.Should().Be(1);
        error.Should().Contain("--dump-ram requires --capture-at");
    }

    [Fact]
    public void Run_ListDiscWithoutDisc_Rejected()
    {
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("openbios.bin", SyntheticRom());
        var listDisc = GetTempPath(dir, "list.json");

        var exit = RunProbe([
            rom, "--engine", "interpreter",
            "--list-disc", listDisc, "--json"
        ], out _, out var error);

        exit.Should().Be(1);
        error.Should().Contain("--list-disc requires --disc");
    }
}