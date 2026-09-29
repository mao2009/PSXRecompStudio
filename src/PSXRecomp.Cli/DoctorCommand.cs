using System.Runtime.InteropServices;
using PSXRecomp.Architecture;
using PSXRecomp.Core;
using PSXRecomp.Core.Execution;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// <c>psxrecomp doctor</c> (Issue #623): a deterministic, read-only preflight of the
/// environment the <c>recompile</c>/<c>run</c> pipeline needs — supported OS/architecture,
/// the .NET runtime, the native <c>PSXRecomp.Native</c> library, and the host C compiler
/// used by <see cref="GeneratedHostBuildService"/>. It reuses those production
/// detection paths and adds no environment framework. Exit codes follow the
/// #459 contract: <c>0</c> all ok; <c>1</c> a tooling check failed; <c>2</c> the
/// OS/architecture is unsupported (blocked).
/// </summary>
[Infrastructure]
internal static class DoctorCommand
{
    public const string Kind = "doctor";
    public const string Ok = "ok";
    public const string Failed = "failed";
    public const string Unsupported = "unsupported";

    private const int CompilerProbeTimeoutMs = 10000;

    [Infrastructure]
    public sealed record Check(string Id, string Status, string? ErrorCode);

    [Infrastructure]
    public sealed record Report(string Kind, bool Success, string Status, IReadOnlyList<Check> Checks);

    /// <summary>The probed facts, injectable so tests can simulate a missing toolchain or OS.</summary>
    internal sealed record Probes(
        string OsPlatform,
        string Architecture,
        string DotNetDescription,
        string CompilerExecutable,
        Func<bool> NativeRuntimeAvailable);

    internal static Probes HostProbes() => new(
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "macOS" : "Unknown",
        RuntimeInformation.ProcessArchitecture.ToString(),
        RuntimeInformation.FrameworkDescription,
        GeneratedHostBuildService.DefaultCompiler,
        ProbeNativeRuntime);

    internal static int Run(bool json, TextWriter standardOutput, Probes probes)
    {
        // Validated release matrix (linux-x64, win-x64, osx-arm64); not the OS x arch product.
        var supported = (probes.OsPlatform, probes.Architecture) is ("Windows", "X64") or ("Linux", "X64") or ("macOS", "Arm64");
        var checks = new[]
        {
            new Check("os", supported ? Ok : Unsupported, supported ? null : "UNSUPPORTED_PLATFORM"),
            new Check("dotnet", Ok, null),
            Probe("native-runtime", probes.NativeRuntimeAvailable(), "NATIVE_RUNTIME_UNAVAILABLE"),
            Probe("c-compiler", ProbeCompiler(probes.CompilerExecutable), "TOOLCHAIN_UNAVAILABLE"),
        };

        var status = checks.Any(c => c.Status == Unsupported) ? Unsupported
            : checks.Any(c => c.Status == Failed) ? Failed
            : Ok;

        if (json)
        {
            standardOutput.WriteLine(CliJson.Serialize(new Report(Kind, status == Ok, status, checks)));
        }
        else
        {
            var detail = new Dictionary<string, string>
            {
                ["os"] = $"{probes.OsPlatform} {probes.Architecture}",
                ["dotnet"] = probes.DotNetDescription,
                ["native-runtime"] = "PSXRecomp.Native",
                ["c-compiler"] = probes.CompilerExecutable,
            };
            var labels = new Dictionary<string, string>
            {
                ["os"] = "OS", ["dotnet"] = ".NET", ["native-runtime"] = "Native runtime", ["c-compiler"] = "C compiler",
            };
            foreach (var check in checks)
            {
                standardOutput.WriteLine($"{labels[check.Id],-15}{check.Status.ToUpperInvariant(),-12}{detail[check.Id]}");
            }
        }

        return status switch
        {
            Ok => RecompiledArtifactExitCode.Success,
            Unsupported => RecompiledArtifactExitCode.Blocked,
            _ => RecompiledArtifactExitCode.Failure,
        };
    }

    private static Check Probe(string id, bool available, string errorCode) =>
        new(id, available ? Ok : Failed, available ? null : errorCode);

    private static bool ProbeCompiler(string compiler) =>
        GeneratedHostBuildService.RunToolchain(compiler, ["--version"], CompilerProbeTimeoutMs).Outcome
            == GeneratedHostBuildService.ToolchainOutcome.Success;

    // Creating (and disposing) a core exercises the same P/Invoke resolution the runtime uses.
    private static bool ProbeNativeRuntime()
    {
        try
        {
            using var core = new PSXCoreWrapper();
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or InvalidOperationException)
        {
            return false;
        }
    }
}
