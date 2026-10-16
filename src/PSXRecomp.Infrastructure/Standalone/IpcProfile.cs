using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// Opt-in profile of the generated-C to RuntimeHost pipe protocol (Issue #768; measurement only). One slot per message
/// kind the host pump handles. Enabled only when <see cref="EnvironmentVariable"/> names an output path: the bridge then
/// holds an instance and pays two <see cref="Stopwatch.GetTimestamp"/> calls per message and one per write; absent it,
/// the bridge holds null and the hot path pays one null check.
/// </summary>
/// <remarks>
/// Definitions (a message is one line the artifact sent and the host handled):
/// <list type="bullet">
/// <item><c>Count</c>: messages of that kind. Each one is a synchronous round trip for the artifact, which blocks until the reply.</item>
/// <item><c>WaitTicks</c>: time from the previous handler's end to this line being read: the artifact running natively, its
/// formatting and write, the pipe wake-up and the host's ReadLine. It is the part the host cannot speed up.</item>
/// <item><c>HandlerTicks</c>: parse + device work + reply writes, i.e. the host's own time per message (includes
/// <c>DeviceTicks</c> and <c>WriteTicks</c>).</item>
/// <item><c>Writes</c>: host-to-artifact write calls (the host's AutoFlush makes each line one pipe write syscall); <c>BytesOut</c>/<c>BytesIn</c>: payload bytes.</item>
/// </list>
/// </remarks>
[Infrastructure]
public sealed class IpcProfile
{
    public const string EnvironmentVariable = "PSXRECOMP_IPC_PROFILE";

    public enum Kind { TimeReport, FusedMmio, MmioRead, MmioWrite, Transfer, Gte, Cop0, Syscall, Other }

    private const int Buckets = 24; // log2 of microseconds: bucket i holds [2^(i-1), 2^i) us, bucket 0 < 1 us.

    private sealed class Slot
    {
        public ulong Count, BytesIn, BytesOut, Writes, WaitTicks, HandlerTicks, DeviceTicks, WriteTicks, MaxHandlerTicks;
        public readonly ulong[] Handler = new ulong[Buckets];
        public readonly ulong[] Wait = new ulong[Buckets];
    }

    private readonly Slot[] _slots = Enumerable.Range(0, Enum.GetValues<Kind>().Length).Select(static _ => new Slot()).ToArray();
    private long _lastDone;
    private Kind _current;
    private long _handlerStart;

    public static IpcProfile? FromEnvironment() =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)) ? null : new IpcProfile();

    public static string? OutputPathFromEnvironment() => Environment.GetEnvironmentVariable(EnvironmentVariable);

    public static Kind Classify(string line)
    {
        if (line.StartsWith(RecompiledArtifactCodeGen.ProtocolRetiredPrefix, StringComparison.Ordinal))
            return line.IndexOf(' ', RecompiledArtifactCodeGen.ProtocolRetiredPrefix.Length) >= 0 ? Kind.FusedMmio : Kind.TimeReport;
        if (line.StartsWith(RecompiledArtifactCodeGen.ProtocolMmioReadPrefix, StringComparison.Ordinal)) return Kind.MmioRead;
        if (line.StartsWith(RecompiledArtifactCodeGen.ProtocolMmioWritePrefix, StringComparison.Ordinal)) return Kind.MmioWrite;
        if (line.StartsWith(RecompiledArtifactCodeGen.ProtocolGtePrefix, StringComparison.Ordinal)) return Kind.Gte;
        if (line.StartsWith(RecompiledArtifactCodeGen.ProtocolCop0AccessPrefix, StringComparison.Ordinal)) return Kind.Cop0;
        if (line.StartsWith(RecompiledArtifactCodeGen.ProtocolSyscallPrefix, StringComparison.Ordinal)) return Kind.Syscall;
        if (line.StartsWith(RecompiledArtifactCodeGen.ProtocolTransferPrefix, StringComparison.Ordinal)
            || line.StartsWith(RecompiledArtifactCodeGen.ProtocolFallbackQueryCommand + " ", StringComparison.Ordinal)) return Kind.Transfer;
        return Kind.Other;
    }

    public void Begin(string line)
    {
        var now = Stopwatch.GetTimestamp();
        _current = Classify(line);
        var slot = _slots[(int)_current];
        slot.Count++;
        slot.BytesIn += (ulong)line.Length + 1;
        var wait = _lastDone == 0 ? 0 : now - _lastDone;
        slot.WaitTicks += (ulong)wait;
        slot.Wait[Bucket(wait)]++;
        _handlerStart = now;
    }

    public void End()
    {
        var now = Stopwatch.GetTimestamp();
        var slot = _slots[(int)_current];
        var handler = now - _handlerStart;
        slot.HandlerTicks += (ulong)handler;
        if ((ulong)handler > slot.MaxHandlerTicks) slot.MaxHandlerTicks = (ulong)handler;
        slot.Handler[Bucket(handler)]++;
        _lastDone = now;
    }

    public void Wrote(int bytes, long ticks)
    {
        var slot = _slots[(int)_current];
        slot.Writes++;
        slot.BytesOut += (ulong)bytes;
        slot.WriteTicks += (ulong)ticks;
    }

    public void Device(long ticks) => _slots[(int)_current].DeviceTicks += (ulong)ticks;

    private static int Bucket(long ticks)
    {
        var micros = (ulong)(ticks * 1_000_000L / Stopwatch.Frequency);
        return micros == 0 ? 0 : Math.Min(Buckets - 1, 64 - System.Numerics.BitOperations.LeadingZeroCount(micros));
    }

    private static double Us(ulong ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;

    private static double Quantile(ulong[] histogram, double q)
    {
        var total = (ulong)histogram.Sum(static v => (decimal)v);
        if (total == 0) return 0;
        var target = (ulong)Math.Ceiling(total * q);
        ulong seen = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            seen += histogram[i];
            if (seen >= target) return i == 0 ? 1 : Math.Pow(2, i); // upper bound of the bucket, microseconds
        }

        return Math.Pow(2, histogram.Length);
    }

    /// <summary>Writes the profile once the pump ended (single writer thread has finished); best-effort, never throws.</summary>
    public void TryWrite(string path, TextWriter error, IReadOnlyDictionary<string, object?> run)
    {
        try
        {
            File.WriteAllText(path, ToJson(run));
        }
        catch (Exception ex)
        {
            error.WriteLine($"runtime host: could not write IPC profile to {path}: {ex.Message}");
        }
    }

    public string ToJson(IReadOnlyDictionary<string, object?> run)
    {
        var kinds = Enum.GetValues<Kind>().Select(k =>
        {
            var s = _slots[(int)k];
            return new
            {
                kind = k.ToString(),
                count = s.Count,
                writes = s.Writes,
                bytesIn = s.BytesIn,
                bytesOut = s.BytesOut,
                handlerSeconds = Us(s.HandlerTicks) / 1e6,
                waitSeconds = Us(s.WaitTicks) / 1e6,
                deviceSeconds = Us(s.DeviceTicks) / 1e6,
                writeSeconds = Us(s.WriteTicks) / 1e6,
                handlerMeanUs = s.Count == 0 ? 0 : Us(s.HandlerTicks) / s.Count,
                handlerMaxUs = Us(s.MaxHandlerTicks),
                handlerP50Us = Quantile(s.Handler, 0.50),
                handlerP95Us = Quantile(s.Handler, 0.95),
                handlerP99Us = Quantile(s.Handler, 0.99),
                waitMeanUs = s.Count == 0 ? 0 : Us(s.WaitTicks) / s.Count,
                waitP50Us = Quantile(s.Wait, 0.50),
                waitP95Us = Quantile(s.Wait, 0.95),
                waitP99Us = Quantile(s.Wait, 0.99),
            };
        }).Where(static k => k.count != 0).ToArray();
        return JsonSerializer.Serialize(new
        {
            run,
            note = "p50/p95/p99 are upper bounds of power-of-two microsecond buckets.",
            totalMessages = kinds.Sum(static k => (decimal)k.count),
            kinds,
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
