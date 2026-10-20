using System.Runtime.InteropServices;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core;

/// <summary>
/// The <c>stat</c> call on macOS (Issue #786 review round 6), for the file type, device and inode of a path. It takes the path, not an
/// open handle, so a FIFO is typed without being opened. The symbol is <c>stat$INODE64</c> on x86-64 and <c>stat</c> on arm64. The
/// fields read are <c>st_dev</c> (32-bit, offset 0), <c>st_mode</c> (16-bit, offset 4) and <c>st_ino</c> (64-bit, offset 8). Other
/// hosts and a libc without either symbol report <see cref="PosixStatResult.Unsupported"/> with the cause named.
/// </summary>
[Domain]
public static class MacOsFileStatInterop
{
    // Larger than struct stat on every architecture read here, so the kernel never writes past the buffer.
    private const int StatBufferBytes = 256;

    /// <summary>Stats the path, following links.</summary>
    public static PosixStatResult Stat(string path)
    {
        if (!OperatingSystem.IsMacOS() || !BitConverter.IsLittleEndian) return PosixStatResult.Unsupported("this host is not little-endian macOS: the stat layout is not verified here");

        var buffer = new byte[StatBufferBytes];
        int result;
        try
        {
            result = StatInode64(path, buffer);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            try
            {
                result = StatPlain(path, buffer);
            }
            catch (Exception fallback) when (fallback is EntryPointNotFoundException or DllNotFoundException)
            {
                return PosixStatResult.Unsupported(PosixStatResult.StatNotExportedCause);
            }
        }

        if (result != 0) return new PosixStatResult(Marshal.GetLastPInvokeError(), 0, 0, 0);

        return new PosixStatResult(
            0,
            BitConverter.ToUInt16(buffer, 4),
            (uint)BitConverter.ToInt32(buffer, 0),
            BitConverter.ToUInt64(buffer, 8));
    }

    [DllImport("libSystem.dylib", EntryPoint = "stat$INODE64", SetLastError = true)]
    private static extern int StatInode64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] statBuffer);

    [DllImport("libSystem.dylib", EntryPoint = "stat", SetLastError = true)]
    private static extern int StatPlain([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] statBuffer);
}
