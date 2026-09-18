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
    private static readonly string[] DefaultCompilerArgTokens = ["-std=c11", "-O0", "-Wall", "-Wextra"];
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

        var objectPath = Path.Combine(request.OutputDirectory, request.BinaryName + ".o");
        var compileArgs = new List<string>(DefaultCompilerArgTokens);
        if (request.ExtraCompilerArguments is { Count: > 0 })
        {
            compileArgs.AddRange(request.ExtraCompilerArguments);
        }
        compileArgs.Add("-c");
        compileArgs.Add(sourcePath);
        compileArgs.Add("-o");
        compileArgs.Add(objectPath);

        var compile = RunToolchain(compiler, compileArgs, ToolchainTimeoutMs);
        if (compile.Outcome != ToolchainOutcome.Success)
        {
            return ToFailure(compile, GeneratedHostBuildStatus.CompileFailed, "COMPILE_FAILED", "Host compilation");
        }

        if (!File.Exists(objectPath))
        {
            return GeneratedHostBuildResult.Failed(
                GeneratedHostBuildStatus.CompileFailed,
                "COMPILE_FAILED",
                $"Host compilation reported success but produced no object file at '{objectPath}'.");
        }

        var binaryPath = Path.Combine(request.OutputDirectory, request.BinaryName);
        var link = RunToolchain(compiler, [objectPath, "-o", binaryPath], ToolchainTimeoutMs);
        if (link.Outcome != ToolchainOutcome.Success)
        {
            return ToFailure(link, GeneratedHostBuildStatus.LinkFailed, "LINK_FAILED", "Host link");
        }

        var resolvedBinaryPath = ResolveBinaryPath(binaryPath);
        if (!File.Exists(resolvedBinaryPath))
        {
            return GeneratedHostBuildResult.Failed(
                GeneratedHostBuildStatus.LinkFailed,
                "LINK_FAILED",
                $"Host link reported success but produced no binary at '{binaryPath}'.");
        }

        return GeneratedHostBuildResult.Succeeded(new GeneratedHostBuildArtifact(resolvedBinaryPath, sourcePath));
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
    /// <param name="arguments">
    /// The argument tokens to pass to it, each as one process argument (via
    /// <see cref="ProcessStartInfo.ArgumentList"/>) so an argument containing
    /// whitespace is not split into multiple arguments.
    /// </param>
    /// <param name="timeoutMs">Bounded wait for the process to exit.</param>
    /// <param name="cleanupMs">
    /// Bounded cleanup budget: how long to wait for the redirected
    /// output/error streams to finish, both after a normal exit (a
    /// descendant can keep an inherited pipe handle open) and after a
    /// post-timeout kill of the process tree.
    /// </param>
    internal static ToolchainResult RunToolchain(
        string fileName, IReadOnlyList<string> arguments, int timeoutMs, int cleanupMs = ToolchainCleanupMs)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

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
                    ToolchainOutcome.TimedOut, int.MinValue, ReadStreamWithinCleanupBudget(stderrTask, cleanupMs));
            }

            process.WaitForExit();
            _ = ReadStreamWithinCleanupBudget(stdoutTask, cleanupMs);
            return process.ExitCode == 0
                ? new ToolchainResult(ToolchainOutcome.Success, 0, string.Empty)
                : new ToolchainResult(
                    ToolchainOutcome.Failed, process.ExitCode, ReadStreamWithinCleanupBudget(stderrTask, cleanupMs));
        }
    }

    // ReadToEndAsync completes only at EOF. Even after a normal exit (and,
    // after Kill(true), once the associated process is gone), a descendant
    // that inherited the stdout/stderr handle can keep the pipe open
    // indefinitely, so a bare `.Result` here could block past the promised
    // timeout. Consume the stream only if it completes within the bounded
    // cleanup period; otherwise return minimal (empty) diagnostics.
    // Internal for the cleanup-bound regression tests.
    internal static string ReadStreamWithinCleanupBudget(Task<string> streamTask, int cleanupMs)
    {
        return streamTask.Wait(cleanupMs) && streamTask.IsCompletedSuccessfully
            ? streamTask.Result
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
