using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// The forwarding of an artifact's stdout to the console on a direct launch: only the guest's own text is written. The host protocol never
/// reaches the console: every <c>RHOST_*</c> line is removed (a failed run prints them too), and so is the <c>RSNAPSHOT_*</c> block, markers and
/// body included, because its body carries the GPR values. A block that is never closed drops everything after its begin marker (fail closed).
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
            var trimmed = line.Trim();
            if (inSnapshot)
            {
                if (trimmed.StartsWith(SnapshotEnd, StringComparison.Ordinal))
                {
                    inSnapshot = false;
                }

                continue;
            }

            if (trimmed.StartsWith(SnapshotBegin, StringComparison.Ordinal))
            {
                inSnapshot = true;
                continue;
            }

            if (trimmed.StartsWith(HostProtocolPrefix, StringComparison.Ordinal) || trimmed.StartsWith(SnapshotPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            console.Write(line);
            console.Write('\n');
        }
    }
}
