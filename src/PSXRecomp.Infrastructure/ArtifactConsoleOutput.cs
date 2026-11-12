using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// The forwarding of an artifact's stdout to the console on a direct launch. Stdout carries only the guest's own text: the state snapshot is
/// not on stdout (it is written to stderr, and only for a run the engine manages, PR #839 ruling), and a non-bridge run has no host protocol
/// on stdout. So this forwarder copies the text as it is. It is not a security boundary and does not look for protocol markers; the
/// separation of the snapshot onto stderr is the boundary.
/// </summary>
[Infrastructure]
public static class ArtifactConsoleOutput
{
    /// <summary>Copies the guest's text from <paramref name="stdout"/> to <paramref name="console"/>, line by line.</summary>
    public static void Forward(TextReader stdout, TextWriter console)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(console);
        string? line;
        while ((line = stdout.ReadLine()) is not null)
        {
            console.Write(line);
            console.Write('\n');
        }
    }
}
