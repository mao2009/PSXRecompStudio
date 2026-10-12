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

        var args = new[]
        {
            "publish", hostProject,
            "-c", "Release",
            "-r", rid,
            "-o", Path.GetFullPath(targetDirectory),
            "--no-restore",
        };

        if (selfContained)
        {
            args = args.Concat(new[]
            {
                "-p:SelfContained=true",
                "-p:PublishSingleFile=true",
            }).ToArray();
        }
        else
        {
            // Framework-dependent deploy: bundle the apphost + app + runtime files.
            // The end-user host must have the matching .NET runtime installed.
            args = args.Concat(new[] { "-p:SelfContained=false" }).ToArray();
        }

        var result = RunToolchain("dotnet", args, timeout);
        if (result.Outcome != ToolchainOutcome.Success)
        {
            throw new RuntimeHostProvisionFailed(
                $"{result.Outcome} while provisioning the runtime host ({string.Join(" ", args)}):\n{result.Stderr}",
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
            dir = Path.GetDirectoryName(dir);
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

    private static ToolchainResult RunToolchain(
        string fileName, IReadOnlyList<string> arguments, int timeoutMs, int cleanupMs = ToolchainCleanupMs)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            Arguments = string.Join(" ", arguments),
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
                : new ToolchainResult(ToolchainOutcome.Failed, process.ExitCode, ReadStreamWithinCleanupBudget(stderrTask, cleanupMs));
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

    internal readonly record struct ToolchainResult(ToolchainOutcome Outcome, int ExitCode, string Stderr);

    internal enum ToolchainOutcome
    {
        Success,
        Unavailable,
        TimedOut,
        Failed,
    }
}
