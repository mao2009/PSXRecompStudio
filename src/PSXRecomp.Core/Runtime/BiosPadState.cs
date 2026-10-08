using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// The kernel's legacy pad start state, set by B0:15 <c>OutdatedPadInitAndStart(type, button_dest, unused, unused)</c>
/// and nothing else the Runtime models: whether the <c>PadCardIrq</c> handler (priority 2) has been enqueued, and the
/// memorized <c>button_dest</c>.
/// </summary>
/// <remarks>
/// <para>
/// CONFIRMED (psx-spx kernelbios): the function fails (returns 0) unless <c>type</c> is <c>20000000h</c> or
/// <c>20000001h</c> ("the type value has no other function"); otherwise it FFh-fills the internal buf1/buf2, calls
/// <c>InitPad(buf1,22h,buf2,22h)</c> (which zero-fills them), calls <c>StartPad()</c> (which enqueues the
/// <c>PadCardIrq</c> handler and "initializes some flags"), memorizes <c>button_dest</c> and returns 2. The two
/// unused parameters have no function. PCSX-Redux OpenBIOS <c>initPadHighLevel</c> agrees (it also prints a TTY line
/// that the retail BIOS is not documented to print).
/// </para>
/// <para>
/// Modelled: the enqueue (as a flag) and <c>button_dest</c>. <c>button_dest</c> is only memorized: nothing reads or
/// writes it at call time (zero is valid and means "do not auto-read"), so no guest pointer is validated here.
/// Not modelled, deliberately: the hidden buf1/buf2 (their addresses are undocumented and the only reader,
/// B0:16 <c>OutdatedPadGetButtons</c>, is unregistered, so the FFh/00h fills cannot be observed), the "some flags"
/// StartPad initializes (including OpenBIOS's auto-ack = 1, left to #661 with the B0:5B setting, which stays
/// untouched here), the stores of the unused parameters to the caller's stack (no <c>$sp</c> reaches a service), and
/// the pad read itself. The handler is never run here: while it is enqueued the exception chain fails closed when it
/// would claim an exception (<see cref="BiosExceptionHandler.DefaultChain"/>), until #661 models the element.
/// </para>
/// <para>
/// The state lives in a guest-RAM kernel variable, like <see cref="BiosPadCardAutoAck"/>, because some engines
/// rebuild <see cref="BiosHleRuntime"/> per segment; the address is this Runtime's own choice (psx-spx does not
/// document the real location). Calling it twice leaves one enqueue (idempotent; the real kernel would enqueue the
/// same element twice).
/// </para>
/// </remarks>
[Domain]
public static class BiosPadState
{
    /// <summary>Guest address of the 8-byte variable: +0 enqueued flag (1), +4 memorized <c>button_dest</c>.</summary>
    public const uint VariableAddress = 0x00000140;

    /// <summary>The only <c>type</c> values B0:15 accepts (psx-spx).</summary>
    public const uint Type20000000 = 0x20000000;

    /// <summary>The second accepted <c>type</c> value.</summary>
    public const uint Type20000001 = 0x20000001;

    /// <summary>B0:15's return value on success (psx-spx: "Return value is 2").</summary>
    public const uint SuccessReturnValue = 2;

    /// <summary>B0:15 handler.</summary>
    internal static BiosServiceResult OutdatedPadInitAndStart(BiosCallIdentity identity, IGuestMemoryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(writer);

        if (identity.Arguments.Count != 4)
        {
            return BiosServiceResult.InvalidArguments(
                identity, $"{identity.StableKey} OutdatedPadInitAndStart requires four arguments: type, button_dest, unused, unused.");
        }

        var type = identity.Arguments[0];
        if (type is not (Type20000000 or Type20000001))
        {
            // psx-spx: "fails unless type is 20000000h or 20000001h ... Return value is ... 0 if type was disliked".
            return BiosServiceResult.Supported(identity, 0);
        }

        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), 1u);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), identity.Arguments[1]);

        return writer.TryWrite(VariableAddress, bytes)
            ? BiosServiceResult.Supported(identity, SuccessReturnValue)
            : BiosServiceResult.UnsupportedState(
                identity, $"{identity.StableKey} OutdatedPadInitAndStart: the pad state variable is not writable.");
    }

    /// <summary>
    /// Marks <c>PadCardIrq</c> enqueued without changing the memorized <c>button_dest</c> (0 when it was not enqueued before),
    /// as StartCARD2's dequeue-then-enqueue of the shared element does (see <see cref="BiosCardState"/>). Idempotent.
    /// False when the variable cannot be read or written.
    /// </summary>
    internal static bool TryEnqueue(IGuestMemoryReader reader, IGuestMemoryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (!TryGetState(reader, out _, out var buttonDestination))
        {
            return false;
        }

        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), 1u);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), buttonDestination);
        return writer.TryWrite(VariableAddress, bytes);
    }

    /// <summary>
    /// Reads the state; false when the variable cannot be read. <paramref name="enqueued"/> is whether
    /// <c>PadCardIrq</c> was enqueued by B0:15.
    /// </summary>
    public static bool TryGetState(IGuestMemoryReader reader, out bool enqueued, out uint buttonDestination)
    {
        ArgumentNullException.ThrowIfNull(reader);

        enqueued = false;
        buttonDestination = 0;
        Span<byte> bytes = stackalloc byte[8];
        if (!reader.TryRead(VariableAddress, bytes))
        {
            return false;
        }

        enqueued = BitConverter.ToUInt32(bytes[..4]) != 0;
        buttonDestination = enqueued ? BitConverter.ToUInt32(bytes[4..]) : 0;
        return true;
    }
}
