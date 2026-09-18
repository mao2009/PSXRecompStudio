using PSXRecomp.Architecture;
using PSXRecomp.Core.Runtime.Gpu;
using GpuInterface = PSXRecomp.Core.Runtime.IGpu;

namespace PSXRecomp.Core.Dma;

/// <summary>
/// GPU MMIO Runtime Adapter (Issue #440).
/// Bridges Physical Address → IMemoryBus → MMIO routing → managed <see cref="GpuDevice"/>.
/// Unlike the DMA/timer/interrupt adapters this does not touch the native core:
/// the GPU is a pure managed model. Implements <see cref="GpuInterface"/> and
/// <see cref="IMemoryBus"/> for register access.
/// </summary>
[Domain]
public sealed class GpuMmioAdapter : GpuInterface, IMemoryBus, IDisposable
{
    private readonly GpuDevice _device;
    private bool _disposed;

    public GpuMmioAdapter(GpuDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
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
        return Ps1MemoryMap.GetGpuRegisterType(address) switch
        {
            GpuRegisterType.Data => _device.ReadGpuread(),
            GpuRegisterType.Status => _device.ReadGpustat(),
            _ => 0,
        };
    }

    public void WriteRegister(uint address, uint value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        switch (Ps1MemoryMap.GetGpuRegisterType(address))
        {
            case GpuRegisterType.Data:
                _device.WriteGP0(value);
                break;
            case GpuRegisterType.Status:
                _device.WriteGP1(value);
                break;
        }
    }

    public void WriteGP0(uint command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _device.WriteGP0(command);
    }

    public void WriteGP1(uint command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _device.WriteGP1(command);
    }

    public uint ReadGpuread()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _device.ReadGpuread();
    }

    public uint ReadGpustat()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _device.ReadGpustat();
    }

    public IntPtr GetVramPointer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _device.GetVramPointer();
    }

    public (ushort Width, ushort Height) GetDisplayResolution()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _device.GetDisplayResolution();
    }

    public bool HasVblank
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _device.HasVblank;
        }
    }

    public void AcknowledgeVblank()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _device.AcknowledgeVblank();
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _device.Reset();
    }

    public void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    ~GpuMmioAdapter()
    {
        Dispose();
    }
}