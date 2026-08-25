using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// PS1 Interrupt Controller interface.
///
/// The interrupt controller manages hardware interrupts from various sources.
/// It supports two interrupt mask registers:
///   - I_STAT (0x1F801070): Interrupt status (which interrupts are pending)
///   - I_MASK (0x1F801074): Interrupt mask (which interrupts are enabled)
///
/// Interrupt sources:
///   - IRQ0: VBlank (GPU vertical blank)
///   - IRQ1: GPU command interrupt
///   - IRQ2: CD-ROM
///   - IRQ3: DMA
///   - IRQ4: Timer 0
///   - IRQ5: Timer 1
///   - IRQ6: Timer 2
///   - IRQ7: Controller/Memory Card (byte received)
///   - IRQ8: SIO (serial interface)
///   - IRQ9: SPU (sound processing)
///   - IRQ10: PIO / Controller lightpen
/// </summary>
[Domain]
public interface IInterruptController
{
    /// <summary>
    /// Check if any interrupts are pending and enabled.
    /// </summary>
    bool HasPendingInterrupts { get; }

    /// <summary>
    /// Get the current interrupt status register value.
    /// </summary>
    uint Status { get; }

    /// <summary>
    /// Get the current interrupt mask register value.
    /// </summary>
    uint Mask { get; }

    /// <summary>
    /// Raise an interrupt from a hardware component.
    /// </summary>
    /// <param name="irq">Interrupt source (0-10).</param>
    void Raise(int irq);

    /// <summary>
    /// Clear an interrupt request.
    /// </summary>
    /// <param name="irq">Interrupt source (0-10).</param>
    void Clear(int irq);

    /// <summary>
    /// Acknowledge interrupts (write to I_STAT to clear).
    /// </summary>
    /// <param name="value">Value written to I_STAT (write-0-to-clear).</param>
    void Acknowledge(uint value);

    /// <summary>
    /// Update the interrupt mask.
    /// </summary>
    /// <param name="value">New mask value.</param>
    void SetMask(uint value);

    /// <summary>
    /// Reset the interrupt controller to initial state.
    /// </summary>
    void Reset();
}
