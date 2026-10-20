using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core;

/// <summary>
/// The device and inode of an open file on macOS (Issue #786 review round 5). The query is <c>fstat</c>, which macOS exports as
/// <c>fstat$INODE64</c> on x86-64 and as <c>fstat</c> on arm64. The fields read are <c>st_dev</c> (a 32-bit value) at offset 0 and
/// <c>st_ino</c> (64-bit) at offset 8 of <c>struct stat</c>. Any other platform, or a libc without either symbol, reports no
/// identity, so the caller refuses rather than guesses.
/// </summary>
[Domain]
public static class MacOsFileIdentityInterop
{
    // Larger than struct stat on every architecture this reads, so the kernel never writes past the buffer.
    private const int StatBufferBytes = 256;

    /// <summary>Reads the device and inode of the open file. Returns false when the query fails or the layout is not known here.</summary>
    public static bool TryGetIdentity(SafeFileHandle handle, out ulong device, out ulong inode)
    {
        device = 0;
        inode = 0;
        if (!OperatingSystem.IsMacOS() || !BitConverter.IsLittleEndian) return false;

        var buffer = new byte[StatBufferBytes];
        var descriptor = handle.DangerousGetHandle().ToInt32();
        int result;
        try
        {
            result = FstatInode64(descriptor, buffer);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            try
            {
                result = Fstat(descriptor, buffer);
            }
            catch (Exception fallback) when (fallback is EntryPointNotFoundException or DllNotFoundException)
            {
                return false;
            }
        }

        if (result != 0) return false;

        device = (uint)BitConverter.ToInt32(buffer, 0);
        inode = BitConverter.ToUInt64(buffer, 8);
        return true;
    }

    [DllImport("libSystem.dylib", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int FstatInode64(int fileDescriptor, byte[] statBuffer);

    [DllImport("libSystem.dylib", EntryPoint = "fstat", SetLastError = true)]
    private static extern int Fstat(int fileDescriptor, byte[] statBuffer);
}
