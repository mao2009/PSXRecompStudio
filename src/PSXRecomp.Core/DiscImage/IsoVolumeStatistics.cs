using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Deterministic, machine-comparable statistics about an ISO 9660 volume. Mirrors
/// <see cref="ChdMapStatistics"/> for the filesystem layer: every value is derived
/// purely from the Primary Volume Descriptor and a full directory traversal, so it is
/// stable for a given disc image.
/// </summary>
[Domain]
public sealed record IsoVolumeStatistics
{
    /// <summary>Volume identifier from the Primary Volume Descriptor, trimmed of padding.</summary>
    public required string? VolumeIdentifier { get; init; }

    /// <summary>Volume space size, in 2048-byte logical sectors.</summary>
    public required uint VolumeSpaceSize { get; init; }

    /// <summary>Logical sector holding the root directory record.</summary>
    public required uint RootDirectoryLocation { get; init; }

    /// <summary>Size of the root directory extent, in bytes.</summary>
    public required uint RootDirectorySize { get; init; }

    /// <summary>Whether a SYSTEM.CNF boot descriptor exists in the volume root.</summary>
    public required bool SystemCnfPresent { get; init; }

    /// <summary>Number of files reachable from the root, excluding "." and "..".</summary>
    public required int FileCount { get; init; }

    /// <summary>Number of directories reachable from the root, including the root itself.</summary>
    public required int DirectoryCount { get; init; }
}
