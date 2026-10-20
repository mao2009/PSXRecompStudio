using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core;

/// <summary>
/// The device and inode of an open file on Linux (Issue #786 review round 5). The query is <c>fstat</c>; the fields read are
/// <c>st_dev</c> at offset 0 and <c>st_ino</c> at offset 8 of <c>struct stat</c>, which is the layout glibc uses for 64-bit
/// little-endian Linux (x86-64 and arm64). Any other platform, or a libc without <c>fstat</c>, reports no identity, so the caller
/// refuses rather than guesses.
/// </summary>
[Domain]
public static class LinuxFileIdentityInterop
{
    // Larger than struct stat on every architecture this reads, so the kernel never writes past the buffer.
    private const int StatBufferBytes = 256;

    /// <summary>Reads the device and inode of the open file. Returns false when the query fails or the layout is not known here.</summary>
    public static bool TryGetIdentity(SafeFileHandle handle, out ulong device, out ulong inode)
    {
        device = 0;
        inode = 0;
        if (!OperatingSystem.IsLinux() || !BitConverter.IsLittleEndian) return false;

        var buffer = new byte[StatBufferBytes];
        try
        {
            if (fstat(handle.DangerousGetHandle().ToInt32(), buffer) != 0) return false;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }

        device = BitConverter.ToUInt64(buffer, 0);
        inode = BitConverter.ToUInt64(buffer, 8);
        return true;
    }

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int fstat(int fileDescriptor, byte[] statBuffer);
}
