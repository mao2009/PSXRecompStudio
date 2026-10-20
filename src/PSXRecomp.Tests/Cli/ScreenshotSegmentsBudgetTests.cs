using System.IO;
using System.Text.Json;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Infrastructure.Execution;
using PSXRecomp.Tests.E2E;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.Cli;

/// <summary>
/// PR #798 review: <c>--segments</c> is the whole-run segment budget on both screenshot paths. Without it the path
/// default applies (HLE 64, OpenBIOS 100). Measured through VBlank count: the scheduler advances one cycle per
/// retired instruction and one frame is 564,480 cycles, so a self-loop with a 100,000-instruction segment budget
/// reaches 0 frames in 3 segments and about 11 frames in 64.
/// </summary>
[Test]
#pragma warning disable AARC003 // writes the input EXE for the command under test
public sealed class ScreenshotSegmentsBudgetTests
{
    private const uint SegmentBudget = 100_000;

    private static byte[] SelfLoopExe() =>
        GeneratedPsxExeFixtures.BuildExe([GeneratedPsxExeFixtures.Jump(0x02, GeneratedPsxExeFixtures.EntryPc), 0u]);

    private static JsonElement RunHle(string dir, params string[] extra)
    {
        var exe = Path.Combine(dir, "self-loop.exe");
        File.WriteAllBytes(exe, SelfLoopExe());
        var output = Path.Combine(dir, "shots");
        var stdout = new StringWriter();
        var args = new List<string> { exe, "--output", output, "--json", "--segment-budget", SegmentBudget.ToString(), "--timeout", "600" };
        args.AddRange(extra);
        ScreenshotCommand.Run(args.ToArray(), stdout, TextWriter.Null);
        return JsonDocument.Parse(stdout.ToString()).RootElement;
    }

    [Fact]
    public void HleRun_ExplicitSegments_IsTheWholeRunBudgetNotTheDefault()
    {
        using var dir = new TempDirectory();

        var json = RunHle(dir.FullPath, "--segments", "3");

        json.GetProperty("stopReason").GetString().Should().Be("execution_budget");
        json.GetProperty("executionResult").GetProperty("state").GetString().Should().Be("BudgetExhausted");
        json.GetProperty("maxVblankReached").GetUInt64().Should().BeLessThanOrEqualTo(1UL,
            "3 segments of 100,000 instructions are under one frame; the HLE default of 64 would reach about 11");
    }

    [Fact]
    public void HleRun_WithoutSegments_UsesTheHleDefaultOf64()
    {
        using var dir = new TempDirectory();

        var json = RunHle(dir.FullPath);

        json.GetProperty("stopReason").GetString().Should().Be("execution_budget");
        json.GetProperty("maxVblankReached").GetUInt64().Should().BeInRange(10UL, 12UL,
            "the default of 64 segments of 100,000 instructions is about 11 frames");
    }

    [Fact]
    public void OpenBiosRun_ExplicitSegments_IsTheWholeRunBudget()
    {
        using var dir = new TempDirectory();
        var rom = new byte[OpenBiosFirmware.ImageSize];
        BitConverter.GetBytes(0x0BF00000u).CopyTo(rom, 0); // j 0xBFC00000: a spin loop
        var firmware = OpenBiosFirmware.FromBytes(rom);

        var result = ScreenshotCommand.RunScreenshotCaptureOpenBios(
            firmware, "00", null, dir.FullPath, segmentBudget: SegmentBudget, segments: 3,
            vblankInterval: 1000, maxScreenshots: 10, startVblank: 1000, TimeSpan.FromMinutes(1), TextWriter.Null);

        result.StopReason.Should().Be("execution_budget");
        result.MaxVblankReached.Should().BeLessThanOrEqualTo(1UL);
    }
}
