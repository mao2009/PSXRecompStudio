using PSXRecomp.Architecture;
using PSXRecomp.Core;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// The file system adapter for <see cref="IFileIdentityProbe"/> (Issue #786). On POSIX the path is typed and identified by <c>stat</c>, so
/// nothing is opened: a FIFO or device is reported as <see cref="FileKind.Other"/> and never blocks. On Windows a regular file is opened
/// read-only for its identity; device names and device namespace paths are typed as <see cref="FileKind.Other"/> before any open.
/// </summary>
[Infrastructure]
public sealed class FileSystemIdentityProbe : IFileIdentityProbe
{
    private const int NoSuchFile = 2;      // ENOENT on Linux and macOS.
    private const int NotADirectory = 20;  // ENOTDIR on Linux and macOS.
    private const uint FileTypeMask = 0xF000;
    private const uint RegularFileType = 0x8000;
    private const uint DirectoryType = 0x4000;

    // The documented reserved device names, with the console handles (CONIN$, CONOUT$) and the COM0/LPT0 and superscript-digit forms
    // (U+00B9, U+00B2, U+00B3) that Win32 documents alongside them. Refusing a name a host does not treat as a device is fail-closed.
    private static readonly string[] DeviceNames =
        ["CON", "CONIN$", "CONOUT$", "PRN", "AUX", "NUL",
         "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
         "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³"];

    /// <inheritdoc />
    public FileProbe Probe(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return ProbeWindows(path);
            if (OperatingSystem.IsLinux()) return FromPosix(LinuxFileStatInterop.Stat(path));
            if (OperatingSystem.IsMacOS()) return FromPosix(MacOsFileStatInterop.Stat(path));
            return Unreadable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Unreadable;
        }
    }

    private static FileProbe Unreadable => new(FileKind.Unreadable, null);

    private static FileProbe FromPosix(PosixStatResult stat)
    {
        if (!stat.Succeeded)
        {
            return stat.Errno is NoSuchFile or NotADirectory ? new FileProbe(FileKind.Absent, null) : Unreadable;
        }

        var type = stat.Mode & FileTypeMask;
        var kind = type switch
        {
            RegularFileType => FileKind.RegularFile,
            DirectoryType => FileKind.Directory,
            _ => FileKind.Other,
        };
        return new FileProbe(kind, new FileIdentity(stat.Device, stat.Inode));
    }

    private static FileProbe ProbeWindows(string path)
    {
        if (IsDeviceName(path)) return new FileProbe(FileKind.Other, null);
        if (Directory.Exists(path)) return new FileProbe(FileKind.Directory, null);
        if (!File.Exists(path)) return new FileProbe(FileKind.Absent, null);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return FileIdentityInterop.TryGetIdentity(stream.SafeFileHandle, out var volume, out var index)
            ? new FileProbe(FileKind.RegularFile, new FileIdentity(volume, index))
            : Unreadable;
    }

    // A device is named by a device namespace path (\\.\ or \\?\, in any slash spelling, raw or normalised), or by any component of the
    // path that is a reserved name. Win32 ignores trailing spaces and dots of a component, and everything after a ':' (an alternate data
    // stream), so each component is reduced the same way before it is compared. A reserved name with an extension (CON.txt) is refused
    // as well: Win32 documents such names as devices, and refusing them is the conservative choice (not confirmed on this host).
    private static bool IsDeviceName(string path)
    {
        var full = Path.GetFullPath(path);
        if (IsDeviceNamespace(path) || IsDeviceNamespace(full)) return true;

        foreach (var spelling in new[] { path, full })
        {
            foreach (var component in spelling.Split(['\\', '/']))
            {
                var name = component.Split(':')[0].TrimEnd(' ', '.');
                var stem = name.Split('.')[0].TrimEnd(' ');
                if (DeviceNames.Any(device => string.Equals(device, stem, StringComparison.OrdinalIgnoreCase))) return true;
            }
        }

        return false;
    }

    private static bool IsDeviceNamespace(string path)
    {
        var normalised = path.Replace('/', '\\');
        return normalised.StartsWith(@"\\.\", StringComparison.Ordinal) || normalised.StartsWith(@"\\?\", StringComparison.Ordinal);
    }
}
