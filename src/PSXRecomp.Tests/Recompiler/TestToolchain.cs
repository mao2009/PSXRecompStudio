using System.Diagnostics;

namespace PSXRecomp.Tests.Recompiler;

/// <summary>
/// Resolves the native tools the compile-and-run tests need from PATH, per
/// platform, instead of assuming POSIX install locations. A tool that is
/// genuinely absent yields <c>null</c> so the caller can skip with a reason.
/// Only compilers that accept the GCC-style command line the tests issue
/// (<c>-std=c11 -O0 -Wall -Wextra src -o out</c>) are returned, so MSVC
/// <c>cl</c> is deliberately not a candidate.
/// </summary>
// File/environment access is the whole point of this helper; the Test layer bans it by default.
#pragma warning disable AARC003
[Test]
internal static class TestToolchain
{
    public static string? FindOnPath(string name)
    {
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir.Trim('"'), name + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>A C compiler for the host: gcc, then cc, then clang.</summary>
    public static string? FindCCompiler() =>
        FindOnPath("gcc") ?? FindOnPath("cc") ?? FindOnPath("clang");

    /// <summary>
    /// A compiler producing x86-64 Windows PE32+ plus the runner for it. On Windows
    /// the host gcc must itself target x86_64 MinGW and the PE runs natively
    /// (<c>Runner</c> is empty); elsewhere a MinGW cross compiler and Wine are required.
    /// </summary>
    public static (string Compiler, string? Runner)? FindWindowsX64Pe()
    {
        if (OperatingSystem.IsWindows())
        {
            var gcc = FindOnPath("gcc");
            return gcc is not null && IsX64Mingw(ProbeMachine(gcc)) ? (gcc, null) : null;
        }

        var cross = FindOnPath("x86_64-w64-mingw32-gcc");
        return cross is null ? null : (cross, FindOnPath("wine"));
    }

    /// <summary>An executable file name for <paramref name="baseName"/> on this platform.</summary>
    public static string ExeName(string baseName) =>
        OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;

    /// <summary>
    /// A long-running command (resolved to a full path) that outlives any test
    /// timeout, or <c>null</c> when the platform's utility is unavailable
    /// (<c>ping</c> on Windows, <c>sleep</c> elsewhere).
    /// </summary>
    public static (string FileName, string Arguments)? LongSleep(int seconds)
    {
        if (OperatingSystem.IsWindows())
        {
            var ping = FindOnPath("ping")
                ?? Path.Combine(Environment.SystemDirectory, "ping.exe");
            return File.Exists(ping) ? (ping, $"-n {seconds + 1} 127.0.0.1") : null;
        }

        var sleep = FindOnPath("sleep");
        return sleep is null ? null : (sleep, seconds.ToString());
    }

    /// <summary>
    /// Runs a tool with an explicit time budget, draining stdout and stderr
    /// asynchronously. Every wait is bounded: the run itself (<paramref name="timeoutMs"/>),
    /// the wait for the child to exit after it is killed (<paramref name="reapTimeoutMs"/>)
    /// and the wait for the output streams to close (<paramref name="drainTimeoutMs"/>).
    /// A timeout, a kill failure, an exit that cannot be confirmed or output streams
    /// that stay open (a descendant still holds the pipe) all throw
    /// <see cref="TimeoutException"/> with the cause, never a normal result.
    /// <paramref name="onStarted"/> receives the child's process id; <paramref name="kill"/>
    /// replaces the default tree kill (a seam for the failure-path tests).
    /// </summary>
    /// <remarks>
    /// Only the child's own exit can be confirmed (<c>HasExited</c>). The
    /// <c>entireProcessTree</c> kill is best-effort for descendants: its outcome is
    /// reported in the diagnostics but not independently verified.
    /// </remarks>
    public static (int ExitCode, string Stdout, string Stderr) RunBounded(
        string fileName, string arguments, int timeoutMs, Action<int>? onStarted = null,
        int reapTimeoutMs = 5000, int drainTimeoutMs = 2000, Action<Process>? kill = null)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(startInfo)!;
        onStarted?.Invoke(process.Id);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var command = $"'{fileName} {arguments}'";

        if (!process.WaitForExit(timeoutMs))
        {
            string killNote;
            try
            {
                (kill ?? (p => p.Kill(entireProcessTree: true)))(process);
                killNote = "process tree kill issued (descendant termination is not verified)";
            }
            catch (InvalidOperationException)
            {
                killNote = "kill skipped: the process had already exited";
            }
            catch (Exception ex)
            {
                killNote = $"process tree kill FAILED ({ex.GetType().Name}: {ex.Message})";
            }

            if (!process.WaitForExit(reapTimeoutMs))
            {
                throw new TimeoutException(
                    $"{command} exceeded {timeoutMs} ms and its exit could not be confirmed within {reapTimeoutMs} ms of the kill; {killNote}");
            }

            WaitForStreams(stdout, stderr, drainTimeoutMs, out var drained);
            throw new TimeoutException(
                $"{command} did not finish within {timeoutMs} ms; child exit confirmed; {killNote}"
                + (drained ? string.Empty : $"; output streams still open after {drainTimeoutMs} ms"));
        }

        // Parameterless WaitForExit() would also wait for stream EOF without a limit.
        if (!WaitForStreams(stdout, stderr, drainTimeoutMs, out _))
        {
            throw new TimeoutException(
                $"{command} exited but its output streams stayed open for {drainTimeoutMs} ms (a descendant process likely holds the pipe)");
        }

        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    private static bool WaitForStreams(Task<string> stdout, Task<string> stderr, int timeoutMs, out bool drained)
    {
        try
        {
            drained = Task.WhenAll(stdout, stderr).Wait(timeoutMs);
        }
        catch (AggregateException)
        {
            drained = true; // a faulted read has finished; the stream is no longer pending
        }
        return drained;
    }

    /// <summary>
    /// Asks <paramref name="compiler"/> for its target triple. A failure to start,
    /// a non-zero exit or a timeout is an environment fault, not "unsupported":
    /// it throws with the captured diagnostics instead of letting the caller skip.
    /// </summary>
    public static string ProbeMachine(string compiler, string arguments = "-dumpmachine", int timeoutMs = 10000)
    {
        (int ExitCode, string Stdout, string Stderr) result;
        try
        {
            result = RunBounded(compiler, arguments, timeoutMs);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"could not start '{compiler} {arguments}': {ex.Message}", ex);
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{compiler} {arguments}' exited {result.ExitCode}; stdout: {result.Stdout.Trim()}; stderr: {result.Stderr.Trim()}");
        }
        return result.Stdout.Trim();
    }

    internal static bool IsX64Mingw(string machine) =>
        machine.StartsWith("x86_64", StringComparison.Ordinal)
        && machine.Contains("mingw", StringComparison.Ordinal);
}
#pragma warning restore AARC003
