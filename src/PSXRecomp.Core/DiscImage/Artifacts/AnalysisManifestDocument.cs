using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage.Artifacts;

/// <summary>
/// <c>manifest.json</c>: the compact index of one fixture's analysis. Small enough to
/// read at a glance and to diff across titles or across analyzer revisions, it carries
/// input identity, headline counts, and a content hash for each sibling document.
///
/// The manifest never hashes itself: <see cref="Documents"/> references
/// <c>report.json</c>, <c>instructions.json</c> and <c>cfg.json</c> only, which keeps
/// the hash set well-defined and free of self-reference.
/// </summary>
[Domain]
public sealed record AnalysisManifestDocument
{
    public required int SchemaVersion { get; init; }
    public required string ArtifactKind { get; init; }
    public required ArtifactFixtureIdentity Fixture { get; init; }
    public required AnalysisCounts Counts { get; init; }

    /// <summary>Sibling documents, ordered by file name (ordinal ascending).</summary>
    public required IReadOnlyList<ArtifactDocumentReference> Documents { get; init; }
}

/// <summary>
/// Content-addressed reference from <c>manifest.json</c> to a sibling artifact document.
/// </summary>
[Domain]
public sealed record ArtifactDocumentReference
{
    /// <summary>File name relative to the fixture directory, e.g. <c>report.json</c>.</summary>
    public required string FileName { get; init; }

    public required string ArtifactKind { get; init; }
    public required int SchemaVersion { get; init; }

    /// <summary>Length of the document's canonical UTF-8 encoding, in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Lowercase hex SHA-256 of the document's canonical UTF-8 encoding.</summary>
    public required string Sha256 { get; init; }
}

/// <summary>
/// Headline analysis counts. These are the numbers a regression diff is expected to
/// compare first: a change in any of them means the analyzer behaved differently on
/// the same input.
/// </summary>
[Domain]
public sealed record AnalysisCounts
{
    public required int DecodedInstructions { get; init; }
    public required int DecodeFailures { get; init; }
    public required int BasicBlocks { get; init; }
    public required int CfgEdges { get; init; }
    public required int Branches { get; init; }
    public required int Jumps { get; init; }
    public required int CallCandidates { get; init; }
    public required int ReturnCandidates { get; init; }
}
