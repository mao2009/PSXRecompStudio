using System.Runtime.InteropServices;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core;

/// <summary>
/// Raw P/Invoke surface for the native <c>PSXRecomp.Native</c> C ABI
/// (<c>src/PSXRecomp.Native/include/psx_core.h</c>).
/// </summary>
/// <remarks>
/// This is the interop boundary named by the project documentation policy
/// (docs/development/documentation-policy.md): every signature here mirrors a
/// native <c>PSX_API</c> function one-to-one, so ownership, lifetime, and unit
/// semantics documented here must stay in lockstep with the C header. It is
/// deliberately kept <c>internal</c> and thin; callers use the managed,
/// disposal-safe wrapper <see cref="PSXCoreWrapper"/> instead of this class
/// directly. All <c>core</c> parameters are the opaque native handle returned
/// by <see cref="PSXCore_Create"/>; the caller never dereferences it, and it
/// must be released exactly once via <see cref="PSXCore_Destroy"/>.
/// </remarks>
[Domain]
internal static partial class NativeInterop
{
    private const string LibName = "PSXRecomp.Native";

    /// <summary>Allocates a native PSX core instance. Ownership transfers to the caller.</summary>
    /// <returns>An opaque handle, or <see cref="IntPtr.Zero"/> on allocation failure.</returns>
    [LibraryImport(LibName)]
    internal static partial IntPtr PSXCore_Create();

    /// <summary>Releases a native core handle previously returned by <see cref="PSXCore_Create"/>. Must be called exactly once.</summary>
    [LibraryImport(LibName)]
    internal static partial void PSXCore_Destroy(IntPtr core);

    /// <summary>Resets CPU registers, COP0 state, and memory-mapped subsystems to their power-on state.</summary>
    [LibraryImport(LibName)]
    internal static partial void PSXCore_Reset(IntPtr core);

    /// <summary>Reads general-purpose register <paramref name="index"/> (0-31; R0 always reads as 0).</summary>
    [LibraryImport(LibName)]
    internal static partial uint PSXCore_GetGPR(IntPtr core, int index);

    /// <summary>Writes general-purpose register <paramref name="index"/> (0-31; writes to R0 are silently ignored by the core).</summary>
    [LibraryImport(LibName)]
    internal static partial void PSXCore_SetGPR(IntPtr core, int index, uint value);

    /// <summary>Reads the current program counter.</summary>
    [LibraryImport(LibName)]
    internal static partial uint PSXCore_GetPC(IntPtr core);

    /// <summary>Sets the program counter and flushes pending branch/load-delay pipeline state (see ADR-004/005).</summary>
    [LibraryImport(LibName)]
    internal static partial void PSXCore_SetPC(IntPtr core, uint value);

    /// <summary>Reads the HI register of the multiply/divide unit.</summary>
    [LibraryImport(LibName)]
    internal static partial uint PSXCore_GetHI(IntPtr core);

    /// <summary>Writes the HI register of the multiply/divide unit.</summary>
    [LibraryImport(LibName)]
    internal static partial void PSXCore_SetHI(IntPtr core, uint value);

    /// <summary>Reads the LO register of the multiply/divide unit.</summary>
    [LibraryImport(LibName)]
    internal static partial uint PSXCore_GetLO(IntPtr core);

    /// <summary>Writes the LO register of the multiply/divide unit.</summary>
    [LibraryImport(LibName)]
    internal static partial void PSXCore_SetLO(IntPtr core, uint value);

    /// <summary>Returns a pointer to the native 2 MiB main-RAM buffer owned by <paramref name="core"/>. Valid only until the core is destroyed.</summary>
    [LibraryImport(LibName)]
    internal static partial IntPtr PSXCore_GetRAM(IntPtr core);

    /// <summary>Returns the fixed PS1 main-RAM size in bytes (see <see cref="PSXCoreWrapper.RamSize"/>).</summary>
    [LibraryImport(LibName)]
    internal static partial uint PSXCore_GetRAMSize();

    /// <summary>Reads a DMA controller register at the given absolute address.</summary>
    [LibraryImport(LibName)]
    internal static partial uint PSXCore_ReadDmaRegister(IntPtr core, uint address);

    /// <summary>Writes a DMA controller register at the given absolute address.</summary>
    [LibraryImport(LibName)]
    internal static partial void PSXCore_WriteDmaRegister(IntPtr core, uint address, uint value);

    /// <summary>Returns non-zero when a DMA-triggered interrupt is pending.</summary>
    [LibraryImport(LibName)]
    internal static partial int PSXCore_GetDmaInterruptPending(IntPtr core);

    /// <summary>Reads a timer (0-2) register at the given absolute address.</summary>
    [LibraryImport(LibName)]
    internal static partial uint PSXCore_ReadTimerRegister(IntPtr core, uint address);

    /// <summary>Writes a timer (0-2) register at the given absolute address.</summary>
    [LibraryImport(LibName)]
    internal static partial void PSXCore_WriteTimerRegister(IntPtr core, uint address, uint value);

    /// <summary>Advances all timer counters by <paramref name="cycles"/> CPU clock cycles, evaluating targets/overflow/sync per timer mode.</summary>
    [LibraryImport(LibName)]
    internal static partial void PSXCore_TickTimers(IntPtr core, uint cycles);

    /// <summary>Returns non-zero when the given timer (0-2) has a pending interrupt.</summary>
    [LibraryImport(LibName)]
    internal static partial int PSXCore_GetTimerInterruptPending(IntPtr core, int timer);

    /// <summary>Acknowledges/clears the pending interrupt for the given timer (0-2).</summary>
    [LibraryImport(LibName)]
    internal static partial void PSXCore_ClearTimerInterrupt(IntPtr core, int timer);

    /// <summary>Sets whether the given timer (0-2) is currently synchronized/paused by its configured sync source.</summary>
    [LibraryImport(LibName)]
    internal static partial void PSXCore_SetTimerSync(IntPtr core, int timer, int active);

    /// <summary>Resets all timer counters, modes, and pending interrupts to their power-on state.</summary>
    [LibraryImport(LibName)]
    internal static partial void PSXCore_ResetTimers(IntPtr core);

    /// <summary>Reads an interrupt controller (I_STAT/I_MASK) register at the given absolute address.</summary>
    [LibraryImport(LibName)]
    internal static partial uint PSXCore_ReadInterruptControllerRegister(IntPtr core, uint address);

    /// <summary>Writes an interrupt controller (I_STAT/I_MASK) register at the given absolute address.</summary>
    [LibraryImport(LibName)]
    internal static partial void PSXCore_WriteInterruptControllerRegister(IntPtr core, uint address, uint value);

    /// <summary>Returns non-zero when any unmasked interrupt is pending (I_STAT and I_MASK combined).</summary>
    [LibraryImport(LibName)]
    internal static partial int PSXCore_GetInterruptPending(IntPtr core);

    /// <summary>Raises (sets pending) the given IRQ line (see <c>docs/cpu/exceptions.md</c> for the IRQ numbering).</summary>
    [LibraryImport(LibName)]
    internal static partial void PSXCore_RaiseInterrupt(IntPtr core, int irq);

    /// <summary>Clears the pending state of the given IRQ line.</summary>
    [LibraryImport(LibName)]
    internal static partial void PSXCore_ClearInterrupt(IntPtr core, int irq);

    /// <summary>Resets the interrupt controller (I_STAT/I_MASK) to its power-on state.</summary>
    [LibraryImport(LibName)]
    internal static partial void PSXCore_ResetInterruptController(IntPtr core);

    /// <summary>Executes a single instruction, honoring branch/load-delay slot semantics.</summary>
    /// <returns>Zero on success; a non-zero native status/exception code otherwise.</returns>
    [LibraryImport(LibName)]
    internal static partial int PSXCore_Step(IntPtr core);

    /// <summary>Executes up to <paramref name="maxInstructions"/> instructions, stopping early on a native exception/halt condition.</summary>
    /// <returns>The number of instructions actually executed, or a negative status on error.</returns>
    [LibraryImport(LibName)]
    internal static partial int PSXCore_Run(IntPtr core, uint maxInstructions);

    /// <summary>Reads a 32-bit little-endian value from the CPU address space (RAM or MMIO), routed through <c>MemoryBus</c> semantics.</summary>
    [LibraryImport(LibName)]
    internal static partial uint PSXCore_ReadMemory32(IntPtr core, uint address);

    /// <summary>Writes a 32-bit little-endian value to the CPU address space (RAM or MMIO).</summary>
    [LibraryImport(LibName)]
    internal static partial void PSXCore_WriteMemory32(IntPtr core, uint address, uint value);

    /// <summary>Reads a 16-bit little-endian value from the CPU address space.</summary>
    [LibraryImport(LibName)]
    internal static partial ushort PSXCore_ReadMemory16(IntPtr core, uint address);

    /// <summary>Writes a 16-bit little-endian value to the CPU address space.</summary>
    [LibraryImport(LibName)]
    internal static partial void PSXCore_WriteMemory16(IntPtr core, uint address, ushort value);

    /// <summary>Reads an 8-bit value from the CPU address space.</summary>
    [LibraryImport(LibName)]
    internal static partial byte PSXCore_ReadMemory8(IntPtr core, uint address);

    /// <summary>Writes an 8-bit value to the CPU address space.</summary>
    [LibraryImport(LibName)]
    internal static partial void PSXCore_WriteMemory8(IntPtr core, uint address, byte value);
}
