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
/// Every member that touches native state calls
/// <see cref="ObjectDisposedException.ThrowIf"/> first, so using the wrapper
/// after <see cref="Dispose"/> throws deterministically instead of touching a
/// freed native pointer.
/// </para>
/// </remarks>
[Domain]
public sealed class PSXCoreWrapper : IDisposable
{
    private IntPtr _handle;
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

    // Instruction execution

    /// <summary>Executes a single instruction, honoring branch/load-delay slot semantics (ADR-004/005).</summary>
    /// <returns>Zero on success; a non-zero native status/exception code otherwise.</returns>
    public int Step()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeInterop.PSXCore_Step(_handle);
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
                NativeInterop.PSXCore_Destroy(_handle);
                _handle = IntPtr.Zero;
            }
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~PSXCoreWrapper()
    {
        Dispose();
    }
}
