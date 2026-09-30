using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// The 0x30-byte BIOS register-state buffer shared by A0:13 setjmp (writer) and
/// B0:19 HookEntryInt (reader): little-endian words +00 <c>$ra</c> (pc), +04
/// <c>$sp</c>, +08 <c>$fp</c>, +0C..+28 <c>$s0..$s7</c>, +2C <c>$gp</c>
/// (PSX-SPX kernelbios, misc-functions and interrupt-exception-handling).
/// The one place the offset table is defined.
/// </summary>
[Domain]
internal static class JmpBufLayout
{
    public const int Size = 0x30;

    // Word i of the buffer holds Registers[i].
    private static readonly R3000aRegister[] Registers =
    [
        R3000aRegister.Ra, R3000aRegister.Sp, R3000aRegister.Fp,
        R3000aRegister.S0, R3000aRegister.S1, R3000aRegister.S2, R3000aRegister.S3,
        R3000aRegister.S4, R3000aRegister.S5, R3000aRegister.S6, R3000aRegister.S7,
        R3000aRegister.Gp,
    ];

    /// <summary>Encodes the saved registers of <paramref name="gpr"/> into a new buffer.</summary>
    public static byte[] Encode(IReadOnlyList<uint> gpr)
    {
        var buffer = new byte[Size];
        for (var i = 0; i < Registers.Length; i++)
        {
            BitConverter.TryWriteBytes(buffer.AsSpan(i * sizeof(uint)), gpr[(int)Registers[i]]);
        }

        return buffer;
    }

    /// <summary>
    /// Copies exactly the buffer's registers into <paramref name="gpr"/>; every
    /// other entry is left untouched.
    /// </summary>
    public static void RestoreInto(ReadOnlySpan<byte> buffer, uint[] gpr)
    {
        for (var i = 0; i < Registers.Length; i++)
        {
            gpr[(int)Registers[i]] = BitConverter.ToUInt32(buffer.Slice(i * sizeof(uint), sizeof(uint)));
        }
    }
}
