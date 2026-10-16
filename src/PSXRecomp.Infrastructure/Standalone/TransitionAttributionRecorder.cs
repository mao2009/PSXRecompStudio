using System.Globalization;
using System.Text.Json;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// Opt-in aggregation of artifact-to-interpreter handoffs by (entry PC, cause, previous return PC, exit PC)
/// (Issue #773 / PR #792 measurement). Counts only: no instruction words, no per-event stream. Bounded: at most
/// <see cref="MaxKeys"/> distinct keys and <see cref="MaxDetailEvents"/> detailed events; the rest is counted as dropped.
/// Enabled by the <see cref="EnvironmentVariable"/> path; absent, nothing is installed and no cost is paid.
/// </summary>
[Infrastructure]
public sealed class TransitionAttributionRecorder
{
    public const string EnvironmentVariable = "PSXRECOMP_TRANSITION_ATTRIBUTION";
    public const int MaxKeys = 4096;
    public const int MaxDetailEvents = 64;

    public const string Unregistered = "unregistered";
    public const string GuardMismatch = "guard-mismatch";
    public const string GuardMatchedAnomaly = "guard-matched-anomaly";
    public const string NoAttribution = "no-attribution";

    private readonly object _lock = new();
    private readonly Dictionary<(uint Entry, string Cause, uint Prev, uint Exit, bool Returned), (ulong Entries, ulong Instructions)> _keys = [];
    private readonly List<object> _detail = [];
    private ulong _total;
    private ulong _droppedKeys;
    private ulong _droppedDetail;

    public static TransitionAttributionRecorder? FromEnvironment() =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)) ? null : new TransitionAttributionRecorder();

    public static string? OutputPathFromEnvironment() => Environment.GetEnvironmentVariable(EnvironmentVariable);

    public void Observe(MixedFallbackTransition t)
    {
        var a = t.Attribution;
        var cause = a is null ? NoAttribution
            : a.MatchedVersion >= 0 ? GuardMatchedAnomaly
            : a.RegisteredVersions == 0 ? Unregistered
            : GuardMismatch;
        var key = (t.EntryPc, cause, a?.PreviousReturnPc ?? uint.MaxValue, t.ExitPc, t.Status == FallbackSegmentStatus.Returned);
        lock (_lock)
        {
            _total++;
            if (_keys.TryGetValue(key, out var v)) _keys[key] = (v.Entries + 1, v.Instructions + t.RetiredInstructions);
            else if (_keys.Count < MaxKeys) _keys[key] = (1, t.RetiredInstructions);
            else _droppedKeys++;

            if (_detail.Count < MaxDetailEvents)
            {
                _detail.Add(new
                {
                    seq = _total,
                    entry = Hex(t.EntryPc),
                    cause,
                    registeredVersions = a?.RegisteredVersions,
                    matchedVersion = a?.MatchedVersion,
                    previousReturn = a?.PreviousReturnPc is { } p ? Hex(p) : null,
                    exit = Hex(t.ExitPc),
                    status = t.Status.ToString(),
                    retired = t.RetiredInstructions,
                    indirect = t.Indirect,
                });
            }
            else _droppedDetail++;
        }
    }

    /// <summary>Writes the summary; best-effort, never throws.</summary>
    public void TryWrite(string path, TextWriter error, IReadOnlyDictionary<string, object?> run)
    {
        try
        {
            File.WriteAllText(path, ToJson(run));
        }
        catch (Exception ex)
        {
            error.WriteLine($"runtime host: could not write transition attribution to {path}: {ex.Message}");
        }
    }

    public string ToJson(IReadOnlyDictionary<string, object?> run)
    {
        {
            object doc;
            lock (_lock)
            {
                doc = new
                {
                    run,
                    totalTransitions = _total,
                    distinctKeys = _keys.Count,
                    droppedKeys = _droppedKeys,
                    droppedDetailEvents = _droppedDetail,
                    byCause = _keys.GroupBy(static k => k.Key.Cause)
                        .Select(static g => new { cause = g.Key, entries = (ulong)g.Sum(static x => (decimal)x.Value.Entries), instructions = (ulong)g.Sum(static x => (decimal)x.Value.Instructions) })
                        .OrderBy(static x => x.cause, StringComparer.Ordinal).ToArray(),
                    byEntry = _keys.GroupBy(static k => k.Key.Entry)
                        .Select(static g => new
                        {
                            entry = Hex(g.Key),
                            entries = (ulong)g.Sum(static x => (decimal)x.Value.Entries),
                            instructions = (ulong)g.Sum(static x => (decimal)x.Value.Instructions),
                            causes = g.GroupBy(static x => x.Key.Cause).ToDictionary(static c => c.Key, static c => (ulong)c.Sum(static x => (decimal)x.Value.Entries)),
                        })
                        .OrderByDescending(static x => x.entries).Take(64).ToArray(),
                    keys = _keys.OrderByDescending(static k => k.Value.Entries).Take(256)
                        .Select(static k => new
                        {
                            entry = Hex(k.Key.Entry),
                            cause = k.Key.Cause,
                            previousReturn = k.Key.Prev == uint.MaxValue ? null : Hex(k.Key.Prev),
                            exit = Hex(k.Key.Exit),
                            returned = k.Key.Returned,
                            entries = k.Value.Entries,
                            instructions = k.Value.Instructions,
                        }).ToArray(),
                    firstEvents = _detail.ToArray(),
                };
            }

            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    private static string Hex(uint v) => "0x" + v.ToString("X8", CultureInfo.InvariantCulture);
}
