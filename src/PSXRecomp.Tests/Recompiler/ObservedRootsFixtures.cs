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
    /// One public export target. It carries no RAM word (the ruling on #839); the word is in the local RAM evidence (<see cref="LocalRamEvidenceJson"/>).
    /// </summary>
    internal static string TargetObject(uint pc, string cause, ulong entries, ulong instructions) =>
        $$"""{"pc":"0x{{pc:X8}}","cause":"{{cause}}","entries":{{entries}},"instructions":{{instructions}},"lastReturnPc":"0x{{Dest:X8}}"}""";

    /// <summary>The RAM word <see cref="Text"/> holds at a PC: what a run over these fixtures fetches there (zero outside the text).</summary>
    internal static uint DefaultRamWord(uint pc) => pc >= Dest && (pc - Dest) / 4 < Text.Length ? Text[(pc - Dest) / 4] : 0u;

    /// <summary>The local RAM evidence (<c>fallback-ram-evidence/v1</c>) for one export, bound to its file's SHA-256.</summary>
    internal static string LocalRamEvidenceJson(string exportSha256, IEnumerable<(uint Pc, uint Word)> words) =>
        JsonSerializer.Serialize(new
        {
            format = ObservedRootPlanner.RamEvidenceFormat,
            exportSha256,
            words = words.Select(w => new { pc = $"0x{w.Pc:X8}", word = $"0x{w.Word:X8}" }).ToArray(),
        }, new JsonSerializerOptions { WriteIndented = true });

    internal static string TargetsJson(uint pc, string cause, ulong instructions, ObservedRunIdentity? run = null) =>
        TargetsJson(run, TargetObject(pc, cause, 1, instructions));

    internal static string TargetsJson(ObservedRunIdentity? run, params string[] targetObjects) =>
        TargetsJsonInState("Completed", run, targetObjects);

    /// <summary>An export whose run ended in <paramref name="state"/> (a <c>TitleExecutionState</c> name).</summary>
    internal static string TargetsJsonInState(string state, ObservedRunIdentity? run, params string[] targetObjects) =>
        TargetsJsonWithStop(state, run, null, targetObjects);

    /// <summary>
    /// An export whose run ended in <paramref name="state"/> with the run's classified cause <paramref name="diagnosticCode"/>
    /// (null when the run recorded none). The outcome carries no <c>stopAt</c>.
    /// </summary>
    internal static string TargetsJsonWithCause(string state, string? diagnosticCode, ObservedRunIdentity? run, params string[] targetObjects)
    {
        run ??= PlaceholderRun;
        var complete = state is "Completed" or "Returned" or "RuntimeHandoff" ? "true" : "false";
        var cause = diagnosticCode is null ? "null" : $"\"{diagnosticCode}\"";
        return $$"""{"format":"psxrecomp.fallback-targets/v2","sha256":"{{run.FirmwareSha256}}","run":{{JsonSerializer.Serialize(ObservedRootPlanner.RunDocument(run))}},"outcome":{"state":"{{state}}","complete":{{complete}},"stopAt":null,"diagnosticCode":{{cause}}},"targets":[{{string.Join(",", targetObjects)}}]}""";
    }

    /// <summary>An export whose run ended in <paramref name="state"/>; <paramref name="stopAt"/> is the <c>--stop-at</c> PC, if any.</summary>
    internal static string TargetsJsonWithStop(string state, ObservedRunIdentity? run, string? stopAt, params string[] targetObjects)
    {
        run ??= PlaceholderRun;
        var complete = state is "Completed" or "Returned" or "RuntimeHandoff" ? "true" : "false";
        var stop = stopAt is null ? "null" : $"\"{stopAt}\"";
        return $$"""{"format":"psxrecomp.fallback-targets/v2","sha256":"{{run.FirmwareSha256}}","run":{{JsonSerializer.Serialize(ObservedRootPlanner.RunDocument(run))}},"outcome":{"state":"{{state}}","complete":{{complete}},"stopAt":{{stop}}},"targets":[{{string.Join(",", targetObjects)}}]}""";
    }
}
