using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// The text a direct launch may print from an artifact's stdout: the guest's own text only. The host protocol never reaches the console:
/// every <c>RHOST_*</c> line is removed (a failed run prints them too), and so is the <c>RSNAPSHOT_*</c> block, markers and body
/// included, because its body carries the GPR values. A block that is never closed drops everything after its begin marker (fail closed).
/// </summary>
[Infrastructure]
public static class ArtifactConsoleOutput
{
    private const string HostProtocolPrefix = "RHOST_";
    private const string SnapshotPrefix = "RSNAPSHOT_";
    private const string SnapshotBegin = "RSNAPSHOT_BEGIN";
    private const string SnapshotEnd = "RSNAPSHOT_END";

    public static string GuestText(string stdout)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        var kept = new List<string>();
        var inSnapshot = false;
        foreach (var line in stdout.Split('\n'))
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

            kept.Add(line);
        }

        return string.Join('\n', kept);
    }
}
