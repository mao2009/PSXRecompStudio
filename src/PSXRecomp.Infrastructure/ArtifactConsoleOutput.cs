using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// The forwarding of an artifact's stdout to the console on a direct launch: only the guest's own text is written. The host protocol never
/// reaches the console. A protocol marker is recognised anywhere in a line, not only at its start: guest text that has no line end before a
/// marker (<c>HELLO</c> then <c>RSNAPSHOT_BEGIN</c> on one line) keeps the text before the marker and drops the marker and everything after
/// it on that line. So every <c>RHOST_*</c> line is dropped, and the <c>RSNAPSHOT_*</c> block is dropped with its markers and body, because its
/// body carries the GPR values. A block that is never closed drops everything after its begin marker (fail closed).
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
            if (inSnapshot)
            {
                // Only the end marker matters inside the block; everything else (the body, GPR values included) is dropped.
                if (line.Contains(SnapshotEnd, StringComparison.Ordinal))
                {
                    inSnapshot = false;
                }

                continue;
            }

            var marker = FirstMarker(line);
            if (marker < 0)
            {
                console.Write(line);
                console.Write('\n');
                continue;
            }

            // The guest's text before the marker is kept; the marker and the rest of the line are not.
            if (marker > 0)
            {
                console.Write(line.AsSpan(0, marker));
                console.Write('\n');
            }

            if (string.CompareOrdinal(line, marker, SnapshotBegin, 0, SnapshotBegin.Length) == 0)
            {
                inSnapshot = true;
            }
        }
    }

    /// <summary>The index of the earliest protocol marker prefix in the line, or -1 when there is none.</summary>
    private static int FirstMarker(string line)
    {
        var host = line.IndexOf(HostProtocolPrefix, StringComparison.Ordinal);
        var snapshot = line.IndexOf(SnapshotPrefix, StringComparison.Ordinal);
        if (host < 0) return snapshot;
        if (snapshot < 0) return host;
        return Math.Min(host, snapshot);
    }
}
