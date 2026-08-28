using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Dma;

/// <summary>
/// Concrete IMemoryBus implementation for PS1 physical memory routing.
/// Routes physical addresses to RAM, BIOS, or MMIO handlers via MmioRoute.
/// </summary>
[Domain]
public sealed class MemoryBus : IMemoryBus, IDisposable
{
    private readonly PSXCoreWrapper _core;
    private DmaMmioAdapter? _dmaAdapter;
    private TimerMmioAdapter? _timerAdapter;
    private bool _disposed;

    public MemoryBus(PSXCoreWrapper core)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
    }

    public void AttachDmaAdapter(DmaMmioAdapter adapter)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _dmaAdapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public void AttachTimerAdapter(TimerMmioAdapter adapter)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _timerAdapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public uint Read(uint address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var _region = Ps1MemoryMap.ClassifyRegion(address);
        return _region switch
        {
            MemoryRegionClass.Ram => ReadRam(address),
            MemoryRegionClass.HardwareRegisters => ReadMmio(address),
            _ => 0,
        };
    }

    public void Write(uint address, uint value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var _region = Ps1MemoryMap.ClassifyRegion(address);
        switch (_region)
        {
            case MemoryRegionClass.Ram:
                WriteRam(address, value);
                break;
            case MemoryRegionClass.HardwareRegisters:
                WriteMmio(address, value);
                break;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _dmaAdapter = null;
            _timerAdapter = null;
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    private uint ReadRam(uint address)
    {
        unsafe
        {
            var _ptr = (uint*)_core.RamPointer;
            var _offset = address / sizeof(uint);
            return _ptr[_offset];
        }
    }

    private void WriteRam(uint address, uint value)
    {
        unsafe
        {
            var _ptr = (uint*)_core.RamPointer;
            var _offset = address / sizeof(uint);
            _ptr[_offset] = value;
        }
    }

    private uint ReadMmio(uint address)
    {
        var _route = MmioRoute.Resolve(address);
        return _route.Target switch
        {
            MmioTarget.DmaController => _dmaAdapter?.ReadRegister(address) ?? 0,
            MmioTarget.Timer => _timerAdapter?.ReadRegister(address) ?? 0,
            _ => 0,
        };
    }

    private void WriteMmio(uint address, uint value)
    {
        var _route = MmioRoute.Resolve(address);
        switch (_route.Target)
        {
            case MmioTarget.DmaController:
                _dmaAdapter?.WriteRegister(address, value);
                break;
            case MmioTarget.Timer:
                _timerAdapter?.WriteRegister(address, value);
                break;
        }
    }

    ~MemoryBus()
    {
        Dispose();
    }
}
