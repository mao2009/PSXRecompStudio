using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// The kernel's memory-card state, set by B0:4A <c>InitCARD2(pad_enable)</c> and B0:4B <c>StartCARD2()</c> and nothing else
/// the Runtime models: whether InitCARD2 ran, the <c>pad_enable</c> flag, and whether StartCARD2 started the card (#708, #710).
/// </summary>
/// <remarks>
/// <para>
/// InitCARD2. CONFIRMED (psx-spx function-summary, memory-card-functions, joypad-functions): <c>B(4Ah) InitCARD2(pad_enable)</c> takes one
/// argument, is called before StartCARD2 and <c>_bu_init</c>, and its <c>pad_enable</c> sets/clears the same "pad_enable_flag" as
/// InitPAD2: "it selects if the Pads are kept handled together with Memory Cards". It is listed as "uses/destroys k0/k1".
/// PCSX-Redux OpenBIOS <c>initCard(padStarted)</c> agrees: it prepares the SIO0/memory-card handler structures
/// (<em>without</em> enqueuing them), resets the card action state, patches the exception handler for the card fast track,
/// stores <c>s_padStarted = pad_enable</c> (any non-zero value acts as 1) and returns the previous "initialized already" flag (0 on
/// the first call, 1 afterwards). It touches no SIO0 register, no I_STAT/I_MASK, no auto-ack and no event, and leaves
/// <c>s_cardStarted</c> alone. INFERRED: the retail return value is the same 0 then 1. Measured (Persona): the result is not read.
/// </para>
/// <para>
/// StartCARD2. psx-spx documents only the signature <c>B(4Bh) StartCARD2()</c>; the body is OpenBIOS's <c>startCard</c>, so everything
/// below is CONFIRMED against OpenBIOS only and INFERRED for the retail BIOS: no arguments, always returns 1; resets SIO0
/// (<c>setupSIO0</c>), then in a critical section dequeues and enqueues the one shared <c>PadCardIrq</c> element (priority 2, the same
/// element B0:15 / StartPAD2 enqueue, so repeating it leaves one copy), sets <c>I_MASK |= IRQ0</c>, forces the SIO0 auto-ack
/// (B0:5B <c>ChangeClearPAD</c> state) to 1 and the VBlank timer auto-ack (C0:0A <c>ChangeClearRCnt(3, flag)</c> state) to 0, and sets
/// <c>s_cardStarted = 1</c>. It does not touch I_STAT, <c>I_MASK</c> bit 7 (IRQ7), any event, or <c>s_padStarted</c>.
/// The two auto-ack forcings are read only by the <c>PadCardIrq</c> handler and the VBlank timer handler's return path; they are not
/// modelled here and stay with the handler (#661), exactly as B0:15's StartPAD auto-ack = 1 does (ADR-014 #703 amendment): the handler is never run.
/// Without a prior InitCARD2 the shared element has a NULL handler and the card state is uninitialised (the retail behaviour is
/// UNKNOWN, OpenBIOS would crash), and psx-spx states the order InitCARD2, StartCARD2, <c>_bu_init</c>; the Runtime fails closed.
/// </para>
/// <para>
/// Modelled: "ran", "started" and the raw <c>pad_enable</c> in one guest-RAM kernel variable (like <see cref="BiosPadState"/>, because
/// some engines rebuild <see cref="BiosHleRuntime"/> per segment); the address is this Runtime's own choice in the reserved slot
/// psx-spx leaves unused at <c>00000148h</c>. StartCARD2's enqueue is <see cref="BiosPadState"/>'s existing enqueued flag (one shared
/// element, so B0:15 and StartCARD2 are the same fact), and the I_MASK bit goes through <see cref="IGuestDeviceAccess"/>. Not modelled,
/// deliberately: the auto-ack forcings (<see cref="BiosPadCardAutoAck"/> and <see cref="BiosRootCounterClearPolicy"/> are unchanged), the SIO0 reset writes (<c>ctrl</c>/<c>mode</c>/<c>baud</c> — the sequence ends with <c>ctrl = 0</c> and nothing in the Runtime reads
/// deliberately: the SIO0 reset writes (<c>ctrl</c>/<c>mode</c>/<c>baud</c> — the sequence ends with <c>ctrl = 0</c> and nothing in the Runtime reads
/// them until a SIO0 transfer is modelled; the retail sequence is UNKNOWN), the hidden handler structures, the exception-handler
/// fast-track patch and the k0/k1 clobber. The enqueued element is never run here: while it is enqueued the exception chain fails
/// closed when it would claim an exception (<see cref="BiosExceptionHandler.DefaultChain"/>), until #661 models the element.
/// </para>
/// <para>
/// Write-failure contract: the pre-checks (state readable, InitCARD2 ran, I_MASK readable) run before any write; the writes are then
/// separate (I_MASK, enqueue, started). <see cref="IGuestMemoryWriter"/> has no transaction, so a failure
/// partway leaves the earlier writes in place and the call reports <c>BIOS_HLE_UNSUPPORTED_STATE</c> (the run stops).
/// </para>
/// </remarks>
[Domain]
public static class BiosCardState
{
    /// <summary>Guest address of the 8-byte variable: +0 flags (bit 0 InitCARD2 ran, bit 1 StartCARD2 started), +4 raw <c>pad_enable</c>.</summary>
    public const uint VariableAddress = 0x00000148;

    /// <summary>Physical address of I_MASK.</summary>
    public const uint InterruptMaskAddress = 0x1F801074;

    /// <summary>B0:4B's return value (OpenBIOS <c>startCard</c>).</summary>
    public const uint StartCard2ReturnValue = 1;

    private const uint RanBit = 1;
    private const uint StartedBit = 2;
    private const uint VblankIrqBit = 1;

    /// <summary>B0:4A handler.</summary>
    internal static BiosServiceResult InitCard2(BiosCallIdentity identity, IGuestMemoryReader reader, IGuestMemoryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        if (identity.Arguments.Count != 1)
        {
            return BiosServiceResult.InvalidArguments(identity, $"{identity.StableKey} InitCARD2 requires one argument: pad_enable.");
        }

        if (!TryReadFlags(reader, out var flags, out _))
        {
            return BiosServiceResult.UnsupportedState(identity, $"{identity.StableKey} InitCARD2: the card state variable is not readable.");
        }

        // s_cardStarted is left as it was (OpenBIOS initCard does not touch it).
        return TryWrite(writer, flags | RanBit, identity.Arguments[0])
            ? BiosServiceResult.Supported(identity, (flags & RanBit) != 0 ? 1u : 0u)
            : BiosServiceResult.UnsupportedState(identity, $"{identity.StableKey} InitCARD2: the card state variable is not writable.");
    }

    /// <summary>B0:4B handler.</summary>
    internal static BiosServiceResult StartCard2(
        BiosCallIdentity identity, IGuestMemoryReader reader, IGuestMemoryWriter writer, IGuestDeviceAccess? devices)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        if (identity.Arguments.Count != 0)
        {
            return BiosServiceResult.InvalidArguments(identity, $"{identity.StableKey} StartCARD2 takes no arguments.");
        }

        if (devices is null)
        {
            return BiosServiceResult.UnsupportedState(
                identity, $"{identity.StableKey} StartCARD2 needs I_MASK, but no device registers are attached to this BIOS runtime.");
        }

        if (!TryReadFlags(reader, out var flags, out var padEnable) ||
            !BiosPadState.TryGetState(reader, out _, out _) ||
            !devices.TryRead32(InterruptMaskAddress, out var mask))
        {
            return BiosServiceResult.UnsupportedState(
                identity, $"{identity.StableKey} StartCARD2: the card state, the pad state or I_MASK is not readable.");
        }

        if ((flags & RanBit) == 0)
        {
            return BiosServiceResult.UnsupportedState(
                identity,
                $"{identity.StableKey} StartCARD2 before InitCARD2: the shared PadCardIrq element and the card state are uninitialised " +
                "(psx-spx requires InitCARD2 first; the retail behaviour is UNKNOWN), so nothing is guessed.");
        }

        var written =
            devices.TryWrite32(InterruptMaskAddress, mask | VblankIrqBit) &&
            BiosPadState.TryEnqueue(reader, writer) &&
            TryWrite(writer, flags | StartedBit, padEnable);

        return written
            ? BiosServiceResult.Supported(identity, StartCard2ReturnValue)
            : BiosServiceResult.UnsupportedState(
                identity, $"{identity.StableKey} StartCARD2: a kernel state variable or I_MASK is not writable (earlier writes are not rolled back).");
    }

    /// <summary>Reads the state; false when the variable cannot be read.</summary>
    public static bool TryGetState(IGuestMemoryReader reader, out bool initialized, out uint padEnable)
    {
        initialized = false;
        padEnable = 0;
        if (!TryReadFlags(reader, out var flags, out var raw))
        {
            return false;
        }

        initialized = (flags & RanBit) != 0;
        padEnable = initialized ? raw : 0;
        return true;
    }

    /// <summary>Whether StartCARD2 started the card; false when the variable cannot be read.</summary>
    public static bool TryGetStarted(IGuestMemoryReader reader, out bool started)
    {
        started = false;
        if (!TryReadFlags(reader, out var flags, out _))
        {
            return false;
        }

        started = (flags & StartedBit) != 0;
        return true;
    }

    private static bool TryReadFlags(IGuestMemoryReader reader, out uint flags, out uint padEnable)
    {
        ArgumentNullException.ThrowIfNull(reader);

        flags = 0;
        padEnable = 0;
        Span<byte> bytes = stackalloc byte[8];
        if (!reader.TryRead(VariableAddress, bytes))
        {
            return false;
        }

        flags = BitConverter.ToUInt32(bytes[..4]);
        padEnable = BitConverter.ToUInt32(bytes[4..]);
        return true;
    }

    private static bool TryWrite(IGuestMemoryWriter writer, uint flags, uint padEnable)
    {
        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), flags);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), padEnable);
        return writer.TryWrite(VariableAddress, bytes);
    }
}
