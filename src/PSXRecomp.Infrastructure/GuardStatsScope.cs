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

    /// <summary>The environment variable that sets the periodic dump interval, in dispatch units (scope-only, test hook).</summary>
    public const string PeriodEnvironmentVariable = "PSX_GUARD_STATS_PERIOD";

    /// <summary>The environment variable that ends the child without its final dump after this many dispatch units (scope-only, test hook).</summary>
    public const string AbortAfterEnvironmentVariable = "PSX_GUARD_STATS_ABORT_AFTER";

    private static readonly AsyncLocal<string?> ScopedPath = new();
    private static readonly AsyncLocal<(ulong PeriodUnits, ulong AbortAfterUnits)> ScopedTiming = new();

    /// <summary>The report path of the scope in effect on this async context, or null when there is none.</summary>
    public static string? CurrentPath => ScopedPath.Value;

    /// <summary>True when the artifact should be generated with guard counters (a scope, or the ambient opt-in).</summary>
    public static bool Enabled =>
        ScopedPath.Value is not null || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PSXRECOMP_NATIVE_GUARD_STATS"));

    /// <summary>The periodic interval and abort point of the scope in effect (0 means the artifact's default).</summary>
    public static (ulong PeriodUnits, ulong AbortAfterUnits) CurrentTiming => ScopedTiming.Value;

    /// <summary>
    /// Builds guard counters for, and collects their report from, the runs started on this async context. Scopes nest.
    /// <paramref name="periodUnits"/> and <paramref name="abortAfterUnits"/> are test hooks: 0 keeps the artifact's defaults.
    /// </summary>
    public static IDisposable Begin(string reportPath, ulong periodUnits = 0, ulong abortAfterUnits = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(reportPath);
        var previous = ScopedPath.Value;
        var previousTiming = ScopedTiming.Value;
        ScopedPath.Value = reportPath;
        ScopedTiming.Value = (periodUnits, abortAfterUnits);
        return new Restore(previous, previousTiming);
    }

    private sealed class Restore(string? previousPath, (ulong PeriodUnits, ulong AbortAfterUnits) previousTiming) : IDisposable
    {
        public void Dispose()
        {
            ScopedPath.Value = previousPath;
            ScopedTiming.Value = previousTiming;
        }
    }
}
