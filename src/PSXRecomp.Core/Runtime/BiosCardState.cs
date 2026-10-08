using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// The kernel's memory-card init state, set by B0:4A <c>InitCARD2(pad_enable)</c> and nothing else the Runtime models:
/// whether InitCARD2 ran before, and the <c>pad_enable</c> flag (#708).
/// </summary>
/// <remarks>
/// <para>
/// CONFIRMED (psx-spx function-summary, memory-card-functions, joypad-functions): <c>B(4Ah) InitCARD2(pad_enable)</c> takes one
/// argument, is called before StartCARD2 and <c>_bu_init</c>, and its <c>pad_enable</c> sets/clears the same "pad_enable_flag" as
/// InitPAD2: "it selects if the Pads are kept handled together with Memory Cards". It is listed as "uses/destroys k0/k1".
/// PCSX-Redux OpenBIOS <c>initCard(padStarted)</c> agrees: it prepares the SIO0/memory-card handler structures
/// (<em>without</em> enqueuing them: that is StartCARD2/StartPAD2), resets the card action state, patches the exception handler for
/// the card fast track, stores <c>s_padStarted = pad_enable</c> (any non-zero value acts as 1; the flag is only read by the
/// PadCardIrq handler to decide whether to poll the pads) and returns the previous "initialized already" flag (0 on the first call,
/// 1 afterwards). It touches no SIO0 register, no I_STAT/I_MASK, no auto-ack and no event.
/// </para>
/// <para>
/// INFERRED: the retail return value is the same 0 then 1 (psx-spx documents none; OpenBIOS is a reimplementation). Measured
/// (Persona) the result is not read.
/// </para>
/// <para>
/// Modelled: the "initialized" flag and the raw <c>pad_enable</c>, in a guest-RAM kernel variable (like <see cref="BiosPadState"/>,
/// because some engines rebuild <see cref="BiosHleRuntime"/> per segment); the address is this Runtime's own choice in the
/// reserved slot psx-spx leaves unused at <c>00000148h</c>. Not modelled, deliberately: the hidden handler structures and card
/// flags (no reader exists until StartCARD2/#661), the exception-handler fast-track patch (the Runtime's kernel exception handler
/// owns that vector and no card operation can run), the k0/k1 clobber, and anything of StartCARD2 (the enqueue of
/// <c>PadCardIrq</c>, which stays <see cref="BiosPadState"/>'s and #661's). The call therefore changes no IRQ, SIO0, event or
/// <see cref="BiosPadState"/> state. A repeat call re-stores <c>pad_enable</c> and returns 1.
/// </para>
/// </remarks>
[Domain]
public static class BiosCardState
{
    /// <summary>Guest address of the 8-byte variable: +0 InitCARD2 ran flag (1), +4 raw <c>pad_enable</c>.</summary>
    public const uint VariableAddress = 0x00000148;

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

        if (!TryGetState(reader, out var initialized, out _))
        {
            return BiosServiceResult.UnsupportedState(identity, $"{identity.StableKey} InitCARD2: the card state variable is not readable.");
        }

        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), 1u);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), identity.Arguments[0]);

        return writer.TryWrite(VariableAddress, bytes)
            ? BiosServiceResult.Supported(identity, initialized ? 1u : 0u)
            : BiosServiceResult.UnsupportedState(identity, $"{identity.StableKey} InitCARD2: the card state variable is not writable.");
    }

    /// <summary>Reads the state; false when the variable cannot be read.</summary>
    public static bool TryGetState(IGuestMemoryReader reader, out bool initialized, out uint padEnable)
    {
        ArgumentNullException.ThrowIfNull(reader);

        initialized = false;
        padEnable = 0;
        Span<byte> bytes = stackalloc byte[8];
        if (!reader.TryRead(VariableAddress, bytes))
        {
            return false;
        }

        initialized = BitConverter.ToUInt32(bytes[..4]) != 0;
        padEnable = initialized ? BitConverter.ToUInt32(bytes[4..]) : 0;
        return true;
    }
}
