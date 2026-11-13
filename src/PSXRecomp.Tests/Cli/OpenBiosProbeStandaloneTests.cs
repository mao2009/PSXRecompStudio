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

    /// <summary>
    /// Issue #774 (W03) wiring: the standalone run's VBlank observer reaches the boundary log. Without it (the observer not
    /// handed to the bridge, or not wired at the probe) the log is empty, and the comparison would see no boundaries.
    /// The budget is sized so the spin loop passes at least one VBlank (one VBlank is 564480 scheduler cycles).
    /// </summary>
    [Fact]
    public void Standalone_Run_Writes_Boundary_Records_From_Its_Own_Scheduler()
    {
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("openbios.bin", SyntheticRom());
        var roots = dir.WriteFile("roots.txt", []);
        var boundaryLog = dir.Combine("standalone-boundary.jsonl");
        var error = new StringWriter();
        string[] args =
        [
            rom, "--engine", "generated-host", "--roots", roots, "--code-bytes", "64",
            "--segments", "1", "--segment-budget", "1000000", "--standalone", "--boundary-log", boundaryLog, "--json",
        ];

        OpenBiosProbeCommand.Run(args, TextWriter.Null, error);

        error.ToString().Should().NotContain("boundary log is incomplete");
#pragma warning disable AARC003 // Test-only read of the probe's own output file; production I/O stays behind Infrastructure.
        var lines = File.ReadAllLines(boundaryLog).Where(static l => l.Trim().Length > 0).ToArray();
#pragma warning restore AARC003
        lines.Should().NotBeEmpty("the standalone scheduler reaches at least one VBlank within the budget");
        JsonNode.Parse(lines[0])!["engine"]!.GetValue<string>().Should().Be("standalone");
        JsonNode.Parse(lines[0])!["vblank"]!.GetValue<ulong>().Should().Be(1ul);
    }

    [Theory]
    [InlineData(25.0, 24000.0, 1.0)] // standalone: the embedded artifact's gcc build is excluded from the run/boundary clock
    [InlineData(25.0, 0.0, 25.0)] // file-driven: nothing to exclude, so the elapsed time is unchanged
    [InlineData(0.4, 0.0, 0.4)]
    public void RunSecondsExcludingBuild_ExcludesTheEmbeddedBuildOnly(double elapsed, double buildOffsetMs, double expected)
    {
        OpenBiosProbeCommand.RunSecondsExcludingBuild(elapsed, buildOffsetMs).Should().BeApproximately(expected, 1e-9);
    }
}
