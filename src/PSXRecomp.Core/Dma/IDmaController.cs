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

    /// <summary>
    /// Immediately completes <paramref name="channel"/>'s in-flight transfer,
    /// independent of elapsed cycles: clears its CHCR start/busy+trigger bits
    /// and sets its DICR flag when that channel's DICR enable is set. No
    /// other channel's CHCR, remaining duration, or DICR flag changes
    /// (Issue #587). Used by an owner that already moved a channel's data
    /// itself (the managed CD-ROM DMA3 bridge) and only needs the existing
    /// register-visible completion signal set once.
    /// </summary>
    void CompleteChannel(uint channel);

    void SetInterruptCallback(Action<uint>? callback);
}
