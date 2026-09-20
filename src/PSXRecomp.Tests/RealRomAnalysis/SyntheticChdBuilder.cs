using System.Text;
using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Wraps an in-memory synthetic ISO 9660 image (see <see cref="SyntheticIsoImageBuilder"/>)
/// into the minimal uncompressed CHD that the production <see cref="ChdReader"/>
/// genuinely opens, so the CLI's real CHD input path can be exercised end-to-end without
/// a copyrighted disc image.
///
/// Layout: a V5 header and an uncompressed per-hunk map, then RAW Mode-1 CD frames, one
/// frame per hunk, each stored at an absolute hunk-unit file offset recorded by the map.
/// The data region begins after enough whole hunk units have been reserved for the complete
/// map, so large synthetic images cannot overlap map entries with frame data. Built entirely
/// in memory; never committed.
/// </summary>
[Test]
public static class SyntheticChdBuilder
{
    /// <summary>Conventional SYSTEM.CNF BOOT value pointing at a synthetic executable.</summary>
    public const string BootValue = @"cdrom:\SLPS_TEST.01;1";
    public const string ExeIsoName = "SLPS_TEST.01;1";

    private const uint CdFrameSize = ChdCdCodec.CdFrameSize;
    private const int IsoSectorSize = Iso9660Reader.SectorSize;
    private const int MapOffset = ChdHeader.V5HeaderSize;

    /// <summary>
    /// Builds a complete, bootable disc in memory: a SYSTEM.CNF booting
    /// <paramref name="exeIsoName"/> from the ISO 9660 image whose boot executable is
    /// <paramref name="exeBytes"/>, wrapped in an uncompressed CHD.
    /// </summary>
    public static byte[] Build(byte[] exeBytes, string? bootValue = null, string? exeIsoName = null)
    {
        ArgumentNullException.ThrowIfNull(exeBytes);
        var iso = new SyntheticIsoImageBuilder()
            .AddSystemCnf(bootValue ?? BootValue)
            .AddFile(exeIsoName ?? ExeIsoName, exeBytes)
            .Build();
        return WrapInChd(iso);
    }

    /// <summary>
    /// Wraps a plain image of 2048-byte user-data sectors in a RAW Mode-1 uncompressed
    /// CHD. Every frame carries the Mode-1 marker at offset 15, because the production
    /// ISO reader unwraps each sector through the mode byte.
    /// </summary>
    public static byte[] WrapInChd(byte[] isoImage)
    {
        ArgumentNullException.ThrowIfNull(isoImage);
        if (isoImage.Length == 0 || isoImage.Length % IsoSectorSize != 0)
        {
            throw new ArgumentException(
                $"ISO image must contain a whole number of {IsoSectorSize}-byte sectors.", nameof(isoImage));
        }

        int sectorCount = isoImage.Length / IsoSectorSize;
        var frames = new byte[(long)sectorCount * CdFrameSize];
        for (int i = 0; i < sectorCount; i++)
        {
            int frameOffset = i * (int)CdFrameSize;
            frames[frameOffset + 15] = 1; // RAW Mode-1: user data starts at offset 16.
            Buffer.BlockCopy(isoImage, i * IsoSectorSize, frames, frameOffset + 16, IsoSectorSize);
        }

        int hunkCount = sectorCount; // one raw CD frame per hunk
        int mapBytes = checked(4 * hunkCount);
        int firstDataUnit = checked(
            (MapOffset + mapBytes + (int)CdFrameSize - 1) / (int)CdFrameSize);
        var chd = new byte[checked((long)(firstDataUnit + hunkCount) * CdFrameSize)];

        Encoding.ASCII.GetBytes("MComprHD").CopyTo(chd, 0);
        WriteUInt32(chd, 8, ChdHeader.V5HeaderSize); // header length
        WriteUInt32(chd, 12, 5);                     // version 5
        WriteUInt64(chd, 32, (ulong)frames.Length);  // logical bytes
        WriteUInt64(chd, 40, (ulong)MapOffset);      // map offset
        WriteUInt32(chd, 56, CdFrameSize);           // hunk bytes
        WriteUInt32(chd, 60, CdFrameSize);           // unit bytes

        for (int hunk = 0; hunk < hunkCount; hunk++)
        {
            // Uncompressed map: absolute hunk-unit file offset. The data region starts
            // after the complete map, aligned to a whole unit, so map entries can never
            // overwrite frame data even when the synthetic image contains many sectors.
            WriteUInt32(chd, MapOffset + 4 * hunk, (uint)(firstDataUnit + hunk));
            Buffer.BlockCopy(frames, hunk * (int)CdFrameSize, chd,
                (firstDataUnit + hunk) * (int)CdFrameSize, (int)CdFrameSize);
        }

        return chd;
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteUInt64(byte[] buffer, int offset, ulong value)
    {
        WriteUInt32(buffer, offset, (uint)(value >> 32));
        WriteUInt32(buffer, offset + 4, (uint)value);
    }
}