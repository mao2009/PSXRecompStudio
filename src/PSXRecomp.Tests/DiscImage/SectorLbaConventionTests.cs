using FluentAssertions;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Runtime.CdRom;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.DiscImageTests;

/// <summary>
/// Issue #10 owner ruling: the 150-sector offset, end to end. Synthetic fixtures only. Each sector carries a marker
/// (its ISO sector number or file index in its user data), so a read proves which physical sector was returned.
/// Conventions: LBA 0 = MSF 00:02:00; a file-origin source (raw image, BIN/CUE) maps LBA L to file index L + 150; a
/// CHD stores the disc from LBA 0 (frame = LBA); the device converts SetLoc MSF to LBA once (minus 150).
/// </summary>
[Test]
public sealed class SectorLbaConventionTests
{
    private const string Cue = "FILE \"a.bin\" BINARY\nTRACK 01 MODE2/2352\nINDEX 01 00:02:00\n";
    private const int RawUserDataOffsetMode2 = 24;
    private const int RawUserDataOffsetMode1 = 16;
    private const int IsoSectorSize = 2048;

    /// <summary>A BINARY file where file sector N carries N in its user data (Mode 1 layout, bytes 16..19).</summary>
    private static byte[] MarkedFileImage(int fileSectors)
    {
        var bin = new byte[fileSectors * ICdSectorSource.RawSectorSize];
        for (var n = 0; n < fileSectors; n++)
        {
            var offset = n * ICdSectorSource.RawSectorSize;
            bin[offset + 15] = 1; // Mode 1: user data begins at byte 16 of the raw sector
            BitConverter.GetBytes((uint)n).CopyTo(bin, offset + RawUserDataOffsetMode1);
        }

        return bin;
    }

    /// <summary>An ISO-like image of 2048-byte sectors where ISO sector N carries N in its first four bytes.</summary>
    private static byte[] MarkedIso(int sectors)
    {
        var iso = new byte[sectors * IsoSectorSize];
        for (var n = 0; n < sectors; n++)
        {
            BitConverter.GetBytes((uint)n).CopyTo(iso, n * IsoSectorSize);
        }

        return iso;
    }

    private static uint Marker(ReadOnlySpan<byte> userData) => BitConverter.ToUInt32(userData[..4]);

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

    /// <summary>SetLoc to an absolute MSF frame count, ReadN one sector, and return the marker in its 2048-byte payload.</summary>
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
        var data = new byte[IsoSectorSize];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = cd.ReadData();
        }

        return Marker(data);
    }

    [Theory]
    [InlineData(150)]
    [InlineData(200)]
    [InlineData(999)]
    public void BinCueAdapter_SetLocOfFileSector_ReturnsThatFileSector(int fileSector)
    {
        var source = new BinCueCdSectorSource(Cue, MarkedFileImage(1000));

        // The file sector's absolute MSF is its frame count; the device reads LBA = frames - 150 and the adapter maps
        // that LBA back to file index LBA + 150.
        ReadMarkerAtAbsoluteFrames(source, fileSector).Should().Be((uint)fileSector);
    }

    [Fact]
    public void RawSource_SetLocOfFileSector200_ReturnsFileSector200()
    {
        // The device passes LBA 50 (= 200 - 150); RawCdSectorSource maps LBA 50 to file index 200. Fixed in Issue #10 (was 50).
        var source = new RawCdSectorSource(MarkedFileImage(1000));

        ReadMarkerAtAbsoluteFrames(source, 200).Should().Be(200u);
    }

    [Fact]
    public void Device_SetLocOfTheLeadIn_ReadsLbaZeroExactlyOnce()
    {
        // MSF 00:02:00 is LBA 0 = ISO sector 0. A double offset would read the lead-in or land past the data.
        var source = new RawCdSectorSource(SyntheticDiscBuilder.Mode2Form1(MarkedIso(10)));

        ReadMarkerAtAbsoluteFrames(source, 150).Should().Be(0u);
        ReadMarkerAtAbsoluteFrames(source, 151).Should().Be(1u);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(149)]
    [InlineData(150)]
    [InlineData(200)]
    [InlineData(299)]
    public void RawSource_Boundaries_ReturnTheIsoSectorOfTheSameLba(int lba)
    {
        var source = new RawCdSectorSource(SyntheticDiscBuilder.Mode2Form1(MarkedIso(300)));
        var raw = new byte[ICdSectorSource.RawSectorSize];

        source.TryReadSector(lba, raw).Should().BeTrue();
        Marker(raw.AsSpan(RawUserDataOffsetMode2)).Should().Be((uint)lba);
    }

    [Fact]
    public void RawSource_LeadOutIsIsoSectorCount_AndOutOfRangeIsAbsent()
    {
        var source = new RawCdSectorSource(SyntheticDiscBuilder.Mode2Form1(MarkedIso(300)));
        var raw = new byte[ICdSectorSource.RawSectorSize];

        source.Toc.LeadOutLba.Should().Be(300);
        source.TryReadSector(300, raw).Should().BeFalse();
        source.TryReadSector(-1, raw).Should().BeFalse();
    }

    /// <summary>The expected 2048 user-data bytes of LBA <paramref name="lba"/>, computed independently of any builder.</summary>
    private static byte[] ExpectedUserData(int lba)
    {
        var data = new byte[IsoSectorSize];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((lba * 31 + i * 7 + 3) & 0xFF);
        }

        return data;
    }

    private static byte[] PatternIso(int sectors)
    {
        var iso = new byte[sectors * IsoSectorSize];
        for (var n = 0; n < sectors; n++)
        {
            ExpectedUserData(n).CopyTo(iso, n * IsoSectorSize);
        }

        return iso;
    }

    [Fact]
    public void RawBinCueAndChd_EachReturnTheExpectedUserDataForTheSameLba()
    {
        // Each path is checked against ExpectedUserData(lba), not against the others, so no path is the reference.
        // The raw and BIN/CUE paths test the lead-in mapping (LBA L = file index L + 150). The CHD path assumes frame = LBA
        // (its remarks: UNVERIFIED for production CHDs). This test pins that assumption; it does not verify it.
        var iso = PatternIso(300);
        var rawImage = SyntheticDiscBuilder.Mode2Form1(iso);
        var raw = new RawCdSectorSource(rawImage);
        var bin = new BinCueCdSectorSource(Cue, rawImage);
        using var chd = ChdReader.Open(new MemoryStream(SyntheticChdBuilder.WrapInChd(iso)));
        var cdChd = new ChdCdSectorSource(chd);

        raw.Toc.LeadOutLba.Should().Be(300);
        cdChd.Toc.LeadOutLba.Should().Be(300);
        bin.Toc.LeadOutLba.Should().Be(300);

        foreach (var lba in new[] { 0, 1, 149, 150, 200, 299 })
        {
            var rawSector = new byte[ICdSectorSource.RawSectorSize];
            var chdSector = new byte[ICdSectorSource.RawSectorSize];
            var binSector = new byte[ICdSectorSource.RawSectorSize];
            raw.TryReadSector(lba, rawSector).Should().BeTrue();
            cdChd.TryReadSector(lba, chdSector).Should().BeTrue();
            bin.TryReadSector(lba, binSector).Should().BeTrue();

            rawSector.AsSpan(RawUserDataOffsetMode2, IsoSectorSize).ToArray().Should().Equal(ExpectedUserData(lba), $"raw at LBA {lba}");
            binSector.AsSpan(RawUserDataOffsetMode2, IsoSectorSize).ToArray().Should().Equal(ExpectedUserData(lba), $"BIN/CUE at LBA {lba}");
            chdSector.AsSpan(RawUserDataOffsetMode1, IsoSectorSize).ToArray().Should().Equal(ExpectedUserData(lba), $"CHD at LBA {lba}");
        }
    }

    /// <summary>The pre-Issue #10 Mode2Form1 layout: no lead-in, ISO sector n at file index n, header MSF n + 150.</summary>
    private static byte[] LegacyMode2Form1NoLeadIn(byte[] iso)
    {
        var sectors = iso.Length / IsoSectorSize;
        var image = new byte[sectors * ICdSectorSource.RawSectorSize];
        for (var lba = 0; lba < sectors; lba++)
        {
            var sector = image.AsSpan(lba * ICdSectorSource.RawSectorSize, ICdSectorSource.RawSectorSize);
            sector.Slice(1, 10).Fill(0xFF);
            var absolute = lba + 150;
            sector[12] = Bcd(absolute / 4500);
            sector[13] = Bcd(absolute / 75 % 60);
            sector[14] = Bcd(absolute % 75);
            sector[15] = 2;
            sector[18] = sector[22] = 0x08;
            iso.AsSpan(lba * IsoSectorSize, IsoSectorSize).CopyTo(sector[24..]);
        }

        return image;
    }

    [Fact]
    public void Mode2Form1_LeadIn_LeavesEveryDataSectorByteIdenticalToTheLegacyLayout()
    {
        // The lead-in adds sectors in front and does not change the data sectors: sector n of the legacy layout (header MSF
        // n + 150, data at file index n) equals the whole 2352-byte sector at file index n + 150 of the current layout.
        var iso = PatternIso(300);
        var legacy = LegacyMode2Form1NoLeadIn(iso);
        var current = SyntheticDiscBuilder.Mode2Form1(iso);

        current.Length.Should().Be(legacy.Length + 150 * ICdSectorSource.RawSectorSize);
        for (var n = 0; n < 300; n++)
        {
            current.AsSpan((n + 150) * ICdSectorSource.RawSectorSize, ICdSectorSource.RawSectorSize).ToArray()
                .Should().Equal(legacy.AsSpan(n * ICdSectorSource.RawSectorSize, ICdSectorSource.RawSectorSize).ToArray(),
                    $"data sector {n}");
        }
    }

    [Fact]
    public void Mode2Form1_LeadInSectors_HaveTheirOwnFileIndexAsHeaderMsfAndZeroUserData()
    {
        var current = SyntheticDiscBuilder.Mode2Form1(PatternIso(10));

        // File index 0 is 00:00:00 and index 149 is 00:01:74: the header MSF is the file index, as for every sector.
        current[12..15].Should().Equal(0x00, 0x00, 0x00);
        var last = 149 * ICdSectorSource.RawSectorSize;
        current[(last + 12)..(last + 15)].ToArray().Should().Equal(0x00, 0x01, 0x74);
        for (var fileIndex = 0; fileIndex < 150; fileIndex++)
        {
            current.AsSpan(fileIndex * ICdSectorSource.RawSectorSize + RawUserDataOffsetMode2, IsoSectorSize).ToArray()
                .Should().OnlyContain(b => b == 0, $"lead-in sector {fileIndex} carries no user data");
        }
    }

    [Fact]
    public void BinCueAdapter_TrackOneStartsAtLbaZero_AndLeadOutIsSectorCountMinus150()
    {
        var source = new BinCueCdSectorSource(Cue, MarkedFileImage(1000));

        source.Toc.Tracks.Should().Equal(new CdTrack(1, 0, false));
        source.Toc.LeadOutLba.Should().Be(1000 - 150);
    }
}
