using System.Buffers.Binary;
using System.Security.Cryptography;
using FluentAssertions;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Runtime.CdRom;
using PSXRecomp.Tests.E2E;
using Xunit;

namespace PSXRecomp.Tests.RealRomAnalysis;

// Issue #743: field-level regression for the corrected synthetic ISO 9660 volume. Two builders agreeing on bytes
// cannot catch a shared omission, so each mandatory both-endian pair is decoded on its own and must agree with its
// partner. The corrected disc has its own pin; the legacy baseline pin in SyntheticDiscReproducibilityTests is kept.
[Test]
public sealed class SyntheticIsoBothEndianTests
{
    private const int RawSector = ICdSectorSource.RawSectorSize;
    private const int Payload = Iso9660Reader.SectorSize;
    private const int PvdRootRecordOffset = 156;

    // Mirrored by scripts/demo/synthetic-disc.ps1 -BothEndianIso9660.
    private const string CorrectedSha256 = "0f6f743f47e7c87b8737f7427354dc582d6c9725d1396d1b3cddfc5b7c061941";
    private const string LegacySha256 = "4f75a05f4f031a0a4b5f650172d325c0b8f60abf3f55b779f19334324152ab27";

    private static byte[] ExeBytes() => GeneratedPsxExeFixtures.BiosPutCharMarker.Generate();

    private static byte[] Disc() => SyntheticDiscBuilder.Bootable(ExeBytes(), iso9660BothEndian: true);

    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static byte[] User(byte[] disc, int lba)
    {
        var raw = new byte[RawSector];
        new RawCdSectorSource(disc).TryReadSector(lba, raw).Should().BeTrue($"the disc holds sector {lba}");
        return raw[24..(24 + Payload)];
    }

    private static uint ReadField(byte[] buffer, int offset, int width, bool bigEndian) => (width, bigEndian) switch
    {
        (2, false) => BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset)),
        (2, true) => BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset)),
        (4, false) => BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset)),
        _ => BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset)),
    };

    private static void AssertRecordBothEndian(ReadOnlySpan<byte> record)
    {
        BinaryPrimitives.ReadUInt32BigEndian(record[6..]).Should().Be(BinaryPrimitives.ReadUInt32LittleEndian(record[2..]), "extent location");
        BinaryPrimitives.ReadUInt32BigEndian(record[14..]).Should().Be(BinaryPrimitives.ReadUInt32LittleEndian(record[10..]), "data length");
    }

    // (little-endian offset, big-endian offset, width) for the PVD numeric pairs ECMA-119 requires in both orders.
    [Theory]
    [InlineData(80, 84, 4)]   // volume space size
    [InlineData(120, 122, 2)] // volume set size
    [InlineData(124, 126, 2)] // volume sequence number
    [InlineData(128, 130, 2)] // logical block size
    [InlineData(132, 136, 4)] // path table size
    public void Pvd_BothEndianPair_DecodesToTheSameNonZeroValue(int littleOffset, int bigOffset, int width)
    {
        var pvd = User(Disc(), 16);

        var little = ReadField(pvd, littleOffset, width, bigEndian: false);
        var big = ReadField(pvd, bigOffset, width, bigEndian: true);

        little.Should().NotBe(0u, "the field is mandatory");
        big.Should().Be(little);
    }

    [Fact]
    public void Pvd_BlockSizeVolumeSetAndSpaceSize_HaveTheExpectedValues()
    {
        var pvd = User(Disc(), 16);

        ReadField(pvd, 128, 2, bigEndian: false).Should().Be(Payload, "logical block size");
        ReadField(pvd, 120, 2, bigEndian: false).Should().Be(1u, "volume set size");
        ReadField(pvd, 124, 2, bigEndian: false).Should().Be(1u, "volume sequence number");
        ReadField(pvd, 80, 4, bigEndian: false).Should().Be((uint)(Disc().Length / RawSector), "volume space size equals the sector count");
    }

    [Fact]
    public void Pvd_RootRecordAndPathTableLocations_AgreeAcrossBothHalves()
    {
        var pvd = User(Disc(), 16);
        var root = pvd.AsSpan(PvdRootRecordOffset, 34);

        AssertRecordBothEndian(root);
        BinaryPrimitives.ReadUInt32LittleEndian(root[2..]).Should().Be(18u, "root directory extent");
        BinaryPrimitives.ReadUInt32LittleEndian(root[10..]).Should().Be((uint)Payload, "root directory size");

        // The L table (LE, @140) comes before the Type M table (BE, @148). Both are mandatory PVD locations.
        var lLocation = ReadField(pvd, 140, 4, bigEndian: false);
        var mLocation = ReadField(pvd, 148, 4, bigEndian: true);
        lLocation.Should().Be(19u);
        mLocation.Should().Be(20u, "the Type M path table sits between the L table and the first file");
    }

    [Fact]
    public void PathTables_RootEntry_AgreesWithPvdAndWithEachOther()
    {
        var disc = Disc();
        var pvd = User(disc, 16);
        var rootLba = ReadField(pvd, PvdRootRecordOffset + 2, 4, bigEndian: false);

        var lTable = User(disc, (int)ReadField(pvd, 140, 4, bigEndian: false));
        var mTable = User(disc, (int)ReadField(pvd, 148, 4, bigEndian: true));

        lTable[0].Should().Be(1, "root entry name length");
        mTable[0].Should().Be(1, "root entry name length");
        ReadField(lTable, 2, 4, bigEndian: false).Should().Be(rootLba, "L table points at the root directory");
        ReadField(mTable, 2, 4, bigEndian: true).Should().Be(rootLba, "M table points at the root directory");
        ReadField(lTable, 6, 2, bigEndian: false).Should().Be(1u, "the root is its own parent (L)");
        ReadField(mTable, 6, 2, bigEndian: true).Should().Be(1u, "the root is its own parent (M)");

        // The path-table size is the 8-byte header + 1-byte name + 1 pad byte, in both halves.
        ReadField(pvd, 132, 4, bigEndian: false).Should().Be(10u);
        ReadField(pvd, 136, 4, bigEndian: true).Should().Be(10u);
    }

    [Fact]
    public void DirectoryRecords_BothEndianLocationAndSize_AgreeForRootAndEveryChild()
    {
        var root = User(Disc(), 18);

        var records = new List<int>();
        for (var offset = 0; offset < Payload && root[offset] != 0; offset += root[offset])
        {
            records.Add(offset);
            AssertRecordBothEndian(root.AsSpan(offset, root[offset]));
        }

        records.Should().HaveCount(4, "'.', '..', SYSTEM.CNF;1 and PSXRECOMP.EXE;1");
        // "..": the root refers to itself.
        ReadField(root, records[1] + 2, 4, bigEndian: false).Should().Be(18u);
    }

    [Fact]
    public void Reader_StillResolvesSystemCnfAndTheBootExe_OnTheCorrectedVolume()
    {
        var disc = Disc();
        var reader = new Iso9660Reader(lba => User(disc, lba));
        reader.Initialize();

        SystemCnfParser.Parse(reader.ReadFile("SYSTEM.CNF")).BootPath.Should().Contain("PSXRECOMP.EXE");
        reader.ReadFile("PSXRECOMP.EXE").Should().Equal(ExeBytes(), "the boot EXE round-trips byte for byte");
    }

    [Fact]
    public void CorrectedDisc_MatchesItsOwnPin_AndTheLegacyBaselineIsUnchanged()
    {
        Sha256Hex(Disc()).Should().Be(CorrectedSha256);
        Sha256Hex(SyntheticDiscBuilder.Bootable(ExeBytes())).Should().Be(LegacySha256);
        Disc().Should().NotEqual(SyntheticDiscBuilder.Bootable(ExeBytes()), "the corrected volume is a distinct fixture");
    }
}
