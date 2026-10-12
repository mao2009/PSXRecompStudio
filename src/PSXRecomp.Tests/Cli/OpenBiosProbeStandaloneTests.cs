using System.Text.Json.Nodes;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.Cli;

/// <summary>
/// Issue #739 Phase 2 Step B (BIOS handoff): the <c>openbios-probe --engine generated-host</c> surface runs a synthetic
/// OpenBIOS-shaped firmware through the standalone embedded-image artifact with <c>--standalone</c>. The same ROM with
/// and without it must reach the same guest state and the same native/fallback accounting: standalone delivery changes
/// only how the ROM reaches the artifact, never what it computes. The probe reports <c>standalone: true</c> so no reader
/// can mistake which path produced a number.
/// </summary>
[Test]
public sealed class OpenBiosProbeStandaloneTests
{
    private const uint RomBase = OpenBiosFirmware.ResetVector;

    private static uint I(uint opcode, uint rs, uint rt, uint imm) => opcode << 26 | rs << 21 | rt << 16 | (imm & 0xFFFFu);

    /// <summary>Writes a word to RAM 0x10100 and then spins at 0xBFC00010, bounded by the probe's segment budget.</summary>
    private static byte[] SyntheticRom()
    {
        uint[] words =
        [
            I(0x0F, 0, 8, 0x8001),                          // lui  t0, 0x8001
            I(0x0D, 0, 9, 0x1234),                          // ori  t1, zero, 0x1234
            I(0x2B, 8, 9, 0x100),                           // sw   t1, 0x100(t0)
            0,                                              // nop
            0x08000000u | ((RomBase + 0x10u & 0x0FFFFFFFu) >> 2), // spin: j 0xBFC00010
            0,
        ];
        var bytes = new byte[OpenBiosFirmware.ImageSize];
        for (var i = 0; i < words.Length; i++) BitConverter.GetBytes(words[i]).CopyTo(bytes, i * 4);
        return bytes;
    }

    private static JsonNode RunProbe(TempDirectory dir, bool standalone)
    {
        var rom = dir.WriteFile("openbios.bin", SyntheticRom());
        var roots = dir.WriteFile("roots.txt", []);
        string[] args =
        [
            rom, "--engine", "generated-host", "--roots", roots, "--code-bytes", "64",
            "--segments", "1", "--segment-budget", "2000", "--json",
        ];
        if (standalone) args = [.. args, "--standalone"];
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = OpenBiosProbeCommand.Run(args, output, error);
        exit.Should().Be(2, "the kernel does not boot: anything less than a verified boot fails closed, and nothing is a title boot. stderr: " + error);
        return JsonNode.Parse(output.ToString())!;
    }

    [Fact]
    public void Standalone_RunsTheFirmwareAndReportsTheStandalonePath_WithoutChangingTheResult()
    {
        using var dir = new TempDirectory();
        var standalone = RunProbe(dir, standalone: true);
        var fileDriven = RunProbe(dir, standalone: false);

        standalone["standalone"]!.GetValue<bool>().Should().BeTrue();
        fileDriven["standalone"]!.GetValue<bool>().Should().BeFalse();
        standalone["engine"]!.ToString().Should().Be("generated-host");

        var native = standalone["execution"]!["nativeInstructions"]!.GetValue<ulong>();
        native.Should().BeGreaterThan(0ul, "the reset-vector block runs natively, never as fallback");
        (standalone["execution"]!["fallbackInstructions"]?.GetValue<ulong>() ?? 0ul).Should().Be(0ul,
            "this ROM runs entirely from its compiled block; the standalone path adds no fallback");

        // Standalone delivery must be behaviourally identical: same guest state and same accounting.
        standalone["guestPc"]!.ToString().Should().Be(fileDriven["guestPc"]!.ToString());
        standalone["executionState"]!.ToString().Should().Be(fileDriven["executionState"]!.ToString());
        native.Should().Be(fileDriven["execution"]!["nativeInstructions"]!.GetValue<ulong>());
        standalone["build"]!["imageInstructions"]!.GetValue<int>()
            .Should().Be(fileDriven["build"]!["imageInstructions"]!.GetValue<int>());

        // The embedded artifact's gcc build happens inside RunSegment, so it must be attributed back to codegen/gcc:
        // codegenMs stays non-negative and the run does not absorb compilation. A regression here showed codegenMs < 0.
        var timings = standalone["timings"]!;
        timings["gccMs"]!.GetValue<double>().Should().BeGreaterThan(0.0, "the embedded artifact is compiled with gcc and that time is measured");
        timings["codegenMs"]!.GetValue<double>().Should().BeGreaterThanOrEqualTo(0.0,
            "compilation time must not be netted out of codegen, which would drive codegenMs negative");
    }
}
