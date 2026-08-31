using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// A single expanded map entry from a CHD v5 file (12 bytes each).
/// Maps hunk indices to compressed data locations in the file.
/// </summary>
[Domain]
public readonly record struct ChdMapEntry
{
    public const byte CompressionNone = 4;

    public required byte CompressionType { get; init; }
    public required uint CompressedLength { get; init; }
    public required ulong FileOffset { get; init; }
    public required ushort Crc16 { get; init; }

    public bool IsCompressed => CompressionType < CompressionNone;
    public bool IsUncompressed => CompressionType == CompressionNone;
    public bool IsSelfRef => CompressionType == 5;
    public bool IsParentRef => CompressionType == 6;
}
