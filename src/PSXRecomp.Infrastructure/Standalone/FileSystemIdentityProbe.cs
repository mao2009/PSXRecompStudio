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

    private static readonly string[] DeviceNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

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

    // A device namespace path (\\.\ or \\?\) or a reserved device name (NUL, COM1 ...) names a device, whatever its directory.
    private static bool IsDeviceName(string path)
    {
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal)) return true;

        var leaf = Path.GetFileName(path);
        var stem = leaf.Split('.')[0];
        return DeviceNames.Any(name => string.Equals(name, stem, StringComparison.OrdinalIgnoreCase));
    }
}
