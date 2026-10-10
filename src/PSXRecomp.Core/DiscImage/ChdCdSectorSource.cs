using PSXRecomp.Architecture;
using PSXRecomp.Core.Runtime.CdRom;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Presents a CHD CD image (<see cref="ChdReader"/>) to the CD-ROM controller as a disc (Issue #732). The reader's
/// frames are the disc's sectors from LBA 0, the layout of a single-data-track PlayStation disc.
/// </summary>
/// <remarks>
/// ponytail: the TOC is one data track over every frame; the CHD track metadata (CHT2) is not parsed, so a multi-track
/// image reports its audio tracks as part of track 1. Parse the metadata when CD-DA titles need a real TOC.
/// </remarks>
[Domain]
public sealed class ChdCdSectorSource : ICdSectorSource
{
    private readonly ChdReader _chd;

    /// <param name="chd">The caller-owned reader; it must outlive this source.</param>
    /// <exception cref="ArgumentException">The image declares no CD frames.</exception>
    public ChdCdSectorSource(ChdReader chd)
    {
        _chd = chd ?? throw new ArgumentNullException(nameof(chd));
        var frames = chd.Header.UnitBytes == 0 ? 0UL : chd.Header.LogicalBytes / chd.Header.UnitBytes;
        if (frames == 0 || frames > int.MaxValue)
        {
            throw new ArgumentException($"The CHD image declares {frames} CD frames.", nameof(chd));
        }

        Toc = CdDiscToc.SingleDataTrack((int)frames);
    }

    public CdDiscToc Toc { get; }

    /// <exception cref="InvalidDataException">The hunk holding the sector cannot be decoded.</exception>
    public bool TryReadSector(int lba, Span<byte> destination)
    {
        if (lba < 0 || lba >= Toc.LeadOutLba)
        {
            return false;
        }

        _chd.ReadSector(lba).CopyTo(destination);
        return true;
    }
}
