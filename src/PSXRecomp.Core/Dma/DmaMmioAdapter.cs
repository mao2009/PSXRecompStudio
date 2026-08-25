using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Dma;

/// <summary>
/// DMA MMIO Runtime Adapter (Issue #124).
/// Bridges Physical Address → IMemoryBus → MMIO routing → DMA controller.
/// Implements both IDmaController and IMemoryBus for DMA register access.
/// </summary>
[Domain]
public sealed class DmaMmioAdapter : IDmaController, IMemoryBus, IDisposable
{
    private readonly PSXCoreWrapper _core;
    private Action<uint>? _interruptCallback;
    private bool _disposed;

    public DmaMmioAdapter(PSXCoreWrapper core)
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
        return _core.ReadDmaRegister(address);
    }

    public void WriteRegister(uint address, uint value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _core.WriteDmaRegister(address, value);
        EvaluateInterrupt();
    }

    public bool GetInterruptPending()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _core.GetDmaInterruptPending();
    }

    public void SetInterruptCallback(Action<uint>? callback)
    {
        _interruptCallback = callback;
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

    private void EvaluateInterrupt()
    {
        if (_interruptCallback is null)
            return;

        if (GetInterruptPending())
            _interruptCallback.Invoke(Ps1MemoryMap.Dicr);
    }

    ~DmaMmioAdapter()
    {
        Dispose();
    }
}
