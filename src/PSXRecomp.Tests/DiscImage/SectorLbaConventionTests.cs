using FluentAssertions;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Runtime.CdRom;

namespace PSXRecomp.Tests.DiscImageTests;

/// <summary>
/// Issue #10: the 150-sector question, end to end through <see cref="CdRomDevice"/>. A synthetic 1000-sector BINARY file
/// marks each file sector with its own index in its user data. The device's SetLoc takes an absolute MSF, computes
/// LBA = frames - 150 (CdRomDevice.ConsumeSeekTarget) and passes that LBA to the source. The file index of a sector is its
/// absolute frame count, because a BIN dump starts at 00:00:00 (an assumption; these fixtures are synthetic).
/// </summary>
/// <remarks>
/// Result at the time of writing: the BIN/CUE adapter (LBA = file index - 150) returns the disc-correct sector, and the
/// raw source (LBA = file index) returns the sector 150 earlier. The raw test pins that current behaviour and is an
/// open question for the owner. Nothing in the existing raw or CHD path is changed here.
/// </remarks>
[Test]
public sealed class SectorLbaConventionTests
{
    private const string Cue = "FILE \"a.bin\" BINARY\nTRACK 01 MODE2/2352\nINDEX 01 00:02:00\n";
    private const int FileSectors = 1000;

    /// <summary>A BINARY file where file sector N carries N in its user data (bytes 16..19, little-endian).</summary>
    private static byte[] MarkedBin()
    {
        var bin = new byte[FileSectors * ICdSectorSource.RawSectorSize];
        for (var n = 0; n < FileSectors; n++)
        {
            var offset = n * ICdSectorSource.RawSectorSize;
            bin[offset + 15] = 1; // Mode 1: user data begins at byte 16 of the raw sector
            BitConverter.GetBytes((uint)n).CopyTo(bin, offset + 16);
        }

        return bin;
    }

    private static byte Bcd(int value) => (byte)((value / 10 << 4) | (value % 10));

    private static byte[] Command(CdRomDevice cd, byte command, params byte[] parameters)
    {
        foreach (var p in parameters)
        {
            cd.WriteRegister(2, p);
        }

        cd.WriteCommand(command);
        return TakeResponse(cd);
    }

    private static byte[] TakeResponse(CdRomDevice cd)
    {
        for (uint cycles = 1; cycles <= 40_000_000 && !cd.HasInterrupt; cycles++)
        {
            cd.Advance(1);
        }

        if (!cd.HasInterrupt)
        {
            throw new InvalidOperationException("No CD-ROM interrupt.");
        }

        var bytes = new List<byte> { (byte)(cd.GetInterruptFlag() & 7) };
        while (cd.ResponseCount > 0)
        {
            bytes.Add(cd.ReadRegister(1));
        }

        cd.SetInterruptFlag(0x07);
        return bytes.ToArray();
    }

    /// <summary>SetLoc to an absolute frame count, ReadN one sector, and return the file-sector marker in its data.</summary>
    private static uint ReadMarkerAtAbsoluteFrames(ICdSectorSource source, int absoluteFrames)
    {
        var cd = new CdRomDevice(CdRomDiscIdentity.LicensedMode2(), source);
        Command(cd, 0x01); // GetStat consumes the power-on ShellOpen flag, as the drive tests do.
        Command(cd, 0x0E, 0x00); // SetMode 0: 2048-byte user data.
        Command(cd, 0x02, Bcd(absoluteFrames / 4500), Bcd(absoluteFrames / 75 % 60), Bcd(absoluteFrames % 75))
            .Should().Equal(3, 0x02);
        Command(cd, 0x06).Should().Equal(3, 0x22);
        TakeResponse(cd).Should().Equal(1, 0x22);

        cd.WriteRegister(3, 0x80); // BFRD: load the sector into the data FIFO.
        var data = new byte[2048];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = cd.ReadData();
        }

        return BitConverter.ToUInt32(data, 0);
    }

    [Theory]
    [InlineData(150)]
    [InlineData(200)]
    [InlineData(999)]
    public void BinCueAdapter_SetLocOfFileSector_ReturnsThatFileSector(int fileSector)
    {
        var source = new BinCueCdSectorSource(Cue, MarkedBin());

        // The file sector's absolute MSF is its frame count; the device reads LBA = frames - 150 and the adapter maps
        // that LBA back to file index LBA + 150.
        ReadMarkerAtAbsoluteFrames(source, fileSector).Should().Be((uint)fileSector);
    }

    [Fact]
    public void RawSource_SetLocOfFileSector200_ReturnsFileSector50_CurrentBehaviourPinnedForOwnerReview()
    {
        // The device passes LBA 50 (= 200 - 150) to RawCdSectorSource, which reads file index 50 directly. A BIN dump's
        // file sector 200 is 00:02:50, so this is 150 sectors early. Pinned here as current behaviour; it flips
        // when the raw path follows the LBA convention. Owner decision pending.
        var source = new RawCdSectorSource(MarkedBin());

        ReadMarkerAtAbsoluteFrames(source, 200).Should().Be(50u);
    }

    [Fact]
    public void BinCueAdapter_TrackOneStartsAtLbaZero_AndLeadOutIsSectorCountMinus150()
    {
        var source = new BinCueCdSectorSource(Cue, MarkedBin());

        source.Toc.Tracks.Should().Equal(new CdTrack(1, 0, false));
        source.Toc.LeadOutLba.Should().Be(FileSectors - 150);
    }
}
