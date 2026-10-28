using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// The forwarding of an artifact's stdout to the console on a direct launch: only the guest's own text is written. The host protocol never
/// reaches the console. A protocol token is recognised anywhere in a line, not only at its start, and each token is found on its own: the
/// line is cut at the first <c>RHOST_</c> or <c>RSNAPSHOT_</c> (so a leading token cannot hide a later one), the guest text before the cut is
/// kept, and the rest of the line is withheld. A <c>RSNAPSHOT_BEGIN</c> found anywhere in the withheld part opens the block, and a
/// <c>RSNAPSHOT_END</c> found anywhere closes it. The block's body, which carries the GPR values, is withheld in full. A block that is never
/// closed drops everything after its begin marker (fail closed).
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
                // Inside the block only the end marker matters, wherever it is on the line; the rest of that line is withheld too.
                if (line.Contains(SnapshotEnd, StringComparison.Ordinal))
                {
                    inSnapshot = false;
                }

                continue;
            }

            var cut = FirstProtocolToken(line);
            if (cut < 0)
            {
                console.Write(line);
                console.Write('\n');
                continue;
            }

            // The guest's text before the first token is kept; the token and the rest of the line are not.
            if (cut > 0)
            {
                console.Write(line.AsSpan(0, cut));
                console.Write('\n');
            }

            // A begin marker anywhere in the withheld part opens the block. Every RSNAPSHOT_BEGIN starts with RSNAPSHOT_, so it cannot lie
            // before the cut. An end marker after it on the same line closes the block again, at once.
            var begin = line.IndexOf(SnapshotBegin, cut, StringComparison.Ordinal);
            if (begin >= 0)
            {
                inSnapshot = line.IndexOf(SnapshotEnd, begin + SnapshotBegin.Length, StringComparison.Ordinal) < 0;
            }
        }
    }

    /// <summary>
    /// The index of the earliest protocol token on the line, or -1 when there is none. <c>RSNAPSHOT_</c> is taken as a whole, so every
    /// <c>RSNAPSHOT_*</c> token (begin, end and any other) starts a cut; each token's own position is found independently.
    /// </summary>
    private static int FirstProtocolToken(string line)
    {
        var host = line.IndexOf(HostProtocolPrefix, StringComparison.Ordinal);
        var snapshot = line.IndexOf(SnapshotPrefix, StringComparison.Ordinal);
        if (host < 0) return snapshot;
        if (snapshot < 0) return host;
        return Math.Min(host, snapshot);
    }
}
