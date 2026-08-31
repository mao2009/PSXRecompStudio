using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Deterministic, machine-comparable statistics about a CHD disc image's map.
/// These describe the size and compression layout of the compressed data region
/// and are derived purely from the CHD header and decompressed map, so they are
/// stable for a given input file.
/// </summary>
[Domain]
public sealed record ChdMapStatistics
{
    public required uint Version { get; init; }

    /// <summary>Total size of the un-compressed logical disc image in bytes.</summary>
    public required ulong LogicalBytes { get; init; }

    /// <summary>Size of a single hunk before compression, in bytes.</summary>
    public required uint HunkBytes { get; init; }

    /// <summary>Total number of hunks in the disc image.</summary>
    public required int TotalHunks { get; init; }

    /// <summary>Number of hunks compressed with the "cdlz" (LZMA) CD codec.</summary>
    public required int CdlzCount { get; init; }

    /// <summary>Number of hunks compressed with the "cdzl" (zlib) CD codec.</summary>
    public required int CdzlCount { get; init; }

    /// <summary>Number of bytes occupied by the map region (header + compressed map).</summary>
    public required long MapBytesConsumed { get; init; }

    /// <summary>Total size in bytes of the compressed data region referenced by the map.</summary>
    public required long DataRegionSize { get; init; }
}
