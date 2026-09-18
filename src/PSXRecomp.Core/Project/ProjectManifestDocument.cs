using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Project;

/// <summary>
/// <c>project.json</c>: the minimal, versioned Project/Workspace management-data
/// manifest (Issue #42, first persistence slice). It carries only the metadata
/// needed to identify and reproduce a project, never a build output, a
/// generated artifact reference, a local filesystem path, a timestamp, or any
/// other machine-specific value.
///
/// This is a deliberately closed, minimal shape for this slice: the exact four
/// properties below are the whole persisted contract. Adding a property here is
/// a format change — bump <see cref="ProjectSchema.ProjectFormatVersion"/> and
/// update <see cref="ProjectMetadataSerializer"/> and the boundary/determinism
/// tests that pin this shape.
/// </summary>
[Domain]
public sealed record ProjectManifestDocument
{
    public required int SchemaVersion { get; init; }

    public required string ArtifactKind { get; init; }

    /// <summary>Stable identifier for this project/workspace. Not a filesystem path.</summary>
    public required string ProjectId { get; init; }

    /// <summary>Lowercase hex SHA-256 identity of the disc/input image this project analyzes.</summary>
    public required string InputSha256 { get; init; }
}
