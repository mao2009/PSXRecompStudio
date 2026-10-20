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

    // A path is refused as a device when it is in a device namespace (\\.\, \\?\ or \??\, in any slash spelling, raw or normalised), or when
    // a component is a reserved name. Win32 ignores trailing spaces and dots of a component, so each component is reduced the same way
    // before it is compared. Two forms are refused conservatively, fail-closed, and are NOT confirmed to be devices on this host: a
    // reserved name with an extension (CON.txt), and a reserved name with an alternate data stream after ':' (CON:stream). A .NET write
    // created both as ordinary files here, so the refusal may be stricter than Windows; it is kept because the cost is only a refusal.
    private static bool IsDeviceName(string path)
    {
        var full = Path.GetFullPath(path);
        if (IsDeviceNamespacePath(path) || IsDeviceNamespacePath(full)) return true;

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

    /// <summary>
    /// The single device-namespace rule, applied to a path as given and to its full form (no I/O). Slashes are read as backslashes and
    /// every leading separator is collapsed. The path is in a device namespace when its server component is "??" (the NT object
    /// namespace, \??\ and \\??\ alike), or when it is "." or "?" behind a doubled leading separator (\\.\, \\?\, ///./, //?/ and any
    /// number of extra separators). A UNC path such as \\server\share is not, and stays an ordinary path.
    /// </summary>
    public static bool IsDeviceNamespacePath(string path)
    {
        // Leading spaces are not part of the path; Win32 does not start a device namespace after them, so they are removed first. A
        // server component has its trailing spaces and dots removed the way Win32 does ("? " is "?", "??." is "??"), except that a server
        // made only of dots and spaces ("." or ". ") is the dot server, which is the one case that trimming would otherwise erase.
        var normalised = path.Replace('/', '\\').TrimStart(' ');
        var doubled = normalised.StartsWith("\\\\", StringComparison.Ordinal);
        var rooted = normalised.StartsWith('\\');
        var server = normalised.TrimStart('\\').Split('\\')[0];
        var trimmed = server.TrimEnd(' ', '.');
        var dotServer = server.Length > 0 && server.Contains('.') && server.All(c => c is '.' or ' ');

        if (rooted && trimmed == "??") return true;
        return doubled && (dotServer || trimmed == "?");
    }
}
