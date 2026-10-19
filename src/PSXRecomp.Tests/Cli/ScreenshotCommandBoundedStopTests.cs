using System.IO;
using System.Text.Json;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Infrastructure.Runtime.Gpu;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.Cli;

/// <summary>
/// Issue #751: bounded shutdown and manifest of the OpenBIOS screenshot path, using a synthetic
/// spin ROM (<c>j reset; nop</c>) instead of a real ROM.
/// </summary>
[Test]
#pragma warning disable AARC003 // reads the output directory that the command under test wrote
public sealed class ScreenshotCommandBoundedStopTests
{
    private static OpenBiosFirmware SpinRom()
    {
        var rom = new byte[OpenBiosFirmware.ImageSize];
        BitConverter.GetBytes(0x0BF00000u).CopyTo(rom, 0); // j 0xBFC00000
        return OpenBiosFirmware.FromBytes(rom);
    }

    [Fact]
    public void ZeroTimeout_StopsAtFirstSegmentBoundaryWithTimeoutReason()
    {
        using var dir = new TempDirectory();
        var result = ScreenshotCommand.RunScreenshotCaptureOpenBios(
            SpinRom(), "00", null, dir.FullPath, segmentBudget: 100_000, segments: 50,
            vblankInterval: 1, maxScreenshots: 10, startVblank: 1, TimeSpan.Zero, TextWriter.Null);

        result.StopReason.Should().Be("timeout");
        result.GoalReached.Should().BeFalse();
    }

    [Fact]
    public void CaptureLimit_StopsExecutionAndWritesManifest()
    {
        using var dir = new TempDirectory();
        var result = ScreenshotCommand.RunScreenshotCaptureOpenBios(
            SpinRom(), "00", null, dir.FullPath, segmentBudget: 100_000, segments: 50,
            vblankInterval: 1, maxScreenshots: 1, startVblank: 1, TimeSpan.FromMinutes(1), TextWriter.Null);

        result.StopReason.Should().Be("goal_reached");
        result.GoalReached.Should().BeTrue();
        result.ExceptionType.Should().BeNull();
        result.ScreenshotsTaken.Should().Be(1);
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullPath, "manifest.json"))).RootElement;
        manifest.GetProperty("stopReason").GetString().Should().Be("goal_reached");
        manifest.GetProperty("goalReached").GetBoolean().Should().BeTrue();
        manifest.GetProperty("exceptionType").ValueKind.Should().Be(JsonValueKind.Null);
        manifest.GetProperty("screenshotsTaken").GetInt32().Should().Be(1);
        var frame = manifest.GetProperty("frames")[0];
        frame.GetProperty("vblank").GetUInt64().Should().Be(1);
        frame.GetProperty("pngSha256").GetString().Should().NotBeNullOrEmpty();
        frame.GetProperty("vramSha256").GetString().Should().NotBeNullOrEmpty();
        frame.GetProperty("width").GetInt32().Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(TitleExecutionState.BudgetExhausted, "execution_budget")]
    [InlineData(TitleExecutionState.RuntimeFailure, "runtime_failure")]
    [InlineData(TitleExecutionState.InvalidState, "invalid_state")]
    [InlineData(TitleExecutionState.UnsupportedTransfer, "unsupported_transfer")]
    [InlineData(TitleExecutionState.RuntimeHandoff, "guest_stopped")]
    [InlineData(TitleExecutionState.Completed, "guest_stopped")]
    [InlineData(TitleExecutionState.Returned, "guest_stopped")]
    public void StopReasonOf_KeepsFailureStatesDistinctFromCleanGuestStops(TitleExecutionState state, string expected)
    {
        ScreenshotCommand.StopReasonOf(state).Should().Be(expected);
    }

    [Theory]
    [InlineData(TitleExecutionState.RuntimeFailure, true)]
    [InlineData(TitleExecutionState.InvalidState, true)]
    [InlineData(TitleExecutionState.UnsupportedTransfer, true)]
    [InlineData(TitleExecutionState.BudgetExhausted, false)]
    [InlineData(TitleExecutionState.RuntimeHandoff, false)]
    public void IsFailureState_NonCleanEndsFailTheRun(TitleExecutionState state, bool expected)
    {
        ScreenshotCommand.IsFailureState(state).Should().Be(expected);
    }

    [Fact]
    public void SetupFailure_WritesManifestNamingTheStop()
    {
        using var dir = new TempDirectory();
        var outputDirectory = Path.Combine(dir.FullPath, "shots");
        var code = ScreenshotCommand.Run(
            ["missing-input.exe", "--output", outputDirectory, "--json"], TextWriter.Null, TextWriter.Null);

        code.Should().NotBe(0);
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(outputDirectory, "manifest.json"))).RootElement;
        manifest.GetProperty("stopReason").GetString().Should().Be("setup_failed");
        manifest.GetProperty("exceptionType").GetString().Should().NotBeNullOrEmpty();
        manifest.GetProperty("goalReached").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void ManifestWithoutCapture_KeepsStopReasonAndExceptionTypeInSeparateFields()
    {
        using var dir = new TempDirectory();
        ScreenshotCapture.WriteManifestFile(dir.FullPath, "runtime_failure", 0, 0, TimeSpan.Zero, null, [],
            exceptionType: "InvalidOperationException");

        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullPath, "manifest.json"))).RootElement;
        manifest.GetProperty("stopReason").GetString().Should().Be("runtime_failure");
        manifest.GetProperty("exceptionType").GetString().Should().Be("InvalidOperationException");
        manifest.GetProperty("screenshotsTaken").GetInt32().Should().Be(0);
    }

    [Fact]
    public void RuntimeCrash_IsNormalizedToRuntimeFailureWithExceptionTypeApart()
    {
        // A negative capture count makes the ScreenshotCapture constructor throw inside the guarded run.
        using var dir = new TempDirectory();
        var result = ScreenshotCommand.RunScreenshotCaptureOpenBios(
            SpinRom(), "00", null, dir.FullPath, segmentBudget: 1_000, segments: 2,
            vblankInterval: 1, maxScreenshots: -1, startVblank: 1, TimeSpan.FromMinutes(1), TextWriter.Null);

        result.StopReason.Should().Be("runtime_failure");
        result.ExecutionResult.State.Should().Be(TitleExecutionState.RuntimeFailure);
        result.ExceptionType.Should().Be("ArgumentOutOfRangeException");
        result.GoalReached.Should().BeFalse();
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullPath, "manifest.json"))).RootElement;
        manifest.GetProperty("stopReason").GetString().Should().Be("runtime_failure");
        manifest.GetProperty("exceptionType").GetString().Should().Be("ArgumentOutOfRangeException");
    }

    [Fact]
    public void BudgetExhausted_IsReportedAsExecutionBudgetAndManifestMatchesPngFiles()
    {
        using var dir = new TempDirectory();
        var result = ScreenshotCommand.RunScreenshotCaptureOpenBios(
            SpinRom(), "00", null, dir.FullPath, segmentBudget: 1_000, segments: 2,
            vblankInterval: 1000, maxScreenshots: 10, startVblank: 1000, TimeSpan.FromMinutes(1), TextWriter.Null);

        result.StopReason.Should().Be("execution_budget");
        result.GoalReached.Should().BeFalse();
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullPath, "manifest.json"))).RootElement;
        manifest.GetProperty("stopReason").GetString().Should().Be("execution_budget");
        manifest.GetProperty("goalReached").GetBoolean().Should().BeFalse();
        // Partial run: the manifest lists exactly the PNG files on disk (the final frame included).
        var pngs = Directory.GetFiles(dir.FullPath, "*.png").Select(Path.GetFileName).Order().ToArray();
        var listed = manifest.GetProperty("frames").EnumerateArray()
            .Select(f => f.GetProperty("file").GetString()).Order().ToArray();
        pngs.Should().NotBeEmpty();
        listed.Should().BeEquivalentTo(pngs);
        manifest.GetProperty("screenshotsTaken").GetInt32().Should().Be(pngs.Length);
        // Normal budget stop with an image saved: execution is clean, capture is only partial, nothing is stale.
        manifest.GetProperty("captureStatus").GetString().Should().Be("partial");
        manifest.GetProperty("writeFailures").GetInt32().Should().Be(0);
        manifest.GetProperty("staleFiles").GetArrayLength().Should().Be(0);
        ScreenshotCommand.IsFailureState(result.ExecutionResult.State).Should().BeFalse();
    }

    [Fact]
    public void StalePngFromEarlierRun_IsKeptAndListedAsStaleNotAsFrame()
    {
        using var dir = new TempDirectory();
        var stalePath = Path.Combine(dir.FullPath, "persona_vblank_0900.png");
        File.WriteAllBytes(stalePath, [1, 2, 3]);

        var result = ScreenshotCommand.RunScreenshotCaptureOpenBios(
            SpinRom(), "00", null, dir.FullPath, segmentBudget: 1_000, segments: 2,
            vblankInterval: 1000, maxScreenshots: 10, startVblank: 1000, TimeSpan.FromMinutes(1), TextWriter.Null);

        // This run writes only the forced final frame; the older PNG is neither deleted nor claimed by the manifest.
        File.ReadAllBytes(stalePath).Should().Equal(new byte[] { 1, 2, 3 });
        result.SavedFiles.Should().ContainSingle().Which.Should().EndWith("persona_vblank_final.png");
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullPath, "manifest.json"))).RootElement;
        ListedFrameNames(manifest).Should().Equal("persona_vblank_final.png");
        manifest.GetProperty("staleFiles").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("persona_vblank_0900.png");
    }

    [Fact]
    public void InterruptedRun_IsFollowedByRerunWithManifestOfTheRerunOnly()
    {
        using var dir = new TempDirectory();
        // First run stops early at the wall-clock timeout: it writes only the forced final frame.
        ScreenshotCommand.RunScreenshotCaptureOpenBios(
            SpinRom(), "00", null, dir.FullPath, segmentBudget: 100_000, segments: 50,
            vblankInterval: 1, maxScreenshots: 3, startVblank: 1, TimeSpan.Zero, TextWriter.Null);

        // Second run completes normally with two frames.
        var result = ScreenshotCommand.RunScreenshotCaptureOpenBios(
            SpinRom(), "00", null, dir.FullPath, segmentBudget: 100_000, segments: 50,
            vblankInterval: 1, maxScreenshots: 2, startVblank: 1, TimeSpan.FromMinutes(1), TextWriter.Null);

        result.StopReason.Should().Be("goal_reached");
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullPath, "manifest.json"))).RootElement;
        manifest.GetProperty("stopReason").GetString().Should().Be("goal_reached");
        manifest.GetProperty("captureStatus").GetString().Should().Be("complete");
        ListedFrameNames(manifest).Should().Equal("persona_vblank_0001.png", "persona_vblank_0002.png");
        manifest.GetProperty("staleFiles").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("persona_vblank_final.png");
    }

    [Fact]
    public void InProgressMarker_IsWrittenAtStartAndReplacedByNextRun()
    {
        using var dir = new TempDirectory();
        // Run start writes the marker; an older PNG in the directory is reported as stale, not as this run's frame.
        File.WriteAllBytes(Path.Combine(dir.FullPath, "persona_vblank_0900.png"), [1]);
        ScreenshotCommand.MarkInProgress(dir.FullPath);
        var marked = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullPath, "manifest.json"))).RootElement;
        marked.GetProperty("stopReason").GetString().Should().Be("in_progress");
        marked.GetProperty("staleFiles").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("persona_vblank_0900.png");

        // A killed run leaves this marker behind; the next complete run replaces it.
        ScreenshotCommand.RunScreenshotCaptureOpenBios(
            SpinRom(), "00", null, dir.FullPath, segmentBudget: 1_000, segments: 2,
            vblankInterval: 1000, maxScreenshots: 10, startVblank: 1000, TimeSpan.FromMinutes(1), TextWriter.Null);

        var after = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullPath, "manifest.json"))).RootElement;
        after.GetProperty("stopReason").GetString().Should().Be("execution_budget");
    }

    [Fact]
    public void PngWriteFailure_IsCountedInManifestAndFailedFrameIsNotListed()
    {
        using var dir = new TempDirectory();
        // A directory at the target name makes the rename of that one frame fail; the other frames are unaffected.
        Directory.CreateDirectory(Path.Combine(dir.FullPath, "persona_vblank_0001.png"));

        var result = ScreenshotCommand.RunScreenshotCaptureOpenBios(
            SpinRom(), "00", null, dir.FullPath, segmentBudget: 100_000, segments: 50,
            vblankInterval: 1, maxScreenshots: 2, startVblank: 1, TimeSpan.FromMinutes(1), TextWriter.Null);

        // The first write failure stops the run at the next segment boundary; the failed frame is not counted.
        result.WriteFailures.Should().Be(1);
        result.StopReason.Should().Be("capture_write_failed");
        result.GoalReached.Should().BeFalse();
        Directory.GetFiles(dir.FullPath, "*.tmp").Should().BeEmpty("a failed write removes its own temp file");

        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullPath, "manifest.json"))).RootElement;
        manifest.GetProperty("stopReason").GetString().Should().Be("capture_write_failed");
        manifest.GetProperty("captureStatus").GetString().Should().Be("write_failed");
        manifest.GetProperty("writeFailures").GetInt32().Should().Be(1);
        // The manifest lists exactly the PNG files on disk; the failed frame is neither listed nor counted.
        var pngs = Directory.GetFiles(dir.FullPath, "*.png").Select(Path.GetFileName).Order().ToArray();
        ListedFrameNames(manifest).Order().Should().Equal(pngs);
        pngs.Should().NotContain("persona_vblank_0001.png");
        manifest.GetProperty("screenshotsTaken").GetInt32().Should().Be(pngs.Length);
    }

    [Fact]
    public void PngWriteFailure_MakesCommandExitNonZeroWhileJsonKeepsExecutionResultApart()
    {
        using var dir = new TempDirectory();
        var romPath = Path.Combine(dir.FullPath, "spin.bin");
        var rom = new byte[OpenBiosFirmware.ImageSize];
        BitConverter.GetBytes(0x0BF00000u).CopyTo(rom, 0); // j 0xBFC00000
        File.WriteAllBytes(romPath, rom);
        var discPath = Path.Combine(dir.FullPath, "blank.bin");
        File.WriteAllBytes(discPath, new byte[2352 * 16]);
        var outputDirectory = Path.Combine(dir.FullPath, "shots");
        Directory.CreateDirectory(Path.Combine(outputDirectory, "persona_vblank_0001.png"));

        var stdout = new StringWriter();
        var code = ScreenshotCommand.Run(
            ["--openbios", romPath, "--disc", discPath, "--output", outputDirectory, "--json",
             "--segment-budget", "100000", "--segments", "50", "--vblank-interval", "1",
             "--max-screenshots", "2", "--start-vblank", "1", "--timeout", "60"],
            stdout, TextWriter.Null);

        code.Should().NotBe(0);
        var json = JsonDocument.Parse(stdout.ToString()).RootElement;
        json.GetProperty("executionOk").GetBoolean().Should().BeTrue();
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("stopReason").GetString().Should().Be("capture_write_failed");
        json.GetProperty("captureStatus").GetString().Should().Be("write_failed");
        json.GetProperty("writeFailures").GetInt32().Should().Be(1);
    }

    [Fact]
    public void CliRun_AfterKilledRun_ReplacesInProgressMarkerAndLeavesStalePngUntouched()
    {
        using var dir = new TempDirectory();
        var romPath = Path.Combine(dir.FullPath, "spin.bin");
        var rom = new byte[OpenBiosFirmware.ImageSize];
        BitConverter.GetBytes(0x0BF00000u).CopyTo(rom, 0); // j 0xBFC00000
        File.WriteAllBytes(romPath, rom);
        var discPath = Path.Combine(dir.FullPath, "blank.bin");
        File.WriteAllBytes(discPath, new byte[2352 * 16]);
        var outputDirectory = Path.Combine(dir.FullPath, "shots");
        Directory.CreateDirectory(outputDirectory);
        var stalePath = Path.Combine(outputDirectory, "persona_vblank_0900.png");
        File.WriteAllBytes(stalePath, [1, 2, 3]);
        // A killed run leaves its start marker behind (no FinishCapture after it).
        ScreenshotCommand.MarkInProgress(outputDirectory);

        ScreenshotCommand.Run(
            ["--openbios", romPath, "--disc", discPath, "--output", outputDirectory, "--json",
             "--segment-budget", "1000", "--segments", "2", "--vblank-interval", "1000",
             "--max-screenshots", "10", "--start-vblank", "1000", "--timeout", "60"],
            TextWriter.Null, TextWriter.Null);

        File.ReadAllBytes(stalePath).Should().Equal(new byte[] { 1, 2, 3 });
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(outputDirectory, "manifest.json"))).RootElement;
        manifest.GetProperty("stopReason").GetString().Should().Be("execution_budget");
        manifest.GetProperty("staleFiles").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("persona_vblank_0900.png");
    }

    private static string?[] ListedFrameNames(JsonElement manifest) =>
        manifest.GetProperty("frames").EnumerateArray().Select(f => f.GetProperty("file").GetString()).ToArray();
}
