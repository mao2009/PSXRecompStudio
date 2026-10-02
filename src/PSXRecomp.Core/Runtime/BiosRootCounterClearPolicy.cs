using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// C0:0A <c>ChangeClearRCnt(t,flag)</c>: what the kernel's timer/vblank IRQ
/// handlers do after processing an IRQ (PSX-SPX kernelbios, timer-functions).
/// </summary>
/// <remarks>
/// <para>
/// CONFIRMED (psx-spx): <c>t</c> is 0..2 for timer 0..2 or 3 for vblank;
/// <c>flag</c> 0 means "do nothing" and 1 means "automatically acknowledge the
/// IRQ and immediately return from exception"; the call returns the old
/// (previous) flag value. NOT documented: behavior for <c>t &gt; 3</c>, for a
/// <c>flag</c> other than 0/1, and the initial value. This service rejects the
/// first two with <c>BIOS_HLE_INVALID_ARGUMENTS</c> rather than guess, and
/// treats the initial value as 0 (INFERRED: the kernel variable is
/// zero-initialised memory, and 0 is the non-acknowledging behavior).
/// </para>
/// <para>
/// Configuration only: the call never touches I_STAT or any timer. The flags are
/// consumed by <see cref="BiosTimerVblankIrqHandler"/> (#658). The four flags live in a 16-byte guest-RAM
/// kernel variable (one word per <c>t</c>) because some engines rebuild
/// <see cref="BiosHleRuntime"/> per segment; the address is this Runtime's
/// own choice inside psx-spx's unused "table of tables" slots 00000130h and
/// 00000138h.
/// </para>
/// </remarks>
[Domain]
public static class BiosRootCounterClearPolicy
{
    /// <summary>Guest address of the four flag words (t = 0..3).</summary>
    public const uint VariableAddress = 0x00000130;

    /// <summary>Highest valid <c>t</c> (3 = vblank).</summary>
    public const uint MaxSource = 3;

    /// <summary>C0:0A handler: stores the new flag and returns the old one.</summary>
    internal static BiosServiceResult Change(
        BiosCallIdentity identity, IGuestMemoryReader reader, IGuestMemoryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        if (identity.Arguments.Count != 2)
        {
            return BiosServiceResult.InvalidArguments(
                identity, $"{identity.StableKey} ChangeClearRCnt requires two arguments: t and flag.");
        }

        var (source, flag) = (identity.Arguments[0], identity.Arguments[1]);
        if (source > MaxSource || flag > 1)
        {
            return BiosServiceResult.InvalidArguments(
                identity,
                $"{identity.StableKey} ChangeClearRCnt: t must be 0..3 and flag 0 or 1 (other values are undocumented).");
        }

        var address = VariableAddress + source * sizeof(uint);
        Span<byte> old = stackalloc byte[sizeof(uint)];
        if (!reader.TryRead(address, old) || !writer.TryWrite(address, BitConverter.GetBytes(flag)))
        {
            return BiosServiceResult.UnsupportedState(
                identity, $"{identity.StableKey} ChangeClearRCnt: the flag variable is not accessible.");
        }

        return BiosServiceResult.Supported(identity, BitConverter.ToUInt32(old));
    }

    /// <summary>Reads the current flag for <paramref name="source"/> (0..3); false when unreadable or out of range.</summary>
    public static bool TryGetFlag(IGuestMemoryReader reader, uint source, out uint flag)
    {
        ArgumentNullException.ThrowIfNull(reader);

        flag = 0;
        if (source > MaxSource)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        if (!reader.TryRead(VariableAddress + source * sizeof(uint), bytes))
        {
            return false;
        }

        flag = BitConverter.ToUInt32(bytes);
        return true;
    }
}
