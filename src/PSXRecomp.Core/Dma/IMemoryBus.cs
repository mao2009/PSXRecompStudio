using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Dma;

/// <summary>
/// Physical memory bus interface (Issue #44 Architecture contract).
/// Routes physical addresses to the appropriate memory region or MMIO handler.
/// </summary>
[Domain]
public interface IMemoryBus
{
    uint Read(uint address);
    void Write(uint address, uint value);
}
