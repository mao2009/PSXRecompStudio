using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Parsed metadata from a CHD (Compressed Hunks of Data) v5 disc image header.
/// All multi-byte integers are stored in big-endian (Motorola) byte order.
/// </summary>
[Domain]
public sealed record ChdHeader
{
    public const uint ExpectedMagicLow = 0x4D436F6D;  // "MCom" as raw bytes
    public const uint ExpectedMagicHigh = 0x70724844;  // "prHD" as raw bytes
    public const int V5HeaderSize = 124;
    public const int MapEntrySize = 12;

    public required uint Version { get; init; }
    public required uint HeaderLength { get; init; }
    public required uint[] Compressors { get; init; }   // 4x uint32 BE codec tags
    public required ulong LogicalBytes { get; init; }
    public required ulong MapOffset { get; init; }
    public required ulong MetaOffset { get; init; }
    public required uint HunkBytes { get; init; }
    public required uint UnitBytes { get; init; }
    public required byte[] RawSha1 { get; init; }
    public required byte[] Sha1 { get; init; }
    public required byte[] ParentSha1 { get; init; }

    public bool IsCompressed => Compressors[0] != 0;
    public bool HasParent => ParentSha1.Any(b => b != 0);

    public int FramesPerHunk => UnitBytes > 0 ? (int)(HunkBytes / UnitBytes) : 0;
    public int TotalHunks => HunkBytes > 0 ? (int)((LogicalBytes + HunkBytes - 1) / HunkBytes) : 0;

    public string CompressionName(int index) => Compressors[index] switch
    {
        0 => "none",
        _ => Encoding.ASCII.GetString([
            (byte)(Compressors[index] >> 24),
            (byte)(Compressors[index] >> 16),
            (byte)(Compressors[index] >> 8),
            (byte)(Compressors[index]),
        ]).TrimEnd('\0'),
    };

    public static uint ReadUInt32BE(ReadOnlySpan<byte> data, int offset) =>
        (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

    public static ulong ReadUInt64BE(ReadOnlySpan<byte> data, int offset) =>
        ((ulong)ReadUInt32BE(data, offset) << 32) | ReadUInt32BE(data, offset + 4);
}
