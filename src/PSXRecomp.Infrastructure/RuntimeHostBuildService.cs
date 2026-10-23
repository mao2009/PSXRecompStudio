using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure.Standalone;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// Provisions the shared managed Runtime host into a packaged distribution
/// (Issue #739 Phase 2 Step C, ADR-027 Option B). The packaging flow runs the
/// RuntimeHost project to disk with <c>dotnet publish</c> — by default a
/// framework-dependent deploy for the host's own platform (the runtime is
/// expected on the end-user host), and with <c>--self-contained</c> a full
/// runtime bundle that needs no host .NET installation. The launcher later
/// <c>execv()</c>s <c>runtime/PSXRecomp.RuntimeHost[.exe]</c> directly, so the
/// packaged distribution runs without Studio and without the <c>psxrecomp</c>
/// CLI. No BIOS/ROM/game bytes are bundled: only the managed runtime host and
/// its dependency graph.
/// </summary>
[Infrastructure]
public static class RuntimeHostBuildService
{
    /// <summary>Default publish timeout; publish is the slow provisioning step.</summary>
    public static TimeSpan DefaultProvisionTimeout { get; } = TimeSpan.FromMinutes(10);

    private const int ToolchainCleanupMs = 2000;
    private const int DiagnosticMessageMaxLength = 4000;

    /// <summary>The published distribution is invalid when the host binary is missing.</summary>
    public sealed class RuntimeHostProvisionFailed : Exception
    {
        public RuntimeHostProvisionFailed(string message, string? diagnosticCode = null)
            : base(message)
        {
            DiagnosticCode = diagnosticCode;
        }

        public string? DiagnosticCode { get; }
    }

    /// <summary>A successfully provisioned Runtime host directory.</summary>
    public sealed class RuntimeHostProvisionResult
    {
        private RuntimeHostProvisionResult(string targetDirectory)
        {
            TargetDirectory = targetDirectory;
        }

        public string TargetDirectory { get; }

        public static RuntimeHostProvisionResult Succeeded(string targetDirectory) =>
            new(targetDirectory);
    }

    /// <summary>
    /// Provisions the Runtime host into <paramref name="targetDirectory"/>.
    /// </summary>
    /// <param name="targetDirectory">
    /// The <c>runtime/</c> subdirectory of the distribution (<see cref="RuntimeHostLayout"/>):
    /// the host binary must be written to its root, alongside its managed dependencies.
    /// </param>
    /// <param name="selfContained">
    /// <c>true</c> to publish with a full runtime bundle and a single self-executable host
    /// (<c>-r</c> + <c>SelfContained=true</c> + <c>PublishSingleFile=true</c>): no .NET
    /// installation is required on the end-user host. <c>false</c> publishes a
    /// framework-dependent deploy that requires the .NET runtime present on the end-user
    /// host (hosted by the bundled apphost); this is the default and is sufficient when
    /// the target distribution machine has .NET installed.
    /// </param>
    /// <param name="targetRid">
    /// The <c>-r</c> runtime identifier to publish for. Defaults to the host's own
    /// platform (<see cref="RuntimeInformation.RuntimeIdentifier">RuntimeInformation.RuntimeIdentifier</see>):
    /// for Step C the CLI normally builds on the same platform it packages for. Passing a
    /// foreign RID enables cross-compile packaging (for example Linux host → <c>win-x64</c>
    /// artifact) when the toolchain and RID pack support it.
    /// </param>
    /// <param name="provisionTimeout">The bound on the <c>dotnet publish</c> step.</param>
    /// <exception cref="RuntimeHostProvisionFailed">The host could not be provisioned.</exception>
    public static RuntimeHostProvisionResult ProvisionRuntimeHost(
        string targetDirectory,
        bool selfContained,
        string? targetRid = null,
        TimeSpan? provisionTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ArgumentException("The runtime host target directory must be non-empty.", nameof(targetDirectory));
        }

        if (provisionTimeout is { TotalMilliseconds: <= 0 or > int.MaxValue })
        {
            throw new ArgumentOutOfRangeException(nameof(provisionTimeout));
        }

        var timeout = (int)(provisionTimeout ?? DefaultProvisionTimeout).TotalMilliseconds;
        var rid = targetRid ?? RuntimeInformation.RuntimeIdentifier;
        Directory.CreateDirectory(targetDirectory);

        var hostProject = LocateRuntimeHostProject()
            ?? throw new RuntimeHostProvisionFailed(
                "The PSXRecomp.RuntimeHost project could not be located; the runtime host must be published from its csproj.",
                "RUNTIME_HOST_NOT_FOUND");

        // Isolate each provision operation to a unique artifacts output directory to avoid
        // concurrent restore/publish races on shared obj/bin and project.assets.json.
        var artifactsRoot = Path.Combine(Path.GetTempPath(), "psxrecomp-runtimehost-artifacts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactsRoot);
        var artifactsPath = Path.Combine(artifactsRoot, "artifacts");

        // The isolated artifacts directory is scratch space: it must be removed on every
        // exit path, including a throw from restore, publish, or layout validation. A
        // cleanup failure must never mask the real outcome, so TryDeleteDirectory swallows
        // removal errors and this finally never throws.
        try
        {
            // Restore the project for the target RID to ensure assets are available.
            var restoreArgs = new List<string>
            {
                "restore",
                hostProject,
                "-r",
                rid,
                "-p:UseArtifactsOutput=true",
                $"-p:ArtifactsPath={artifactsPath}",
                // Issue #813: this publish gets its own native build directory under ArtifactsPath.
                "-p:PsxIsolatedNativeBuild=true",
            };
            var restoreResult = RunToolchain("dotnet", restoreArgs, timeout);
            if (restoreResult.Outcome != ToolchainOutcome.Success)
            {
                throw new RuntimeHostProvisionFailed(
                    restoreResult.Outcome + " while restoring the runtime host (" + string.Join(" ", restoreArgs) + "):\n" + DescribeToolchainFailure(restoreResult),
                    MapProvisionFailure(restoreResult.Outcome));
            }

            var args = new List<string>
            {
                "publish",
                hostProject,
                "-c",
                "Release",
                "-r",
                rid,
                "-o",
                Path.GetFullPath(targetDirectory),
                "-p:UseArtifactsOutput=true",
                $"-p:ArtifactsPath={artifactsPath}",
                // Issue #813: this publish gets its own native build directory under ArtifactsPath.
                "-p:PsxIsolatedNativeBuild=true",
                // Restore is done explicitly above; --no-restore avoids duplicate work.
                "--no-restore",
            };

            // Issue #B2 (MEDIUM-5): no PDB is shipped, so the host carries no developer build path (PDB source paths).
            args.Add("-p:DebugType=none");
            if (selfContained)
            {
                args.Add("-p:SelfContained=true");
                args.Add("-p:PublishSingleFile=true");
            }
            else
            {
                // Framework-dependent deploy: bundle the apphost + app + runtime files.
                // The end-user host must have the matching .NET runtime installed.
                args.Add("-p:SelfContained=false");
            }

            var result = RunToolchain("dotnet", args, timeout);
            if (result.Outcome != ToolchainOutcome.Success)
            {
                throw new RuntimeHostProvisionFailed(
                    result.Outcome + " while provisioning the runtime host (" + string.Join(" ", args) + "):\n" + DescribeToolchainFailure(result),
                    MapProvisionFailure(result.Outcome));
            }

            // Validate the published layout: the launcher execs the host at the layout's
            // canonical name relative to the distribution root.
            var hostName = RuntimeHostLayout.HostFileName(rid.StartsWith("win"));
            var hostPath = Path.Combine(targetDirectory, hostName);
            if (!File.Exists(hostPath))
            {
                throw new RuntimeHostProvisionFailed(
                    $"Published runtime host directory does not contain the host binary at {hostPath}.",
                    "RUNTIME_HOST_MISSING");
            }

            return RuntimeHostProvisionResult.Succeeded(Path.GetFullPath(targetDirectory));
        }
        finally
        {
            TryDeleteDirectory(artifactsRoot);
        }
    }

    private static string? LocateRuntimeHostProject()
    {
        // Walk upward from this assembly's location until a PSXRecomp.RuntimeHost.csproj is found.
        var dir = Path.GetDirectoryName(typeof(RuntimeHostBuildService).Assembly.Location)
            ?? Environment.CurrentDirectory;
        while (dir != null && dir != Path.GetPathRoot(dir))
        {
            var candidate = Path.Combine(dir, "PSXRecomp.RuntimeHost.csproj");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            // Also check in PSXRecomp.RuntimeHost subdirectory
            var subCandidate = Path.Combine(dir, "PSXRecomp.RuntimeHost", "PSXRecomp.RuntimeHost.csproj");
            if (File.Exists(subCandidate))
            {
                return subCandidate;
            }
            dir = Path.GetDirectoryName(dir);
        }

        // Fallback: try from the solution root (for when running from published CLI or test projects)
        // The solution root contains the src/ directory with all projects.
        var baseDir = AppContext.BaseDirectory;
        var relatives = new[]
        {
            // From test project output: bin/Debug/net10.0 -> ../../../../src/PSXRecompRuntimeHost/PSXRecomp.RuntimeHost.csproj
            Path.Combine(baseDir, "..", "..", "..", "..", "src", "PSXRecomp.RuntimeHost", "PSXRecomp.RuntimeHost.csproj"),
            // From CLI output: bin/Release/net10.0 -> ../../../../src/PSXRecompRuntimeHost/PSXRecomp.RuntimeHost.csproj
            Path.Combine(baseDir, "..", "..", "..", "..", "src", "PSXRecomp.RuntimeHost", "PSXRecomp.RuntimeHost.csproj"),
            // From solution root
            Path.Combine(baseDir, "src", "PSXRecomp.RuntimeHost", "PSXRecomp.RuntimeHost.csproj"),
            // Direct from source tree
            Path.Combine(baseDir, "..", "..", "..", "src", "PSXRecomp.RuntimeHost", "PSXRecomp.RuntimeHost.csproj"),
        };
        foreach (var rel in relatives)
        {
            var full = Path.GetFullPath(rel);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    private static string MapProvisionFailure(ToolchainOutcome outcome) =>
        outcome switch
        {
            ToolchainOutcome.Unavailable => "DOTNET_TOOLCHAIN_NOT_AVAILABLE",
            ToolchainOutcome.TimedOut => "DOTNET_PUBLISH_TIMEOUT",
            ToolchainOutcome.Failed => "DOTNET_PUBLISH_FAILED",
            _ => "DOTNET_PUBLISH_FAILED",
        };

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
            return new ToolchainResult(ToolchainOutcome.Unavailable, int.MinValue, string.Empty, string.Empty);
        }

        using (process)
        {
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                TryKill(process);
                return new ToolchainResult(
                    ToolchainOutcome.TimedOut, int.MinValue,
                    ReadStreamWithinCleanupBudget(stdoutTask, cleanupMs), ReadStreamWithinCleanupBudget(stderrTask, cleanupMs));
            }

            process.WaitForExit();
            var stdout = ReadStreamWithinCleanupBudget(stdoutTask, cleanupMs);
            return process.ExitCode == 0
                ? new ToolchainResult(ToolchainOutcome.Success, 0, string.Empty, string.Empty)
                : new ToolchainResult(ToolchainOutcome.Failed, process.ExitCode, stdout, ReadStreamWithinCleanupBudget(stderrTask, cleanupMs));
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

    private static string ReadStreamWithinCleanupBudget(Task<string> streamTask, int cleanupMs)
    {
        return streamTask.Wait(cleanupMs) && streamTask.IsCompletedSuccessfully
            ? streamTask.Result
            : string.Empty;
    }

    /// <summary>
    /// Result of running the Runtime host.
    /// </summary>
    public sealed class RuntimeHostRunResult
    {
        private RuntimeHostRunResult(int exitCode, string stdout, string stderr)
        {
            ExitCode = exitCode;
            Stdout = stdout;
            Stderr = stderr;
        }

        public int ExitCode { get; }
        public string Stdout { get; }
        public string Stderr { get; }

        public static RuntimeHostRunResult Succeeded(int exitCode, string stdout, string stderr) =>
            new(exitCode, stdout, stderr);
    }

    /// <summary>
    /// Runs the provisioned Runtime host with the given manifest.
    /// </summary>
    /// <param name="hostPath">Absolute path to the Runtime host executable.</param>
    /// <param name="manifestPath">Absolute path to the runtime-host.json manifest.</param>
    /// <param name="timeout">Optional timeout for the host execution.</param>
    /// <param name="extraArguments">Optional extra arguments passed to the host (for example <c>--diagnostics</c>).</param>
    /// <returns>The run result with exit code, stdout, and stderr.</returns>
    public static RuntimeHostRunResult RunRuntimeHost(
        string hostPath,
        string manifestPath,
        TimeSpan? timeout = null,
        IReadOnlyList<string>? extraArguments = null)
    {
        if (string.IsNullOrWhiteSpace(hostPath) || !File.Exists(hostPath))
        {
            throw new ArgumentException("The runtime host executable does not exist.", nameof(hostPath));
        }

        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            throw new ArgumentException("The manifest file does not exist.", nameof(manifestPath));
        }

        var timeoutMs = (int)(timeout ?? TimeSpan.FromMinutes(5)).TotalMilliseconds;

        var psi = new ProcessStartInfo(hostPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(manifestPath);
        if (extraArguments is not null)
        {
            foreach (var argument in extraArguments) psi.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Win32Exception ex)
        {
            return RuntimeHostRunResult.Succeeded(int.MinValue, string.Empty, ex.Message);
        }

        using (process)
        {
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                TryKill(process);
                return RuntimeHostRunResult.Succeeded(int.MinValue, string.Empty, "Runtime host timed out");
            }

            var stdout = stdoutTask.Result;
            var stderr = stderrTask.Result;

            return RuntimeHostRunResult.Succeeded(process.ExitCode, stdout, stderr);
        }
    }

    // dotnet writes MSBuild and NuGet errors to stdout as well as stderr, so a failed step is explained by both (Issue #805).
    private const int DiagnosticTailChars = 8000;

    internal static string DescribeToolchainFailure(ToolchainResult result) =>
        $"exit code {result.ExitCode}\nstdout (last {DiagnosticTailChars} chars):\n{Tail(result.Stdout)}\nstderr (last {DiagnosticTailChars} chars):\n{Tail(result.Stderr)}";

    private static string Tail(string text) =>
        text.Length <= DiagnosticTailChars ? text : text[^DiagnosticTailChars..];

    internal readonly record struct ToolchainResult(ToolchainOutcome Outcome, int ExitCode, string Stdout, string Stderr);

    internal static void TryDeleteDirectory(string? directory)
    {
        if (directory is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; do not throw.
        }
    }


    internal enum ToolchainOutcome
    {
        Success,
        Unavailable,
        TimedOut,
        Failed,
    }
}
