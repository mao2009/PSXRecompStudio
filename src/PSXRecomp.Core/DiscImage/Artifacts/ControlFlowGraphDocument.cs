using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage.Artifacts;

/// <summary>
/// <c>cfg.json</c>: the detailed control-flow artifact. Basic blocks and edges are each
/// emitted in a fixed canonical order, recorded in <see cref="BlockOrdering"/> and
/// <see cref="EdgeOrdering"/>, so the file is a stable description of the graph rather
/// than of the order in which the builder happened to discover it.
/// </summary>
[Domain]
public sealed record ControlFlowGraphDocument
{
    public required int SchemaVersion { get; init; }
    public required string ArtifactKind { get; init; }
    public required ArtifactFixtureIdentity Fixture { get; init; }

    /// <summary>Canonical ordering contract of <see cref="BasicBlocks"/>.</summary>
    public required string BlockOrdering { get; init; }

    /// <summary>Canonical ordering contract of <see cref="Edges"/>.</summary>
    public required string EdgeOrdering { get; init; }

    public required int BasicBlockCount { get; init; }
    public required int EdgeCount { get; init; }
    public required IReadOnlyList<BasicBlockRecord> BasicBlocks { get; init; }
    public required IReadOnlyList<CfgEdgeRecord> Edges { get; init; }
}

/// <summary>
/// One basic block. <see cref="EndAddress"/> is the address of the block's last
/// instruction (inclusive), not the address one past its end.
/// </summary>
[Domain]
public sealed record BasicBlockRecord
{
    public required string StartAddress { get; init; }
    public required string EndAddress { get; init; }
    public required int InstructionCount { get; init; }
}

/// <summary>
/// One directed control-flow edge. <see cref="Kind"/> is the analyzer's edge
/// classification (<c>branch</c>, <c>jump</c>, <c>fallthrough</c>, <c>indirect</c>);
/// an unresolved indirect target is recorded as <c>0x00000000</c>.
/// </summary>
[Domain]
public sealed record CfgEdgeRecord
{
    public required string SourceAddress { get; init; }
    public required string TargetAddress { get; init; }
    public required string Kind { get; init; }
}
