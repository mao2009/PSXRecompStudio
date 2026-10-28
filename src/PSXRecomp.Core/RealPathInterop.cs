using System.Runtime.InteropServices;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core;

/// <summary>
/// The real location of an existing path: every symbolic link, junction and mount point on it resolved (Windows: the final path of an
/// open handle; POSIX: <c>realpath</c>). This is the interop boundary for that query. Callers decide what the location may be used for.
/// </summary>
[Domain]
public static class RealPathInterop
{
    /// <summary>
    /// The real location of <paramref name="path"/>, or null when it cannot be determined (missing, a dangling link, or a failed query).
    /// A null is a refusal for any caller that must know where a file really is.
    /// </summary>
    public static string? TryGetRealPath(string path) => OperatingSystem.IsWindows() ? WindowsRealPath(path) : PosixRealPath(path);

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
            if (length == 0 || length >= buffer.Length)
            {
                return null;
            }

            var final = new string(buffer, 0, (int)length);
            if (final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            {
                return @"\\" + final[8..];
            }

            return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
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
            return Marshal.PtrToStringUTF8(resolved);
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
