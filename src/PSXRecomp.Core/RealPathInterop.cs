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
    /// <summary>True when <see cref="TryGetRealPath(SafeFileHandle)"/> can confirm a file's location on this platform (Windows).</summary>
    public static bool CanResolveHandle => OperatingSystem.IsWindows();

    /// <summary>
    /// The real location of <paramref name="path"/>, or null when it cannot be determined (missing, a dangling link, an unusable form, or a
    /// failed query). A null is a refusal for any caller that must know where a file really is.
    /// </summary>
    public static string? TryGetRealPath(string path) => OperatingSystem.IsWindows() ? WindowsRealPath(path) : PosixRealPath(path);

    /// <summary>
    /// The real location of the file an open handle refers to (Windows only; null elsewhere or when the query fails). Used to confirm that a
    /// file created at a verified location is the file that was created there.
    /// </summary>
    public static string? TryGetRealPath(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows() || handle.IsInvalid)
        {
            return null;
        }

        var buffer = new char[32768];
        var length = GetFinalPathNameByHandleW(handle.DangerousGetHandle(), buffer, (uint)buffer.Length, 0 /* VOLUME_NAME_DOS */);
        return length == 0 || length >= buffer.Length ? null : NormalizeFinalPath(new string(buffer, 0, (int)length));
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

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath(string path, IntPtr resolved);

    [DllImport("libc")]
    private static extern void free(IntPtr pointer);
}
