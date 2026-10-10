using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>What B0:5B <c>ChangeClearPAD(int)</c> last configured.</summary>
[Domain]
public enum BiosPadCardAutoAckSetting : byte
{
    /// <summary>ChangeClearPAD has not been called; the BIOS default applies.</summary>
    NotConfigured,

    /// <summary>The last argument was 0.</summary>
    Zero,

    /// <summary>The last argument was non-zero.</summary>
    NonZero,
}

/// <summary>
/// B0:5B <c>ChangeClearPAD(int)</c>: the Pad/Card IRQ handler's automatic IRQ0
/// (VBlank) acknowledge policy (PSX-SPX kernelbios: "pad AND card"; patches:
/// "disable auto-ack via B(5Bh) ChangeClearPAD(int)").
/// </summary>
/// <remarks>
/// <para>
/// CONFIRMED (psx-spx): the call configures whether the Pad/Card IRQ handler
/// acknowledges IRQ0 on its own, and applies to pad and card alike. NOT
/// documented: which argument value enables it, and any return value. The
/// raw argument is therefore stored as given, with no polarity interpreted
/// here, and B0:5B reports no return value. Its consumer interprets 0 and 1
/// from sources outside psx-spx (see <see cref="BiosPadCardIrqHandler"/>). The relation to C0:0D
/// <c>SetIrqAutoAck</c> (the DefaultInterruptHandler's per-IRQ auto-ack) is
/// likewise undocumented, so this state is kept separate from it.
/// </para>
/// <para>
/// This is configuration only. The call never touches I_STAT or any device;
/// IRQ0 stays pending until the guest acknowledges it. The consumer is
/// <see cref="BiosPadCardIrqHandler"/>. The setting lives in a guest-RAM kernel variable, like
/// <see cref="BiosExceptionHook"/>, because some engines rebuild
/// <see cref="BiosHleRuntime"/> per segment; the address is this Runtime's own
/// choice inside psx-spx's unused "table of tables" slot 00000128h.
/// </para>
/// </remarks>
[Domain]
public static class BiosPadCardAutoAck
{
    /// <summary>Guest address of the 8-byte variable: +0 configured flag (1), +4 last argument.</summary>
    public const uint VariableAddress = 0x00000128;

    /// <summary>B0:5B handler: records the argument as the current setting.</summary>
    internal static BiosServiceResult Change(BiosCallIdentity identity, IGuestMemoryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(writer);

        if (identity.Arguments.Count != 1)
        {
            return BiosServiceResult.InvalidArguments(
                identity, $"{identity.StableKey} ChangeClearPAD requires one integer argument.");
        }

        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), 1u);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), identity.Arguments[0]);

        return writer.TryWrite(VariableAddress, bytes)
            ? BiosServiceResult.Supported(identity)
            : BiosServiceResult.UnsupportedState(
                identity, $"{identity.StableKey} ChangeClearPAD: the auto-ack variable is not writable.");
    }

    /// <summary>
    /// StartPAD's / StartCARD's <c>setSIO0AutoAck(1)</c> (OpenBIOS <c>startPad</c>, <c>startCard</c>): the setting becomes
    /// "configured, argument 1", overwriting an earlier B0:5B. False when the variable cannot be written.
    /// </summary>
    internal static bool TryEnable(IGuestMemoryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), 1u);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), 1u);
        return writer.TryWrite(VariableAddress, bytes);
    }

    /// <summary>Reads the current setting; false when the variable cannot be read.</summary>
    public static bool TryGetSetting(IGuestMemoryReader reader, out BiosPadCardAutoAckSetting setting) =>
        TryGetSetting(reader, out setting, out _);

    /// <summary>
    /// Reads the current setting and the raw last argument (0 when not configured);
    /// false when the variable cannot be read.
    /// </summary>
    public static bool TryGetSetting(
        IGuestMemoryReader reader, out BiosPadCardAutoAckSetting setting, out uint argument)
    {
        ArgumentNullException.ThrowIfNull(reader);

        setting = BiosPadCardAutoAckSetting.NotConfigured;
        argument = 0;
        Span<byte> bytes = stackalloc byte[8];
        if (!reader.TryRead(VariableAddress, bytes))
        {
            return false;
        }

        if (BitConverter.ToUInt32(bytes[..4]) == 0)
        {
            return true;
        }

        argument = BitConverter.ToUInt32(bytes[4..]);
        setting = argument == 0
            ? BiosPadCardAutoAckSetting.Zero
            : BiosPadCardAutoAckSetting.NonZero;
        return true;
    }
}
