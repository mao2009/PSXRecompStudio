using System.Security.Cryptography;
using System.Text;

namespace PSXRecomp.Tests.Recompiler;

// Issue #773: shared synthetic fixtures for the observed-roots tests. Each is a few words with the shape under test.
[Test]
internal static class ObservedRootsFixtures
{
    internal const uint Dest = 0x80010000u;

    // Word 4 is a function entry (addiu $sp,$sp,-16 prologue) that direct reachability from the entry does not reach.
    internal const uint Callee = Dest + 16;

    internal static readonly uint[] Text =
    [
        MipsEncoding.JumpRegister(31), MipsEncoding.Nop, MipsEncoding.Nop, MipsEncoding.Nop,
        MipsEncoding.I(0x09, 29, 29, 0xFFF0), MipsEncoding.JumpRegister(31), MipsEncoding.Nop,
    ];

    internal static byte[] Exe(uint[] text)
    {
        var bytes = new byte[2048 + text.Length * 4];
        Encoding.ASCII.GetBytes("PS-X EXE").CopyTo(bytes, 0);
        BitConverter.GetBytes(Dest).CopyTo(bytes, 0x10);
        BitConverter.GetBytes(Dest).CopyTo(bytes, 0x18);
        BitConverter.GetBytes((uint)text.Length * 4).CopyTo(bytes, 0x1C);
        for (var i = 0; i < text.Length; i++)
        {
            BitConverter.GetBytes(text[i]).CopyTo(bytes, 2048 + i * 4);
        }

        return bytes;
    }

    internal static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static string TargetsJson(uint pc, string cause, ulong instructions) =>
        $$"""{"format":"psxrecomp.fallback-targets/v1","sha256":"0","targets":[{"pc":"0x{{pc:X8}}","cause":"{{cause}}","entries":1,"instructions":{{instructions}},"lastReturnPc":"0x{{Dest:X8}}"}]}""";
}
