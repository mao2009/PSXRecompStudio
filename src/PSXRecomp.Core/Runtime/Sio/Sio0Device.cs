using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Sio;

/// <summary>
/// Managed SIO0 (controller / memory card serial port) register model
/// (Issue #542). A pure managed model like <c>GpuDevice</c> (ADR-022 precedent):
/// deterministic read/write semantics for SIO_DATA, SIO_STAT, SIO_MODE, SIO_CTRL
/// and SIO_BAUD over a <see cref="Sio0State"/> register file.
///
/// Scope: register semantics only. No serial transfer, controller or
/// memory-card protocol is modeled (Issue #543), so:
/// <list type="bullet">
/// <item>A SIO_TX_DATA write is latched in <see cref="TxData"/> but never transmitted.</item>
/// <item>The RX FIFO is filled only through <see cref="EnqueueReceivedByte"/>, the
/// seam a transaction model will drive; nothing calls it in production yet.</item>
/// <item>No IRQ7 is ever raised; SIO_STAT.9 always reads 0 and SIO_CTRL.4
/// (acknowledge) is accepted with no observable effect.</item>
/// </list>
/// </summary>
[Domain]
public sealed class Sio0Device
{
    /// <summary>SIO_MODE writable bits (0-8).</summary>
    public const ushort ModeWriteMask = 0x01FF;

    /// <summary>SIO_CTRL stored bits: 0-13 minus bit4 (acknowledge) and bit6 (reset), which are write-only actions.</summary>
    public const ushort ControlStoreMask = 0x3FAF;

    /// <summary>SIO_CTRL.6: reset every SIO0 register to zero.</summary>
    public const ushort ControlResetBit = 0x0040;

    /// <summary>SIO_STAT.0: TX ready flag 1 (TX latch free).</summary>
    public const uint StatusTxReady1 = 1u << 0;

    /// <summary>SIO_STAT.1: RX FIFO not empty.</summary>
    public const uint StatusRxNotEmpty = 1u << 1;

    /// <summary>SIO_STAT.2: TX ready flag 2 (no transfer in progress).</summary>
    public const uint StatusTxReady2 = 1u << 2;

    private readonly Sio0State _state = new();

    /// <summary>Last byte written to SIO_TX_DATA (never transmitted, see class remarks).</summary>
    public byte TxData => _state.TxData;

    /// <summary>
    /// SIO_RX_DATA read: pops the oldest RX FIFO byte. With an empty FIFO it
    /// returns the most recently popped byte again (0 after reset).
    /// </summary>
    public byte ReadData()
    {
        if (_state.RxFifo.Count > 0)
            _state.LastRxData = _state.RxFifo.Dequeue();
        return _state.LastRxData;
    }

    /// <summary>SIO_TX_DATA write: latches the byte. No transfer starts (Issue #543).</summary>
    public void WriteData(byte value) => _state.TxData = value;

    /// <summary>
    /// SIO_STAT read. TX ready 1/2 (bits 0, 2) read 1 because no transfer can be
    /// in progress in this model — the hardware idle value, not a claim that a
    /// transfer completed. RX not empty (bit 1) reflects the FIFO. Parity error
    /// (3), DSR/ACK input (7), IRQ (9) and the baud timer (11-31) read 0: no
    /// device, transfer, interrupt source or timer is modeled. SIO1-only bits
    /// (4-6, 8) and bit 10 are always 0.
    /// </summary>
    public uint ReadStatus()
    {
        uint status = StatusTxReady1 | StatusTxReady2;
        if (_state.RxFifo.Count > 0)
            status |= StatusRxNotEmpty;
        return status;
    }

    public ushort ReadMode() => _state.Mode;

    public void WriteMode(ushort value) => _state.Mode = (ushort)(value & ModeWriteMask);

    public ushort ReadControl() => _state.Control;

    /// <summary>
    /// SIO_CTRL write. Bit6 (reset) zeroes every SIO0 register, including
    /// SIO_CTRL itself, and wins over the other bits in the same write. Bit4
    /// (acknowledge) has nothing to clear yet and is not stored.
    /// </summary>
    public void WriteControl(ushort value)
    {
        if ((value & ControlResetBit) != 0)
        {
            _state.Reset();
            return;
        }
        _state.Control = (ushort)(value & ControlStoreMask);
    }

    public ushort ReadBaud() => _state.Baud;

    public void WriteBaud(ushort value) => _state.Baud = value;

    /// <summary>
    /// Transaction-side seam (Issue #543): appends a received byte to the RX
    /// FIFO. A byte arriving while the 8-byte FIFO is full is dropped.
    /// </summary>
    public void EnqueueReceivedByte(byte value)
    {
        if (_state.RxFifo.Count < Sio0State.RxFifoCapacity)
            _state.RxFifo.Enqueue(value);
    }

    /// <summary>Power-on reset: every register reads 0 except the idle SIO_STAT bits.</summary>
    public void Reset() => _state.Reset();
}
