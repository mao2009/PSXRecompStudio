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

    [LibraryImport(LibName)]
    internal static partial uint PSXCore_ReadDmaRegister(IntPtr core, uint address);

    [LibraryImport(LibName)]
    internal static partial void PSXCore_WriteDmaRegister(IntPtr core, uint address, uint value);

    [LibraryImport(LibName)]
    internal static partial int PSXCore_GetDmaInterruptPending(IntPtr core);

    [LibraryImport(LibName)]
    internal static partial uint PSXCore_ReadTimerRegister(IntPtr core, uint address);

    [LibraryImport(LibName)]
    internal static partial void PSXCore_WriteTimerRegister(IntPtr core, uint address, uint value);

    [LibraryImport(LibName)]
    internal static partial void PSXCore_TickTimers(IntPtr core, uint cycles);

    [LibraryImport(LibName)]
    internal static partial int PSXCore_GetTimerInterruptPending(IntPtr core, int timer);

    [LibraryImport(LibName)]
    internal static partial void PSXCore_ClearTimerInterrupt(IntPtr core, int timer);

    [LibraryImport(LibName)]
    internal static partial void PSXCore_SetTimerSync(IntPtr core, int timer, int active);

    [LibraryImport(LibName)]
    internal static partial void PSXCore_ResetTimers(IntPtr core);

    [LibraryImport(LibName)]
    internal static partial int PSXCore_Step(IntPtr core);

    [LibraryImport(LibName)]
    internal static partial int PSXCore_Run(IntPtr core, uint maxInstructions);

    [LibraryImport(LibName)]
    internal static partial uint PSXCore_ReadMemory32(IntPtr core, uint address);

    [LibraryImport(LibName)]
    internal static partial void PSXCore_WriteMemory32(IntPtr core, uint address, uint value);

    [LibraryImport(LibName)]
    internal static partial ushort PSXCore_ReadMemory16(IntPtr core, uint address);

    [LibraryImport(LibName)]
    internal static partial void PSXCore_WriteMemory16(IntPtr core, uint address, ushort value);

    [LibraryImport(LibName)]
    internal static partial byte PSXCore_ReadMemory8(IntPtr core, uint address);

    [LibraryImport(LibName)]
    internal static partial void PSXCore_WriteMemory8(IntPtr core, uint address, byte value);
}
