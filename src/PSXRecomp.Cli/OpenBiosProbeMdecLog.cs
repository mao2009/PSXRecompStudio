using System.Globalization;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Runtime.Mdec;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// Writes MDEC port events as JSON Lines for one probe run (Issue #774, B-7). Diagnostic only: the probe owns the file, and
/// only events whose scheduler time lies in [From, To] are written, so a window keeps the log small.
/// </summary>
/// <remarks>
/// The log is called from inside the guest's MDEC port access, so it must never throw into that path. A write or flush
/// failure is contained here: the log stops writing, <see cref="FailureMessage"/> records why, and the trace is incomplete.
/// The guest run continues unchanged.
/// </remarks>
[Infrastructure]
internal sealed class OpenBiosProbeMdecLog : IDisposable
{
    private readonly TextWriter _writer;
    private readonly ulong _from;
    private readonly ulong _to;
    private ulong _sequence;

    public OpenBiosProbeMdecLog(string path, ulong from, ulong to)
        : this(new StreamWriter(path, append: false), from, to)
    {
    }

    internal OpenBiosProbeMdecLog(TextWriter writer, ulong from, ulong to)
    {
        _writer = writer;
        _from = from;
        _to = to;
    }

    /// <summary>Why the trace is incomplete, or null while every write has succeeded.</summary>
    public string? FailureMessage { get; private set; }

    /// <summary>
    /// Writes one event. <paramref name="pc"/> is set only when the caller knows the instruction that caused the event (the
    /// interpreter); a host run passes null, because native code does not report the PC of a port access.
    /// </summary>
    public void Write(string engine, MdecDevice.MdecEvent e, string? pc, string? lastFallbackFetchPc)
    {
        var sequence = _sequence++;
        if (FailureMessage is not null) return;
        if (e.Cycles < _from || e.Cycles > _to) return;
        var pcField = pc is null ? "null" : $"\"{pc}\"";
        var fallbackField = lastFallbackFetchPc is null ? "null" : $"\"{lastFallbackFetchPc}\"";
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{{\"engine\":\"{engine}\",\"seq\":{sequence},\"g\":{e.Cycles},\"kind\":\"{e.Kind}\",\"value\":\"0x{e.Value:X8}\"," +
            $"\"pc\":{pcField},\"lastFallbackFetchPc\":{fallbackField},\"command\":\"0x{e.Command:X8}\",\"remaining\":{e.Remaining}," +
            $"\"control\":\"0x{e.Control:X8}\",\"fifoWords\":{e.FifoWords},\"busyBefore\":{Bool(e.BusyBefore)},\"busyAfter\":{Bool(e.BusyAfter)}}}");
        try
        {
            _writer.WriteLine(line);
        }
        catch (Exception ex) when (IsOutputFailure(ex))
        {
            Fail(ex);
        }
    }

    public void Dispose()
    {
        try
        {
            _writer.Dispose();
        }
        catch (Exception ex) when (IsOutputFailure(ex))
        {
            Fail(ex);
        }
    }

    private void Fail(Exception ex) => FailureMessage ??= $"{ex.GetType().Name}: {ex.Message}";

    private static bool IsOutputFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or ObjectDisposedException;

    private static string Bool(bool value) => value ? "true" : "false";
}
