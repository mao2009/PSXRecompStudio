using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage.AnalysisArtifacts;

/// <summary>
/// Selected-function provenance: the #215 contract that identifies which guest
/// range of which executable a downstream consumer selected, reproduced from
/// metadata alone — never from the executable's bytes.
///
/// <para>
/// This is the persisted/reportable shape of the smaller, Recompiler-internal
/// <c>RealRomFunctionProvenance</c> (Issue #225). It is a standalone, optional
/// document: it is not part of <c>manifest.json</c>'s <c>documents[]</c> set, so
/// readers of the four-document set are unaffected by the absence of a provenance
/// document, and no manifest schema bump is implied by adding one.
/// </para>
///
/// <para>
/// The document carries no raw instruction words and no executable bytes. The
/// selected range is identified by guest addresses plus a deterministic SHA-256
/// over the range's basic blocks (see
/// <see cref="FunctionProvenanceBuilder.Build"/>), and the executable is
/// identified by <see cref="ArtifactFixtureIdentity.ExecutableSha256"/>.
/// </para>
/// </summary>
[Domain]
public sealed record SelectedFunctionProvenanceDocument
{
    public required int SchemaVersion { get; init; }
    public required string ArtifactKind { get; init; }

    /// <summary>Disc/executable identity shared with the sibling analysis documents.</summary>
    public required ArtifactFixtureIdentity Fixture { get; init; }

    /// <summary>Address of the first selected instruction, canonical <c>0xXXXXXXXX</c>.</summary>
    public required string StartAddress { get; init; }

    /// <summary>Address of the last selected instruction (inclusive), canonical <c>0xXXXXXXXX</c>.</summary>
    public required string EndAddress { get; init; }

    /// <summary>Total decoded instruction count over the selected range's blocks.</summary>
    public required int InstructionCount { get; init; }

    /// <summary>How the range was selected, e.g. <c>"entry-point-reachability"</c> or <c>"self-contained-candidate-window"</c>.</summary>
    public required string SelectionRule { get; init; }

    /// <summary>Canonical ordering contract of <see cref="BasicBlocks"/> (identical to the CFG artifact's).</summary>
    public required string BlockOrdering { get; init; }

    /// <summary>Basic blocks covering the selected range, start then end address ascending.</summary>
    public required IReadOnlyList<BasicBlockRecord> BasicBlocks { get; init; }

    /// <summary>
    /// Deterministic identity of the selected range's CFG subset: lowercase hex SHA-256
    /// over the canonical JSON of <see cref="BasicBlocks"/>. Computed by
    /// <see cref="FunctionProvenanceBuilder"/> over the blocks alone, so it never depends
    /// on — and never equals — the hash of the whole document.
    /// </summary>
    public required string BlockIdentitySha256 { get; init; }

    /// <summary>Distinct instruction classes the range requires, name-ordinal ascending.</summary>
    public required string SubsetOrdering { get; init; }

    /// <summary>Distinct opcode names the range requires, e.g. <c>addiu</c>, <c>beq</c>, <c>lui</c>; name-ordinal ascending.</summary>
    public required IReadOnlyList<string> RequiredInstructionSubset { get; init; }

    /// <summary>Canonical ordering contract of <see cref="UnresolvedFlags"/>.</summary>
    public required string FlagsOrdering { get; init; }

    /// <summary>
    /// Stable, explicitly named analysis limitations that were <em>not</em> resolved for
    /// this range, e.g. <c>indirect-control-flow</c>, <c>bios-call</c>,
    /// <c>decode-failure</c>. Empty means the selection reports no unresolved analysis
    /// flags; it never asserts a category was validated when it was not analyzed at all —
    /// the producer decides which flags exist, this schema only records them.
    /// </summary>
    public required IReadOnlyList<string> UnresolvedFlags { get; init; }

    public string ToCanonicalJson() => ArtifactJson.Serialize(this);
}