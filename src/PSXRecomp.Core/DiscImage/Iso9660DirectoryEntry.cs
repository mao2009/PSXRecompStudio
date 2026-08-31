using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Minimal ISO 9660 directory entry for locating files within a disc image.
/// </summary>
[Domain]
public sealed record Iso9660DirectoryEntry
{
    public required uint Location { get; init; }
    public required uint Size { get; init; }
    public required byte Flags { get; init; }
    public required byte FileNameLength { get; init; }
    public required string FileName { get; init; }

    public bool IsDirectory => (Flags & 0x02) != 0;
    public bool IsFile => (Flags & 0x02) == 0;

    /// <summary>
    /// Location is a logical sector number (LSN). Multiply by sector size for byte offset.
    /// </summary>
    public long ByteOffset => (long)Location * Iso9660Reader.SectorSize;
}
