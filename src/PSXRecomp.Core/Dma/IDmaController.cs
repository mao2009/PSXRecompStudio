using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Dma;

/// <summary>
/// DMA controller interface (Issue #44 Architecture contract).
/// Single Source of Truth for DMA register state.
/// </summary>
[Domain]
public interface IDmaController
{
    uint ReadRegister(uint address);
    void WriteRegister(uint address, uint value);
    bool GetInterruptPending();
    void SetInterruptCallback(Action<uint>? callback);
}
