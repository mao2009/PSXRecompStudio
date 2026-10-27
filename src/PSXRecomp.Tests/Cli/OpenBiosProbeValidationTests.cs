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
    public void Run_BoundaryLogWithEngineGeneratedHost_Rejected_Without_Creating_The_Log()
    {
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("openbios.bin", SyntheticRom());
        var roots = dir.WriteFile("roots.txt", []);
        var boundaryLog = GetTempPath(dir, "boundary.jsonl");

        var exit = RunProbe([
            rom, "--engine", "generated-host", "--roots", roots,
            "--boundary-log", boundaryLog, "--json"
        ], out _, out var error);

        exit.Should().Be(1);
        error.Should().Contain("--boundary-log requires --engine interpreter");
        new FileInfo(boundaryLog).Exists.Should().BeFalse("the refusal happens before the log is opened");
    }

    [Theory]
    [InlineData("--dump-ram")]
    [InlineData("--dump-frame")]
    [InlineData("--pc-histogram")]
    public void Run_ListDiscWithInterpreterOutput_Rejected(string outputOption)
    {
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("openbios.bin", SyntheticRom());
        var args = new List<string>
        {
            rom, "--engine", "interpreter",
            "--disc", GetTempPath(dir, "unused.chd"),
            "--list-disc", GetTempPath(dir, "list.json"),
            outputOption, GetTempPath(dir, "output.bin")
        };
        if (outputOption == "--dump-ram")
        {
            args.Add("--capture-at");
            args.Add("80010000");
        }

        var exit = RunProbe(args.ToArray(), out _, out var error);

        exit.Should().Be(1);
        error.Should().Contain("--list-disc cannot be combined");
    }

    // PR #839 review (MAJOR): --capture-at at a PC in the BIOS ROM range must not export the BIOS word. The reset vector is
    // the first fetch, and the first synthetic ROM word (lui t0, 0x8001 = 0x3C088001) must not appear in the probe output.
    [Fact]
    public void Run_CaptureAtRomRange_DoesNotExportTheBiosWord()
    {
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("openbios.bin", SyntheticRom());

        RunProbe([
            rom, "--engine", "interpreter",
            "--capture-at", "BFC00000", "--segments", "1", "--segment-budget", "2000", "--json"
        ], out var output, out _);

        output.ToLowerInvariant().Should().NotContain("3c088001", "the BIOS word at a ROM-range capture PC must not be exported");
        var captured = JsonNode.Parse(output)!["captured"]!;
        captured["pc"]!.ToString().Should().Be("0xBFC00000");
        captured["word"].Should().BeNull("a PC outside guest RAM has no RAM word to export");
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