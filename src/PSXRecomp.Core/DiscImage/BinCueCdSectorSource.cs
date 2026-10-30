using PSXRecomp.Architecture;
using PSXRecomp.Core.Runtime.CdRom;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Presents a BIN/CUE disc (Issue #10) to the CD-ROM controller as <see cref="ICdSectorSource"/>: the CUE's TRACK
/// entries become the TOC, and the single BINARY file's 2352-byte sectors are read by absolute LBA. The image is held
/// in memory, as <see cref="RawCdSectorSource"/> does.
/// </summary>
/// <remarks>
/// Three position spaces are kept apart (owner ruling, Issue #10):
/// <list type="bullet">
/// <item>File index: a sector's position from the start of the BINARY file. CUE MSF values (INDEX 01 and so on) count
/// in this space, and <see cref="BinCueTrack.Index01Frames"/> is in it.</item>
/// <item>LBA: the ICdSectorSource.cs:5 convention, LBA 0 = MSF 00:02:00. <see cref="TryReadSector"/>, <see cref="CdTrack.StartLba"/>
/// and <see cref="CdDiscToc.LeadOutLba"/> are in this space.</item>
/// <item>Absolute MSF frames: LBA + 150, as the CD-ROM controller's SetLoc and GetTD report them.</item>
/// </list>
/// The mapping is LBA = file index - 150 (<see cref="FileIndexToLba"/>, <see cref="LbaToFileIndex"/>). A track start is
/// its INDEX 01 file index minus 150; the lead-out is the file's sector count minus 150. INDEX 00 (pregap) is parsed and
/// validated but not represented in the TOC, because the pregap lies before LBA 0 for a track 1 at 00:02:00 and the
/// TOC has no position for it. A multi-track sheet yields one <see cref="CdTrack"/> per TRACK, in file order.
/// </remarks>
/// <remarks>
/// This source and <see cref="RawCdSectorSource"/> both store the image from file index 0 and apply the same
/// 150-sector mapping, so a BIN dump and its raw image read the same physical sector for each LBA. The CHD source
/// stores the disc from LBA 0 (see its remarks), which is the one open question for the owner.
/// </remarks>
/// <remarks>
/// The image is referenced, not copied: the caller's <paramref name="bin"/> array must not be mutated while this source
/// is in use, or the sectors it returns change.
/// </remarks>
/// <remarks>
/// A <paramref name="destination"/> shorter than <see cref="ICdSectorSource.RawSectorSize"/> is not written to and
/// returns false, the same as a sector the disc does not hold.
/// </remarks>
[Domain]
public sealed class BinCueCdSectorSource : ICdSectorSource
{
    /// <summary>Frames before LBA 0: LBA 0 is MSF 00:02:00, and the file index of LBA 0 is this value.</summary>
    public const int LeadInFrames = CdSectorAddress.LeadInFrames;

    private readonly ReadOnlyMemory<byte> _bin;

    /// <param name="cueText">The CUE sheet text.</param>
    /// <param name="bin">The BINARY file's bytes: a non-empty whole number of raw 2352-byte sectors, at least
    /// <see cref="LeadInFrames"/> of them. Referenced, not copied.</param>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    /// <exception cref="ArgumentException">The file is empty, not a whole number of raw sectors, or shorter than the
    /// 150-sector lead-in, or the TOC is invalid (for example an INDEX 01 before 00:02:00 or at or after the end of the file).</exception>
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

        var fileSectors = bin.Length / ICdSectorSource.RawSectorSize;
        if (fileSectors < LeadInFrames)
        {
            throw new ArgumentException(
                $"A BIN file holds {fileSectors} sectors, fewer than the {LeadInFrames}-sector lead-in before LBA 0.", nameof(bin));
        }

        var sheet = BinCueSheet.Parse(cueText);
        var tracks = sheet.Tracks
            .Select(t => new CdTrack(t.Number, FileIndexToLba(t.Index01Frames), t.IsAudio))
            .ToArray();
        Toc = new CdDiscToc(tracks, FileIndexToLba(fileSectors));
        _bin = bin;
    }

    public CdDiscToc Toc { get; }

    /// <summary>Converts a file index (sector position from the start of the BINARY file) to an LBA.</summary>
    public static int FileIndexToLba(int fileIndex) => CdSectorAddress.FileIndexToLba(fileIndex);

    /// <summary>Converts an LBA (LBA 0 = MSF 00:02:00) to the file index of that sector in the BINARY file.</summary>
    public static int LbaToFileIndex(int lba) => CdSectorAddress.LbaToFileIndex(lba);

    /// <summary>Reads the sector at LBA <paramref name="lba"/>, which is file index <c>lba + 150</c>.</summary>
    public bool TryReadSector(int lba, Span<byte> destination)
    {
        if (lba < 0 || lba >= Toc.LeadOutLba || destination.Length < ICdSectorSource.RawSectorSize)
        {
            return false;
        }

        _bin.Span.Slice(LbaToFileIndex(lba) * ICdSectorSource.RawSectorSize, ICdSectorSource.RawSectorSize)
            .CopyTo(destination);
        return true;
    }
}
