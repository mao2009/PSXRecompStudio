using PSXRecomp.Architecture;
using PSXRecomp.Core.Runtime;
using TimerId = PSXRecomp.Core.Runtime.ITimer.TimerId;

namespace PSXRecomp.Core.Dma;

/// <summary>
/// PS1 Timer MMIO Runtime Adapter (Issue #125).
/// Bridges Physical Address → IMemoryBus → MMIO routing → native timer controller.
/// Implements both ITimer (Issue #44 contract) and IMemoryBus for timer register access.
/// </summary>
[Domain]
public sealed class TimerMmioAdapter : global::PSXRecomp.Core.Runtime.ITimer, IMemoryBus, IDisposable
{
    private readonly PSXCoreWrapper _core;
    private Action<uint>? _interruptCallback;
    private readonly bool[] _reported = new bool[Ps1MemoryMap.TimerCount];
    private bool _disposed;

    public TimerMmioAdapter(PSXCoreWrapper core)
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
        return _core.ReadTimerRegister(address);
    }

    public void WriteRegister(uint address, uint value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _core.WriteTimerRegister(address, value);
        EvaluateInterrupts();
    }

    public void SetInterruptCallback(Action<uint>? callback)
    {
        _interruptCallback = callback;
        for (int i = 0; i < _reported.Length; i++)
            _reported[i] = false;
    }

    // ITimer implementation.

    public uint GetCount(TimerId timer) =>
        ReadRegister(Ps1MemoryMap.GetTimerBase((int)timer) + Ps1MemoryMap.TimerCountOffset);

    public uint GetTarget(TimerId timer) =>
        ReadRegister(Ps1MemoryMap.GetTimerBase((int)timer) + Ps1MemoryMap.TimerTargetOffset);

    public uint GetMode(TimerId timer) =>
        ReadRegister(Ps1MemoryMap.GetTimerBase((int)timer) + Ps1MemoryMap.TimerModeOffset);

    public void SetTarget(TimerId timer, uint value) =>
        WriteRegister(Ps1MemoryMap.GetTimerBase((int)timer) + Ps1MemoryMap.TimerTargetOffset, value);

    public void SetMode(TimerId timer, uint value) =>
        WriteRegister(Ps1MemoryMap.GetTimerBase((int)timer) + Ps1MemoryMap.TimerModeOffset, value);

    public void Tick(uint cycles)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _core.TickTimers(cycles);
        EvaluateInterrupts();
    }

    public bool HasInterrupt(TimerId timer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _core.GetTimerInterruptPending((int)timer);
    }

    public void AcknowledgeInterrupt(TimerId timer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _core.ClearTimerInterrupt((int)timer);
        _reported[(int)timer] = false;
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _core.ResetTimers();
        for (int i = 0; i < _reported.Length; i++)
            _reported[i] = false;
    }

    /// <summary>
    /// Drive the external synchronization line (Hblank/Vblank) for a timer.
    /// Used to exercise Timer 0/1 synchronization modes.
    /// </summary>
    public void SetSyncLine(TimerId timer, bool active)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _core.SetTimerSync((int)timer, active);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _interruptCallback = null;
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    private void EvaluateInterrupts()
    {
        if (_interruptCallback is null)
            return;

        for (int i = 0; i < Ps1MemoryMap.TimerCount; i++)
        {
            if (_core.GetTimerInterruptPending(i) && !_reported[i])
            {
                _reported[i] = true;
                _interruptCallback.Invoke(Ps1MemoryMap.GetTimerBase(i));
            }
        }
    }

    ~TimerMmioAdapter()
    {
        Dispose();
    }
}
