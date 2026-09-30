using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Core.Runtime;

/// <summary>Outcome of <see cref="BiosExceptionHook.TryComplete"/>.</summary>
[Domain]
internal enum BiosExceptionHookStatus : byte
{
    /// <summary>No hook is registered; nothing was changed.</summary>
    NotRegistered,

    /// <summary>The hook's saved state was restored and control moves to the returned PC.</summary>
    Resolved,

    /// <summary>The registered buffer could not be read in full; nothing was changed.</summary>
    InvalidState,
}

/// <summary>
/// B0:19 <c>HookEntryInt(addr)</c>: the exception-completion hook registration
/// and its firing semantics (PSX-SPX kernelbios, interrupt-exception-handling).
/// </summary>
/// <remarks>
/// <para>
/// <c>addr</c> <em>points to</em> a 0x30-byte <see cref="JmpBufLayout"/> buffer
/// (the spec's wording: "addr points to a structure"), so registration stores
/// only the guest address and the buffer is read when the hook fires, never at
/// registration time.
/// </para>
/// <para>
/// The registered address lives in a guest-RAM kernel variable at
/// <see cref="PointerAddress"/> rather than in a <see cref="BiosHleRuntime"/>
/// field: the Runtime is rebuilt per execution segment by some engines, and the
/// jump tables already use guest RAM as the state's source of truth for the
/// same reason. The address is this Runtime's own design choice inside the
/// documented-unused/reserved slot of the "table of tables" (psx-spx: 00000118h
/// unused); the real kernel's variable location is not documented.
/// </para>
/// <para>
/// The hook fires only through <see cref="BiosExceptionCompletion.Complete"/>,
/// the completion step of a fully executed kernel exception handler (#651);
/// <see cref="TryComplete"/> is internal for that reason.
/// </para>
/// </remarks>
[Domain]
public static class BiosExceptionHook
{
    /// <summary>Guest address of the 4-byte kernel variable holding the registered buffer address (0 = none).</summary>
    public const uint PointerAddress = 0x00000118;

    /// <summary>Value <c>$v0</c> holds when the hook is entered (psx-spx: "called with r2=1").</summary>
    public const uint HookReturnValue = 1;

    /// <summary>B0:19 handler: registers the address in <c>$a0</c> as the current hook.</summary>
    internal static BiosServiceResult Register(BiosCallIdentity identity, IGuestMemoryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(writer);

        if (identity.Arguments.Count != 1)
        {
            return BiosServiceResult.InvalidArguments(
                identity, $"{identity.StableKey} HookEntryInt requires one buffer-pointer argument.");
        }

        // B0:19 has no documented return value, so none is reported and $v0 stays untouched.
        return writer.TryWrite(PointerAddress, BitConverter.GetBytes(identity.Arguments[0]))
            ? BiosServiceResult.Supported(identity)
            : BiosServiceResult.UnsupportedState(
                identity, $"{identity.StableKey} HookEntryInt: the hook pointer slot is not writable.");
    }

    /// <summary>
    /// Fires the registered hook against <paramref name="gpr"/>: reads the
    /// registered buffer's <em>current</em> contents, and only when all 0x30
    /// bytes were read restores <c>$ra/$sp/$fp/$s0-$s7/$gp</c>, sets
    /// <c>$v0 = 1</c> and reports the saved <c>$ra</c> as the PC to continue at.
    /// Every other register is left as it was.
    /// </summary>
    /// <param name="reader">Guest-memory read boundary.</param>
    /// <param name="gpr">The full 32-entry register file; mutated only on <see cref="BiosExceptionHookStatus.Resolved"/>.</param>
    /// <param name="pc">The PC to continue at; 0 unless resolved.</param>
    internal static BiosExceptionHookStatus TryComplete(IGuestMemoryReader reader, uint[] gpr, out uint pc)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(gpr);
        if (gpr.Length != 32)
        {
            throw new ArgumentException("A full 32-entry register file is required.", nameof(gpr));
        }

        pc = 0;
        Span<byte> pointer = stackalloc byte[sizeof(uint)];
        if (!reader.TryRead(PointerAddress, pointer))
        {
            return BiosExceptionHookStatus.InvalidState;
        }

        var address = BitConverter.ToUInt32(pointer);
        if (address == 0)
        {
            return BiosExceptionHookStatus.NotRegistered;
        }

        // All-or-nothing read: the register file is not touched unless the whole
        // buffer is in hand.
        Span<byte> buffer = stackalloc byte[JmpBufLayout.Size];
        if (!reader.TryRead(address, buffer))
        {
            return BiosExceptionHookStatus.InvalidState;
        }

        JmpBufLayout.RestoreInto(buffer, gpr);
        gpr[(int)R3000aRegister.V0] = HookReturnValue;
        pc = gpr[(int)R3000aRegister.Ra];
        return BiosExceptionHookStatus.Resolved;
    }
}
