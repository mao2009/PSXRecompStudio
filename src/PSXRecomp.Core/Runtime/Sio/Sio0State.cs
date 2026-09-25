using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Sio;

/// <summary>
/// Pure domain model of the SIO0 (controller / memory card serial port)
/// register file (Issue #542). No I/O, no side effects. <see cref="Sio0Device"/>
/// derives SIO_STAT from these fields rather than from a magic status word.
/// </summary>
[Domain]
public sealed class Sio0State
{
    /// <summary>Hardware RX FIFO depth in bytes.</summary>
    public const int RxFifoCapacity = 8;

    /// <summary>SIO_MODE (0x1F801048), bits 0-8; bits 9-15 always read 0.</summary>
    public ushort Mode { get; set; }

    /// <summary>
    /// SIO_CTRL (0x1F80104A) as stored: bits 0-3, 5, 7-13. Bit4 (acknowledge) and
    /// bit6 (reset) are write-only actions and bits 14-15 always read 0.
    /// </summary>
    public ushort Control { get; set; }

    /// <summary>SIO_BAUD (0x1F80104E) reload value.</summary>
    public ushort Baud { get; set; }

    /// <summary>Last byte written to SIO_TX_DATA. Not transmitted: no transfer is modeled (Issue #543).</summary>
    public byte TxData { get; set; }

    /// <summary>Received bytes awaiting a SIO_RX_DATA read. Nothing fills it in Issue #542.</summary>
    public Queue<byte> RxFifo { get; } = new(RxFifoCapacity);

    /// <summary>Most recently popped RX byte; returned when SIO_RX_DATA is read with an empty FIFO.</summary>
    public byte LastRxData { get; set; }

    /// <summary>Resets every register to zero (power-on and SIO_CTRL.6 semantics).</summary>
    public void Reset()
    {
        Mode = 0;
        Control = 0;
        Baud = 0;
        TxData = 0;
        RxFifo.Clear();
        LastRxData = 0;
    }
}
