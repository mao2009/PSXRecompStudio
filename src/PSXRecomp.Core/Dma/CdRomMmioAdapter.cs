using PSXRecomp.Architecture;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Core.Dma;

/// <summary>
/// CD-ROM MMIO Runtime Adapter (Issue #587).
/// Bridges Physical Address -> IMemoryBus/native byte-MMIO callback -> the
/// managed <see cref="ICdRom"/>. Unlike the DMA/timer/interrupt adapters this
/// does not touch the native core's own register state: the CD-ROM
/// controller is a pure managed model, the same shape as
/// <see cref="GpuMmioAdapter"/> but at byte width, since real hardware and
/// this codebase's guest software address these four ports individually.
/// </summary>
[Domain]
public sealed class CdRomMmioAdapter : IMemoryBus, IDisposable
{
    private readonly ICdRom _cdRom;
    private bool _disposed;

    public CdRomMmioAdapter(ICdRom cdRom)
    {
        _cdRom = cdRom ?? throw new ArgumentNullException(nameof(cdRom));
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
        return _cdRom.ReadRegister((int)(address - Ps1MemoryMap.CdRomBase));
    }

    public void WriteRegister(uint address, uint value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _cdRom.WriteRegister((int)(address - Ps1MemoryMap.CdRomBase), (byte)value);
    }

    public void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    ~CdRomMmioAdapter()
    {
        Dispose();
    }
}
