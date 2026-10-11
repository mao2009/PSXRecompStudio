using System.IO;
using System.Text;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.Cli;

/// <summary>
/// Tests for <c>psxrecomp screenshot</c> command (Issue #XXX).
/// </summary>
[Test]
public sealed class ScreenshotCommandTests
{
    private static byte[] BuildSyntheticExe(
        uint textStart, uint entryPoint, uint spInitial, uint gpInitial, uint[] words)
    {
        var fileContent = new byte[PsxExeHeader.HeaderSize + words.Length * 4];

        Buffer.BlockCopy(BitConverter.GetBytes(PsxExeHeader.Magic), 0, fileContent, 0, 8);
        BitConverter.GetBytes(entryPoint).CopyTo(fileContent, 0x10);
        BitConverter.GetBytes(gpInitial).CopyTo(fileContent, 0x14);
        BitConverter.GetBytes(textStart).CopyTo(fileContent, 0x18);
        BitConverter.GetBytes((uint)(words.Length * 4)).CopyTo(fileContent, 0x1C);
        BitConverter.GetBytes(spInitial).CopyTo(fileContent, 0x30);

        for (var i = 0; i < words.Length; i++)
        {
            BitConverter.GetBytes(words[i]).CopyTo(fileContent, PsxExeHeader.HeaderSize + i * 4);
        }

        return fileContent;
    }

    [Fact]
    public void ScreenshotCommand_Help_ShowsUsage()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = ScreenshotCommand.Run(["--help"], output, error);

        exitCode.Should().Be(0);
        output.ToString().Should().Contain("psxrecomp screenshot");
        output.ToString().Should().Contain("--output");
        output.ToString().Should().Contain("--vblank-interval");
        output.ToString().Should().Contain("--max-screenshots");
    }

    [Fact]
    public void ScreenshotCommand_MissingInput_ReturnsError()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = ScreenshotCommand.Run(["--output", "/tmp/test"], output, error);

        exitCode.Should().Be(1);
        error.ToString().Should().Contain("missing input path");
    }

    [Fact]
    public void ScreenshotCommand_MissingOutput_ReturnsError()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = ScreenshotCommand.Run(["dummy.exe", "--json"], output, error);

        exitCode.Should().Be(1);
        error.ToString().Should().Contain("missing required option '--output <dir>'");
    }

    [Fact]
    public void ScreenshotCommand_InvalidVBlankInterval_ReturnsError()
    {
        using var dir = new TempDirectory();
        var exePath = dir.WriteFile("test.exe", BuildSyntheticExe(
            0x80010000u, 0x80010000u, 0x801FFF00u, 0x00000000u,
            [0x03E00008u])); // jr $ra

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = ScreenshotCommand.Run([
            exePath,
            "--output", dir.FullPath,
            "--vblank-interval", "0",
            "--json"], output, error);

        exitCode.Should().Be(1);
        error.ToString().Should().Contain("invalid vblank interval");
    }

    [Fact]
    public void ScreenshotCommand_InvalidMaxScreenshots_ReturnsError()
    {
        using var dir = new TempDirectory();
        var exePath = dir.WriteFile("test.exe", BuildSyntheticExe(
            0x80010000u, 0x80010000u, 0x801FFF00u, 0x00000000u,
            [0x03E00008u])); // jr $ra

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = ScreenshotCommand.Run([
            exePath,
            "--output", dir.FullPath,
            "--max-screenshots", "0",
            "--json"], output, error);

        exitCode.Should().Be(1);
        error.ToString().Should().Contain("invalid max screenshots");
    }

    [Fact]
    public void ScreenshotCommand_InvalidTimeout_ReturnsError()
    {
        using var dir = new TempDirectory();
        var exePath = dir.WriteFile("test.exe", BuildSyntheticExe(
            0x80010000u, 0x80010000u, 0x801FFF00u, 0x00000000u,
            [0x03E00008u])); // jr $ra

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = ScreenshotCommand.Run([
            exePath,
            "--output", dir.FullPath,
            "--timeout", "-1",
            "--json"], output, error);

        exitCode.Should().Be(1);
        error.ToString().Should().Contain("invalid timeout");
    }

    [Fact]
    public void ScreenshotCommand_UnknownOption_ReturnsError()
    {
        using var dir = new TempDirectory();
        var exePath = dir.WriteFile("test.exe", BuildSyntheticExe(
            0x80010000u, 0x80010000u, 0x801FFF00u, 0x00000000u,
            [0x03E00008u])); // jr $ra

        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = ScreenshotCommand.Run([
            exePath,
            "--output", dir.FullPath,
            "--unknown-option",
            "--json"], output, error);

        exitCode.Should().Be(1);
        error.ToString().Should().Contain("unknown option");
    }

    [Fact]
    public void ScreenshotCommand_ValidArguments_ParsesCorrectly()
    {
        // This test verifies the parsing logic without actually running the command
        // (which would require a full native environment)
        var args = new[]
        {
            "test.exe",
            "--output", "/tmp/out",
            "--segment-budget", "50000",
            "--vblank-interval", "150",
            "--max-screenshots", "5",
            "--start-vblank", "150",
            "--timeout", "60",
            "--json"
        };

        var result = ScreenshotCommand.TryParse(args, out var parsed, out var error);

        result.Should().BeTrue(error);
        parsed.Input.Should().Be("test.exe");
        parsed.OutputDirectory.Should().Be("/tmp/out");
        parsed.SegmentBudget.Should().Be(50000u);
        parsed.VBlankInterval.Should().Be(150u);
        parsed.MaxScreenshots.Should().Be(5);
        parsed.StartVBlank.Should().Be(150ul);
        parsed.TimeoutSeconds.Should().Be(60);
        parsed.Json.Should().BeTrue();
        parsed.Help.Should().BeFalse();
    }

    // Note: Full end-to-end test with actual execution would require:
    // 1. A valid PS-X EXE that runs long enough to reach VBlank intervals
    // 2. The native library to be built and available
    // This is tested in CI via the E2E tests
}