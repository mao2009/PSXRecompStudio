using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core;

/// <summary>
/// The file identity an open file reports: the volume serial number and file index that two names share when they are one file
/// (Issue #786 review round 4). Windows only. This is the interop boundary for that query; callers open the file themselves.
/// </summary>
[Domain]
public static class FileIdentityInterop
{
    /// <summary>Reads the volume and file index of the open file. Returns false when the query fails or the platform is not Windows.</summary>
    public static bool TryGetIdentity(SafeFileHandle handle, out uint volume, out ulong index)
    {
        volume = 0;
        index = 0;
        if (!OperatingSystem.IsWindows()) return false;

        if (!GetFileInformationByHandle(handle, out var information)) return false;

        volume = information.VolumeSerialNumber;
        index = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);
}
