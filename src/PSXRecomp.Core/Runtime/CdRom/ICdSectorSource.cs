using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.CdRom;

/// <summary>One table-of-contents entry: track number (1-99) and its first sector as an absolute LBA (0 = MSF 00:02:00).</summary>
[Domain]
public readonly record struct CdTrack(int Number, int StartLba, bool IsAudio);

/// <summary>A disc's table of contents: its tracks in ascending order and the lead-out start LBA.</summary>
[Domain]
public sealed record CdDiscToc
{
    /// <exception cref="ArgumentException">No track, a track number outside 1-99 or not consecutive, or start LBAs that are
    /// negative, not ascending, or not before the lead-out.</exception>
    public CdDiscToc(IReadOnlyList<CdTrack> tracks, int leadOutLba)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0)
        {
            throw new ArgumentException("A disc has at least one track.", nameof(tracks));
        }

        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            var previous = i == 0 ? (CdTrack?)null : tracks[i - 1];
            if (track.Number is < 1 or > 99 || (previous is { } p && (track.Number != p.Number + 1 || track.StartLba <= p.StartLba))
                || track.StartLba < 0 || track.StartLba >= leadOutLba)
            {
                throw new ArgumentException($"Track {i} ({track}) is not a valid, ascending TOC entry before lead-out {leadOutLba}.", nameof(tracks));
            }
        }

        Tracks = tracks.ToArray();
        LeadOutLba = leadOutLba;
    }

    public IReadOnlyList<CdTrack> Tracks { get; }

    /// <summary>First sector after the last track (the lead-out area), absolute LBA.</summary>
    public int LeadOutLba { get; }

    public int FirstTrack => Tracks[0].Number;

    public int LastTrack => Tracks[^1].Number;

    /// <summary>A single data track 1 starting at LBA 0 and holding <paramref name="sectorCount"/> sectors.</summary>
    public static CdDiscToc SingleDataTrack(int sectorCount) => new([new CdTrack(1, 0, false)], sectorCount);
}

/// <summary>
/// The disc in the drive as the CD-ROM controller sees it (Issue #732): a TOC and raw 2352-byte sectors by absolute LBA.
/// Implementations never fabricate data: a sector the image does not hold is reported absent, never zero-filled.
/// </summary>
[Domain]
public interface ICdSectorSource
{
    /// <summary>Raw CD sector size: sync, header, subheader, user data and EDC/ECC.</summary>
    public const int RawSectorSize = 2352;

    CdDiscToc Toc { get; }

    /// <summary>
    /// Copies the raw sector at absolute <paramref name="lba"/> (0 = MSF 00:02:00) into the first
    /// <see cref="RawSectorSize"/> bytes of <paramref name="destination"/>.
    /// </summary>
    /// <returns>False, with <paramref name="destination"/> untouched, when the disc holds no such sector.</returns>
    bool TryReadSector(int lba, Span<byte> destination);
}

/// <summary>
/// The three position spaces of a CD (owner ruling, Issue #10). LBA 0 is MSF 00:02:00, the convention of
/// <see cref="CdTrack"/> above. A file index is a sector's position from the start of a raw image or BINARY file, which
/// is MSF 00:00:00 for a disc dump and so carries the 150-sector lead-in. The offset is applied here, once per conversion:
/// <see cref="CdRomDevice"/> converts a SetLoc MSF to an LBA (minus 150), and a source converts its LBA to its own file
/// index (plus 150) only when the source stores the disc from file index 0.
/// </summary>
[Domain]
public static class CdSectorAddress
{
    /// <summary>Frames before LBA 0 (MSF 00:00:00 to 00:01:74). LBA 0 is MSF 00:02:00.</summary>
    public const int LeadInFrames = 150;

    /// <summary>Converts a file index (sector position from the start of a file-origin image) to an LBA.</summary>
    public static int FileIndexToLba(int fileIndex) => fileIndex - LeadInFrames;

    /// <summary>Converts an LBA to the file index of that sector in a file-origin image.</summary>
    public static int LbaToFileIndex(int lba) => lba + LeadInFrames;
}

/// <summary>
/// A raw image of 2352-byte sectors held in memory, from a file origin: sector index 0 is MSF 00:00:00, so the image
/// begins with the 150-sector lead-in and track 1 at 00:02:00 is LBA 0 (<see cref="CdSectorAddress"/>). Used for a
/// <c>.bin</c> dump and for synthetic test discs. The lead-out is the file's sector count less 150.
/// </summary>
/// <remarks>
/// Before Issue #10 owner ruling this class mapped LBA to file index directly, so an LBA read 150 sectors early. It is
/// now the LBA-to-file mapping of <see cref="CdSectorAddress"/>. The CHD source (ChdCdSectorSource, in
/// PSXRecomp.Core.DiscImage) stores the disc from LBA 0 and is not offset; see its remarks.
/// </remarks>
[Domain]
public sealed class RawCdSectorSource : ICdSectorSource
{
    private readonly ReadOnlyMemory<byte> _image;

    /// <exception cref="ArgumentException">The image is empty, not a whole number of raw sectors, or not longer than the
    /// 150-sector lead-in.</exception>
    public RawCdSectorSource(ReadOnlyMemory<byte> image)
    {
        if (image.Length == 0 || image.Length % ICdSectorSource.RawSectorSize != 0)
        {
            throw new ArgumentException(
                $"A raw CD image is a non-empty multiple of {ICdSectorSource.RawSectorSize} bytes; got {image.Length}.", nameof(image));
        }

        var fileSectors = image.Length / ICdSectorSource.RawSectorSize;
        if (fileSectors <= CdSectorAddress.LeadInFrames)
        {
            throw new ArgumentException(
                $"A raw CD image holds {fileSectors} sectors, not more than the {CdSectorAddress.LeadInFrames}-sector lead-in.", nameof(image));
        }

        _image = image;
        Toc = new CdDiscToc([new CdTrack(1, 0, false)], CdSectorAddress.FileIndexToLba(fileSectors));
    }

    public CdDiscToc Toc { get; }

    public bool TryReadSector(int lba, Span<byte> destination)
    {
        if (lba < 0 || lba >= Toc.LeadOutLba)
        {
            return false;
        }

        _image.Span.Slice(CdSectorAddress.LbaToFileIndex(lba) * ICdSectorSource.RawSectorSize, ICdSectorSource.RawSectorSize)
            .CopyTo(destination);
        return true;
    }
}
