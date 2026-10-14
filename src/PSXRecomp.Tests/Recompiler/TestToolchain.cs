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
            return gcc is not null && TargetsX64Mingw(gcc) ? (gcc, null) : null;
        }

        var cross = FindOnPath("x86_64-w64-mingw32-gcc");
        return cross is null ? null : (cross, FindOnPath("wine"));
    }

    /// <summary>An executable file name for <paramref name="baseName"/> on this platform.</summary>
    public static string ExeName(string baseName) =>
        OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;

    /// <summary>A long-running command that outlives any test timeout.</summary>
    public static (string FileName, string Arguments) LongSleep(int seconds) =>
        OperatingSystem.IsWindows()
            ? ("ping", $"-n {seconds + 1} 127.0.0.1")
            : ("sleep", seconds.ToString());

    private static bool TargetsX64Mingw(string gcc)
    {
        var startInfo = new ProcessStartInfo(gcc, "-dumpmachine")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(startInfo)!;
        var machine = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return machine.StartsWith("x86_64", StringComparison.Ordinal)
            && machine.Contains("mingw", StringComparison.Ordinal);
    }
}
#pragma warning restore AARC003
