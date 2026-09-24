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
    private GpuMmioAdapter? _gpuAdapter;
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

    public void AttachGpuAdapter(GpuMmioAdapter adapter)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gpuAdapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public uint Read32(uint address) => Read(address);

    public void Write32(uint address, uint value) => Write(address, value);

    public ushort Read16(uint address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var region = Ps1MemoryMap.ClassifyRegion(address);
        if (region is not (MemoryRegionClass.Ram or MemoryRegionClass.Scratchpad))
            return (ushort)(Read(address) & 0xFFFF);
        uint word = ReadWord(address & ~3u);
        return (ushort)((word >> (8 * (int)(address & 2))) & 0xFFFF);
    }

    public void Write16(uint address, ushort value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var region = Ps1MemoryMap.ClassifyRegion(address);
        if (region == MemoryRegionClass.HardwareRegisters)
        {
            WriteMmio(address, value);
            return;
        }
        if (region is not (MemoryRegionClass.Ram or MemoryRegionClass.Scratchpad))
            return;
        uint word = ReadWord(address & ~3u);
        int shift = 8 * (int)(address & 2);
        word = (word & ~(0xFFFFu << shift)) | ((uint)value << shift);
        WriteWord(address & ~3u, word);
    }

    public byte Read8(uint address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var region = Ps1MemoryMap.ClassifyRegion(address);
        if (region is not (MemoryRegionClass.Ram or MemoryRegionClass.Scratchpad))
            return (byte)(Read(address) & 0xFF);
        uint word = ReadWord(address & ~3u);
        return (byte)((word >> (8 * (int)(address & 3))) & 0xFF);
    }

    public void Write8(uint address, byte value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var region = Ps1MemoryMap.ClassifyRegion(address);
        if (region == MemoryRegionClass.HardwareRegisters)
        {
            WriteMmio(address, value);
            return;
        }
        if (region is not (MemoryRegionClass.Ram or MemoryRegionClass.Scratchpad))
            return;
        uint word = ReadWord(address & ~3u);
        int shift = 8 * (int)(address & 3);
        word = (word & ~(0xFFu << shift)) | ((uint)value << shift);
        WriteWord(address & ~3u, word);
    }

    public uint Read(uint address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var region = Ps1MemoryMap.ClassifyRegion(address);
        return region switch
        {
            MemoryRegionClass.Ram => ReadRam(address),
            MemoryRegionClass.Scratchpad => _core.ReadMemory32(address),
            MemoryRegionClass.HardwareRegisters => ReadMmio(address),
            _ => 0,
        };
    }

    public void Write(uint address, uint value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var region = Ps1MemoryMap.ClassifyRegion(address);
        switch (region)
        {
            case MemoryRegionClass.Ram:
                WriteRam(address, value);
                break;
            case MemoryRegionClass.Scratchpad:
                _core.WriteMemory32(address, value);
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
            _gpuAdapter = null;
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    private uint ReadRam(uint address)
    {
        unsafe
        {
            var ptr = (uint*)_core.RamPointer;
            // Main RAM aliases across the low mirror window (Issue #386): drop
            // the mirror bit instead of indexing past the 2 MiB buffer.
            var offset = (address & (Ps1MemoryMap.RamSize - 1)) / sizeof(uint);
            return ptr[offset];
        }
    }

    private void WriteRam(uint address, uint value)
    {
        unsafe
        {
            var ptr = (uint*)_core.RamPointer;
            var offset = (address & (Ps1MemoryMap.RamSize - 1)) / sizeof(uint);
            ptr[offset] = value;
        }
    }

    private uint ReadWord(uint address) =>
        Ps1MemoryMap.ClassifyRegion(address) == MemoryRegionClass.Scratchpad
            ? _core.ReadMemory32(address)
            : ReadRam(address);

    private void WriteWord(uint address, uint value)
    {
        if (Ps1MemoryMap.ClassifyRegion(address) == MemoryRegionClass.Scratchpad)
            _core.WriteMemory32(address, value);
        else
            WriteRam(address, value);
    }

    private uint ReadMmio(uint address)
    {
        var _route = MmioRoute.Resolve(address);
        return _route.Target switch
        {
            MmioTarget.DmaController => _dmaAdapter?.ReadRegister(address) ?? 0,
            MmioTarget.Timer => _timerAdapter?.ReadRegister(address) ?? 0,
            MmioTarget.InterruptController => _interruptControllerAdapter?.ReadRegister(address) ?? 0,
            MmioTarget.Gpu => _gpuAdapter?.ReadRegister(address) ?? 0,
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
            case MmioTarget.Gpu:
                _gpuAdapter?.WriteRegister(address, value);
                break;
        }
    }

    ~MemoryBus()
    {
        Dispose();
    }
}
