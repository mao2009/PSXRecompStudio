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
    private readonly bool _identityVerified;

    /// <summary>The probe for this host: the operating system and the processor architecture are read from the runtime.</summary>
    public FileSystemIdentityProbe()
        : this(() => OperatingSystem.IsMacOS(), () => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture)
    {
    }

    /// <summary>
    /// The production construction with its two inputs injected. The tests pass a simulated macOS x86-64 host, so the rule that refuses it runs on
    /// any machine; the public constructor passes the real readers, so it is the same code path.
    /// </summary>
    internal FileSystemIdentityProbe(Func<bool> isMacOS, Func<System.Runtime.InteropServices.Architecture> processArchitecture)
        : this(IsVerified(isMacOS(), processArchitecture()))
    {
    }

    /// <summary>
    /// A probe whose identity reads are verified (<paramref name="identityVerified"/> true) or not. When they are not verified, an existing
    /// name is reported Unreadable, so that every replacement of an existing file is refused (fail-closed).
    /// </summary>
    private FileSystemIdentityProbe(bool identityVerified)
    {
        _identityVerified = identityVerified;
    }

    /// <summary>
    /// The identity-verification rule. False on macOS x86-64: the identity is read through the stat$INODE64 symbol, whose layout is not verified
    /// on any x86-64 Mac available here. True on every other host: Linux (x86-64 and arm64), macOS arm64 (native), and Windows. A Rosetta-translated
    /// process reports x86-64 and is refused as well.
    /// </summary>
    internal static bool IsVerified(bool macOS, System.Runtime.InteropServices.Architecture architecture) =>
        !(macOS && architecture == System.Runtime.InteropServices.Architecture.X64);

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
            if (OperatingSystem.IsWindows()) return ProbeWindows(path, _identityVerified);
            if (OperatingSystem.IsLinux()) return FromPosix(LinuxFileStatInterop.Stat(path), _identityVerified, path);
            if (OperatingSystem.IsMacOS()) return FromPosix(MacOsFileStatInterop.Stat(path), _identityVerified, path);
            return Unreadable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Unreadable;
        }
    }

    private static FileProbe Unreadable => new(FileKind.Unreadable, null);

    // True when the path is itself a symbolic link (whatever its target), or when the link entry cannot be read: an unknown entry is treated as
    // present (fail-closed). FileInfo.LinkTarget reads the link entry, not what it points to.
    private static bool IsSymbolicLinkOrUnknown(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    private static FileProbe FromPosix(PosixStatResult stat, bool identityVerified, string path)
    {
        if (!stat.Succeeded)
        {
            if (stat.Errno is not (NoSuchFile or NotADirectory)) return new FileProbe(FileKind.Unreadable, null, stat.Cause);

            // stat follows a symbolic link, so a dangling link reports ENOENT as if nothing were there. The link entry itself exists, and a write
            // would replace it, so on every POSIX host (verified or not) it is an existing name: Unreadable, refused before any write. A regular
            // missing path is still Absent, so a new report can be created.
            return IsSymbolicLinkOrUnknown(path) ? Unreadable : new FileProbe(FileKind.Absent, null);
        }

        // An existing name on a host whose identity is unverified cannot be shown to be no input: Unreadable, fail-closed.
        if (!identityVerified) return Unreadable;

        var type = stat.Mode & FileTypeMask;
        var kind = type switch
        {
            RegularFileType => FileKind.RegularFile,
            DirectoryType => FileKind.Directory,
            _ => FileKind.Other,
        };
        return new FileProbe(kind, new FileIdentity(stat.Device, stat.Inode));
    }

    private static FileProbe ProbeWindows(string path, bool identityVerified)
    {
        if (IsDeviceName(path)) return new FileProbe(FileKind.Other, null);

        // Absent is only a not-found answer. File.Exists and Directory.Exists return false on an access denial as well, which would make an
        // unreadable name look absent; the attributes call throws the denial instead, and the caller's catch makes it Unreadable.
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // A dangling reparse point (symbolic link) also reports not-found, but its link entry exists and a write would replace it: the same rule
            // as the POSIX dangling link. A regular missing name stays Absent, so a new report can be created.
            return IsSymbolicLinkOrUnknown(path) ? Unreadable : new FileProbe(FileKind.Absent, null);
        }

        if (attributes.HasFlag(FileAttributes.Directory)) return new FileProbe(FileKind.Directory, null);

        // An existing file on a host whose identity is unverified is Unreadable, fail-closed, before any open.
        if (!identityVerified) return Unreadable;

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
    /// The single device-namespace rule, applied to a path as given and to its full form (no I/O). Slashes are read as backslashes, and
    /// leading spaces and separators are skipped. The server component is the first component, with leading and trailing spaces removed,
    /// and trailing dots removed as Win32 does ("? " is "?", "??." is "??"). The path is a device namespace path when:
    /// the server is "." behind a doubled separator (\\.\, ///./, \\. \X and the like: a server made only of dots and spaces, such as "."
    /// or ". ", is the dot server); or the server is "?" behind a doubled separator (\\?\, //?/, \\ ?\X); or the server is "??" (the NT
    /// object namespace, \??\ and \\??\ alike). Exception: for "?" and "??", a first component that is a drive letter and colon (\\?\C:\...,
    /// \??\C:\...) is an ordinary local path, and is not refused. A UNC path such as \\server\share is not a device namespace path.
    /// </summary>
    public static bool IsDeviceNamespacePath(string path)
    {
        // Leading spaces are not part of the path, so they are removed before the separators are read.
        var normalised = path.Replace('/', '\\').TrimStart(' ');
        var doubled = normalised.StartsWith("\\\\", StringComparison.Ordinal);
        var rooted = normalised.StartsWith('\\');
        var parts = normalised.TrimStart('\\').Split('\\');
        var server = parts[0].Trim(' ');
        var key = server.TrimEnd(' ', '.');
        var dotServer = server.Length > 0 && server.Contains('.') && server.All(c => c is '.' or ' ');

        var extendedOrNt = (key == "?" && doubled) || (key == "??" && rooted);
        if (extendedOrNt) return !(parts.Length > 1 && IsDriveLetterAndColon(parts[1]));
        return doubled && dotServer;
    }

    private static bool IsDriveLetterAndColon(string component) =>
        component.Length == 2 && char.IsAsciiLetter(component[0]) && component[1] == ':';
}
