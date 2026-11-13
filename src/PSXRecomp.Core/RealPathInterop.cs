using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core;

/// <summary>
/// The real location of an existing path or of an open file: every symbolic link, junction and mount point on it resolved (Windows: the
/// final path of the path's or the handle's file; POSIX: <c>realpath</c>). This is the interop boundary for that query. Callers decide what
/// the location may be used for. A location is returned only as a drive-absolute or UNC path (Windows) or an absolute path (POSIX): a
/// volume with no drive letter, or any other form, is reported as null, so no caller can anchor it to the working directory by accident.
/// </summary>
[Domain]
public static class RealPathInterop
{
    /// <summary>True when <see cref="TryGetRealPath(SafeFileHandle)"/> can confirm a file's location on this platform (Windows and POSIX).</summary>
    public static bool CanResolveHandle => true;

    /// <summary>
    /// The real location of <paramref name="path"/>, or null when it cannot be determined (missing, a dangling link, an unusable form, or a
    /// failed query). A null is a refusal for any caller that must know where a file really is.
    /// </summary>
    public static string? TryGetRealPath(string path) => OperatingSystem.IsWindows() ? WindowsRealPath(path) : PosixRealPath(path);

    /// <summary>
    /// The real location of the file an open handle refers to (Windows: the handle's final path; POSIX: the
    /// <c>/proc/self/fd</c> link of the handle, canonicalized), or null when it cannot be determined. Used to confirm
    /// that a file created at a verified location is the file that was created there. A null is a refusal for any
    /// caller that must know where a file really is.
    /// </summary>
    public static string? TryGetRealPath(SafeFileHandle handle)
    {
        if (handle.IsInvalid)
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            var buffer = new char[32768];
            var length = GetFinalPathNameByHandleW(handle.DangerousGetHandle(), buffer, (uint)buffer.Length, 0 /* VOLUME_NAME_DOS */);
            return length == 0 || length >= buffer.Length ? null : NormalizeFinalPath(new string(buffer, 0, (int)length));
        }

        return PosixRealPathOfHandle(handle);
    }

    /// <summary>
    /// Turns a Windows final path (<c>\\?\C:\...</c>, <c>\\?\UNC\server\share\...</c>) into its ordinary form. Returns null for any other
    /// form, including <c>\\?\Volume{GUID}\...</c> (a volume with no drive letter): stripping its prefix would leave a relative path.
    /// </summary>
    internal static string? NormalizeFinalPath(string final)
    {
        if (final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            return @"\\" + final[8..];
        }

        if (!final.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = final[4..];
        return rest.Length >= 3 && char.IsAsciiLetter(rest[0]) && rest[1] == ':' && rest[2] == '\\' ? rest : null;
    }

    /// <summary>
    /// Creates a new file at <paramref name="path"/> (Windows only) and returns its open handle. The handle has the access needed to delete
    /// the file through it (<see cref="TryDeleteByHandle"/>), and no sharing, so nothing else can open it meanwhile. Fails when anything exists
    /// at the name, a dangling link included; it does not follow a link.
    /// </summary>
    public static SafeFileHandle CreateNewFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("the create-and-delete-by-handle path is Windows only");
        }

        const uint GenericWrite = 0x40000000;
        const uint Delete = 0x00010000;
        const uint FileReadAttributes = 0x80;
        const uint CreateNew = 1;
        const uint FileAttributeNormal = 0x80;
        var handle = CreateFileW(path, GenericWrite | Delete | FileReadAttributes, 0, IntPtr.Zero, CreateNew, FileAttributeNormal, IntPtr.Zero);
        if (handle == new IntPtr(-1))
        {
            throw new IOException($"the file could not be created exclusively (Win32 error {Marshal.GetLastPInvokeError()})");
        }

        return new SafeFileHandle(handle, ownsHandle: true);
    }

    /// <summary>
    /// Marks the file the handle refers to for deletion; it is removed when the last handle to it closes. Deletes that file, whatever name it
    /// has now. Windows only; false elsewhere or when the request fails.
    /// </summary>
    public static bool TryDeleteByHandle(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows() || handle.IsInvalid)
        {
            return false;
        }

        const int FileDispositionInfo = 4;
        byte deleteFile = 1;
        return SetFileInformationByHandle(handle.DangerousGetHandle(), FileDispositionInfo, ref deleteFile, sizeof(byte));
    }

    /// <summary>
    /// Creates a new file at the verified location <paramref name="path"/> on POSIX and returns its open handle together
    /// with a handle to the directory the file was created in. The parent directory is opened with
    /// <c>O_DIRECTORY | O_NOFOLLOW</c> (a parent swapped for a symlink fails the open: fail closed), and the file is made
    /// with <c>openat(dir, name, O_CREAT | O_EXCL | O_NOFOLLOW)</c> (an existing file, a dangling link included, fails;
    /// a link at the final name is not followed). The caller confirms the created file through its handle and removes it
    /// through the directory handle when the check fails. Fails (false) whenever anything cannot be verified.
    /// </summary>
    public static bool TryCreateNewPinned(string path, out SafeFileHandle handle, out SafeFileHandle directory)
    {
        handle = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        directory = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        var parent = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
        {
            return false;
        }

        const int O_RDONLY = 0;
        const int O_DIRECTORY_LINUX = 0x10000;
        const int O_NOFOLLOW_LINUX = 0x20000;
        const int O_CLOEXEC_LINUX = 0x80000;
        const int O_DIRECTORY_MACOS = 0x100000;
        const int O_NOFOLLOW_MACOS = 0x0100;
        const int O_CLOEXEC_MACOS = 0x1000000;
        var dirFd = open(parent, O_RDONLY | O_DIRECTORY_LINUX | O_NOFOLLOW_LINUX | O_CLOEXEC_LINUX, 0);
        if (dirFd < 0)
        {
            // macOS flag values differ; retry with those before giving up (open has no side effect on failure).
            dirFd = open(parent, O_RDONLY | O_DIRECTORY_MACOS | O_NOFOLLOW_MACOS | O_CLOEXEC_MACOS, 0);
            if (dirFd < 0)
            {
                return false;
            }
        }

        const int O_WRONLY = 1;
        const int O_CREAT = 0x40;
        const int O_EXCL = 0x80;
        var fd = openat(dirFd, name, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW_LINUX | O_CLOEXEC_LINUX, 0x180 /* 0600 */);
        if (fd < 0)
        {
            fd = openat(dirFd, name, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW_MACOS | O_CLOEXEC_MACOS, 0x180);
            if (fd < 0)
            {
                close(dirFd);
                return false;
            }
        }

        directory = new SafeFileHandle((IntPtr)dirFd, ownsHandle: true);
        handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        return true;
    }

    /// <summary>
    /// Opens the existing file at the verified location <paramref name="path"/> on POSIX without following a link at
    /// the final name (<c>O_NOFOLLOW</c>), for read-only confirmation through the handle. False when it cannot be opened that way.
    /// </summary>
    public static bool TryOpenPinned(string path, out SafeFileHandle handle)
    {
        handle = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
        const int O_RDONLY = 0;
        var fd = open(path, O_RDONLY | 0x20000 /* O_NOFOLLOW Linux */ | 0x80000 /* O_CLOEXEC Linux */, 0);
        if (fd < 0)
        {
            fd = open(path, O_RDONLY | 0x0100 /* O_NOFOLLOW macOS */ | 0x1000000 /* O_CLOEXEC macOS */, 0);
            if (fd < 0)
            {
                return false;
            }
        }

        handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        return true;
    }

    /// <summary>Removes <paramref name="name"/> through the pinned <paramref name="directory"/> handle. Best effort.</summary>
    public static bool TryUnlinkPinned(SafeFileHandle directory, string name)
    {
        if (directory.IsInvalid || string.IsNullOrEmpty(name))
        {
            return false;
        }

        return unlinkat((int)directory.DangerousGetHandle().ToInt64(), name, 0) == 0;
    }

    // The handle is opened by following the links (no FILE_FLAG_OPEN_REPARSE_POINT), so its final path is the resolved one.
    private static string? WindowsRealPath(string path)
    {
        const uint FileReadAttributes = 0x80;
        const uint ShareAll = 0x7;
        const uint OpenExisting = 3;
        const uint FileFlagBackupSemantics = 0x02000000; // needed to open a directory
        var handle = CreateFileW(path, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle == new IntPtr(-1))
        {
            return null;
        }

        try
        {
            var buffer = new char[32768];
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0 /* VOLUME_NAME_DOS */);
            return length == 0 || length >= buffer.Length ? null : NormalizeFinalPath(new string(buffer, 0, (int)length));
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? PosixRealPath(string path)
    {
        var resolved = realpath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var real = Marshal.PtrToStringUTF8(resolved);
            return real is not null && real.StartsWith('/') ? real : null;
        }
        finally
        {
            free(resolved);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(IntPtr file, char[] filePath, uint filePathLength, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(IntPtr file, int fileInformationClass, ref byte fileInformation, uint bufferSize);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath(string path, IntPtr resolved);

    [DllImport("libc")]
    private static extern void free(IntPtr pointer);

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int openat(int dirfd, string path, int flags, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlinkat(int dirfd, string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern long readlink(string path, byte[] buffer, UIntPtr size);

    private const int O_CLOEXEC = 0x80000; // Linux value; macOS callers retry with their own flag value on failure.

    private static string? PosixRealPathOfHandle(SafeFileHandle handle)
    {
        // The /proc/self/fd link names the open file; read it, then canonicalize. Anything unexpected is a refusal.
        int fd;
        try
        {
            fd = (int)handle.DangerousGetHandle().ToInt64();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }

        var link = $"/proc/self/fd/{fd}";
        var buffer = new byte[4096];
        var length = readlink(link, buffer, (UIntPtr)buffer.Length);
        if (length < 0 || length >= buffer.Length)
        {
            return null; // no /proc (or an implausibly long name): unverifiable, fail closed
        }

        var target = System.Text.Encoding.UTF8.GetString(buffer, 0, (int)length);
        if (target.EndsWith(" (deleted)", StringComparison.Ordinal))
        {
            return null;
        }

        return PosixRealPath(target);
    }
}
