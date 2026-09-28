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

    /// <summary>Advance active DMA channels by deterministic model cycles.</summary>
    void Tick(uint cycles);

    void SetInterruptCallback(Action<uint>? callback);
}
