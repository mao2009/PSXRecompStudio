using System.Globalization;
using System.Text;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>The state of a guest TTY sink. Anything other than <see cref="Open"/> discards all later input.</summary>
public enum GuestOutputState
{
    /// <summary>Output is accepted and checked.</summary>
    Open,

    /// <summary>A forbidden pattern was found. Its notice was written; everything after it is discarded until the end of the stream.</summary>
    Suppressed,

    /// <summary>More than <see cref="PublicGuestOutputSink.MaxEscapeBacklog"/> escapes are undecided. The stream ends here, fail-closed.</summary>
    Terminated,

    /// <summary>Writing to the output failed. Undecided text was dropped, and the stream is refused from then on.</summary>
    Failed,

    /// <summary>The stream ended normally: every checked byte was written, and nothing held was published unchecked.</summary>
    Completed,
}

/// <summary>
/// The guest TTY sink of the public stdout, and the one gate for both the HLE and the native path. Guest bytes are published as
/// text: a printable byte is written as itself and every other byte as a <c>\xNN</c> escape, in order.
/// </summary>
/// <remarks>
/// <para>
/// Three views are checked. The logical view is the printable characters with the escapes removed, so a path split by a control byte
/// is still caught. The boundary view is the same with each escape written as <c>!</c>, a word boundary, so a word-start rule sees a
/// control byte as a boundary. The digest view is the published text with the escape digits kept, so a digest-shaped run made of escape
/// digits (the <c>0A</c> of <c>\x0A</c>) is caught.
/// </para>
/// <para>
/// Per character. Each appended character is checked at once against the last <see cref="TailLength"/> units of each view. Every rule
/// match is at most <see cref="PendingLength"/> units long, so a match that ends at the new character lies inside that tail, and it is
/// refused before anything after it is written. Nothing is committed without that check.
/// </para>
/// <para>
/// Undecided tail. Writing stops short of the last part of the held text: the smallest tail that holds at least
/// <see cref="PendingLength"/> logical and at least <see cref="PendingLength"/> published characters. <see cref="Flush"/> keeps it, so a
/// match split across a flush is still checked when it completes.
/// </para>
/// <para>
/// Flush and completion. <see cref="Flush"/> is a mid-stream request: it checks the whole held text and writes everything except the
/// undecided tail; the stream stays open. <see cref="Complete"/> is the end of the stream: it checks and writes everything, then closes
/// the sink. <see cref="Dispose"/> completes.
/// </para>
/// <para>
/// Rule hit. Everything held is dropped and a fixed notice names the rule. The stream is then <see cref="GuestOutputState.Suppressed"/>
/// until its end. This is the stated trade-off: a path can contain spaces and control bytes, so no boundary that ends its remainder can be
/// verified.
/// </para>
/// <para>
/// Escape backlog. When more than <see cref="MaxEscapeBacklog"/> escapes are held, the held text is checked and the decided part is
/// written. If escapes remain above the limit (the undecided tail holds them), no resync point can be proven, so the stream ends in
/// <see cref="GuestOutputState.Terminated"/> with a fixed notice.
/// </para>
/// <para>
/// Predecessor context. Each view keeps the last <see cref="PendingLength"/> units it has written, so a word-start or separator test at the
/// start of the held text sees its real predecessor. The <c>cdrom:\</c> device token is exempt only at a word start.
/// </para>
/// <para>
/// Failure. A failed write marks the sink <see cref="GuestOutputState.Failed"/> before anything else happens, drops the held text, and
/// refuses all later output. The exception stays in <see cref="InternalFailure"/> for private diagnostics only; it is never published.
/// </para>
/// </remarks>
[Infrastructure]
public sealed class PublicGuestOutputSink : IRuntimeOutputSink, IDisposable
{
    /// <summary>Logical printable characters held back while the window is written.</summary>
    public const int HoldbackLength = 256;

    /// <summary>Escapes that may stay held before the stream ends.</summary>
    public const int MaxEscapeBacklog = 1024;

    /// <summary>The logical and the published length that an undecided tail keeps. A rule match is never longer than this.</summary>
    public const int PendingLength = 32;

    /// <summary>The logical characters written per check. Equal to the holdback, so one check covers a batch and its holdback.</summary>
    public const int BatchLength = HoldbackLength;

    private const string LimitReason = "the run exceeds the public buffer limit";
    private const string LimitNotice = "[guest output withheld: the run exceeds the public buffer limit]";
    private const char EscapeMarker = '!';

    private readonly object _lock = new();
    private readonly TextWriter _out;
    private readonly List<Token> _queue = new();
    private int _printable;
    private int _escapes;
    private string _logicalHistory = string.Empty;
    private string _boundaryHistory = string.Empty;
    private GuestOutputState _state = GuestOutputState.Open;
    private string? _refusalRule;
    private Exception? _internalFailure;

    public PublicGuestOutputSink(TextWriter output)
    {
        _out = output ?? throw new ArgumentNullException(nameof(output));
    }

    private readonly record struct Token(bool IsPrintable, string Text);

    /// <summary>The current state. Only an <see cref="GuestOutputState.Open"/> sink accepts input.</summary>
    public GuestOutputState State
    {
        get { lock (_lock) { return _state; } }
        private set { lock (_lock) { _state = value; } }
    }

    /// <summary>The name of the rule that withheld output, never the text. Null when nothing was withheld.</summary>
    public string? RefusalRule
    {
        get { lock (_lock) { return _refusalRule; } }
        private set { lock (_lock) { _refusalRule = value; } }
    }

    /// <summary>The internal failure, kept for private diagnostics. It is never published.</summary>
    public Exception? InternalFailure
    {
        get { lock (_lock) { return _internalFailure; } }
        private set { lock (_lock) { _internalFailure = value; } }
    }

    public void WriteByte(byte value)
    {
        lock (_lock)
        {
            if (State != GuestOutputState.Open)
            {
                return;
            }

            if (value >= 32 && value < 127)
            {
                AppendPrintable((char)value);
                return;
            }

            AppendEscape("\\x" + value.ToString("X2", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Mid-stream request to write what is decided. The undecided tail stays held and the stream stays open.
    /// </summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (State != GuestOutputState.Open)
            {
                return;
            }

            if (Refusal() is { } rule)
            {
                Withhold(rule, GuestOutputState.Suppressed);
                return;
            }

            CommitTokens(PendingBoundary());
            FlushWriter();
        }
    }

    /// <summary>
    /// End of the stream: checks and writes everything held, then closes the sink. Safe to call more than once. Returns the state the
    /// stream ended in, so the caller can report a withheld or failed stream by its fixed code.
    /// </summary>
    public GuestOutputState Complete()
    {
        lock (_lock)
        {
            if (State != GuestOutputState.Open)
            {
                return State;
            }

            if (Refusal() is { } rule)
            {
                Withhold(rule, GuestOutputState.Suppressed);
                return State;
            }

            CommitTokens(_queue.Count);
            FlushWriter();
            if (State == GuestOutputState.Open)
            {
                State = GuestOutputState.Completed;
            }

            return State;
        }
    }

    public void Dispose() => Complete();

    private void AppendPrintable(char c)
    {
        if (_queue.Count > 0 && _queue[^1].IsPrintable)
        {
            _queue[^1] = new Token(true, _queue[^1].Text + c);
        }
        else
        {
            _queue.Add(new Token(true, c.ToString()));
        }

        _printable++;
        Settle();
    }

    private void AppendEscape(string escape)
    {
        _queue.Add(new Token(false, escape));
        _escapes++;
        if (_escapes > MaxEscapeBacklog)
        {
            EscapeBacklogExceeded();
        }
    }

    /// <summary>
    /// Checks and writes the oldest characters in batches. A batch is written only after the full held text has been checked, so every
    /// character that is written was inside a checked text. Any match that overlaps the batch is at most <see cref="PendingLength"/> units
    /// long, and the <see cref="HoldbackLength"/> characters that stay held after the batch cover its end, so it is inside the checked text.
    /// The batch equals the holdback, so the cost of the check per character stays constant.
    /// </summary>
    private void Settle()
    {
        if (_printable <= HoldbackLength + BatchLength)
        {
            return;
        }

        if (Refusal() is { } rule)
        {
            Withhold(rule, GuestOutputState.Suppressed);
            return;
        }

        CommitPrintable(_printable - HoldbackLength);
    }

    private void EscapeBacklogExceeded()
    {
        if (Refusal() is { } rule)
        {
            Withhold(rule, GuestOutputState.Suppressed);
            return;
        }

        CommitTokens(PendingBoundary());
        if (_escapes > MaxEscapeBacklog)
        {
            Withhold(LimitReason, GuestOutputState.Terminated, LimitNotice);
        }
    }

    /// <summary>
    /// The full check on the whole held text, in all three views. Used by flush, completion and the escape backlog.
    /// </summary>
    private string? Refusal()
    {
        return PublicLog.SensitiveTextRefusal(_logicalHistory + LogicalText())
            ?? PublicLog.SensitiveTextRefusal(_boundaryHistory + BoundaryText())
            ?? PublicLog.DigestRefusal(PublishedText());
    }

    private string LogicalText()
    {
        var builder = new StringBuilder(_printable);
        foreach (var token in _queue)
        {
            if (token.IsPrintable)
            {
                builder.Append(token.Text);
            }
        }

        return builder.ToString();
    }

    /// <summary>The logical text with each escape as "!": a control byte is a word boundary for the word-start rules.</summary>
    private string BoundaryText()
    {
        var builder = new StringBuilder();
        foreach (var token in _queue)
        {
            builder.Append(token.IsPrintable ? token.Text : EscapeMarker.ToString());
        }

        return builder.ToString();
    }

    /// <summary>
    /// The published text with each escape's own backslash replaced by "!". The escape digits stay, so a hex run made of escape digits is
    /// still seen, while the escape's backslash cannot form a drive or rooted-path match.
    /// </summary>
    private string PublishedText()
    {
        var builder = new StringBuilder();
        foreach (var token in _queue)
        {
            builder.Append(token.IsPrintable ? token.Text : token.Text.Replace('\\', '!'));
        }

        return builder.ToString();
    }

    /// <summary>
    /// The index of the first token of the undecided tail. The tail is the smallest suffix that holds at least
    /// <see cref="PendingLength"/> logical and at least <see cref="PendingLength"/> published characters. A printable token that must be
    /// split is split, so the tail starts exactly where it must.
    /// </summary>
    private int PendingBoundary()
    {
        var index = _queue.Count;
        var logical = 0;
        var published = 0;
        while (index > 0 && (logical < PendingLength || published < PendingLength))
        {
            var token = _queue[index - 1];
            if (!token.IsPrintable)
            {
                published += token.Text.Length;
                index--;
                continue;
            }

            var take = Math.Min(token.Text.Length, Math.Max(PendingLength - logical, PendingLength - published));
            if (take == token.Text.Length)
            {
                logical += take;
                published += take;
                index--;
                continue;
            }

            _queue[index - 1] = new Token(true, token.Text[..^take]);
            _queue.Insert(index, new Token(true, token.Text[^take..]));
            return index;
        }

        return index;
    }

    /// <summary>
    /// Writes the first <paramref name="count"/> logical printable characters, and the escapes that come before the next printable character
    /// beyond them. Escapes after that point stay held, so the order of the stream is kept.
    /// </summary>
    private void CommitPrintable(int count)
    {
        var committed = 0;
        while (_queue.Count > 0)
        {
            var token = _queue[0];
            if (!token.IsPrintable)
            {
                _queue.RemoveAt(0);
                _escapes--;
                EmitToken(token);
                if (State != GuestOutputState.Open)
                {
                    return;
                }

                continue;
            }

            if (committed >= count)
            {
                return;
            }

            var take = Math.Min(token.Text.Length, count - committed);
            var piece = new Token(true, token.Text[..take]);
            if (take == token.Text.Length)
            {
                _queue.RemoveAt(0);
            }
            else
            {
                _queue[0] = new Token(true, token.Text[take..]);
            }

            committed += take;
            _printable -= take;
            EmitToken(piece);
            if (State != GuestOutputState.Open)
            {
                return;
            }
        }
    }

    /// <summary>Writes the first <paramref name="count"/> queued tokens, in order. Only decided tokens are passed here.</summary>
    private void CommitTokens(int count)
    {
        for (var i = 0; i < count && _queue.Count > 0; i++)
        {
            var token = _queue[0];
            _queue.RemoveAt(0);
            if (token.IsPrintable)
            {
                _printable -= token.Text.Length;
            }
            else
            {
                _escapes--;
            }

            EmitToken(token);
            if (State != GuestOutputState.Open)
            {
                return;
            }
        }
    }

    private void EmitToken(Token token)
    {
        Emit(token.Text);
        if (State == GuestOutputState.Open)
        {
            _logicalHistory = Last(_logicalHistory + (token.IsPrintable ? token.Text : string.Empty));
            _boundaryHistory = Last(_boundaryHistory + (token.IsPrintable ? token.Text : EscapeMarker.ToString()));
        }
    }

    private static string Last(string text) => text.Length <= PendingLength ? text : text[^PendingLength..];

    /// <summary>
    /// Drops everything held and writes a fixed notice. The state is set before anything is written, so a failed write cannot make
    /// the dropped text publishable.
    /// </summary>
    private void Withhold(string reason, GuestOutputState next, string? notice = null)
    {
        _queue.Clear();
        _printable = 0;
        _escapes = 0;
        State = next;
        RefusalRule = reason;
        Emit(notice ?? $"[guest output withheld: {reason}]");
    }

    private void Emit(string text)
    {
        if (State == GuestOutputState.Failed || text.Length == 0)
        {
            return;
        }

        try
        {
            _out.Write(text);
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void FlushWriter()
    {
        if (State == GuestOutputState.Failed)
        {
            return;
        }

        try
        {
            _out.Flush();
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void Fail(Exception exception)
    {
        InternalFailure = exception;
        State = GuestOutputState.Failed;
        _queue.Clear();
        _printable = 0;
        _escapes = 0;
    }
}

/// <summary>
/// The one shared guest TTY sink of the RuntimeHost process. It writes to the process console and is completed when the process ends, so
/// held text is always checked before exit. Both the HLE path and the native path write through it.
/// </summary>
[Infrastructure]
public static class PublicGuestOutput
{
    private static readonly object Gate = new();
    private static PublicGuestOutputSink? _shared;
    private static bool _outcomeReported;

    /// <summary>The shared sink, created on first use on the process console.</summary>
    public static PublicGuestOutputSink Shared
    {
        get
        {
            lock (Gate)
            {
                return _shared ??= new PublicGuestOutputSink(Console.Out);
            }
        }
    }

    /// <summary>
    /// Ends the shared stream: checks and writes any held text. Called at process exit; safe to call when nothing was written. A stream
    /// that was withheld, terminated or failed is reported by its fixed public code only.
    /// </summary>
    public static GuestOutputState FlushAll()
    {
        lock (Gate)
        {
            if (_shared is null)
            {
                return GuestOutputState.Completed;
            }

            var state = _shared.Complete();
            if (_outcomeReported)
            {
                return state;
            }

            if (state == GuestOutputState.Failed)
            {
                _outcomeReported = true;
                Console.Error.WriteLine("runtime host: guest output failed; details are not published (code GUEST_OUTPUT_FAILED).");
            }
            else if (state is GuestOutputState.Suppressed or GuestOutputState.Terminated)
            {
                _outcomeReported = true;
                Console.Error.WriteLine($"runtime host: guest output {state} (code GUEST_OUTPUT_WITHHELD); details are not published.");
            }

            return state;
        }
    }
}
