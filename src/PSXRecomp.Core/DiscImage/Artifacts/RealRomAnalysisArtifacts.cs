using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage.Artifacts;

/// <summary>
/// The complete deterministic artifact set for one fixture, together with the exact
/// canonical text of each document. <see cref="Files"/> is the authoritative list of
/// what a writer must persist: file name plus content, in a fixed order, with no
/// further formatting decisions left to the caller.
/// </summary>
[Domain]
public sealed record RealRomAnalysisArtifacts
{
    public required AnalysisManifestDocument Manifest { get; init; }
    public required AnalysisReportDocument Report { get; init; }
    public required InstructionListDocument Instructions { get; init; }
    public required ControlFlowGraphDocument Cfg { get; init; }

    /// <summary>
    /// Canonical file name / content pairs, ordered by file name (ordinal ascending).
    /// Content is already newline- and encoding-canonical; a writer must persist it
    /// verbatim as UTF-8 without a BOM.
    /// </summary>
    public required IReadOnlyList<ArtifactFile> Files { get; init; }
}

/// <summary>One persisted artifact file: its name within the fixture directory and its canonical text.</summary>
[Domain]
public sealed record ArtifactFile
{
    public required string FileName { get; init; }
    public required string Content { get; init; }

    /// <summary>The exact bytes to write: UTF-8, no BOM, LF line endings.</summary>
    public byte[] ToUtf8Bytes() => ArtifactJson.ToUtf8Bytes(Content);
}
