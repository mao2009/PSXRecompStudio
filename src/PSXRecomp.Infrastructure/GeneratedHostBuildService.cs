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
    private const int DiagnosticMessageMaxLength = 2000;

    public GeneratedHostBuildResult Build(GeneratedHostBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Directory.CreateDirectory(request.OutputDirectory);
        var sourcePath = Path.Combine(request.OutputDirectory, request.BinaryName + ".c");
        File.WriteAllText(sourcePath, request.Source);

        var compiler = request.CompilerExecutable ?? DefaultCompiler;
        var extraArgs = request.ExtraCompilerArguments is { Count: > 0 }
            ? " " + string.Join(' ', request.ExtraCompilerArguments)
            : string.Empty;

        var objectPath = Path.Combine(request.OutputDirectory, request.BinaryName + ".o");
        var compile = RunToolchain(
            compiler, $"{DefaultCompilerArgs}{extraArgs} -c \"{sourcePath}\" -o \"{objectPath}\"");
        if (compile.Outcome != ToolchainOutcome.Success)
        {
            return ToFailure(compile, GeneratedHostBuildStatus.CompileFailed, "COMPILE_FAILED", "Host compilation");
        }

        var binaryPath = Path.Combine(request.OutputDirectory, request.BinaryName);
        var link = RunToolchain(compiler, $"\"{objectPath}\" -o \"{binaryPath}\"");
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

    private enum ToolchainOutcome
    {
        Success,
        Unavailable,
        TimedOut,
        Failed,
    }

    private readonly record struct ToolchainResult(ToolchainOutcome Outcome, int ExitCode, string Stderr);

    private static ToolchainResult RunToolchain(string fileName, string arguments)
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

            if (!process.WaitForExit(ToolchainTimeoutMs))
            {
                TryKill(process);
                return new ToolchainResult(ToolchainOutcome.TimedOut, int.MinValue, stderrTask.Result);
            }

            process.WaitForExit();
            _ = stdoutTask.Result;
            return process.ExitCode == 0
                ? new ToolchainResult(ToolchainOutcome.Success, 0, string.Empty)
                : new ToolchainResult(ToolchainOutcome.Failed, process.ExitCode, stderrTask.Result);
        }
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
