using PSXRecomp.Architecture;
using PSXRecomp.Core;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// The identity of a file independent of its name (Issue #786): two names are one file when they share a device and an index
/// (Windows: volume serial and file index; Linux and macOS: <c>st_dev</c> and <c>st_ino</c>). Hard links, junctions, symbolic links
/// and 8.3 short names all open the same identity. The query itself is in Core's interop boundary; this class opens the file
/// read-only to ask it, and reads no content.
/// </summary>
[Infrastructure]
public static class RuntimeHostFileIdentity
{
    /// <summary>The device and index of one file.</summary>
    public readonly record struct Key(ulong Device, ulong Index);

    /// <summary>
    /// The identity of the path, following links. Returns true when the identity is known, or when the path names nothing (then
    /// <paramref name="identity"/> is null, and nothing can collide with it). Returns false when the path names something whose
    /// identity cannot be read here: the caller must then refuse, since it cannot show the file is not an input.
    /// </summary>
    public static bool TryOf(string path, out Key? identity)
    {
        identity = null;
        if (!File.Exists(path) && !Directory.Exists(path)) return true;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var handle = stream.SafeFileHandle;
            bool known;
            ulong device;
            ulong index;
            if (OperatingSystem.IsWindows())
            {
                known = FileIdentityInterop.TryGetIdentity(handle, out var volume, out index);
                device = volume;
            }
            else if (OperatingSystem.IsLinux())
            {
                known = LinuxFileIdentityInterop.TryGetIdentity(handle, out device, out index);
            }
            else if (OperatingSystem.IsMacOS())
            {
                known = MacOsFileIdentityInterop.TryGetIdentity(handle, out device, out index);
            }
            else
            {
                known = false;
                device = 0;
                index = 0;
            }

            if (!known) return false;

            identity = new Key(device, index);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
