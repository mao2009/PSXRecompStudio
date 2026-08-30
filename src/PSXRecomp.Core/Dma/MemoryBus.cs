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
    private InterruptControllerMmioAdapter? _interruptControllerAdapter;
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

    public void AttachInterruptControllerAdapter(InterruptControllerMmioAdapter adapter)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _interruptControllerAdapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public uint Read32(uint address) => Read(address);

    public void Write32(uint address, uint value) => Write(address, value);

    public ushort Read16(uint address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Ps1MemoryMap.ClassifyRegion(address) != MemoryRegionClass.Ram)
            return (ushort)(Read(address) & 0xFFFF);
        uint _word = ReadRam(address & ~3u);
        return (ushort)((_word >> (8 * (int)(address & 2))) & 0xFFFF);
    }

    public void Write16(uint address, ushort value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var _region = Ps1MemoryMap.ClassifyRegion(address);
        if (_region == MemoryRegionClass.HardwareRegisters)
        {
            WriteMmio(address, value);
            return;
        }
        if (_region != MemoryRegionClass.Ram)
            return;
        uint word = ReadRam(address & ~3u);
        int _shift = 8 * (int)(address & 2);
        word = (word & ~(0xFFFFu << _shift)) | ((uint)value << _shift);
        WriteRam(address & ~3u, word);
    }

    public byte Read8(uint address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Ps1MemoryMap.ClassifyRegion(address) != MemoryRegionClass.Ram)
            return (byte)(Read(address) & 0xFF);
        uint _word = ReadRam(address & ~3u);
        return (byte)((_word >> (8 * (int)(address & 3))) & 0xFF);
    }

    public void Write8(uint address, byte value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var _region = Ps1MemoryMap.ClassifyRegion(address);
        if (_region == MemoryRegionClass.HardwareRegisters)
        {
            WriteMmio(address, value);
            return;
        }
        if (_region != MemoryRegionClass.Ram)
            return;
        uint word = ReadRam(address & ~3u);
        int _shift = 8 * (int)(address & 3);
        word = (word & ~(0xFFu << _shift)) | ((uint)value << _shift);
        WriteRam(address & ~3u, word);
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
            _interruptControllerAdapter = null;
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
            MmioTarget.InterruptController => _interruptControllerAdapter?.ReadRegister(address) ?? 0,
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
            case MmioTarget.InterruptController:
                _interruptControllerAdapter?.WriteRegister(address, value);
                break;
        }
    }

    ~MemoryBus()
    {
        Dispose();
    }
}
