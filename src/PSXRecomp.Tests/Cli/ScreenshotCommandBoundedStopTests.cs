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
        frame.GetProperty("sha256").GetString().Should().NotBeNullOrEmpty();
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
    }
}
