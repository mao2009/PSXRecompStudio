using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// The forwarding of an artifact's stdout to the console on a direct launch: only the guest's own text is written. The host protocol never
/// reaches the console. Its tokens are recognised anywhere in a line, and the line is read with its tokens in mind:
/// <list type="bullet">
/// <item>Guest text before the first protocol token (<c>RHOST_</c> or <c>RSNAPSHOT_</c>) is kept when no block was open at the start of the line.</item>
/// <item>The block state after a line is decided by the line's LAST begin and LAST end marker: a begin after the last end opens the block, an end
/// after the last begin closes it. The native emitter writes each marker as the last token of its own line, so for the real block this holds
/// whatever guest text precedes it.</item>
/// <item>Text after the last end marker, up to the next protocol token, is guest text and is kept (a closed block's text is not the block's body).</item>
/// <item>While a block is open, nothing on the line is printed except text after an end marker. The body, which carries the GPR values, is withheld.
/// A block that is never closed drops everything after its begin marker (fail closed).</item>
/// </list>
/// </summary>
[Infrastructure]
public static class ArtifactConsoleOutput
{
    private const string HostProtocolPrefix = "RHOST_";
    private const string SnapshotPrefix = "RSNAPSHOT_";
    private const string SnapshotBegin = "RSNAPSHOT_BEGIN";
    private const string SnapshotEnd = "RSNAPSHOT_END";

    /// <summary>Copies the guest's text from <paramref name="stdout"/> to <paramref name="console"/>, line by line, without the host protocol.</summary>
    public static void Forward(TextReader stdout, TextWriter console)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(console);
        var inSnapshot = false;
        string? line;
        while ((line = stdout.ReadLine()) is not null)
        {
            var firstToken = FirstProtocolToken(line);
            var lastBegin = line.LastIndexOf(SnapshotBegin, StringComparison.Ordinal);
            var lastEnd = line.LastIndexOf(SnapshotEnd, StringComparison.Ordinal);

            if (!inSnapshot)
            {
                // Guest text before the first token is not body: no block was open when the line began, and every marker starts at or after it.
                if (firstToken < 0)
                {
                    console.Write(line);
                    console.Write('\n');
                    continue;
                }

                WriteText(console, line.AsSpan(0, firstToken));
            }

            // Text after the last end marker, up to the next token: outside any block that closed on this line.
            if (lastEnd >= 0)
            {
                WriteText(console, TextUpToNextToken(line, lastEnd + SnapshotEnd.Length));
            }

            // The state after the line: the last marker decides it. A line with no marker keeps the state it began with.
            if (lastBegin >= 0 && lastBegin > lastEnd)
            {
                inSnapshot = true;
            }
            else if (lastEnd >= 0)
            {
                inSnapshot = false;
            }
        }
    }

    /// <summary>The text from <paramref name="start"/> up to the first protocol token after it (the whole rest of the line when there is none).</summary>
    private static ReadOnlySpan<char> TextUpToNextToken(string line, int start)
    {
        var rest = line.AsSpan(start);
        var token = FirstProtocolToken(rest.ToString());
        return token < 0 ? rest : rest[..token];
    }

    /// <summary>Writes a line's guest text, with its line end, unless it holds nothing but white space.</summary>
    private static void WriteText(TextWriter console, ReadOnlySpan<char> text)
    {
        if (text.IsWhiteSpace())
        {
            return;
        }

        console.Write(text);
        console.Write('\n');
    }

    /// <summary>The index of the earliest protocol token (<c>RHOST_</c> or <c>RSNAPSHOT_</c>, the whole <c>RSNAPSHOT_*</c> family) on the line, or -1.</summary>
    private static int FirstProtocolToken(string line)
    {
        var host = line.IndexOf(HostProtocolPrefix, StringComparison.Ordinal);
        var snapshot = line.IndexOf(SnapshotPrefix, StringComparison.Ordinal);
        if (host < 0) return snapshot;
        if (snapshot < 0) return host;
        return Math.Min(host, snapshot);
    }
}
