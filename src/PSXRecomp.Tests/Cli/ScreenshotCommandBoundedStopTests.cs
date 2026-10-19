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
    }

    [Fact]
    public void CaptureLimit_StopsExecutionAndWritesManifest()
    {
        using var dir = new TempDirectory();
        var result = ScreenshotCommand.RunScreenshotCaptureOpenBios(
            SpinRom(), "00", null, dir.FullPath, segmentBudget: 100_000, segments: 50,
            vblankInterval: 1, maxScreenshots: 1, startVblank: 1, TimeSpan.FromMinutes(1), TextWriter.Null);

        result.StopReason.Should().Be("capture_limit");
        result.ScreenshotsTaken.Should().Be(1);
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullPath, "manifest.json"))).RootElement;
        manifest.GetProperty("stopReason").GetString().Should().Be("capture_limit");
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
    public void ManifestWithoutCapture_RecordsStopReasonInOutputDirectory()
    {
        using var dir = new TempDirectory();
        ScreenshotCapture.WriteManifestFile(dir.FullPath, "runtime_failure", 0, 0, TimeSpan.Zero, null, []);

        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullPath, "manifest.json"))).RootElement;
        manifest.GetProperty("stopReason").GetString().Should().Be("runtime_failure");
        manifest.GetProperty("screenshotsTaken").GetInt32().Should().Be(0);
    }

    [Fact]
    public void BudgetExhausted_IsReportedAsExecutionBudgetAndKeepsFinalFrame()
    {
        using var dir = new TempDirectory();
        var result = ScreenshotCommand.RunScreenshotCaptureOpenBios(
            SpinRom(), "00", null, dir.FullPath, segmentBudget: 1_000, segments: 2,
            vblankInterval: 1000, maxScreenshots: 10, startVblank: 1000, TimeSpan.FromMinutes(1), TextWriter.Null);

        result.StopReason.Should().Be("execution_budget");
        File.Exists(Path.Combine(dir.FullPath, "manifest.json")).Should().BeTrue();
    }
}
