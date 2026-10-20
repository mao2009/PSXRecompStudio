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
    internal static ObservedRunIdentity PlaceholderRun { get; } = new(new string('0', 64), [], [], null, new string('0', 64));

    /// <summary>
    /// One export target. Its <c>ramWord</c> is the word <see cref="Text"/> holds at the PC (what a run over these fixtures fetches),
    /// unless <paramref name="ramWord"/> says otherwise.
    /// </summary>
    internal static string TargetObject(uint pc, string cause, ulong entries, ulong instructions, uint? ramWord = null)
    {
        var word = ramWord ?? (pc >= Dest && (pc - Dest) / 4 < Text.Length ? Text[(pc - Dest) / 4] : 0u);
        return $$"""{"pc":"0x{{pc:X8}}","cause":"{{cause}}","entries":{{entries}},"instructions":{{instructions}},"lastReturnPc":"0x{{Dest:X8}}","ramWord":"0x{{word:X8}}"}""";
    }

    internal static string TargetsJson(uint pc, string cause, ulong instructions, ObservedRunIdentity? run = null) =>
        TargetsJson(run, TargetObject(pc, cause, 1, instructions));

    internal static string TargetsJson(ObservedRunIdentity? run, params string[] targetObjects) =>
        TargetsJsonInState("Completed", run, targetObjects);

    /// <summary>An export whose run ended in <paramref name="state"/> (a <c>TitleExecutionState</c> name).</summary>
    internal static string TargetsJsonInState(string state, ObservedRunIdentity? run, params string[] targetObjects) =>
        TargetsJsonWithStop(state, run, null, targetObjects);

    /// <summary>An export whose run ended in <paramref name="state"/>; <paramref name="stopAt"/> is the <c>--stop-at</c> PC, if any.</summary>
    internal static string TargetsJsonWithStop(string state, ObservedRunIdentity? run, string? stopAt, params string[] targetObjects)
    {
        run ??= PlaceholderRun;
        var complete = state is "Completed" or "Returned" or "RuntimeHandoff" ? "true" : "false";
        var stop = stopAt is null ? "null" : $"\"{stopAt}\"";
        return $$"""{"format":"psxrecomp.fallback-targets/v2","sha256":"{{run.FirmwareSha256}}","run":{{JsonSerializer.Serialize(ObservedRootPlanner.RunDocument(run))}},"outcome":{"state":"{{state}}","complete":{{complete}},"stopAt":{{stop}}},"targets":[{{string.Join(",", targetObjects)}}]}""";
    }
}
