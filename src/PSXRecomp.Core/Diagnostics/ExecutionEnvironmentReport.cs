using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Diagnostics;

/// <summary>
/// Privacy-safe host/toolchain metadata for Issue #457's <c>environment.json</c>.
/// The schema intentionally excludes machine identity, accounts, paths, network
/// data, environment variables, and arbitrary host files.
/// </summary>
[Domain]
public sealed record ExecutionEnvironmentReport
{
    /// <summary>The stable schema identity for the first Alpha environment contract.</summary>
    public const string CurrentSchema = "psxrecomp.execution-environment.v1";

    private ExecutionEnvironmentReport(
        string osPlatform,
        string osVersion,
        string osArchitecture,
        string processArchitecture,
        string dotNetVersion,
        string? runtimeIdentifier,
        string? compilerVersion,
        string? targetArchitecture)
    {
        Schema = CurrentSchema;
        OsPlatform = RequireSingleLine(osPlatform, nameof(osPlatform));
        OsVersion = RequireSingleLine(osVersion, nameof(osVersion));
        OsArchitecture = RequireSingleLine(osArchitecture, nameof(osArchitecture));
        ProcessArchitecture = RequireSingleLine(processArchitecture, nameof(processArchitecture));
        DotNetVersion = RequireSingleLine(dotNetVersion, nameof(dotNetVersion));
        RuntimeIdentifier = OptionalSingleLine(runtimeIdentifier, nameof(runtimeIdentifier));
        CompilerVersion = OptionalSingleLine(compilerVersion, nameof(compilerVersion));
        TargetArchitecture = OptionalSingleLine(targetArchitecture, nameof(targetArchitecture));
    }

    public string Schema { get; }
    public string OsPlatform { get; }
    public string OsVersion { get; }
    public string OsArchitecture { get; }
    public string ProcessArchitecture { get; }
    public string DotNetVersion { get; }
    public string? RuntimeIdentifier { get; }
    public string? CompilerVersion { get; }
    public string? TargetArchitecture { get; }

    /// <summary>
    /// Creates one environment report from already-selected reproduction facts.
    /// Host probing is deliberately outside this contract.
    /// </summary>
    public static ExecutionEnvironmentReport Create(
        string osPlatform,
        string osVersion,
        string osArchitecture,
        string processArchitecture,
        string dotNetVersion,
        string? runtimeIdentifier = null,
        string? compilerVersion = null,
        string? targetArchitecture = null) =>
        new(
            osPlatform,
            osVersion,
            osArchitecture,
            processArchitecture,
            dotNetVersion,
            runtimeIdentifier,
            compilerVersion,
            targetArchitecture);

    private static string RequireSingleLine(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return ValidateSingleLine(value, parameterName);
    }

    private static string? OptionalSingleLine(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? null : ValidateSingleLine(value, parameterName);

    private static string ValidateSingleLine(string value, string parameterName)
    {
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("Environment metadata must be a single line.", parameterName);
        }

        return value.Trim();
    }
}
