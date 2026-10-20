using PSXRecomp.Architecture;
using PSXRecomp.Core;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// The identity of a file independent of its name (Issue #786 review round 4): two names are one file when they share a volume
/// serial number and a file index. Hard links, junctions, symbolic links and 8.3 short names all open the same identity, so a
/// report target is compared with the inputs by identity, not only by path. Windows only; elsewhere <see cref="Of"/> returns null
/// and callers rely on path comparison.
/// </summary>
[Infrastructure]
public static class RuntimeHostFileIdentity
{
    /// <summary>The volume and file index of one file.</summary>
    public readonly record struct Key(uint Volume, ulong Index);

    /// <summary>The identity of the file at <paramref name="path"/> (following links), or null when it is absent or unreadable.</summary>
    public static Key? Of(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return FileIdentityInterop.TryGetIdentity(stream.SafeFileHandle, out var volume, out var index)
                ? new Key(volume, index)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
