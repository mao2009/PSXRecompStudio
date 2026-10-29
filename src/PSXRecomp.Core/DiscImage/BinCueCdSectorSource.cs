using PSXRecomp.Architecture;
using PSXRecomp.Core.Runtime.CdRom;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Presents a BIN/CUE disc (Issue #10) to the CD-ROM controller as <see cref="ICdSectorSource"/>: the CUE's TRACK
/// entries become the TOC, and the single BINARY file's 2352-byte sectors are read by absolute LBA. The image is held
/// in memory, as <see cref="RawCdSectorSource"/> does.
/// </summary>
/// <remarks>
/// LBA 0 is the first sector of the BINARY file, so INDEX 01 MSF values are used as LBAs directly; a typical data
/// track 1 with INDEX 01 00:02:00 starts at LBA 150. The lead-out is the file's sector count.
/// </remarks>
[Domain]
public sealed class BinCueCdSectorSource : ICdSectorSource
{
    private readonly ReadOnlyMemory<byte> _bin;

    /// <param name="cueText">The CUE sheet text.</param>
    /// <param name="bin">The BINARY file's bytes: a non-empty whole number of raw 2352-byte sectors.</param>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    /// <exception cref="ArgumentException">The file is empty or not a whole number of raw sectors, or the TOC is invalid
    /// (for example an INDEX 01 at or after the end of the file).</exception>
    /// <exception cref="InvalidDataException">The CUE sheet is malformed.</exception>
    /// <exception cref="NotSupportedException">The CUE sheet uses a track mode that is not read.</exception>
    public BinCueCdSectorSource(string cueText, ReadOnlyMemory<byte> bin)
    {
        ArgumentNullException.ThrowIfNull(cueText);
        if (bin.Length == 0 || bin.Length % ICdSectorSource.RawSectorSize != 0)
        {
            throw new ArgumentException(
                $"A BIN file is a non-empty multiple of {ICdSectorSource.RawSectorSize} bytes; got {bin.Length}.", nameof(bin));
        }

        var sheet = BinCueSheet.Parse(cueText);
        var tracks = sheet.Tracks.Select(t => new CdTrack(t.Number, t.Index01Frames, t.IsAudio)).ToArray();
        Toc = new CdDiscToc(tracks, bin.Length / ICdSectorSource.RawSectorSize);
        _bin = bin;
    }

    public CdDiscToc Toc { get; }

    public bool TryReadSector(int lba, Span<byte> destination)
    {
        if (lba < 0 || lba >= Toc.LeadOutLba)
        {
            return false;
        }

        _bin.Span.Slice(lba * ICdSectorSource.RawSectorSize, ICdSectorSource.RawSectorSize).CopyTo(destination);
        return true;
    }
}
