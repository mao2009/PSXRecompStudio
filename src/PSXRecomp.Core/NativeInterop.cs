using System.Runtime.InteropServices;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core;

[Domain]
internal static partial class NativeInterop
{
    private const string LibName = "PSXRecomp.Native";

    [LibraryImport(LibName)]
    internal static partial IntPtr PSXCore_Create();

    [LibraryImport(LibName)]
    internal static partial void PSXCore_Destroy(IntPtr core);

    [LibraryImport(LibName)]
    internal static partial void PSXCore_Reset(IntPtr core);

    [LibraryImport(LibName)]
    internal static partial uint PSXCore_GetGPR(IntPtr core, int index);

    [LibraryImport(LibName)]
    internal static partial void PSXCore_SetGPR(IntPtr core, int index, uint value);

    [LibraryImport(LibName)]
    internal static partial uint PSXCore_GetPC(IntPtr core);

    [LibraryImport(LibName)]
    internal static partial void PSXCore_SetPC(IntPtr core, uint value);

    [LibraryImport(LibName)]
    internal static partial uint PSXCore_GetHI(IntPtr core);

    [LibraryImport(LibName)]
    internal static partial void PSXCore_SetHI(IntPtr core, uint value);

    [LibraryImport(LibName)]
    internal static partial uint PSXCore_GetLO(IntPtr core);

    [LibraryImport(LibName)]
    internal static partial void PSXCore_SetLO(IntPtr core, uint value);

    [LibraryImport(LibName)]
    internal static partial IntPtr PSXCore_GetRAM(IntPtr core);

    [LibraryImport(LibName)]
    internal static partial uint PSXCore_GetRAMSize();
}
