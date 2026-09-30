using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// HLE implementation of the documented BIOS service <c>A(13h) setjmp(buf)</c>:
/// store the ABI-saved CPU registers in the 0x30-byte guest buffer at
/// <c>$a0</c> and return 0 to the caller (PSX-SPX kernelbios, misc-functions).
/// </summary>
/// <remarks>
/// Buffer layout, little-endian words: +00 <c>$ra</c> (the caller's return
/// address), +04 <c>$sp</c>, +08 <c>$fp</c>, +0C..+28 <c>$s0..$s7</c>, +2C
/// <c>$gp</c>. Nothing else is saved. A pure function over the injected writer;
/// it never branches on title identity. The later "return again" half of the
/// contract belongs to longjmp (A0:14), which is a separate service.
/// </remarks>
[Domain]
public static class SetJmpService
{
    /// <summary>Size in bytes of the guest <c>jmp_buf</c> this service fills.</summary>
    public const int BufferSize = 0x30;

    // Order is the documented buffer order: word i of the buffer holds SavedRegisters[i].
    private static readonly R3000aRegister[] SavedRegisters =
    [
        R3000aRegister.Ra, R3000aRegister.Sp, R3000aRegister.Fp,
        R3000aRegister.S0, R3000aRegister.S1, R3000aRegister.S2, R3000aRegister.S3,
        R3000aRegister.S4, R3000aRegister.S5, R3000aRegister.S6, R3000aRegister.S7,
        R3000aRegister.Gp,
    ];

    /// <summary>Invokes <c>setjmp</c> for the given call identity.</summary>
    /// <returns>
    /// <see cref="BiosServiceResult.Supported(BiosCallIdentity, uint?)"/> with return
    /// value 0 once all 0x30 bytes were written; <c>BIOS_HLE_INVALID_ARGUMENTS</c>
    /// when the argument count is not one; or <c>BIOS_HLE_UNSUPPORTED_STATE</c> when
    /// the call carries no register file or the buffer is not fully writable.
    /// A failure writes nothing (the writer's range write is all-or-nothing).
    /// </returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static BiosServiceResult Invoke(BiosCallIdentity identity, IGuestMemoryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(writer);

        if (identity.Arguments.Count != 1)
        {
            return BiosServiceResult.InvalidArguments(
                identity, $"{identity.StableKey} setjmp requires one buffer-pointer argument.");
        }

        var registers = identity.GuestRegisters;
        if (registers is null || registers.Count <= (int)R3000aRegister.Ra)
        {
            return BiosServiceResult.UnsupportedState(
                identity, $"{identity.StableKey} setjmp: the call carries no guest register file to save.");
        }

        var address = identity.Arguments[0];
        var buffer = new byte[BufferSize];
        for (var i = 0; i < SavedRegisters.Length; i++)
        {
            BitConverter.TryWriteBytes(buffer.AsSpan(i * sizeof(uint)), registers[(int)SavedRegisters[i]]);
        }

        // The writer rejects address wraparound, unmapped and out-of-RAM ranges
        // without a partial write.
        return writer.TryWrite(address, buffer)
            ? BiosServiceResult.Supported(identity, 0u)
            : BiosServiceResult.UnsupportedState(
                identity, $"{identity.StableKey} setjmp: guest buffer address is invalid or unmapped.");
    }
}
