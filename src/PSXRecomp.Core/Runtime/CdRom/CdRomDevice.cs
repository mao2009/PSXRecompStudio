using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.CdRom;

/// <summary>BCD location remembered by SetLoc until the next read command consumes it.</summary>
[Domain]
public readonly record struct CdRomLocation(byte Minute, byte Second, byte Frame);

/// <summary>
/// Minimal virtual-disc identity used by GetID and bounded read-state tests.
/// This is metadata only: #586 deliberately does not provide sector contents.
/// </summary>
[Domain]
public readonly record struct CdRomDiscIdentity(bool IsPresent, bool IsLicensed, byte Type, byte RegionCode)
{
    public static CdRomDiscIdentity NoDisc => new(false, false, 0, 0);

    /// <summary>Licensed Mode2 data disc with an ASCII SCE region suffix (normally I/A/E).</summary>
    public static CdRomDiscIdentity LicensedMode2(byte regionCode = (byte)'I') =>
        new(true, true, 0x20, regionCode);
}

/// <summary>
/// Managed PS1 CD-ROM controller register/FIFO substrate plus the minimal
/// deterministic command protocol tracked by Issues #585/#586.
///
/// Implemented commands: GetStat/Nop (01h), SetLoc (02h), ReadN (06h),
/// Init (0Ah), GetID (1Ah) and ReadS (1Bh). Multi-response commands are exposed
/// one interrupt packet at a time: the next packet becomes visible only after
/// the current response FIFO is drained and its interrupt is acknowledged.
///
/// Sector bytes arrive only through <see cref="LoadData"/>, which no production
/// code calls yet, and there is no audio model; DMA3/IRQ2 wiring lives in
/// CdRomDmaTransfer and DeviceScheduler (#587). ReadN/ReadS expose one
/// bounded INT1/data-ready event rather than a repeating hardware read stream;
/// <see cref="HasInterrupt"/> remains only the device-side enabled-line state.
/// </summary>
[Domain]
public sealed class CdRomDevice : ICdRom
{
    /// <summary>Capacity of the parameter and response FIFOs, in bytes.</summary>
    public const int FifoCapacity = 16;

    /// <summary>
    /// Capacity of the data FIFO, in bytes: one raw 2352-byte CD sector (the
    /// same size as <c>ChdCdCodec.CdSectorDataSize</c>). It holds either read
    /// mode's per-sector payload — ReadN's 2048-byte user data or ReadS's
    /// 2340-byte whole sector minus sync — so a single sector always fits.
    /// Independent of <see cref="FifoCapacity"/>.
    /// </summary>
    public const int DataFifoCapacity = 2352;

    /// <summary>
    /// Interrupt-enable register value after a hardware reset: all five sources enabled. Guest libraries such as
    /// libcd never write the register (the boot BIOS leaves it as reset), so a zero default would mask every
    /// CD-ROM response and no IRQ2 would ever be raised.
    /// </summary>
    public const byte ResetInterruptEnable = 0x1F;

    public const byte IntDataReady = 0x01;
    public const byte IntComplete = 0x02;
    public const byte IntAcknowledge = 0x03;

    /// <summary>INT5: command error.</summary>
    public const byte IntError = 0x05;

    /// <summary>Response stat error bit (bit 0).</summary>
    public const byte ErrorStat = 0x01;

    public const byte ErrorInvalidParameter = 0x10;
    public const byte ErrorWrongParameterCount = 0x20;
    public const byte ErrorInvalidCommand = 0x40;
    public const byte ErrorNotReady = 0x80;

    private const byte StatMotorOn = 0x02;
    private const byte StatRead = 0x20;

    private readonly Queue<byte> _parameters = new(FifoCapacity);
    private readonly Queue<byte> _responses = new(FifoCapacity);
    private readonly Queue<byte> _data = new();
    private readonly Queue<(byte Interrupt, byte[] Response, bool MarksDataReady)> _pendingResponses = new();

    private readonly CdRomDiscIdentity _discIdentity;
    private int _index;
    private byte _interruptEnable = ResetInterruptEnable;
    private byte _interruptFlag;
    private bool _activeResponseMarksDataReady;
    private ulong _interruptGeneration;

    public CdRomDevice()
        : this(CdRomDiscIdentity.NoDisc)
    {
    }

    public CdRomDevice(CdRomDiscIdentity discIdentity)
    {
        _discIdentity = discIdentity;
    }

    /// <summary>Currently selected register index (0-3).</summary>
    public int Index => _index;

    /// <summary>Pending parameter bytes, oldest first.</summary>
    public IReadOnlyCollection<byte> Parameters => _parameters.ToArray();

    public int ResponseCount => _responses.Count;

    public int DataBytesAvailable => _data.Count;

    public ulong InterruptGeneration => _interruptGeneration;

    /// <summary>Interrupt enable bits 0-4.</summary>
    public byte InterruptEnable => _interruptEnable;

    /// <summary>Most recent command byte written, or null since reset.</summary>
    public byte? LastCommand { get; private set; }

    public CdRomLocation? Location { get; private set; }

    /// <summary>True after SetLoc until the next successful ReadN/ReadS consumes it.</summary>
    public bool HasPendingLocation { get; private set; }

    public bool IsReading { get; private set; }

    /// <summary>True for ReadS, false for ReadN. Meaningful only while <see cref="IsReading"/> is true.</summary>
    public bool ReadSectorsRaw { get; private set; }

    /// <summary>
    /// One bounded sector-ready token (#586/#587). It becomes true once the
    /// active response packet marks data ready (the queued INT1 from
    /// ReadN/ReadS) and stays true as long as the data FIFO still has
    /// unconsumed bytes — acknowledging that packet's interrupt does not
    /// discard it. It goes false once the FIFO is actually drained via
    /// <see cref="ReadData"/>, or a new command dispatch/<see cref="Reset"/>
    /// retires the packet that announced it: interrupt acknowledgement and
    /// data-FIFO availability are deliberately separate states, matching real
    /// hardware's independent INT-ack and BFRD/DRQSTS handshakes.
    /// </summary>
    public bool DataReady => _activeResponseMarksDataReady && _data.Count > 0;

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
                // Sound map, CD audio volume and request-register behavior remain
                // outside this command-layer slice.
                break;
            default: throw new ArgumentOutOfRangeException(nameof(index), index, "CD-ROM port must be 0-3.");
        }
    }

    /// <summary>Reads the oldest byte in the bounded data FIFO, or zero when empty.</summary>
    public byte ReadData() => _data.TryDequeue(out var value) ? value : (byte)0;

    /// <summary>
    /// Supplies bytes from the disc/sector layer without coupling this device to
    /// any image format. The active ReadN/ReadS command owns interpretation of
    /// those bytes; DMA3 consumes them through <see cref="ReadData"/>.
    /// The load is atomic: input that does not fit in the remaining
    /// <see cref="DataFifoCapacity"/> is rejected before any byte is enqueued.
    /// </summary>
    public void LoadData(ReadOnlySpan<byte> data)
    {
        if (!IsReading)
            throw new InvalidOperationException("CD-ROM data can be supplied only while ReadN/ReadS is active.");
        if (data.Length > DataFifoCapacity - _data.Count)
            throw new InvalidOperationException(
                $"CD-ROM data FIFO overflow: {data.Length} bytes exceed the remaining {DataFifoCapacity - _data.Count} of {DataFifoCapacity}.");

        foreach (var value in data)
            _data.Enqueue(value);
    }

    /// <summary>
    /// 0x1F801800 read: bits 0-1 index, bit3 PRMEMPT, bit4 PRMWRDY,
    /// bit5 RSLRRDY and bit6 DRQSTS (bounded DataReady token).
    /// </summary>
    public byte ReadStatus()
    {
        var status = _index;
        if (_parameters.Count == 0) status |= 1 << 3;
        if (_parameters.Count < FifoCapacity) status |= 1 << 4;
        if (_responses.Count > 0) status |= 1 << 5;
        if (DataReady) status |= 1 << 6;
        return (byte)status;
    }

    /// <summary>Dispatch one command synchronously into deterministic response packets.</summary>
    public void WriteCommand(byte command)
    {
        LastCommand = command;
        var parameters = _parameters.ToArray();
        _parameters.Clear();

        // A newly accepted command owns the command-response channel. This keeps
        // the original substrate's replacement behavior and avoids unbounded
        // accumulation when software sends another command before completing one.
        ClearResponseSequence();

        switch (command)
        {
            case 0x01: ExecuteGetStat(parameters); break;
            case 0x02: ExecuteSetLoc(parameters); break;
            case 0x06: ExecuteRead(parameters, raw: false); break;
            case 0x0A: ExecuteInit(parameters); break;
            case 0x1A: ExecuteGetId(parameters); break;
            case 0x1B: ExecuteRead(parameters, raw: true); break;
            default: QueueError(ErrorInvalidCommand); break;
        }
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

        // Acknowledging the interrupt flag must not discard unconsumed data:
        // DataReady tracks the data FIFO independently of the interrupt ack
        // (Issue #587), so no state is cleared here beyond the flag itself.
        TryPromotePendingResponse();
    }

    public void AcknowledgeInterrupt()
    {
        _interruptFlag = 0;

        // See SetInterruptFlag: acknowledging must not discard unconsumed data.
        TryPromotePendingResponse();
    }

    public void Reset()
    {
        _parameters.Clear();
        _data.Clear();
        ClearResponseSequence();
        _index = 0;
        _interruptEnable = ResetInterruptEnable;
        LastCommand = null;
        Location = null;
        HasPendingLocation = false;
        IsReading = false;
        ReadSectorsRaw = false;
    }

    private byte CommandStatus
    {
        get
        {
            byte status = 0;
            if (_discIdentity.IsPresent) status |= StatMotorOn;
            if (IsReading) status |= StatRead;
            return status;
        }
    }

    private void ExecuteGetStat(IReadOnlyCollection<byte> parameters)
    {
        if (!RequireParameterCount(parameters, 0)) return;
        QueueResponse(IntAcknowledge, CommandStatus);
    }

    private void ExecuteInit(IReadOnlyCollection<byte> parameters)
    {
        if (!RequireParameterCount(parameters, 0)) return;

        Location = null;
        HasPendingLocation = false;
        IsReading = false;
        ReadSectorsRaw = false;
        _activeResponseMarksDataReady = false;
        _data.Clear();

        QueueResponse(IntAcknowledge, CommandStatus);
        QueueResponse(IntComplete, CommandStatus);
    }

    private void ExecuteGetId(IReadOnlyCollection<byte> parameters)
    {
        if (!RequireParameterCount(parameters, 0)) return;

        QueueResponse(IntAcknowledge, CommandStatus);

        if (!_discIdentity.IsPresent)
        {
            QueueResponse(IntError, false, 0x08, ErrorInvalidCommand, 0, 0, 0, 0, 0, 0);
            return;
        }

        if (!_discIdentity.IsLicensed)
        {
            QueueResponse(IntError, false, 0x0A, ErrorNotReady, _discIdentity.Type, 0, 0, 0, 0, 0);
            return;
        }

        QueueResponse(
            IntComplete,
            false,
            StatMotorOn,
            0x00,
            _discIdentity.Type,
            0x00,
            (byte)'S',
            (byte)'C',
            (byte)'E',
            _discIdentity.RegionCode);
    }

    private void ExecuteSetLoc(IReadOnlyCollection<byte> parameters)
    {
        if (!RequireParameterCount(parameters, 3)) return;

        var values = parameters.ToArray();
        if (!IsBcd(values[0], 99) || !IsBcd(values[1], 59) || !IsBcd(values[2], 74))
        {
            QueueError(ErrorInvalidParameter);
            return;
        }

        Location = new CdRomLocation(values[0], values[1], values[2]);
        HasPendingLocation = true;
        QueueResponse(IntAcknowledge, CommandStatus);
    }

    private void ExecuteRead(IReadOnlyCollection<byte> parameters, bool raw)
    {
        if (!RequireParameterCount(parameters, 0)) return;
        if (!_discIdentity.IsPresent)
        {
            QueueError(ErrorNotReady);
            return;
        }

        IsReading = true;
        ReadSectorsRaw = raw;
        HasPendingLocation = false;
        _data.Clear();

        QueueResponse(IntAcknowledge, CommandStatus);
        QueueResponse(IntDataReady, true, CommandStatus);
    }

    private bool RequireParameterCount(IReadOnlyCollection<byte> parameters, int expected)
    {
        if (parameters.Count == expected) return true;
        QueueError(ErrorWrongParameterCount);
        return false;
    }

    private void QueueError(byte errorCode) =>
        QueueResponse(IntError, false, (byte)(CommandStatus | ErrorStat), errorCode);

    private void QueueResponse(byte interrupt, params byte[] response) =>
        QueueResponse(interrupt, false, response);

    private void QueueResponse(byte interrupt, bool marksDataReady, params byte[] response)
    {
        if (_interruptFlag == 0 && _responses.Count == 0 && _pendingResponses.Count == 0)
        {
            ActivateResponse(interrupt, response, marksDataReady);
            return;
        }

        _pendingResponses.Enqueue((interrupt, response, marksDataReady));
    }

    private void ActivateResponse(byte interrupt, IEnumerable<byte> response, bool marksDataReady)
    {
        _responses.Clear();
        foreach (var value in response) _responses.Enqueue(value);
        _interruptFlag = interrupt;
        _activeResponseMarksDataReady = marksDataReady;
        unchecked
        {
            _interruptGeneration++;
        }
    }

    private void TryPromotePendingResponse()
    {
        if (_interruptFlag != 0 || _responses.Count != 0 || _pendingResponses.Count == 0) return;

        var next = _pendingResponses.Dequeue();
        ActivateResponse(next.Interrupt, next.Response, next.MarksDataReady);
    }

    private void ClearResponseSequence()
    {
        _responses.Clear();
        _pendingResponses.Clear();
        _interruptFlag = 0;
        _activeResponseMarksDataReady = false;
    }

    private void PushParameter(byte value)
    {
        if (_parameters.Count >= FifoCapacity)
            throw new InvalidOperationException($"CD-ROM parameter FIFO overflow: capacity is {FifoCapacity} bytes.");
        _parameters.Enqueue(value);
    }

    private byte PopResponse()
    {
        if (!_responses.TryDequeue(out var value)) return 0;
        TryPromotePendingResponse();
        return value;
    }

    private static bool IsBcd(byte value, int maximum)
    {
        var high = value >> 4;
        var low = value & 0x0F;
        if (high > 9 || low > 9) return false;
        return high * 10 + low <= maximum;
    }
}
