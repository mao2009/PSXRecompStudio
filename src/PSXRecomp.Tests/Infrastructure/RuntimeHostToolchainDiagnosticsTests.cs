using System.Diagnostics;
using System.Text.RegularExpressions;
using FluentAssertions;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// A failed runtime host toolchain step keeps its stdout and exit code. dotnet reports MSBuild and NuGet errors on stdout,
/// so a stderr-only failure message left the macOS publish failure of PR #784 without a cause (Issue #805).
/// The driver is a shell script (cmd on Windows, sh elsewhere), so the test needs no C compiler on any platform.
/// </summary>
[Test]
public sealed class RuntimeHostToolchainDiagnosticsTests
{
    private const int DiagnosticTailChars = 8000;

    [Fact]
    public void FailedStep_KeepsItsStdoutAndExitCode_AlongsideStderr()
    {
        using var dir = new TempDirectory();
        var result = RunDriver(dir, payloadChars: DiagnosticTailChars + 1000, exitCode: 3, timeoutMs: 20000);

        result.Outcome.Should().Be(RuntimeHostBuildService.ToolchainOutcome.Failed);
        result.ExitCode.Should().Be(3);
        result.Stdout.Should().Contain("stdout-head-marker").And.Contain("stdout-tail-marker",
            "a failed step's stdout is where dotnet reports its errors");
        result.Stderr.Should().Contain("stderr-head-marker").And.Contain("stderr-tail-marker");
    }

    [Fact]
    public void FailureMessage_KeepsOnlyTheLast8000CharsOfEachStream()
    {
        using var dir = new TempDirectory();
        var result = RunDriver(dir, payloadChars: 9000, exitCode: 3, timeoutMs: 20000);
        result.Stdout.Length.Should().BeGreaterThan(DiagnosticTailChars);

        var message = RuntimeHostBuildService.DescribeToolchainFailure(result);

        message.Should().StartWith("exit code 3\n");
        var stdoutTail = Regex.Match(message, @"stdout \(last 8000 chars\):\n([\s\S]*)\nstderr \(last 8000 chars\):\n").Groups[1].Value;
        var stderrTail = Regex.Match(message, @"stderr \(last 8000 chars\):\n([\s\S]*)$").Groups[1].Value;
        stdoutTail.Should().HaveLength(DiagnosticTailChars, "the stdout section is the last 8000 chars only");
        stderrTail.Should().HaveLength(DiagnosticTailChars, "the stderr section is the last 8000 chars only");
        stdoutTail.Should().Contain("stdout-tail-marker").And.NotContain("stdout-head-marker");
        stderrTail.Should().Contain("stderr-tail-marker").And.NotContain("stderr-head-marker");
    }

    [Fact]
    public void FailedChild_WritingLargeOutputOnBothStreams_ReturnsWithinBound()
    {
        using var dir = new TempDirectory();
        var clock = Stopwatch.StartNew();

        var result = RunDriver(dir, payloadChars: 1_000_000, exitCode: 5, timeoutMs: 20000);

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "the streams must be drained while the child runs");
        result.Outcome.Should().Be(RuntimeHostBuildService.ToolchainOutcome.Failed, "a deadlock would surface as a timeout");
        result.ExitCode.Should().Be(5);
        result.Stdout.Length.Should().BeGreaterThan(1_000_000);
        result.Stderr.Length.Should().BeGreaterThan(1_000_000);
    }

    [Fact]
    public void FailedStep_WhileTheThreadPoolIsSaturated_StillDrainsItsOutput()
    {
        // Pool work queued ahead of the driver's stream reads cannot start until the pool injects threads, which is slow.
        // A child that fills its stdout pipe then blocks until those reads run, and the step times out.
        ThreadPool.GetMinThreads(out var minWorkers, out _);
        var release = new ManualResetEventSlim(false);
        var holders = new Task[minWorkers * 2];
        for (var i = 0; i < holders.Length; i++)
        {
            holders[i] = Task.Run(() => release.Wait());
        }

        try
        {
            using var dir = new TempDirectory();
            var result = RunDriver(dir, payloadChars: DiagnosticTailChars + 1000, exitCode: 3, timeoutMs: 5000);

            result.Outcome.Should().Be(RuntimeHostBuildService.ToolchainOutcome.Failed,
                "the child must not block on a full pipe while the pool is saturated");
            result.ExitCode.Should().Be(3);
        }
        finally
        {
            release.Set();
            // Queued holders must finish before the test ends, or they would starve the pool of later tests.
            Task.WaitAll(holders);
        }
    }

    [Fact]
    public void RuntimeHost_WhileTheThreadPoolIsSaturated_StillDrainsItsOutput()
    {
        // RunRuntimeHost reads the host's streams like RunToolchain, so the same saturation applies (Issue #822).
        ThreadPool.GetMinThreads(out var minWorkers, out _);
        var release = new ManualResetEventSlim(false);
        var holders = new Task[minWorkers * 2];
        for (var i = 0; i < holders.Length; i++)
        {
            holders[i] = Task.Run(() => release.Wait());
        }

        try
        {
            using var dir = new TempDirectory();
            var result = RunHostDriver(dir, payloadChars: 1_000_000, timeout: TimeSpan.FromSeconds(5));

            result.Stderr.Should().NotBe("Runtime host timed out",
                "the host must not block on a full pipe while the pool is saturated");
            result.Stdout.Should().Contain("stdout-tail-marker");
            // A host that exits normally has both pipes read to their end, so neither is reported as cut short (Issue #832).
            result.StdoutDrain.Should().Be(RuntimeHostBuildService.RuntimeHostPipeDrain.Complete);
            result.StderrDrain.Should().Be(RuntimeHostBuildService.RuntimeHostPipeDrain.Complete);
        }
        finally
        {
            release.Set();
            Task.WaitAll(holders);
        }
    }

    [Fact]
    public void SuccessfulStep_ReturnsSuccessWithoutCapturedOutput()
    {
        using var dir = new TempDirectory();
        var result = RunDriver(dir, payloadChars: 0, exitCode: 0, timeoutMs: 20000);

        result.Outcome.Should().Be(RuntimeHostBuildService.ToolchainOutcome.Success);
        result.ExitCode.Should().Be(0);
        result.Stdout.Should().BeEmpty();
        result.Stderr.Should().BeEmpty();
    }

    // The host is started with the manifest path as its first argument. sh runs the driver script directly. On Windows,
    // powershell.exe runs the cmd driver named by that first argument, since cmd.exe cannot take a script as its first argument.
    private static RuntimeHostBuildService.RuntimeHostRunResult RunHostDriver(
        TempDirectory dir, int payloadChars, TimeSpan timeout)
    {
#pragma warning disable AARC003
        File.WriteAllText(Path.Combine(dir.FullPath, "payload.txt"), new string('a', payloadChars));
#pragma warning restore AARC003

        var windows = OperatingSystem.IsWindows();
        var scriptPath = Path.Combine(dir.FullPath, windows ? "driver.cmd" : "driver.sh");
#pragma warning disable AARC003
        File.WriteAllText(scriptPath, windows ? CmdDriver : ShDriver);
#pragma warning restore AARC003
#pragma warning disable AARC003 // Test-only process launch: the host is the Windows PowerShell executable on Windows.
        var host = windows
            ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
            : "/bin/sh";
#pragma warning restore AARC003
        return RuntimeHostBuildService.RunRuntimeHost(host, scriptPath, timeout, extraArguments: ["0"]);
    }

    private static RuntimeHostBuildService.ToolchainResult RunDriver(
        TempDirectory dir, int payloadChars, int exitCode, int timeoutMs)
    {
        var payloadPath = Path.Combine(dir.FullPath, "payload.txt");
#pragma warning disable AARC003
        File.WriteAllText(payloadPath, new string('a', payloadChars));
#pragma warning restore AARC003

        var windows = OperatingSystem.IsWindows();
        var scriptPath = Path.Combine(dir.FullPath, windows ? "driver.cmd" : "driver.sh");
#pragma warning disable AARC003
        File.WriteAllText(scriptPath, windows ? CmdDriver : ShDriver);
        var result = RuntimeHostBuildService.RunToolchain(
            windows ? "cmd" : "sh",
            windows ? ["/c", scriptPath, exitCode.ToString()] : [scriptPath, exitCode.ToString()],
            timeoutMs);
#pragma warning restore AARC003
        return result;
    }

    private const string ShDriver =
        "payload=\"$(dirname \"$0\")/payload.txt\"\n" +
        "printf 'stdout-head-marker\\n'\n" +
        "cat \"$payload\"\n" +
        "printf 'stdout-tail-marker\\n'\n" +
        "printf 'stderr-head-marker\\n' >&2\n" +
        "cat \"$payload\" >&2\n" +
        "printf 'stderr-tail-marker\\n' >&2\n" +
        "exit \"$1\"\n";

    private const string CmdDriver =
        "@echo off\r\n" +
        "echo stdout-head-marker\r\n" +
        "type \"%~dp0payload.txt\"\r\n" +
        "echo stdout-tail-marker\r\n" +
        "echo stderr-head-marker 1>&2\r\n" +
        "type \"%~dp0payload.txt\" 1>&2\r\n" +
        "echo stderr-tail-marker 1>&2\r\n" +
        "exit /b %1\r\n";
}
