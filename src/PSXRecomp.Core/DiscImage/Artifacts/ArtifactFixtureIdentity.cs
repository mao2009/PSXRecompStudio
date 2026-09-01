using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage.Artifacts;

/// <summary>
/// Identity of one analyzed disc image, embedded verbatim in every artifact document
/// so each file is independently attributable without reading its siblings.
///
/// The formal identity is <see cref="DiscImageSha256"/> (and, for the analyzed code,
/// <see cref="ExecutableSha256"/>). <see cref="FixtureId"/> is a human-facing alias
/// used only for the on-disk directory name: two fixtures may share an alias across
/// machines, but never a hash. No local filesystem path ever appears here.
/// </summary>
[Domain]
public sealed record ArtifactFixtureIdentity
{
    /// <summary>Canonical fixture alias; see <see cref="AnalysisArtifactSchema.NormalizeFixtureId"/>.</summary>
    public required string FixtureId { get; init; }

    /// <summary>Container format of the analyzed disc image, e.g. <c>"CHD"</c>.</summary>
    public required string DiscImageFormat { get; init; }

    /// <summary>Lowercase hex SHA-256 of the whole disc image file. The formal input identity.</summary>
    public required string DiscImageSha256 { get; init; }

    /// <summary>Size of the disc image file in bytes.</summary>
    public required long DiscImageSizeBytes { get; init; }

    /// <summary>Boot executable file name as stored on the disc (ISO 9660 name, e.g. <c>SLPS_012.34</c>).</summary>
    public required string ExecutableFileName { get; init; }

    /// <summary>Serial derived from the executable name; see <see cref="AnalysisArtifactSchema.DeriveExecutableSerial"/>.</summary>
    public required string ExecutableSerial { get; init; }

    /// <summary>Size of the extracted boot executable in bytes.</summary>
    public required long ExecutableSizeBytes { get; init; }

    /// <summary>Lowercase hex SHA-256 of the extracted boot executable.</summary>
    public required string ExecutableSha256 { get; init; }
}
