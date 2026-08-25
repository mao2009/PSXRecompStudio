using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// PS1 DMA Controller interface.
///
/// The PS1 has 7 DMA channels:
///   - Channel 0: MDECin (Macro Block Decode Input)
///   - Channel 1: MDECout (Macro Block Decode Output)
///   - Channel 2: GPU (Graphics)
///   - Channel 3: CD-ROM
///   - Channel 4: SPU
///   - Channel 5: PIO (Expansion Port)
///   - Channel 6: OTC (Reverse Clear OT)
///
/// DMA registers (per channel):
///   - DPCR (0x1F8010F0): DMA Control Register
///   - DICR (0x1F8010F4): DMA Interrupt Control Register
///   - Channel base + 0x00: MADR (Memory Address)
///   - Channel base + 0x04: BCR (Block Control)
///   - Channel base + 0x08: CHCR (Channel Control)
///
/// DMA transfer modes (CHCR bits 9-10):
///   - 0: Burst (transfer all at once after DREQ)
///   - 1: Slice (transfer in blocks on DREQ)
///   - 2: Linked-list (GPU command lists)
/// </summary>
[Domain]
public interface IDmaController
{
    /// <summary>
    /// DMA channel identifiers.
    /// </summary>
    enum DmaChannel
    {
        MdecIn = 0,
        MdecOut = 1,
        Gpu = 2,
        CdRom = 3,
        Spu = 4,
        Pio = 5,
        Otc = 6
    }

    /// <summary>
    /// DMA transfer direction.
    /// </summary>
    enum DmaDirection
    {
        ToRam = 0,
        FromRam = 1
    }

    /// <summary>
    /// DMA transfer mode (CHCR bits 9-10).
    /// </summary>
    enum DmaSyncMode
    {
        Burst = 0,
        Slice = 1,
        LinkedList = 2
    }

    /// <summary>
    /// Check if DMA is enabled for a specific channel.
    /// </summary>
    bool IsChannelEnabled(DmaChannel channel);

    /// <summary>
    /// Get the DMA control register value.
    /// </summary>
    uint ControlRegister { get; }

    /// <summary>
    /// Get the DMA interrupt control register value.
    /// </summary>
    uint InterruptControlRegister { get; }

    /// <summary>
    /// Start a DMA transfer for the specified channel.
    /// </summary>
    /// <param name="channel">DMA channel.</param>
    void StartTransfer(DmaChannel channel);

    /// <summary>
    /// Abort a DMA transfer for the specified channel.
    /// </summary>
    /// <param name="channel">DMA channel.</param>
    void AbortTransfer(DmaChannel channel);

    /// <summary>
    /// Get the number of words transferred for a channel.
    /// </summary>
    /// <param name="channel">DMA channel.</param>
    /// <returns>Words transferred.</returns>
    uint GetTransferCount(DmaChannel channel);

    /// <summary>
    /// Update DMA control register.
    /// </summary>
    /// <param name="value">New control value.</param>
    void SetControlRegister(uint value);

    /// <summary>
    /// Update DMA interrupt control register.
    /// </summary>
    /// <param name="value">New interrupt control value.</param>
    void SetInterruptControlRegister(uint value);

    /// <summary>
    /// Reset the DMA controller to initial state.
    /// </summary>
    void Reset();
}
