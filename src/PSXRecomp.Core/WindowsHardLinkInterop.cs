using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core;

/// <summary>
/// The kernel32 hard-link and file-information calls (Issue #846), used only by the developer-only firmware measurement to
/// reference a local disc image without copying it. A hard link is a second directory entry for the same file.
/// </summary>
[Domain]
public static class WindowsHardLinkInterop
{
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFileInformationByHandle(IntPtr handle, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    /// <summary>
    /// Opens an existing file or directory for its identity only: no data access, all sharing allowed, and a link is opened as itself
    /// (not followed). The handle is read with <see cref="GetFileInformationByHandle"/>. Returns an invalid handle when the path cannot be opened.
    /// </summary>
    public static SafeFileHandle OpenForIdentity(string path) =>
        CreateFileW(path, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);

    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareAll = 0x0000_0007; // read, write, delete
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000; // a directory can be opened
    private const uint OpenReparsePoint = 0x0020_0000; // a link is identified as itself

    // Pack = 4: the FILETIME fields are two DWORDs in Win32, so no 8-byte alignment padding may be inserted before NumberOfLinks.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
