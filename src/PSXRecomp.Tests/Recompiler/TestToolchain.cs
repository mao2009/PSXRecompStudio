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
    /// asynchronously. On timeout the whole process tree is killed and reaped and
    /// a <see cref="TimeoutException"/> is thrown. <paramref name="onStarted"/>
    /// receives the child's process id so callers can verify it was terminated.
    /// </summary>
    public static (int ExitCode, string Stdout, string Stderr) RunBounded(
        string fileName, string arguments, int timeoutMs, Action<int>? onStarted = null)
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
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.WaitForExit();
            try { Task.WhenAll(stdout, stderr).Wait(TimeSpan.FromMilliseconds(500)); } catch { }
            throw new TimeoutException($"'{fileName} {arguments}' did not finish within {timeoutMs} ms");
        }

        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
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
