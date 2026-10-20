using PSXRecomp.Architecture;
using PSXRecomp.Core;

namespace PSXRecomp.Infrastructure.RuntimeHost;

/// <summary>
/// A probe decorator for a host whose file identity cannot be verified (Issue #786, round 19): every existing name is reported
/// <see cref="FileKind.Unreadable"/>, so every replacement of an existing name is refused before any write. An absent name stays
/// <see cref="FileKind.Absent"/>, so a new report can still be created. Internal: only the test project uses it, to simulate such a host
/// through the same port the host uses.
/// </summary>
[Infrastructure]
internal sealed class UnverifiedIdentityProbe(IFileIdentityProbe inner) : IFileIdentityProbe
{
    /// <inheritdoc />
    public FileProbe Probe(string path)
    {
        var probe = inner.Probe(path);
        return probe.Kind == FileKind.Absent ? probe : new FileProbe(FileKind.Unreadable, null);
    }
}
