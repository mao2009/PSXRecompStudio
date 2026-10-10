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
/// A raw single-track image of 2352-byte sectors held in memory (a <c>.bin</c> data track, or a synthetic test disc),
/// track 1 starting at LBA 0.
/// </summary>
[Domain]
public sealed class RawCdSectorSource : ICdSectorSource
{
    private readonly ReadOnlyMemory<byte> _image;

    /// <exception cref="ArgumentException">The image is empty or not a whole number of raw sectors.</exception>
    public RawCdSectorSource(ReadOnlyMemory<byte> image)
    {
        if (image.Length == 0 || image.Length % ICdSectorSource.RawSectorSize != 0)
        {
            throw new ArgumentException(
                $"A raw CD image is a non-empty multiple of {ICdSectorSource.RawSectorSize} bytes; got {image.Length}.", nameof(image));
        }

        _image = image;
        Toc = CdDiscToc.SingleDataTrack(image.Length / ICdSectorSource.RawSectorSize);
    }

    public CdDiscToc Toc { get; }

    public bool TryReadSector(int lba, Span<byte> destination)
    {
        if (lba < 0 || lba >= Toc.LeadOutLba)
        {
            return false;
        }

        _image.Span.Slice(lba * ICdSectorSource.RawSectorSize, ICdSectorSource.RawSectorSize).CopyTo(destination);
        return true;
    }
}
