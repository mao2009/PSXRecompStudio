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
/// Managed PS1 CD-ROM controller: register/FIFO substrate plus the command protocol (Issues #585/#586/#732).
///
/// Implemented commands: GetStat/Nop (01h), SetLoc (02h), ReadN (06h), Standby (07h), Stop (08h), Pause (09h),
/// Init (0Ah), Mute (0Bh), Demute (0Ch), SetMode (0Eh), GetParam (0Fh), GetLocL (10h), GetLocP (11h),
/// SetSession (12h), GetTN (13h), GetTD (14h), SeekL (15h), SeekP (16h), Test 20h (19h), GetID (1Ah), ReadS (1Bh)
/// and ReadTOC (1Eh). Any other command answers INT5 (invalid command) instead of a silent success.
///
/// Two timing models, chosen at construction:
/// <list type="bullet">
/// <item>With an <see cref="ICdSectorSource"/> (a real disc, Issue #732) the device is clocked by
/// <see cref="Advance"/> (the <see cref="DeviceScheduler"/>): every response arrives after its hardware delay
/// (psx-spx timing table, DuckStation's acknowledge delay), only once the previous interrupt was acknowledged, and
/// ReadN/ReadS stream one INT1 per sector at 75/150 sectors per second. A sector's payload (2048 bytes, or 2340 with
/// SetMode bit 5) enters the data FIFO when the guest writes the request register (index 0 port 3, BFRD bit 7), as on
/// the hardware; DMA3 drains it.</item>
/// <item>Without a source the legacy BIOS-less model is unchanged: responses are queued without delay, the next packet
/// becomes visible only after the current response FIFO is drained and acknowledged, a read exposes one bounded INT1
/// and its bytes come only from <see cref="LoadData"/>.</item>
/// </list>
/// There is no audio model. <see cref="HasInterrupt"/> is only the device-side enabled-line state.
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

    public const byte ErrorSeekFailed = 0x04;
    public const byte ErrorInvalidParameter = 0x10;
    public const byte ErrorWrongParameterCount = 0x20;
    public const byte ErrorInvalidCommand = 0x40;
    public const byte ErrorNotReady = 0x80;

    /// <summary>CPU clock the delays below are measured in (33.8688 MHz).</summary>
    public const uint CpuClockHz = 33_868_800;

    /// <summary>Command to first response (INT3/INT5) with a disc in the drive: DuckStation's measured acknowledge delay.</summary>
    public const uint AcknowledgeDelayCycles = 25_000;

    /// <summary>
    /// Least time between acknowledging one interrupt and the next being raised. Without it a response that is
    /// already due would be raised while the guest's handler for the previous one is still running, and the handler's
    /// final I_STAT acknowledge would swallow its edge.
    /// </summary>
    public const uint MinimumInterruptDelayCycles = 2_000;

    // Second-response (INT2) delays after the first response: psx-spx "CDROM - Response Timings" averages.
    public const uint InitCompleteCycles = 0x13CCE;
    public const uint GetIdCompleteCycles = 0x4A00;
    public const uint PauseSingleSpeedCycles = 0x21181C;
    public const uint PauseDoubleSpeedCycles = 0x10BD93;
    public const uint PauseWhilePausedCycles = 0x1DF2;
    public const uint StopSingleSpeedCycles = 0xD38ACA;
    public const uint StopDoubleSpeedCycles = 0x18A6076;
    public const uint StopWhileStoppedCycles = 0x1D7B;

    /// <summary>ponytail: one fixed seek/spin-up/TOC time (about 4 ms) instead of a head-distance model.</summary>
    public const uint SeekCycles = 0x20000;

    /// <summary>Sector period at single speed (75 sectors/s); SetMode bit 7 halves it.</summary>
    public const uint SingleSpeedSectorCycles = CpuClockHz / 75;

    private const byte StatMotorOn = 0x02;
    private const byte StatShellOpen = 0x10;
    private const byte StatRead = 0x20;
    private const byte StatSeek = 0x40;
    private const byte ModeDoubleSpeed = 0x80;
    private const byte ModeWholeSector = 0x20;

    private readonly Queue<byte> _parameters = new(FifoCapacity);
    private readonly Queue<byte> _responses = new(FifoCapacity);
    private readonly Queue<byte> _data = new();
    private readonly List<PendingResponse> _pendingResponses = new();

    private readonly CdRomDiscIdentity _discIdentity;
    private readonly ICdSectorSource? _disc;
    private int _index;
    private byte _interruptEnable = ResetInterruptEnable;
    private byte _interruptFlag;
    private bool _activeResponseMarksDataReady;
    private ulong _interruptGeneration;

    // Timed (disc) model state.
    private ulong _now;
    private ulong _lastAcknowledge;
    private ulong _nextSectorDue;
    private bool _motorOn;
    private bool _shellOpen;
    private int _position;
    private byte[]? _lastSectorHeader;
    private byte[]? _announcedSector;

    public CdRomDevice()
        : this(CdRomDiscIdentity.NoDisc)
    {
    }

    public CdRomDevice(CdRomDiscIdentity discIdentity)
        : this(discIdentity, null)
    {
    }

    /// <param name="discIdentity">What GetID reports.</param>
    /// <param name="disc">The disc's sectors and TOC; null keeps the legacy undelayed model with no sector source.</param>
    /// <exception cref="ArgumentException">A disc is supplied with an identity that says no disc is present.</exception>
    public CdRomDevice(CdRomDiscIdentity discIdentity, ICdSectorSource? disc)
    {
        if (disc is not null && !discIdentity.IsPresent)
        {
            throw new ArgumentException("A disc source needs an identity that reports a present disc.", nameof(discIdentity));
        }

        _discIdentity = discIdentity;
        _disc = disc;
        _motorOn = discIdentity.IsPresent;
        _shellOpen = disc is not null;
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

    /// <summary>True after SetLoc until the next successful ReadN/ReadS/SeekL/SeekP consumes it.</summary>
    public bool HasPendingLocation { get; private set; }

    public bool IsReading { get; private set; }

    /// <summary>CD audio mute flag set by Mute (0Bh) and cleared by Demute (0Ch), Init (0Ah) and <see cref="Reset"/>. No audio consumer exists yet.</summary>
    public bool IsMuted { get; private set; }

    /// <summary>True for ReadS, false for ReadN. Meaningful only while <see cref="IsReading"/> is true.</summary>
    public bool ReadSectorsRaw { get; private set; }

    /// <summary>Drive mode set by SetMode (0Eh); Init sets 20h.</summary>
    public byte Mode { get; private set; }

    /// <summary>
    /// Whether the data FIFO may be drained (DRQSTS). With a disc: the FIFO holds bytes the guest requested. Without one
    /// (#586/#587): the active response packet marked data ready (the queued INT1 from ReadN/ReadS) and the FIFO still
    /// has unconsumed bytes — acknowledging that interrupt does not discard it, a new command or <see cref="Reset"/>
    /// does. Interrupt acknowledgement and data availability are separate states, as with the hardware's INT-ack and
    /// BFRD/DRQSTS handshakes.
    /// </summary>
    public bool DataReady => _data.Count > 0 && (_disc is not null || _activeResponseMarksDataReady);

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
            case (3, 0) when _disc is not null: WriteRequest(value); break;
            case (3, 1): SetInterruptFlag(value); break;
            case (1 or 2 or 3, _):
                // Sound map, CD audio volume and (without a disc) the request register
                // remain outside the model.
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
    /// bit5 RSLRRDY and bit6 DRQSTS (<see cref="DataReady"/>).
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

    /// <summary>
    /// Advances the drive by <paramref name="cycles"/> CPU cycles: the next sector of an active read and any response
    /// that has become due. Without a disc nothing is timed and this does nothing.
    /// </summary>
    public void Advance(uint cycles)
    {
        if (_disc is null)
        {
            return;
        }

        var target = checked(_now + cycles);
        for (;;)
        {
            var sectorDue = IsReading ? Math.Max(_now, _nextSectorDue) : ulong.MaxValue;
            var responseDue = _interruptFlag == 0 && _pendingResponses.Count != 0
                ? Math.Max(_now, Math.Max(_lastAcknowledge + MinimumInterruptDelayCycles,
                    _pendingResponses.Min(static response => response.Due)))
                : ulong.MaxValue;
            var next = Math.Min(sectorDue, responseDue);
            if (next > target || next == ulong.MaxValue) break;
            _now = next;
            // Preserve single-cycle stage ordering when a sector and response coincide.
            if (sectorDue == next)
            {
                _nextSectorDue += SectorCycles;
                ReadNextSector();
            }
            DeliverDueResponse();
        }
        _now = target;
    }

    /// <summary>Execute one command. Its responses are queued (and, with a disc, timed) for interrupt delivery.</summary>
    public void WriteCommand(byte command)
    {
        LastCommand = command;
        var parameters = _parameters.ToArray();
        _parameters.Clear();

        // Without a disc, a newly accepted command owns the command-response channel: this keeps the original
        // substrate's replacement behavior and avoids unbounded accumulation. With one, an acknowledged response is
        // never discarded; each command's own responses simply join the timed queue.
        if (_disc is null)
        {
            ClearResponseSequence();
        }

        switch (command)
        {
            case 0x01: ExecuteGetStat(parameters); break;
            case 0x02: ExecuteSetLoc(parameters); break;
            case 0x06: ExecuteRead(parameters, raw: false); break;
            case 0x07: ExecuteStandby(parameters); break;
            case 0x08: ExecuteStop(parameters); break;
            case 0x09: ExecutePause(parameters); break;
            case 0x0A: ExecuteInit(parameters); break;
            case 0x0B: ExecuteSetMuted(parameters, muted: true); break;
            case 0x0C: ExecuteSetMuted(parameters, muted: false); break;
            case 0x0E: ExecuteSetMode(parameters); break;
            case 0x0F: ExecuteGetParam(parameters); break;
            case 0x10: ExecuteGetLocL(parameters); break;
            case 0x11: ExecuteGetLocP(parameters); break;
            case 0x12: ExecuteSetSession(parameters); break;
            case 0x13: ExecuteGetTN(parameters); break;
            case 0x14: ExecuteGetTD(parameters); break;
            case 0x15 or 0x16: ExecuteSeek(parameters); break;
            case 0x19: ExecuteTest(parameters); break;
            case 0x1A: ExecuteGetId(parameters); break;
            case 0x1B: ExecuteRead(parameters, raw: true); break;
            case 0x1E: ExecuteReadToc(parameters); break;
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
        var wasRaised = _interruptFlag != 0;
        _interruptFlag &= (byte)~(value & 0x1F);
        if ((value & 0x40) != 0) _parameters.Clear();

        // Acknowledging the interrupt flag must not discard unconsumed data:
        // DataReady tracks the data FIFO independently of the interrupt ack
        // (Issue #587), so no state is cleared here beyond the flag itself.
        OnAcknowledged(wasRaised);
    }

    public void AcknowledgeInterrupt()
    {
        var wasRaised = _interruptFlag != 0;
        _interruptFlag = 0;

        // See SetInterruptFlag: acknowledging must not discard unconsumed data.
        OnAcknowledged(wasRaised);
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
        IsMuted = false;
        Mode = 0;
        _motorOn = _discIdentity.IsPresent;
        _shellOpen = _disc is not null;
        _position = 0;
        _lastSectorHeader = null;
        _announcedSector = null;
    }

    private uint SectorCycles => (Mode & ModeDoubleSpeed) != 0 ? SingleSpeedSectorCycles / 2 : SingleSpeedSectorCycles;

    private byte CommandStatus
    {
        get
        {
            byte status = 0;
            if (_motorOn) status |= StatMotorOn;
            if (IsReading) status |= StatRead;
            if (_shellOpen) status |= StatShellOpen;
            return status;
        }
    }

    private void ExecuteGetStat(IReadOnlyCollection<byte> parameters)
    {
        if (!RequireParameterCount(parameters, 0)) return;
        QueueResponse(IntAcknowledge, CommandStatus);
        _shellOpen = false; // psx-spx stat bit 4: set once the shell was opened, cleared by the GetStat that reports it
    }

    /// <summary>
    /// Mute (0Bh) / Demute (0Ch): no parameters, one INT3(stat) response (psx-spx: <c>INT3(stat)</c>; DuckStation
    /// sets its muted flag and sends ACK + stat). Only the flag is kept: this Runtime has no CD-DA/XA audio path
    /// yet, so nothing reads <see cref="IsMuted"/> but the command contract and its tests.
    /// </summary>
    private void ExecuteSetMuted(IReadOnlyCollection<byte> parameters, bool muted)
    {
        if (!RequireParameterCount(parameters, 0)) return;

        IsMuted = muted;
        QueueResponse(IntAcknowledge, CommandStatus);
    }

    /// <summary>Init (0Ah): INT3(stat), INT2(stat). psx-spx: sets mode 20h, starts the motor, aborts reading.</summary>
    private void ExecuteInit(IReadOnlyCollection<byte> parameters)
    {
        if (!RequireParameterCount(parameters, 0)) return;

        IsMuted = false; // DuckStation SoftReset clears muted

        Location = null;
        HasPendingLocation = false;
        StopReading();
        _activeResponseMarksDataReady = false;
        _data.Clear();
        if (_disc is not null)
        {
            Mode = ModeWholeSector;
        }

        QueueResponse(IntAcknowledge, CommandStatus);
        _motorOn = _discIdentity.IsPresent;
        QueueSecondResponse(InitCompleteCycles, IntComplete, CommandStatus);
    }

    private void ExecuteGetId(IReadOnlyCollection<byte> parameters)
    {
        if (!RequireParameterCount(parameters, 0)) return;

        QueueResponse(IntAcknowledge, CommandStatus);

        if (!_discIdentity.IsPresent)
        {
            QueueSecondResponse(GetIdCompleteCycles, IntError, 0x08, ErrorInvalidCommand, 0, 0, 0, 0, 0, 0);
            return;
        }

        if (!_discIdentity.IsLicensed)
        {
            QueueSecondResponse(GetIdCompleteCycles, IntError, 0x0A, ErrorNotReady, _discIdentity.Type, 0, 0, 0, 0, 0);
            return;
        }

        QueueSecondResponse(
            GetIdCompleteCycles,
            IntComplete,
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

        var seek = ConsumeSeekTarget();
        IsReading = true;
        ReadSectorsRaw = raw;
        _motorOn = true;
        _data.Clear();

        QueueResponse(IntAcknowledge, CommandStatus);
        if (_disc is null)
        {
            QueueResponse(IntDataReady, true, CommandStatus);
            return;
        }

        // The first sector follows the acknowledge, the seek to a new SetLoc target and one sector period.
        DropPendingDataReady();
        _nextSectorDue = _now + AcknowledgeDelayCycles + (seek ? SeekCycles : 0) + SectorCycles;
    }

    /// <summary>SeekL (15h) / SeekP (16h): INT3(stat with seek bit), INT2(stat) at the SetLoc target; reading stops.</summary>
    private void ExecuteSeek(IReadOnlyCollection<byte> parameters)
    {
        if (!RequireParameterCount(parameters, 0)) return;
        if (!_discIdentity.IsPresent)
        {
            QueueError(ErrorNotReady);
            return;
        }

        StopReading();
        ConsumeSeekTarget();
        _motorOn = true;
        QueueResponse(IntAcknowledge, (byte)(CommandStatus | StatSeek));
        QueueSecondResponse(SeekCycles, IntComplete, CommandStatus);
    }

    /// <summary>Standby (07h): INT3(stat), INT2(stat) with the motor spun up; psx-spx: INT5(stat+1, 20h) if it already spins.</summary>
    private void ExecuteStandby(IReadOnlyCollection<byte> parameters)
    {
        if (!RequireParameterCount(parameters, 0)) return;
        if (_motorOn)
        {
            QueueError(ErrorWrongParameterCount);
            return;
        }

        QueueResponse(IntAcknowledge, CommandStatus);
        _motorOn = _discIdentity.IsPresent;
        QueueSecondResponse(SeekCycles, IntComplete, CommandStatus);
    }

    /// <summary>Stop (08h): INT3(stat), INT2(stat) once the motor has stopped.</summary>
    private void ExecuteStop(IReadOnlyCollection<byte> parameters)
    {
        if (!RequireParameterCount(parameters, 0)) return;

        var delay = !_motorOn ? StopWhileStoppedCycles
            : (Mode & ModeDoubleSpeed) != 0 ? StopDoubleSpeedCycles : StopSingleSpeedCycles;
        QueueResponse(IntAcknowledge, CommandStatus);
        StopReading();
        _motorOn = false;
        QueueSecondResponse(delay, IntComplete, CommandStatus);
    }

    /// <summary>Pause (09h): INT3(stat while still reading), INT2(stat) once the read has stopped.</summary>
    private void ExecutePause(IReadOnlyCollection<byte> parameters)
    {
        if (!RequireParameterCount(parameters, 0)) return;

        var delay = !IsReading ? PauseWhilePausedCycles
            : (Mode & ModeDoubleSpeed) != 0 ? PauseDoubleSpeedCycles : PauseSingleSpeedCycles;
        QueueResponse(IntAcknowledge, CommandStatus);
        StopReading();
        QueueSecondResponse(delay, IntComplete, CommandStatus);
    }

    private void ExecuteSetMode(IReadOnlyCollection<byte> parameters)
    {
        if (!RequireParameterCount(parameters, 1)) return;

        Mode = parameters.First();
        QueueResponse(IntAcknowledge, CommandStatus);
    }

    /// <summary>GetParam (0Fh): INT3(stat, mode, 00h, filter file, filter channel); no XA filter is modelled.</summary>
    private void ExecuteGetParam(IReadOnlyCollection<byte> parameters)
    {
        if (!RequireParameterCount(parameters, 0)) return;
        QueueResponse(IntAcknowledge, CommandStatus, Mode, 0, 0, 0);
    }

    /// <summary>GetLocL (10h): INT3(the last read sector's header and subheader); INT5(80h) before any sector was read.</summary>
    private void ExecuteGetLocL(IReadOnlyCollection<byte> parameters)
    {
        if (!RequireParameterCount(parameters, 0)) return;
        if (_lastSectorHeader is null)
        {
            QueueError(ErrorNotReady);
            return;
        }

        QueueResponse(IntAcknowledge, _lastSectorHeader);
    }

    /// <summary>GetLocP (11h): INT3(track, index, relative MSF, absolute MSF) of the head position, from the TOC.</summary>
    private void ExecuteGetLocP(IReadOnlyCollection<byte> parameters)
    {
        if (!RequireParameterCount(parameters, 0) || !RequireDisc(out var toc)) return;

        var track = toc.Tracks.Last(t => t.StartLba <= _position || t == toc.Tracks[0]);
        var relative = Math.Max(0, _position - track.StartLba) - 150; // relative MSF counts from 00:00:00
        var (am, @as, af) = LbaToMsf(_position);
        var (rm, rs, rf) = LbaToMsf(relative);
        QueueResponse(IntAcknowledge, ToBcd(track.Number), 0x01, rm, rs, rf, am, @as, af);
    }

    /// <summary>SetSession (12h): session 1 is the only one a PlayStation disc has; any other answers INT5(10h).</summary>
    private void ExecuteSetSession(IReadOnlyCollection<byte> parameters)
    {
        if (!RequireParameterCount(parameters, 1) || !RequireDisc(out _)) return;
        if (parameters.First() != 1)
        {
            QueueError(ErrorInvalidParameter);
            return;
        }

        StopReading();
        QueueResponse(IntAcknowledge, CommandStatus);
        QueueSecondResponse(SeekCycles, IntComplete, CommandStatus);
    }

    /// <summary>GetTN (13h): INT3(stat, first track, last track), BCD.</summary>
    private void ExecuteGetTN(IReadOnlyCollection<byte> parameters)
    {
        if (!RequireParameterCount(parameters, 0) || !RequireDisc(out var toc)) return;
        QueueResponse(IntAcknowledge, CommandStatus, ToBcd(toc.FirstTrack), ToBcd(toc.LastTrack));
    }

    /// <summary>GetTD (14h) track: INT3(stat, minute, second) of the track start (track 0: lead-out), BCD; INT5(10h) otherwise.</summary>
    private void ExecuteGetTD(IReadOnlyCollection<byte> parameters)
    {
        if (!RequireParameterCount(parameters, 1) || !RequireDisc(out var toc)) return;

        var value = parameters.First();
        int? lba = !IsBcd(value, 99) ? null
            : FromBcd(value) == 0 ? toc.LeadOutLba
            : toc.Tracks.Where(t => t.Number == FromBcd(value)).Select(t => (int?)t.StartLba).FirstOrDefault();
        if (lba is not { } start)
        {
            QueueError(ErrorInvalidParameter);
            return;
        }

        var (minute, second, _) = LbaToMsf(start);
        QueueResponse(IntAcknowledge, CommandStatus, minute, second);
    }

    /// <summary>Test (19h): sub-function 20h answers the controller BIOS date/version (PU-7, 19 Sep 1994, C0h); others INT5(10h).</summary>
    private void ExecuteTest(IReadOnlyCollection<byte> parameters)
    {
        if (parameters.Count == 0)
        {
            QueueError(ErrorWrongParameterCount);
            return;
        }

        if (parameters.Count != 1 || parameters.First() != 0x20)
        {
            QueueError(ErrorInvalidParameter);
            return;
        }

        QueueResponse(IntAcknowledge, 0x94, 0x09, 0x19, 0xC0);
    }

    /// <summary>ReadTOC (1Eh): INT3(stat), INT2(stat) after the TOC is re-read.</summary>
    private void ExecuteReadToc(IReadOnlyCollection<byte> parameters)
    {
        if (!RequireParameterCount(parameters, 0) || !RequireDisc(out _)) return;

        StopReading();
        QueueResponse(IntAcknowledge, CommandStatus);
        QueueSecondResponse(SeekCycles, IntComplete, CommandStatus);
    }

    /// <summary>Moves the head to a pending SetLoc target, if any. Returns whether it moved.</summary>
    private bool ConsumeSeekTarget()
    {
        if (!HasPendingLocation || Location is not { } location)
        {
            return false;
        }

        HasPendingLocation = false;
        _position = (FromBcd(location.Minute) * 60 + FromBcd(location.Second)) * 75 + FromBcd(location.Frame) - 150;
        return true;
    }

    private void StopReading()
    {
        IsReading = false;
        ReadSectorsRaw = false;
        DropPendingDataReady();
    }

    /// <summary>Reads the sector under the head into the sector buffer and queues its INT1 (timed model only).</summary>
    private void ReadNextSector()
    {
        var sector = new byte[ICdSectorSource.RawSectorSize];
        if (!_disc!.TryReadSector(_position, sector))
        {
            // Fail closed: a sector the image does not hold is a read error, never zero-filled data.
            StopReading();
            QueueResponse(IntError, (byte)(CommandStatus | ErrorStat | ErrorSeekFailed), ErrorSeekFailed);
            return;
        }

        _lastSectorHeader = sector[12..20];
        _position++;

        // SetMode bit 5: the whole sector after the sync bytes (924h); otherwise the 800h user-data field, which
        // starts after the subheader on Mode 2 sectors.
        var payload = (Mode & ModeWholeSector) != 0 ? sector[12..]
            : sector.AsSpan(sector[15] == 2 ? 24 : 16, 2048).ToArray();

        // An unacknowledged previous sector is overwritten, as the drive's buffer would be.
        DropPendingDataReady();
        _pendingResponses.Add(new PendingResponse(IntDataReady, [CommandStatus], true, _now, payload));
    }

    /// <summary>Request register (index 0 port 3): BFRD (bit 7) loads the announced sector into the data FIFO; 0 clears it.</summary>
    private void WriteRequest(byte value)
    {
        if ((value & 0x80) == 0)
        {
            _data.Clear();
            return;
        }

        if (_data.Count == 0 && _announcedSector is { } sector)
        {
            foreach (var b in sector)
            {
                _data.Enqueue(b);
            }
        }
    }

    private bool RequireDisc(out CdDiscToc toc)
    {
        if (_disc is { } disc)
        {
            toc = disc.Toc;
            return true;
        }

        toc = null!;
        QueueError(ErrorNotReady);
        return false;
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

    private void QueueResponse(byte interrupt, bool marksDataReady, params byte[] response) =>
        Enqueue(new PendingResponse(interrupt, response, marksDataReady, _now + AcknowledgeDelayCycles, null));

    /// <summary>Queues a command's second response <paramref name="delay"/> cycles after its first.</summary>
    private void QueueSecondResponse(uint delay, byte interrupt, params byte[] response) =>
        Enqueue(new PendingResponse(interrupt, response, false, _now + AcknowledgeDelayCycles + delay, null));

    private void Enqueue(PendingResponse response)
    {
        if (_disc is null && _interruptFlag == 0 && _responses.Count == 0 && _pendingResponses.Count == 0)
        {
            ActivateResponse(response);
            return;
        }

        _pendingResponses.Add(response);
    }

    private void ActivateResponse(PendingResponse response)
    {
        _responses.Clear();
        foreach (var value in response.Bytes) _responses.Enqueue(value);
        _interruptFlag = response.Interrupt;
        _activeResponseMarksDataReady = response.MarksDataReady;
        if (response.Sector is not null)
        {
            _announcedSector = response.Sector;
        }

        unchecked
        {
            _interruptGeneration++;
        }
    }

    private void OnAcknowledged(bool wasRaised)
    {
        if (_disc is null)
        {
            TryPromotePendingResponse();
            return;
        }

        if (wasRaised && _interruptFlag == 0)
        {
            _lastAcknowledge = _now;
        }
    }

    /// <summary>
    /// Timed model: raises the earliest due response once the previous interrupt is acknowledged. A new response
    /// replaces whatever the guest left unread in the response FIFO, as on the hardware.
    /// </summary>
    private void DeliverDueResponse()
    {
        if (_interruptFlag != 0 || _now < _lastAcknowledge + MinimumInterruptDelayCycles)
        {
            return;
        }

        var next = -1;
        for (var i = 0; i < _pendingResponses.Count; i++)
        {
            if (_pendingResponses[i].Due <= _now && (next < 0 || _pendingResponses[i].Due < _pendingResponses[next].Due))
            {
                next = i;
            }
        }

        if (next >= 0)
        {
            var response = _pendingResponses[next];
            _pendingResponses.RemoveAt(next);
            ActivateResponse(response);
        }
    }

    /// <summary>Legacy model: the next packet becomes visible once the current one is acknowledged and drained.</summary>
    private void TryPromotePendingResponse()
    {
        if (_interruptFlag != 0 || _responses.Count != 0 || _pendingResponses.Count == 0) return;

        var next = _pendingResponses[0];
        _pendingResponses.RemoveAt(0);
        ActivateResponse(next);
    }

    private void DropPendingDataReady() =>
        _pendingResponses.RemoveAll(response => response.Sector is not null);

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
        if (_disc is null)
        {
            TryPromotePendingResponse();
        }

        return value;
    }

    private static (byte Minute, byte Second, byte Frame) LbaToMsf(int lba)
    {
        var absolute = lba + 150;
        return (ToBcd(absolute / 4500), ToBcd(absolute / 75 % 60), ToBcd(absolute % 75));
    }

    private static byte ToBcd(int value) => (byte)((value / 10 << 4) | (value % 10));

    private static int FromBcd(byte value) => (value >> 4) * 10 + (value & 0x0F);

    private static bool IsBcd(byte value, int maximum)
    {
        var high = value >> 4;
        var low = value & 0x0F;
        if (high > 9 || low > 9) return false;
        return high * 10 + low <= maximum;
    }

    /// <summary>One queued interrupt packet: its INT code, response bytes, due cycle (timed model) and, for INT1, the sector payload it announces.</summary>
    private sealed record PendingResponse(byte Interrupt, byte[] Bytes, bool MarksDataReady, ulong Due, byte[]? Sector);
}
