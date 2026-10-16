using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PSXRecomp.Core.Recompiler;

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

    /// <summary>A run identity that is well formed but describes no real input (for tests that only parse an export).</summary>
    internal static ObservedRunIdentity PlaceholderRun { get; } = new(new string('0', 64), [], []);

    internal static string TargetsJson(uint pc, string cause, ulong instructions, ObservedRunIdentity? run = null) =>
        TargetsJson(run, $$"""{"pc":"0x{{pc:X8}}","cause":"{{cause}}","entries":1,"instructions":{{instructions}},"lastReturnPc":"0x{{Dest:X8}}"}""");

    internal static string TargetsJson(ObservedRunIdentity? run, params string[] targetObjects)
    {
        run ??= PlaceholderRun;
        return $$"""{"format":"psxrecomp.fallback-targets/v2","sha256":"{{run.FirmwareSha256}}","run":{{JsonSerializer.Serialize(ObservedRootPlanner.RunDocument(run))}},"targets":[{{string.Join(",", targetObjects)}}]}""";
    }
}
