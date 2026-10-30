using System.Runtime.InteropServices;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core;

/// <summary>
/// The kernel32 directory-creation call used by the developer-only firmware measurement (Issue #846). Unlike
/// <c>Directory.CreateDirectory</c>, which succeeds when the directory already exists, <c>CreateDirectoryW</c> fails with
/// <c>ERROR_ALREADY_EXISTS</c>, so a directory another process created is never adopted.
/// </summary>
[Domain]
public static class WindowsDirectoryInterop
{
    /// <summary>The Win32 error code for a name that already exists.</summary>
    public const int ErrorAlreadyExists = 183;

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);
}
