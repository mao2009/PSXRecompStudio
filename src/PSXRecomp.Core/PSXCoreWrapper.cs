using System.Runtime.InteropServices;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core;

/// <summary>
/// Managed, disposal-safe wrapper around a native PSX core instance.
/// </summary>
/// <remarks>
/// This is the public API surface named by the project documentation policy
/// (docs/development/documentation-policy.md): the primary entry point for
/// callers that need CPU/memory/MMIO access without touching the raw P/Invoke
/// declarations in <see cref="NativeInterop"/> directly.
///
/// <para>
/// <b>Ownership/lifetime:</b> each instance owns exactly one native core
/// handle, created in the constructor and released exactly once in
/// <see cref="Dispose"/> (also reachable via the finalizer as a safety net).
/// Every instance member that accesses the owned handle calls
/// <see cref="ObjectDisposedException.ThrowIf"/> first, so using the wrapper
/// after <see cref="Dispose"/> throws deterministically instead of touching a
/// freed native pointer. Static members that need no live instance (e.g.
/// <see cref="GetRamSize"/>) and lifecycle members (<see cref="Dispose"/>
/// itself) are exempt: they never touch the handle.
/// </para>
/// </remarks>
[Domain]
public sealed class PSXCoreWrapper : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GpuMmioRead32Callback(IntPtr context, uint address);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GpuMmioWrite32Callback(IntPtr context, uint address, uint value);

    private static readonly GpuMmioRead32Callback GpuRead32Thunk = ReadGpuMmio32;
    private static readonly GpuMmioWrite32Callback GpuWrite32Thunk = WriteGpuMmio32;

    private IntPtr _handle;
    private GCHandle _gpuMmioContext;
    private bool _disposed;

    /// <summary>Number of general-purpose registers (R0-R31) exposed by <see cref="GetGpr"/>/<see cref="SetGpr"/>.</summary>
    public const int GprCount = 32;

    /// <summary>Fixed PS1 main-RAM size in bytes (2 MiB).</summary>
    public const uint RamSize = 2 * 1024 * 1024;

    /// <summary>Creates a new native core instance and takes ownership of its handle.</summary>
    /// <exception cref="InvalidOperationException">The native core failed to allocate.</exception>
    public PSXCoreWrapper()
    {
        _handle = NativeInterop.PSXCore_Create();
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create PSXCore");
    }

    /// <summary>Resets CPU registers, COP0 state, and memory-mapped subsystems to their power-on state.</summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeInterop.PSXCore_Reset(_handle);
    }

    /// <summary>Reads general-purpose register <paramref name="index"/>. R0 always reads as 0.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside 0..<see cref="GprCount"/>-1.</exception>
    public uint GetGpr(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (index < 0 || index >= GprCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return NativeInterop.PSXCore_GetGPR(_handle, index);
    }

    /// <summary>Writes general-purpose register <paramref name="index"/>. Writes to R0 are silently ignored by the native core.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside 0..<see cref="GprCount"/>-1.</exception>
    public void SetGpr(int index, uint value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (index < 0 || index >= GprCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        NativeInterop.PSXCore_SetGPR(_handle, index, value);
    }

    /// <summary>The current program counter. Setting it flushes pending branch/load-delay pipeline state (ADR-004/005).</summary>
    public uint Pc
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return NativeInterop.PSXCore_GetPC(_handle);
        }
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            NativeInterop.PSXCore_SetPC(_handle, value);
        }
    }

    /// <summary>The HI register of the multiply/divide unit.</summary>
    public uint Hi
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return NativeInterop.PSXCore_GetHI(_handle);
        }
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            NativeInterop.PSXCore_SetHI(_handle, value);
        }
    }

    /// <summary>The LO register of the multiply/divide unit.</summary>
    public uint Lo
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return NativeInterop.PSXCore_GetLO(_handle);
        }
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            NativeInterop.PSXCore_SetLO(_handle, value);
        }
    }

    /// <summary>Reads COP0 register <paramref name="index"/> (docs/cpu/cop0.md: 12 = SR, 13 = CAUSE, 14 = EPC).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside 0..31.</exception>
    public uint GetCop0(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (index < 0 || index >= 32)
            throw new ArgumentOutOfRangeException(nameof(index));
        return NativeInterop.PSXCore_GetCop0(_handle, index);
    }

    /// <summary>Pointer to the native 2 MiB main-RAM buffer. Valid only until this instance is disposed; do not cache across a <see cref="Dispose"/> call.</summary>
    public IntPtr RamPointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return NativeInterop.PSXCore_GetRAM(_handle);
        }
    }

    /// <summary>Returns the fixed PS1 main-RAM size in bytes. Equivalent to <see cref="RamSize"/>; does not require a live instance.</summary>
    public static uint GetRamSize() => NativeInterop.PSXCore_GetRAMSize();

    /// <summary>
    /// Attaches a managed 32-bit GPU-MMIO target to this core's production CPU
    /// memory path (Issue #572). The callbacks remain owned and rooted by this
    /// wrapper until <see cref="DetachGpuMmio"/> or <see cref="Dispose"/>.
    /// </summary>
    internal void AttachGpuMmio(Func<uint, uint> read32, Action<uint, uint> write32)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(read32);
        ArgumentNullException.ThrowIfNull(write32);

        DetachGpuMmioCore();

        var state = new GpuMmioCallbackState(read32, write32);
        _gpuMmioContext = GCHandle.Alloc(state);
        try
        {
            NativeInterop.PSXCore_SetGpuMmioCallbacks(
                _handle,
                GCHandle.ToIntPtr(_gpuMmioContext),
                Marshal.GetFunctionPointerForDelegate(GpuRead32Thunk),
                Marshal.GetFunctionPointerForDelegate(GpuWrite32Thunk));
        }
        catch
        {
            _gpuMmioContext.Free();
            throw;
        }
    }

    /// <summary>
    /// Detaches the managed GPU-MMIO bridge before its target is disposed.
    /// Safe to call when no bridge is attached.
    /// </summary>
    internal void DetachGpuMmio()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DetachGpuMmioCore();
    }

    /// <summary>Reads a DMA controller register at the given absolute address.</summary>
    public uint ReadDmaRegister(uint address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeInterop.PSXCore_ReadDmaRegister(_handle, address);
    }

    /// <summary>Writes a DMA controller register at the given absolute address.</summary>
    public void WriteDmaRegister(uint address, uint value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeInterop.PSXCore_WriteDmaRegister(_handle, address, value);
    }

    /// <summary>Returns whether a DMA-triggered interrupt is pending.</summary>
    public bool GetDmaInterruptPending()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeInterop.PSXCore_GetDmaInterruptPending(_handle) != 0;
    }

    /// <summary>Advances started DMA transfers by <paramref name="cycles"/> CPU clock cycles; a transfer completes after its modelled duration (Issue #442).</summary>
    public void TickDma(uint cycles)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeInterop.PSXCore_TickDma(_handle, cycles);
    }

    /// <summary>Reads a timer (0-2) register at the given absolute address.</summary>
    public uint ReadTimerRegister(uint address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeInterop.PSXCore_ReadTimerRegister(_handle, address);
    }

    /// <summary>Writes a timer (0-2) register at the given absolute address.</summary>
    public void WriteTimerRegister(uint address, uint value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeInterop.PSXCore_WriteTimerRegister(_handle, address, value);
    }

    /// <summary>Advances all timer counters by <paramref name="cycles"/> CPU clock cycles, evaluating targets/overflow/sync per timer mode.</summary>
    public void TickTimers(uint cycles)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeInterop.PSXCore_TickTimers(_handle, cycles);
    }

    /// <summary>Returns whether the given timer (0-2) has a pending interrupt.</summary>
    public bool GetTimerInterruptPending(int timer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeInterop.PSXCore_GetTimerInterruptPending(_handle, timer) != 0;
    }

    /// <summary>Acknowledges/clears the pending interrupt for the given timer (0-2).</summary>
    public void ClearTimerInterrupt(int timer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeInterop.PSXCore_ClearTimerInterrupt(_handle, timer);
    }

    /// <summary>Sets whether the given timer (0-2) is currently synchronized/paused by its configured sync source.</summary>
    public void SetTimerSync(int timer, bool active)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeInterop.PSXCore_SetTimerSync(_handle, timer, active ? 1 : 0);
    }

    /// <summary>Resets all timer counters, modes, and pending interrupts to their power-on state.</summary>
    public void ResetTimers()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeInterop.PSXCore_ResetTimers(_handle);
    }

    // Interrupt controller (Issue #143)

    /// <summary>Reads an interrupt controller (I_STAT/I_MASK) register at the given absolute address.</summary>
    public uint ReadInterruptControllerRegister(uint address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeInterop.PSXCore_ReadInterruptControllerRegister(_handle, address);
    }

    /// <summary>Writes an interrupt controller (I_STAT/I_MASK) register at the given absolute address.</summary>
    public void WriteInterruptControllerRegister(uint address, uint value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeInterop.PSXCore_WriteInterruptControllerRegister(_handle, address, value);
    }

    /// <summary>Returns whether any unmasked interrupt is pending (I_STAT and I_MASK combined).</summary>
    public bool GetInterruptPending()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeInterop.PSXCore_GetInterruptPending(_handle) != 0;
    }

    /// <summary>Raises (sets pending) the given IRQ line (see <c>docs/cpu/exceptions.md</c> for the IRQ numbering).</summary>
    public void RaiseInterrupt(int irq)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeInterop.PSXCore_RaiseInterrupt(_handle, irq);
    }

    /// <summary>Clears the pending state of the given IRQ line.</summary>
    public void ClearInterrupt(int irq)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeInterop.PSXCore_ClearInterrupt(_handle, irq);
    }

    /// <summary>Resets the interrupt controller (I_STAT/I_MASK) to its power-on state.</summary>
    public void ResetInterruptController()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeInterop.PSXCore_ResetInterruptController(_handle);
    }

    /// <summary>Returns whether SIO0 has an unacknowledged "byte received" (IRQ7) latch (Issue #543).</summary>
    public bool GetSio0InterruptPending()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeInterop.PSXCore_GetSio0InterruptPending(_handle) != 0;
    }

    /// <summary>Acknowledges/clears SIO0's pending "byte received" (IRQ7) latch.</summary>
    public void ClearSio0Interrupt()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeInterop.PSXCore_ClearSio0Interrupt(_handle);
    }

    /// <summary>SIO0's last-transaction command classification (Issue #543): 0 = no command byte seen since the last transaction reset, 1 = recognized (the one supported command), 2 = unsupported/unrecognized.</summary>
    public uint GetSio0CommandStatus()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeInterop.PSXCore_GetSio0CommandStatus(_handle);
    }

    /// <summary>The command byte last classified unsupported (Issue #543); only meaningful when <see cref="GetSio0CommandStatus"/> returns 2.</summary>
    public byte GetSio0LastCommandByte()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return (byte)NativeInterop.PSXCore_GetSio0LastCommandByte(_handle);
    }

    // Instruction execution

    /// <summary>Executes a single instruction, honoring branch/load-delay slot semantics (ADR-004/005).</summary>
    /// <returns>
    /// Zero when the step was taken; a negative native status otherwise. A guest
    /// exception is deliberately not reported here — the step that takes a fault
    /// succeeds and lands the PC on the exception vector. Ask
    /// <see cref="ExceptionRaised"/> instead (Issue #377).
    /// </returns>
    public int Step()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeInterop.PSXCore_Step(_handle);
    }

    /// <summary>
    /// <see cref="Step"/> with the CPU's hardware interrupt input held low: device
    /// IRQs stay latched in I_STAT but raise no INT exception, and CAUSE.IP2 reads 0.
    /// For a caller that cannot continue into the exception handler.
    /// </summary>
    /// <returns>As <see cref="Step"/>.</returns>
    public int StepWithoutInterrupts()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeInterop.PSXCore_StepWithoutInterrupts(_handle);
    }

    /// <summary>
    /// Whether the most recent <see cref="Step"/> raised a guest exception
    /// (INT, SYSCALL, RI/CpU/AdEL/AdES). Reset by every step.
    /// </summary>
    public bool ExceptionRaised
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return NativeInterop.PSXCore_GetExceptionRaised(_handle) != 0;
        }
    }

    /// <summary>
    /// The CAUSE Excode of the exception the most recent <see cref="Step"/>
    /// raised (Issue #481). Meaningful only while <see cref="ExceptionRaised"/>
    /// is true; an unraised step leaves it at zero.
    /// </summary>
    public uint ExceptionCode
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return NativeInterop.PSXCore_GetExceptionCode(_handle);
        }
    }

    /// <summary>
    /// The faulting PC (EPC) of the exception the most recent <see cref="Step"/>
    /// raised (Issue #481): the owning branch PC when the faulting instruction
    /// was in a branch delay slot, else the faulting instruction's own PC.
    /// Meaningful only while <see cref="ExceptionRaised"/> is true.
    /// </summary>
    public uint ExceptionFaultPc
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return NativeInterop.PSXCore_GetExceptionFaultPc(_handle);
        }
    }

    /// <summary>
    /// Whether the faulting instruction of the most recent <see cref="Step"/>'s
    /// exception was in a branch delay slot (Issue #481). Meaningful only while
    /// <see cref="ExceptionRaised"/> is true.
    /// </summary>
    public bool ExceptionInDelaySlot
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return NativeInterop.PSXCore_GetExceptionInDelaySlot(_handle) != 0;
        }
    }

    /// <summary>
    /// Whether the most recent <see cref="Step"/> executed RFE, i.e. the guest
    /// returned from an exception handler (PR #502). Reset by every step.
    /// </summary>
    public bool RfeExecuted
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return NativeInterop.PSXCore_GetRfeExecuted(_handle) != 0;
        }
    }

    /// <summary>Executes up to <paramref name="maxInstructions"/> instructions, stopping early on a native exception/halt condition.</summary>
    /// <returns>The number of instructions actually executed, or a negative status on error.</returns>
    public int Run(uint maxInstructions)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeInterop.PSXCore_Run(_handle, maxInstructions);
    }

    // Memory access

    /// <summary>Reads a 32-bit little-endian value from the CPU address space (RAM or MMIO).</summary>
    public uint ReadMemory32(uint address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeInterop.PSXCore_ReadMemory32(_handle, address);
    }

    /// <summary>Writes a 32-bit little-endian value to the CPU address space (RAM or MMIO).</summary>
    public void WriteMemory32(uint address, uint value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeInterop.PSXCore_WriteMemory32(_handle, address, value);
    }

    /// <summary>Reads a 16-bit little-endian value from the CPU address space.</summary>
    public ushort ReadMemory16(uint address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeInterop.PSXCore_ReadMemory16(_handle, address);
    }

    /// <summary>Writes a 16-bit little-endian value to the CPU address space.</summary>
    public void WriteMemory16(uint address, ushort value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeInterop.PSXCore_WriteMemory16(_handle, address, value);
    }

    /// <summary>Reads an 8-bit value from the CPU address space.</summary>
    public byte ReadMemory8(uint address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeInterop.PSXCore_ReadMemory8(_handle, address);
    }

    /// <summary>Writes an 8-bit value to the CPU address space.</summary>
    public void WriteMemory8(uint address, byte value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeInterop.PSXCore_WriteMemory8(_handle, address, value);
    }

    /// <summary>Releases the native core handle. Safe to call multiple times; subsequent calls are no-ops.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            if (_handle != IntPtr.Zero)
            {
                DetachGpuMmioCore();
                NativeInterop.PSXCore_Destroy(_handle);
                _handle = IntPtr.Zero;
            }
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    private void DetachGpuMmioCore()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeInterop.PSXCore_SetGpuMmioCallbacks(
                _handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }

        if (_gpuMmioContext.IsAllocated)
        {
            _gpuMmioContext.Free();
        }
    }

    private static uint ReadGpuMmio32(IntPtr context, uint address)
    {
        if (context == IntPtr.Zero)
        {
            return 0;
        }

        var handle = GCHandle.FromIntPtr(context);
        return handle.Target is GpuMmioCallbackState state ? state.Read32(address) : 0;
    }

    private static void WriteGpuMmio32(IntPtr context, uint address, uint value)
    {
        if (context == IntPtr.Zero)
        {
            return;
        }

        var handle = GCHandle.FromIntPtr(context);
        if (handle.Target is GpuMmioCallbackState state)
        {
            state.Write32(address, value);
        }
    }

    private sealed class GpuMmioCallbackState
    {
        public GpuMmioCallbackState(Func<uint, uint> read32, Action<uint, uint> write32)
        {
            Read32 = read32;
            Write32 = write32;
        }

        public Func<uint, uint> Read32 { get; }

        public Action<uint, uint> Write32 { get; }
    }

    ~PSXCoreWrapper()
    {
        Dispose();
    }
}
