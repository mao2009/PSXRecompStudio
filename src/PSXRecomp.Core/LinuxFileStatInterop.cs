using System.Runtime.InteropServices;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core;

/// <summary>
/// The <c>stat</c> call on Linux (Issue #786 review round 6), for the file type, device and inode of a path. It takes the path, not an
/// open handle, so a FIFO is typed without being opened. The fields read are <c>st_dev</c> (offset 0), <c>st_ino</c> (offset 8) and
/// <c>st_mode</c>, whose offset is 24 on x86-64 and 16 on arm64 (glibc). Other architectures, big-endian hosts and a libc without
/// <c>stat</c> report <see cref="PosixStatResult.Unsupported"/> with the cause named.
/// </summary>
[Domain]
public static class LinuxFileStatInterop
{
    // Larger than struct stat on every architecture read here, so the kernel never writes past the buffer.
    private const int StatBufferBytes = 256;

    /// <summary>Stats the path, following links.</summary>
    public static PosixStatResult Stat(string path)
    {
        if (!OperatingSystem.IsLinux() || !BitConverter.IsLittleEndian) return PosixStatResult.Unsupported("this host is not little-endian Linux: the stat layout is not verified here");

        var modeOffset = RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => 24,
            System.Runtime.InteropServices.Architecture.Arm64 => 16,
            _ => -1,
        };
        if (modeOffset < 0) return PosixStatResult.Unsupported("the stat layout of this processor architecture is not verified (x86-64 and arm64 are)");

        var buffer = new byte[StatBufferBytes];
        try
        {
            if (stat(path, buffer) != 0) return new PosixStatResult(Marshal.GetLastPInvokeError(), 0, 0, 0);
        }
        catch (EntryPointNotFoundException)
        {
            return PosixStatResult.Unsupported(PosixStatResult.StatNotExportedCause);
        }
        catch (DllNotFoundException)
        {
            return PosixStatResult.Unsupported("the libc could not be loaded for stat: identity unavailable");
        }

        return new PosixStatResult(0, BitConverter.ToUInt32(buffer, modeOffset), BitConverter.ToUInt64(buffer, 0), BitConverter.ToUInt64(buffer, 8));
    }

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] statBuffer);
}
