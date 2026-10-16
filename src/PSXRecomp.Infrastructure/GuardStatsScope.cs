using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// Selects the native guard counters (Issue #773, measurement only) for the runs started on the current async context,
/// without touching process-wide environment variables. The ambient switches still work for packaging and manual runs
/// (<c>PSXRECOMP_NATIVE_GUARD_STATS</c> builds the counters, <c>PSX_GUARD_STATS_FILE</c> names the report); a scope takes
/// precedence and is handed to the child process only, so parallel tests and a developer's own settings never interact.
/// </summary>
[Infrastructure]
public static class GuardStatsScope
{
    /// <summary>The environment variable the generated artifact reads for the report path.</summary>
    public const string FileEnvironmentVariable = "PSX_GUARD_STATS_FILE";

    private static readonly AsyncLocal<string?> ScopedPath = new();

    /// <summary>The report path of the scope in effect on this async context, or null when there is none.</summary>
    public static string? CurrentPath => ScopedPath.Value;

    /// <summary>True when the artifact should be generated with guard counters (a scope, or the ambient opt-in).</summary>
    public static bool Enabled =>
        ScopedPath.Value is not null || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PSXRECOMP_NATIVE_GUARD_STATS"));

    /// <summary>Builds guard counters for, and collects their report from, the runs started on this async context. Scopes nest.</summary>
    public static IDisposable Begin(string reportPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(reportPath);
        var previous = ScopedPath.Value;
        ScopedPath.Value = reportPath;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => ScopedPath.Value = previous;
    }
}
