using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.CdRom;

/// <summary>
/// Managed PS1 CD-ROM controller register/FIFO substrate (Issue #585).
/// Implements <see cref="ICdRom"/> for the 0x1F801800-0x1F801803 window:
/// <c>ReadRegister</c>/<c>WriteRegister</c> take the port (address &amp; 3);
/// ports 1-3 are multiplexed by the index selected through port 0 bits 0-1.
///
/// Scope: index selection, 16-byte parameter and response FIFOs, a status
/// register derived live from FIFO state, interrupt enable/flag registers, and
/// reset. No CD-ROM command is implemented yet (Issue #586): every command
/// fails closed with the controller's own "invalid command" error, INT5 with
/// response <c>[0x01, 0x40]</c>. There is no drive, disc, data FIFO, sound map,
/// DMA3 or IRQ2 wiring (Issue #587); <see cref="HasInterrupt"/> is only the
/// device-side line state.
/// </summary>
[Domain]
public sealed class CdRomDevice : ICdRom
{
    public const int FifoCapacity = 16;

    /// <summary>INT5: command error.</summary>
    public const byte IntError = 0x05;

    /// <summary>Response stat byte for an error: only the error bit (bit 0); no drive state is modeled.</summary>
    public const byte ErrorStat = 0x01;

    /// <summary>Hardware error code for an invalid/unsupported command.</summary>
    public const byte ErrorInvalidCommand = 0x40;

    private readonly Queue<byte> _parameters = new(FifoCapacity);
    private readonly Queue<byte> _responses = new(FifoCapacity);

    private int _index;
    private byte _interruptEnable;
    private byte _interruptFlag;

    /// <summary>Currently selected register index (0-3).</summary>
    public int Index => _index;

    /// <summary>Pending parameter bytes, oldest first (command dispatch will consume them in this order).</summary>
    public IReadOnlyCollection<byte> Parameters => _parameters.ToArray();

    public int ResponseCount => _responses.Count;

    /// <summary>Interrupt enable bits 0-4.</summary>
    public byte InterruptEnable => _interruptEnable;

    /// <summary>Most recent command byte written, or null since reset.</summary>
    public byte? LastCommand { get; private set; }

    public bool HasInterrupt => (_interruptFlag & _interruptEnable & 0x1F) != 0;

    public byte ReadRegister(int index)
    {
        switch (index)
        {
            case 0: return ReadStatus();
            case 1: return PopResponse();
            case 2: return ReadData();
            case 3: return (_index & 1) == 0 ? (byte)(_interruptEnable | 0xE0) : GetInterruptFlag();
            default: throw new ArgumentOutOfRangeException(nameof(index), index, "CD-ROM port must be 0-3.");
        }
    }

    public void WriteRegister(int index, byte value)
    {
        switch (index, _index)
        {
            case (0, _): _index = value & 3; break;
            case (1, 0): WriteCommand(value); break;
            case (2, 0): PushParameter(value); break;
            case (2, 1): _interruptEnable = (byte)(value & 0x1F); break;
            case (3, 1): SetInterruptFlag(value); break;
            case (1 or 2 or 3, _):
                // ponytail: sound map, CD audio volume and request register (incl. 1F801803h.Index0
                // BFRD/SMEN) are accepted as no-ops until audio/data-transfer slices model them.
                break;
            default: throw new ArgumentOutOfRangeException(nameof(index), index, "CD-ROM port must be 0-3.");
        }
    }

    /// <summary>Data FIFO read. No sector data exists yet, so this is always 0 (DRQSTS stays clear).</summary>
    public byte ReadData() => 0;

    /// <summary>
    /// 0x1F801800 read: bits 0-1 index, bit3 PRMEMPT, bit4 PRMWRDY, bit5 RSLRRDY.
    /// ADPBUSY (2), DRQSTS (6) and BUSYSTS (7) are 0: no XA-ADPCM, no data FIFO,
    /// and commands complete synchronously.
    /// </summary>
    public byte ReadStatus()
    {
        var status = _index;
        if (_parameters.Count == 0) status |= 1 << 3;
        if (_parameters.Count < FifoCapacity) status |= 1 << 4;
        if (_responses.Count > 0) status |= 1 << 5;
        return (byte)status;
    }

    /// <summary>
    /// Command register write. Fails closed: consumes the parameter FIFO, replaces
    /// the response FIFO with <c>[ErrorStat, ErrorInvalidCommand]</c> and raises INT5.
    /// </summary>
    public void WriteCommand(byte command)
    {
        LastCommand = command;
        _parameters.Clear();
        _responses.Clear();
        _responses.Enqueue(ErrorStat);
        _responses.Enqueue(ErrorInvalidCommand);
        _interruptFlag = IntError;
    }

    /// <summary>Interrupt flag register read: bits 0-4 are the flag, bits 5-7 read as 1.</summary>
    public byte GetInterruptFlag() => (byte)(_interruptFlag | 0xE0);

    /// <summary>
    /// Interrupt flag register write (0x1F801803 index 1): bits 0-4 written as 1
    /// acknowledge those flag bits; bit 6 (CLRPRM) clears the parameter FIFO.
    /// </summary>
    public void SetInterruptFlag(byte value)
    {
        _interruptFlag &= (byte)~(value & 0x1F);
        if ((value & 0x40) != 0) _parameters.Clear();
    }

    public void AcknowledgeInterrupt() => _interruptFlag = 0;

    public void Reset()
    {
        _parameters.Clear();
        _responses.Clear();
        _index = 0;
        _interruptEnable = 0;
        _interruptFlag = 0;
        LastCommand = null;
    }

    private void PushParameter(byte value)
    {
        if (_parameters.Count >= FifoCapacity)
            throw new InvalidOperationException($"CD-ROM parameter FIFO overflow: capacity is {FifoCapacity} bytes.");
        _parameters.Enqueue(value);
    }

    // ponytail: empty response FIFO reads 0; real hardware returns stale buffer bytes. Model if a game depends on it.
    private byte PopResponse() => _responses.TryDequeue(out var value) ? value : (byte)0;
}
