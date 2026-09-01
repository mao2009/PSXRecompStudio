using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage.Artifacts;

/// <summary>
/// <c>instructions.json</c>: the detailed instruction artifact. One entry per decoded
/// instruction, always in ascending address order (<see cref="Ordering"/> records that
/// contract inside the file itself), so two runs — or two analyzer revisions — can be
/// compared with an ordinary line diff.
/// </summary>
[Domain]
public sealed record InstructionListDocument
{
    public required int SchemaVersion { get; init; }
    public required string ArtifactKind { get; init; }
    public required ArtifactFixtureIdentity Fixture { get; init; }

    /// <summary>Canonical ordering contract of <see cref="Instructions"/>.</summary>
    public required string Ordering { get; init; }

    public required int Count { get; init; }
    public required IReadOnlyList<InstructionRecord> Instructions { get; init; }
}

/// <summary>
/// One decoded instruction. Addresses and raw words use the canonical
/// <c>0xXXXXXXXX</c> literal form; mnemonic, operands, format and control-flow
/// classification are the analyzer's own strings, reproduced verbatim.
/// </summary>
[Domain]
public sealed record InstructionRecord
{
    public required string Address { get; init; }
    public required string RawWord { get; init; }
    public required string Mnemonic { get; init; }
    public required string Operands { get; init; }

    /// <summary>MIPS encoding format of the instruction (R/I/J-type and friends).</summary>
    public required string Format { get; init; }

    /// <summary>Control-flow classification, e.g. <c>None</c>, <c>ConditionalBranch</c>, <c>JumpRegister</c>.</summary>
    public required string ControlFlow { get; init; }
}
