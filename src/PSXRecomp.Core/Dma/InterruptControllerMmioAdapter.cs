using PSXRecomp.Architecture;
using PSXRecomp.Core.Runtime;
using InterruptControllerInterface = PSXRecomp.Core.Runtime.IInterruptController;

namespace PSXRecomp.Core.Dma;

/// <summary>
/// PS1 Interrupt Controller MMIO Runtime Adapter (Issue #143).
/// Bridges Physical Address → IMemoryBus → MMIO routing → native interrupt controller.
/// Implements IInterruptController (Issue #143 contract) and IMemoryBus for register access.
/// </summary>
[Domain]
public sealed class InterruptControllerMmioAdapter : InterruptControllerInterface, IMemoryBus, IDisposable
{
    private readonly PSXCoreWrapper _core;
    private Action<uint>? _interruptCallback;
    private bool _reported;
    private bool _disposed;

    public InterruptControllerMmioAdapter(PSXCoreWrapper core)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
    }

    public uint Read(uint address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ReadRegister(address);
    }

    public void Write(uint address, uint value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WriteRegister(address, value);
    }

    public uint ReadRegister(uint address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _core.ReadInterruptControllerRegister(address);
    }

    public void WriteRegister(uint address, uint value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _core.WriteInterruptControllerRegister(address, value);
        EvaluateInterrupt();
    }

    /// <summary>
    /// Drive the external synchronization callback for the aggregated pending line.
    /// Invoked once per pending edge, until the pending condition is cleared.
    /// </summary>
    public void SetInterruptCallback(Action<uint>? callback)
    {
        _interruptCallback = callback;
        _reported = false;
    }

    public bool HasPendingInterrupts
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _core.GetInterruptPending();
        }
    }

    public uint Status
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _core.ReadInterruptControllerRegister(Ps1MemoryMap.IStat);
        }
    }

    public uint Mask
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _core.ReadInterruptControllerRegister(Ps1MemoryMap.IMask);
        }
    }

    public void Raise(int irq)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _core.RaiseInterrupt(irq);
        EvaluateInterrupt();
    }

    public void Clear(int irq)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _core.ClearInterrupt(irq);
        EvaluateInterrupt();
    }

    public void Acknowledge(uint value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WriteRegister(Ps1MemoryMap.IStat, value);
    }

    public void SetMask(uint value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WriteRegister(Ps1MemoryMap.IMask, value);
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _core.ResetInterruptController();
        _reported = false;
        EvaluateInterrupt();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _interruptCallback = null;
            _reported = false;
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    private void EvaluateInterrupt()
    {
        if (_interruptCallback is null)
            return;

        if (HasPendingInterrupts)
        {
            // Report each newly-pending aggregated line once (one-shot),
            // until it is cleared (acknowledged via I_STAT, unmasked, or reset).
            if (!_reported)
            {
                _reported = true;
                _interruptCallback.Invoke(Ps1MemoryMap.IStat);
            }
        }
        else
        {
            // No longer pending: allow the next pending edge to be reported again.
            _reported = false;
        }
    }

    ~InterruptControllerMmioAdapter()
    {
        Dispose();
    }
}