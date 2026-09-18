using System.ComponentModel;
using System.Diagnostics;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// Concrete managed adapter for <see cref="IGeneratedHostBuildService"/>: writes
/// the requested source into the caller-selected output directory and invokes an
/// external C compiler/linker toolchain to turn it into a native artifact
/// (Issue #458, realizing the Option B groundwork sketched in ADR-015).
/// </summary>
/// <remarks>
/// Compile and link run as two separate toolchain invocations so a caller can
/// tell a compiler failure from a linker failure. This adapter creates no
/// temporary workspace of its own: every file it writes lives directly under
/// <see cref="GeneratedHostBuildRequest.OutputDirectory"/>, whose lifecycle the
/// caller owns.
/// </remarks>
[Infrastructure]
public sealed class GeneratedHostBuildService : IGeneratedHostBuildService
{
    private const string DefaultCompiler = "gcc";
    private const string DefaultCompilerArgs = "-std=c11 -O0 -Wall -Wextra";
    private const int ToolchainTimeoutMs = 30000;
    private const int ToolchainCleanupMs = 2000;
    private const int DiagnosticMessageMaxLength = 2000;

    public GeneratedHostBuildResult Build(GeneratedHostBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateBinaryName(request.BinaryName);

        var sourcePath = Path.Combine(request.OutputDirectory, request.BinaryName + ".c");
        try
        {
            Directory.CreateDirectory(request.OutputDirectory);
            File.WriteAllText(sourcePath, request.Source);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The caller-owned output location is unusable for an environmental
            // reason (missing parent, locked/busy file, denied access): a
            // structured output failure, not a toolchain failure. Argument
            // errors (e.g. an invalid OutputDirectory) are intentionally not
            // caught here and surface as exceptions.
            return GeneratedHostBuildResult.Failed(
                GeneratedHostBuildStatus.OutputFailed,
                "OUTPUT_FAILED",
                $"Host output setup failed for '{request.OutputDirectory}': {e.Message}");
        }

        var compiler = request.CompilerExecutable ?? DefaultCompiler;
        var extraArgs = request.ExtraCompilerArguments is { Count: > 0 }
            ? " " + string.Join(' ', request.ExtraCompilerArguments)
            : string.Empty;

        var objectPath = Path.Combine(request.OutputDirectory, request.BinaryName + ".o");
        var compile = RunToolchain(
            compiler, $"{DefaultCompilerArgs}{extraArgs} -c \"{sourcePath}\" -o \"{objectPath}\"", ToolchainTimeoutMs);
        if (compile.Outcome != ToolchainOutcome.Success)
        {
            return ToFailure(compile, GeneratedHostBuildStatus.CompileFailed, "COMPILE_FAILED", "Host compilation");
        }

        var binaryPath = Path.Combine(request.OutputDirectory, request.BinaryName);
        var link = RunToolchain(compiler, $"\"{objectPath}\" -o \"{binaryPath}\"", ToolchainTimeoutMs);
        if (link.Outcome != ToolchainOutcome.Success)
        {
            return ToFailure(link, GeneratedHostBuildStatus.LinkFailed, "LINK_FAILED", "Host link");
        }

        return GeneratedHostBuildResult.Succeeded(
            new GeneratedHostBuildArtifact(ResolveBinaryPath(binaryPath), sourcePath));
    }

    private static GeneratedHostBuildResult ToFailure(
        ToolchainResult result, GeneratedHostBuildStatus failedStepStatus, string failedStepCode, string stepName)
    {
        return result.Outcome switch
        {
            ToolchainOutcome.Unavailable => GeneratedHostBuildResult.Failed(
                GeneratedHostBuildStatus.ToolchainUnavailable,
                "TOOLCHAIN_UNAVAILABLE",
                $"{stepName} could not start the requested compiler executable."),
            ToolchainOutcome.TimedOut => GeneratedHostBuildResult.Failed(
                GeneratedHostBuildStatus.TimedOut,
                "TOOLCHAIN_TIMEOUT",
                $"{stepName} exceeded the build timeout."),
            _ => GeneratedHostBuildResult.Failed(
                failedStepStatus,
                failedStepCode,
                $"{stepName} failed (exit {result.ExitCode}):\n{Truncate(result.Stderr)}"),
        };
    }

    internal enum ToolchainOutcome
    {
        Success,
        Unavailable,
        TimedOut,
        Failed,
    }

    internal readonly record struct ToolchainResult(ToolchainOutcome Outcome, int ExitCode, string Stderr);

    // Rejects anything that is not a simple file name so the generated .c/.o
    // and binary always land directly under the caller-owned OutputDirectory:
    // a rooted value, a path separator, or a directory component ("..", "sub/x")
    // would otherwise let artifacts escape that directory.
    private static void ValidateBinaryName(string binaryName)
    {
        if (string.IsNullOrWhiteSpace(binaryName))
        {
            throw new ArgumentException(
                "BinaryName must be a non-empty, non-whitespace file name.", nameof(GeneratedHostBuildRequest.BinaryName));
        }

        if (Path.IsPathRooted(binaryName)
            || binaryName.Contains('/')
            || binaryName.Contains('\\')
            || binaryName is "." or "..")
        {
            throw new ArgumentException(
                "BinaryName must be a simple file name without path components.", nameof(GeneratedHostBuildRequest.BinaryName));
        }
    }

    /// <summary>
    /// Runs one toolchain invocation. The composed arguments are trusted call
    /// input (the caller owns the output directory and the compiler to run).
    /// </summary>
    /// <param name="fileName">The compiler/linker executable to start.</param>
    /// <param name="arguments">The argument string to pass to it.</param>
    /// <param name="timeoutMs">Bounded wait for the process to exit.</param>
    /// <param name="cleanupMs">
    /// Bounded post-kill cleanup budget: how long to wait for the killed
    /// process tree to release the redirected output/error streams.
    /// </param>
    internal static ToolchainResult RunToolchain(
        string fileName, string arguments, int timeoutMs, int cleanupMs = ToolchainCleanupMs)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Win32Exception)
        {
            // The requested executable does not exist / cannot be launched: an
            // unavailable toolchain, not a compile or link failure.
            return new ToolchainResult(ToolchainOutcome.Unavailable, int.MinValue, string.Empty);
        }

        using (process)
        {
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                TryKill(process);
                return new ToolchainResult(
                    ToolchainOutcome.TimedOut, int.MinValue, ReadStderrWithinCleanupBudget(stderrTask, cleanupMs));
            }

            process.WaitForExit();
            _ = stdoutTask.Result;
            return process.ExitCode == 0
                ? new ToolchainResult(ToolchainOutcome.Success, 0, string.Empty)
                : new ToolchainResult(ToolchainOutcome.Failed, process.ExitCode, stderrTask.Result);
        }
    }

    // ReadToEndAsync completes only at EOF. After Kill(true) the associated
    // process is gone, but a descendant that inherited the stderr handle can
    // keep the pipe open indefinitely, so a bare `.Result` here could block
    // past the promised timeout. Consume stderr only if it completes within the
    // bounded cleanup period; otherwise return minimal (empty) diagnostics.
    // Internal for the timeout regression tests.
    internal static string ReadStderrWithinCleanupBudget(Task<string> stderrTask, int cleanupMs)
    {
        return stderrTask.Wait(cleanupMs) && stderrTask.IsCompletedSuccessfully
            ? stderrTask.Result
            : string.Empty;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    // MinGW appends ".exe" on Windows; POSIX toolchains produce the requested
    // name verbatim. Resolve whichever the platform actually produced.
    private static string ResolveBinaryPath(string requestedPath)
    {
        if (File.Exists(requestedPath)) return requestedPath;
        var withExeSuffix = requestedPath + ".exe";
        return File.Exists(withExeSuffix) ? withExeSuffix : requestedPath;
    }

    private static string Truncate(string value) =>
        value.Length <= DiagnosticMessageMaxLength ? value : value[..DiagnosticMessageMaxLength] + "...";
}
