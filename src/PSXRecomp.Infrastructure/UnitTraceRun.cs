using System.Globalization;
using System.Text;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// The opt-in switch for the unit retirement trace of Issue #774. Inside a <see cref="Begin"/> scope every generated host
/// is built as a trace build and writes its unit trace to the given path. Outside any scope the
/// <c>PSX_UNIT_TRACE_FILE</c> environment variable is the command-line route. Off, nothing is generated, launched or
/// written differently.
/// </summary>
[Infrastructure]
public static class UnitTraceScope
{
    /// <summary>The environment variable naming the native unit-trace file. Its presence also selects a trace build.</summary>
    public const string EnvironmentVariable = "PSX_UNIT_TRACE_FILE";

    private static readonly AsyncLocal<string?> ScopedPath = new();

    /// <summary>The native unit-trace path in effect, or null when tracing is off.</summary>
    public static string? CurrentPath =>
        ScopedPath.Value ?? (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvironmentVariable))
            ? null
            : Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>
    /// Traces the runs started on this async context to <paramref name="tracePath"/>. The fallback trace is written beside
    /// it as <c>tracePath + ".fallback"</c>. Scopes nest; disposing restores the previous path.
    /// </summary>
    public static IDisposable Begin(string tracePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(tracePath);
        var previous = ScopedPath.Value;
        ScopedPath.Value = tracePath;
        return new Restore(previous);
    }

    /// <summary>The fallback-trace path that accompanies <paramref name="tracePath"/>.</summary>
    public static string FallbackPathFor(string tracePath) => tracePath + ".fallback";

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => ScopedPath.Value = previous;
    }
}

/// <summary>
/// One run's parent-side trace: a session id shared with the child, and the fallback trace file. Each transfer the child
/// sends is recorded once, in the order received, so the handoff numbers match the child's handoff markers.
/// </summary>
[Infrastructure]
internal sealed class UnitTraceRun : IDisposable
{
    private const int SchemaVersion = 1;
    private const ulong Cap = 64UL * 1024 * 1024;
    private const ulong Reserve = 512;

    private readonly StreamWriter? _writer;
    private ulong _seq;
    private ulong _bytes;
    private ulong _handoffs;
    private bool _truncated;
    private bool _failed;
    private bool _closed;

    private UnitTraceRun(string sessionId, string childTracePath, StreamWriter? writer)
    {
        SessionId = sessionId;
        ChildTracePath = childTracePath;
        _writer = writer;
    }

    /// <summary>The session id the parent generated and gave the child in <c>PSX_TRACE_SESSION</c>.</summary>
    public string SessionId { get; }

    /// <summary>The native unit-trace path the child is told to write.</summary>
    public string ChildTracePath { get; }

    /// <summary>Starts a run trace when tracing is on; null when it is off.</summary>
    public static UnitTraceRun? TryStart()
    {
        var path = UnitTraceScope.CurrentPath;
        if (path is null)
            return null;

        var session = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        StreamWriter? writer = null;
        try
        {
            writer = new StreamWriter(UnitTraceScope.FallbackPathFor(path), append: false, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (IOException)
        {
            // The fallback trace cannot be written: the run goes on untraced on the parent side. The missing file makes the
            // join report the run as not comparable, rather than as a complete one.
        }
        catch (UnauthorizedAccessException)
        {
        }

        var run = new UnitTraceRun(session, path, writer);
        run.WriteLine($"{{\"v\":{SchemaVersion},\"kind\":\"header\",\"session\":\"{session}\",\"stream\":\"fallback\",\"cap\":{Cap}}}");
        return run;
    }

    /// <summary>The environment the child process needs to write the same run's native trace.</summary>
    public IReadOnlyDictionary<string, string> ChildEnvironment() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [UnitTraceScope.EnvironmentVariable] = ChildTracePath,
        ["PSX_TRACE_SESSION"] = SessionId,
    };

    /// <summary>
    /// Records one transfer from the child. <paramref name="transferPc"/> is the transfer's guest PC; <paramref name="segment"/>
    /// is the fallback segment that transfer ran, or null when the host served it without running guest instructions.
    /// </summary>
    public void RecordTransfer(ulong transferPc, ArtifactFallbackSession.SegmentRecord? segment)
    {
        _handoffs++;
        var pc = $"0x{transferPc:X8}";
        var line = segment is null
            ? $"{{\"v\":{SchemaVersion},\"kind\":\"transfer\",\"seq\":{_seq},\"handoff\":{_handoffs},\"retired\":0,\"status\":\"host\",\"start_pc\":\"{pc}\",\"end_pc\":\"{pc}\"}}"
            : $"{{\"v\":{SchemaVersion},\"kind\":\"fallback\",\"seq\":{_seq},\"handoff\":{_handoffs},\"retired\":{segment.Retired},\"status\":\"{segment.Status}\",\"start_pc\":\"{pc}\",\"end_pc\":\"0x{segment.EndPc:X8}\"{(segment.StopCode is null ? string.Empty : $",\"stop\":\"{segment.StopCode}\"")}{StateField("entry", segment.Entry)}{StateField("exit", segment.Exit)}}}";
        WriteRecord(line);
    }

    // LOCAL ONLY: these are register values (Decision A, PR #839); see docs/development/unit-trace.md.
    /// <summary>
    /// The optional CPU state of a fallback record (Issue #774, B-6b): <c>,"name":{...}</c>, or nothing when the state is absent.
    /// Written in hex, one value per register, in the order gpr[0..31], then hi, lo, sr, cause and epc.
    /// </summary>
    private static string StateField(string name, FallbackCpuState? state)
    {
        if (state is null) return string.Empty;
        var gpr = string.Join(",", state.Gpr.Select(static value => $"\"0x{value:X8}\""));
        return $",\"{name}\":{{\"pc\":\"0x{state.Pc:X8}\",\"gpr\":[{gpr}],\"hi\":\"0x{state.Hi:X8}\",\"lo\":\"0x{state.Lo:X8}\",\"sr\":\"0x{state.Sr:X8}\",\"cause\":\"0x{state.Cause:X8}\",\"epc\":\"0x{state.Epc:X8}\"}}";
    }

    public void Dispose()
    {
        if (_closed)
            return;
        _closed = true;
        if (_writer is null || _failed)
            return;
        try
        {
            _writer.WriteLine(_truncated
                ? $"{{\"v\":{SchemaVersion},\"kind\":\"end\",\"seq\":{_seq},\"complete\":false,\"reason\":\"output_cap\"}}"
                : $"{{\"v\":{SchemaVersion},\"kind\":\"end\",\"seq\":{_seq},\"complete\":true}}");
            _writer.Dispose();
        }
        catch (IOException)
        {
        }
    }

    private void WriteRecord(string line)
    {
        if (_writer is null || _truncated || _failed)
            return;
        var length = (ulong)Encoding.UTF8.GetByteCount(line) + 1;
        if (_bytes + length + Reserve > Cap)
        {
            _truncated = true;
            WriteLine($"{{\"v\":{SchemaVersion},\"kind\":\"truncated\",\"seq\":{_seq},\"reason\":\"output_cap\"}}");
            return;
        }

        WriteLine(line);
        _bytes += length;
        _seq++;
    }

    private void WriteLine(string line)
    {
        if (_writer is null || _failed)
            return;
        try
        {
            _writer.WriteLine(line);
        }
        catch (IOException)
        {
            // A failed write stops the parent trace quietly; the run itself is unaffected. No end line follows, so the
            // join reports the trace as incomplete.
            _failed = true;
            _writer.Dispose();
        }
    }
}
