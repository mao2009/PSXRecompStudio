using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Runtime.CdRom;
using PSXRecomp.Tests.E2E;
using Xunit;

namespace PSXRecomp.Tests.RealRomAnalysis;

// Issue #743: field-level regression for the corrected synthetic ISO 9660 volume. Two builders agreeing on bytes
// cannot catch a shared omission, so every check below decodes the raw sectors itself (both halves of each pair,
// the path tables, and a walk of the directory records from the PVD root) and compares those results with each other
// and with the layout ECMA-119 requires. The corrected disc has its own pin; the legacy baseline pin in
// SyntheticDiscReproducibilityTests is kept.
[Test]
public sealed class SyntheticIsoBothEndianTests
{
    private const int RawSector = ICdSectorSource.RawSectorSize;
    private const int Payload = Iso9660Reader.SectorSize;
    private const int PvdRootRecordOffset = 156;
    private const int FileStructureVersionOffset = 881; // PVD BP 882

    // Mirrored by scripts/demo/synthetic-disc.ps1 -BothEndianIso9660. The root-only corrected disc is the checked fixture.
    // Re-pinned for the 150-sector lead-in in Mode2Form1 (Issue #10 owner ruling); the previous values were 39892b2b..., 4f75a05f..., bbae905d....
    private const string CorrectedSha256 = "78dd3d0019d99677745799480e5d89e38277f8ad4168e1441e014cfdb7c1e546";
    private const string LegacySha256 = "2f9773f517448647df7afba5616275f67fa3d25006e7ccde58b360f71e2a6043";
    private const string OpenBiosSha256 = "ce3981541466bf84f1589334b2e306b5e253ef7352b30b6e4bac101d09d41606";

    private static byte[] ExeBytes() => GeneratedPsxExeFixtures.BiosPutCharMarker.Generate();

    private static byte[] Disc() => SyntheticDiscBuilder.Bootable(ExeBytes(), iso9660BothEndian: true);

    // A hierarchy with nested directories, siblings, and a directory with two levels below it. The files are added
    // in an order that differs from the sorted path-table order (E before A, D before B), so a builder that kept
    // insertion order instead of ECMA-119 6.9.1 order fails the path-table checks.
    private static byte[] NestedDisc() => SyntheticDiscBuilder.Mode2Form1(new SyntheticIsoImageBuilder()
        .WithBothEndianNumericFields()
        .AddSystemCnf(SyntheticDiscBuilder.BootPath)
        .AddFile("E;1/H.BIN;1", Bytes(2048, 3))
        .AddFile("A;1/D;1/MID.BIN;1", Bytes(10, 2))
        .AddFile("A;1/B;1/C;1/DEEP.BIN;1", Bytes(3000, 1))
        .AddFile("ROOT.BIN;1", Bytes(5000, 4))
        .Build());

    private static byte[] Bytes(int length, byte seed)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(seed + i * 7);
        }
        return bytes;
    }

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

    private readonly record struct DirRecord(string Name, bool IsDirectory, uint Extent, uint Size, int Offset);

    // Decodes one directory sector independently of the builder: "." is 0x00, ".." is 0x01, names keep their ";1".
    private static List<DirRecord> Records(byte[] sector)
    {
        var records = new List<DirRecord>();
        for (var offset = 0; offset < sector.Length && sector[offset] != 0; offset += sector[offset])
        {
            var nameLength = sector[offset + 32];
            var name = nameLength == 1 && sector[offset + 33] <= 1
                ? (sector[offset + 33] == 0 ? "." : "..")
                : Encoding.ASCII.GetString(sector, offset + 33, nameLength);
            records.Add(new DirRecord(
                name,
                (sector[offset + 25] & 0x02) != 0,
                ReadField(sector, offset + 2, 4, bigEndian: false),
                ReadField(sector, offset + 10, 4, bigEndian: false),
                offset));
        }
        return records;
    }

    private static string StripVersion(string name)
    {
        var semicolon = name.LastIndexOf(';');
        return semicolon >= 0 ? name[..semicolon] : name;
    }

    // Walks the hierarchy from the PVD root record. Keys are normalized paths without version numbers.
    private static (Dictionary<string, uint> Directories, Dictionary<string, (uint Extent, uint Size)> Files) Walk(byte[] disc)
    {
        var directories = new Dictionary<string, uint>(StringComparer.Ordinal) { [""] = 18 };
        var files = new Dictionary<string, (uint Extent, uint Size)>(StringComparer.Ordinal);

        void Visit(string path, uint extent)
        {
            foreach (var record in Records(User(disc, (int)extent)))
            {
                if (record.Name is "." or "..")
                {
                    continue;
                }

                // Directory identifiers have no version in the corrected layout (ECMA-119 7.6.1); files keep ";1" on disc.
                var name = StripVersion(record.Name);
                var child = path.Length == 0 ? name : path + "/" + name;
                if (record.IsDirectory)
                {
                    directories[child] = record.Extent;
                    Visit(child, record.Extent);
                }
                else
                {
                    files[child] = (record.Extent, record.Size);
                }
            }
        }

        Visit("", 18);
        return (directories, files);
    }

    private readonly record struct PathRecord(uint Extent, ushort Parent, string Identifier, int Size);

    // Decodes a path table through the PVD (L @140 little-endian, M @148 big-endian), ECMA-119 9.4.
    private static List<PathRecord> DecodePathTable(byte[] disc, bool bigEndian)
    {
        var pvd = User(disc, 16);
        var tableSize = ReadField(pvd, 132, 4, bigEndian: false);
        var tableLba = (int)ReadField(pvd, bigEndian ? 148 : 140, 4, bigEndian);
        var table = User(disc, tableLba);

        var records = new List<PathRecord>();
        for (var offset = 0; offset < tableSize;)
        {
            var length = table[offset];
            var identifier = length == 1 && table[offset + 8] == 0 ? "\0" : Encoding.ASCII.GetString(table, offset + 8, length);
            var size = 8 + length + (length % 2);
            records.Add(new PathRecord(
                ReadField(table, offset + 2, 4, bigEndian),
                (ushort)ReadField(table, offset + 6, 2, bigEndian),
                identifier,
                size));
            offset += size;
        }

        records.Sum(r => r.Size).Should().Be((int)tableSize, "the PVD path table size covers exactly the encoded records");
        return records;
    }

    // Independent level of each path-table record, from its parent chain.
    private static List<int> LevelsOf(List<PathRecord> records)
    {
        var levels = new List<int>();
        foreach (var record in records)
        {
            // The root is level 1; every other record is one level below its parent.
            var level = 1;
            for (var current = record; current.Identifier != "\0"; current = records[current.Parent - 1])
            {
                level++;
            }
            levels.Add(level);
        }
        return levels;
    }

    // Each table is one sector in the corrected layout (sectors 19 and 20). Every test here reads it through the PVD.
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
        ReadField(pvd, 80, 4, bigEndian: false).Should().Be((uint)(Disc().Length / RawSector - CdSectorAddress.LeadInFrames), "volume space size equals the ISO sector count (the disc less its lead-in)");
    }

    [Fact]
    public void Pvd_FileStructureVersionIsOne_AndTheLegacyLayoutLeavesItZero()
    {
        var corrected = User(Disc(), 16);
        var legacy = User(SyntheticDiscBuilder.Bootable(ExeBytes()), 16);

        corrected[FileStructureVersionOffset].Should().Be(1, "File Structure Version (BP 882)");
        corrected[6].Should().Be(1, "Descriptor Version is a separate field");
        legacy[FileStructureVersionOffset].Should().Be(0, "the legacy volume is unchanged");
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
        ReadField(pvd, 140, 4, bigEndian: false).Should().Be(19u);
        ReadField(pvd, 148, 4, bigEndian: true).Should().Be(20u, "the Type M path table sits between the L table and the first file");
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
    public void DirectoryRecords_BothEndianLocationSizeAndVolumeSequence_AgreeForRootAndEveryChild()
    {
        var root = User(Disc(), 18);

        var records = new List<int>();
        for (var offset = 0; offset < Payload && root[offset] != 0; offset += root[offset])
        {
            records.Add(offset);
            AssertRecordBothEndian(root.AsSpan(offset, root[offset]));
            ReadField(root, offset + 28, 2, bigEndian: false).Should().Be(1u, "Volume Sequence Number (LE)");
            ReadField(root, offset + 30, 2, bigEndian: true).Should().Be(1u, "Volume Sequence Number (BE)");
        }

        records.Should().HaveCount(4, "'.', '..', SYSTEM.CNF;1 and PSXRECOMP.EXE;1");
        // "..": the root refers to itself.
        ReadField(root, records[1] + 2, 4, bigEndian: false).Should().Be(18u);
    }

    [Fact]
    public void Nested_EveryDirectoryRecord_CarriesBothEndianValuesAndVolumeSequenceOne()
    {
        var disc = NestedDisc();
        var (directories, _) = Walk(disc);
        directories.Should().HaveCount(6, "the root plus A, A/B, A/B/C, A/D and E");

        foreach (var extent in directories.Values)
        {
            var sector = User(disc, (int)extent);
            foreach (var record in Records(sector))
            {
                var offset = record.Offset;
                AssertRecordBothEndian(sector.AsSpan(offset, sector[offset]));
                ReadField(sector, offset + 28, 2, bigEndian: false).Should().Be(1u, $"VSN LE in {record.Name}");
                ReadField(sector, offset + 30, 2, bigEndian: true).Should().Be(1u, $"VSN BE in {record.Name}");
            }
        }
    }

    [Fact]
    public void Nested_DirectoryRecords_FollowTheFileIdentifierOrder_OfECMA119()
    {
        // ECMA-119 6.8.1.2 and 9.3. The expected names are written out here, not taken from the builder's comparer.
        var disc = NestedDisc();
        var root = User(disc, 18);
        var nestedA = User(disc, (int)Walk(disc).Directories["A"]);
        var nestedAB = User(disc, (int)Walk(disc).Directories["A/B"]);

        Records(root).Select(r => r.Name).Should().Equal(".", "..", "A", "E", "ROOT.BIN;1", "SYSTEM.CNF;1");
        Records(nestedA).Select(r => r.Name).Should().Equal(".", "..", "B", "D");
        Records(nestedAB).Select(r => r.Name).Should().Equal(".", "..", "C");
    }

    [Fact]
    public void Nested_PathTables_ListEveryDirectory_InLevelParentIdentifierOrder()
    {
        // ECMA-119 6.9.1: level, then parent directory number, then identifier.
        var disc = NestedDisc();
        var lTable = DecodePathTable(disc, bigEndian: false);
        var mTable = DecodePathTable(disc, bigEndian: true);

        lTable.Select(r => r.Identifier).Should().Equal("\0", "A", "E", "B", "D", "C");
        lTable.Select(r => r.Parent).Should().Equal((ushort)1, 1, 1, 2, 2, 4);
        LevelsOf(lTable).Should().Equal(1, 2, 2, 3, 3, 4);

        // The M table describes the same records with the opposite byte order.
        mTable.Should().BeEquivalentTo(lTable, options => options.ComparingByMembers<PathRecord>());
    }

    [Fact]
    public void Nested_PathTables_AgreeWithTheDirectoryHierarchy()
    {
        var disc = NestedDisc();
        var (directories, _) = Walk(disc);
        var lTable = DecodePathTable(disc, bigEndian: false);
        var mTable = DecodePathTable(disc, bigEndian: true);

        // Path-table records in order, with the path each record names in the hierarchy.
        var paths = new[] { "", "A", "E", "A/B", "A/D", "A/B/C" };
        for (var i = 0; i < paths.Length; i++)
        {
            lTable[i].Extent.Should().Be(directories[paths[i]], $"L record {i + 1} names {paths[i]}");
            mTable[i].Extent.Should().Be(directories[paths[i]], $"M record {i + 1} names {paths[i]}");
        }

        // The M table also uses the same parent numbers and identifiers as the L table.
        mTable.Select(r => r.Parent).Should().Equal(lTable.Select(r => r.Parent));
        mTable.Select(r => r.Identifier).Should().Equal(lTable.Select(r => r.Identifier));
    }

    [Fact]
    public void Nested_DirectoryAndFileExtents_DoNotOverlap_AndFollowThePathTables()
    {
        var disc = NestedDisc();
        var pvd = User(disc, 16);
        var (directories, files) = Walk(disc);

        var extents = new List<(uint Start, uint End, string Owner)>
        {
            (16, 17, "PVD"),
            (17, 18, "terminator"),
            (19, 20, "L path table"),
            (20, 21, "M path table"),
        };
        foreach (var (path, extent) in directories)
        {
            extents.Add((extent, extent + 1, "directory " + path));
        }
        foreach (var (path, file) in files)
        {
            var sectors = Math.Max(1u, (file.Size + (uint)Payload - 1) / (uint)Payload);
            extents.Add((file.Extent, file.Extent + sectors, "file " + path));
        }

        extents.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (var i = 1; i < extents.Count; i++)
        {
            extents[i].Start.Should().BeGreaterThanOrEqualTo(extents[i - 1].End,
                $"{extents[i].Owner} must not overlap {extents[i - 1].Owner}");
        }

        extents.Max(e => e.End).Should().BeLessThanOrEqualTo(ReadField(pvd, 80, 4, bigEndian: false), "every extent lies inside the volume");
        directories.Values.Where(v => v != 18).Min().Should().Be(21u, "subdirectories start after the path tables");
    }

    [Fact]
    public void Nested_ReaderResolvesEveryPathThroughTheCorrectedVolume()
    {
        var disc = NestedDisc();
        var reader = new Iso9660Reader(lba => User(disc, lba));
        reader.Initialize();

        reader.ReadFile("A/B/C/DEEP.BIN").Should().Equal(Bytes(3000, 1));
        reader.ReadFile("A/D/MID.BIN").Should().Equal(Bytes(10, 2));
        reader.ReadFile("E/H.BIN").Should().Equal(Bytes(2048, 3));
        reader.ReadFile("ROOT.BIN").Should().Equal(Bytes(5000, 4));
        SystemCnfParser.Parse(reader.ReadFile("SYSTEM.CNF")).BootPath.Should().Contain("PSXRECOMP.EXE");
    }

    [Fact]
    public void Nested_PathTableLargerThanOneSector_IsRejectedInsteadOfTruncated()
    {
        var builder = new SyntheticIsoImageBuilder().WithBothEndianNumericFields();
        for (var i = 0; i < 200; i++)
        {
            builder.AddFile($"DIRECTORY_NUMBER_{i:D4};1/F.BIN;1", [1]);
        }

        new Action(() => builder.Build()).Should().Throw<NotSupportedException>(
            "one sector per path table is the supported limit");
    }

    [Fact]
    public void CorrectedDisc_MatchesItsOwnPin_AndTheLegacyBaselinesAreUnchanged()
    {
        Sha256Hex(Disc()).Should().Be(CorrectedSha256);
        Sha256Hex(SyntheticDiscBuilder.Bootable(ExeBytes())).Should().Be(LegacySha256);
        Sha256Hex(SyntheticDiscBuilder.Bootable(SyntheticDiscBuilder.OpenBiosCompatibleExe(ExeBytes()))).Should().Be(OpenBiosSha256);
        Disc().Should().NotEqual(SyntheticDiscBuilder.Bootable(ExeBytes()), "the corrected volume is a distinct fixture");
    }

    // Issue #743 comparison fixture (the 4th): the corrected ISO 9660 volume carrying the OpenBIOS-compatible EXE.
    // Mirrored by scripts/demo/synthetic-disc.ps1 -OpenBiosCompatible -BothEndianIso9660. The three pins above are unchanged.
    // Re-pinned for the 150-sector lead-in (previous value 1a5c41f9...).
    private const string OpenBiosBothEndianSha256 = "89d11fb9a4c9262f03385965ad8d7fb15edb361e2d3b6ee1e8f4aba4da6febf7";

    private static byte[] OpenBiosBothEndianExe() => SyntheticDiscBuilder.OpenBiosCompatibleExe(ExeBytes());

    private static byte[] OpenBiosBothEndianDisc() => SyntheticDiscBuilder.Bootable(OpenBiosBothEndianExe(), iso9660BothEndian: true);

    [Fact]
    public void OpenBiosBothEndianDisc_MatchesItsOwnPin_AndIsADistinctFourthFixture()
    {
        var disc = OpenBiosBothEndianDisc();
        Sha256Hex(disc).Should().Be(OpenBiosBothEndianSha256);
        new[] { LegacySha256, OpenBiosSha256, CorrectedSha256 }.Should().NotContain(Sha256Hex(disc));
        Sha256Hex(disc).Should().Be(Sha256Hex(OpenBiosBothEndianDisc()), "generation is a pure function of the inputs");
    }

    [Fact]
    public void OpenBiosBothEndianDisc_VolumeIsCorrected_AndTheBootExeIsSectorAlignedAndEntersAtTheMarker()
    {
        var disc = OpenBiosBothEndianDisc();
        var pvd = User(disc, 16);

        ReadField(pvd, 128, 2, bigEndian: false).Should().Be(Payload, "logical block size");
        ReadField(pvd, 120, 2, bigEndian: false).Should().Be(1u, "volume set size");
        ReadField(pvd, 124, 2, bigEndian: false).Should().Be(1u, "volume sequence number");
        pvd[FileStructureVersionOffset].Should().Be(1, "File Structure Version (BP 882)");
        ReadField(pvd, 80, 4, bigEndian: false).Should().Be((uint)(disc.Length / RawSector - CdSectorAddress.LeadInFrames), "volume space size equals the ISO sector count (the disc less its lead-in)");
        ReadField(pvd, 80, 4, bigEndian: false).Should().Be(ReadField(pvd, 84, 4, bigEndian: true), "both-endian space size");

        var walk = Walk(disc);
        walk.Files.Should().ContainKey("PSXRECOMP.EXE");
        var (extent, size) = walk.Files["PSXRECOMP.EXE"];
        size.Should().Be((uint)OpenBiosBothEndianExe().Length, "the file size is the sector-aligned EXE");
        (size % (uint)Payload).Should().Be(0u, "the EXE is a whole number of 2048-byte sectors");

        var bytes = new byte[size];
        for (var i = 0; i < size / Payload; i++)
        {
            User(disc, (int)extent + i).AsSpan(0, Payload).CopyTo(bytes.AsSpan(i * Payload));
        }
        bytes.Should().Equal(OpenBiosBothEndianExe(), "the EXE bytes read back through the corrected volume");

        var exe = PsxExe.Load(bytes, "synthetic");
        exe.Header.TextSize.Should().Be(2048u, "the OpenBIOS CD driver reads whole sectors");
        exe.Header.TextSize.Should().Be((uint)(bytes.Length - 0x800));
        exe.Header.TextStart.Should().Be(SyntheticPsxExeBuilder.DefaultTextStart, "the text segment loads at the synthetic text start");
        exe.Header.EntryPoint.Should().Be(SyntheticPsxExeBuilder.DefaultTextStart, "the entry point is the synthetic text start");
    }

    // Issue #743 review: a corrected directory's records (".", "..", and children) must fit its one sector, and
    // identifiers must stay unique once the version is stripped. Names below are 12 characters, so each file record is
    // 33 + 12 = 45 bytes, padded to 46 (ECMA-119 6.1.1 / 7.1.1 even-length rule). With "." and ".." (34 bytes each) a
    // directory holds 43 such files (68 + 43 * 46 = 2046) and rejects a 44th (68 + 44 * 46 = 2092).
    private static SyntheticIsoImageBuilder WithFilesIn(SyntheticIsoImageBuilder builder, string directory, int count)
    {
        for (var i = 0; i < count; i++)
        {
            builder.AddFile($"{directory}ABCD{i:D2}.BIN;1", Bytes(1, 7));
        }
        return builder;
    }

    [Fact]
    public void RootRecordArea_ExactlyOneSector_IsAccepted_AndEveryFileKeepsItsBytes()
    {
        var disc = SyntheticDiscBuilder.Mode2Form1(WithFilesIn(new SyntheticIsoImageBuilder().WithBothEndianNumericFields(), "", 43).Build());

        AssertEveryFileIntact(disc, expectedFiles: 43);
    }

    [Fact]
    public void RootRecordArea_OneRecordTooMany_IsRejectedBeforeAnyWrite()
    {
        var builder = WithFilesIn(new SyntheticIsoImageBuilder().WithBothEndianNumericFields(), "", 44);

        new Action(() => builder.Build()).Should().Throw<NotSupportedException>()
            .WithMessage("*(root)*2092*2048*");
    }

    [Fact]
    public void SubdirectoryRecordArea_ExactlyOneSector_IsAccepted_AndEveryFileKeepsItsBytes()
    {
        var disc = SyntheticDiscBuilder.Mode2Form1(WithFilesIn(new SyntheticIsoImageBuilder().WithBothEndianNumericFields(), "D;1/", 43).Build());

        AssertEveryFileIntact(disc, expectedFiles: 43);
    }

    [Fact]
    public void SubdirectoryRecordArea_OneRecordTooMany_IsRejectedNamingTheDirectory()
    {
        var builder = WithFilesIn(new SyntheticIsoImageBuilder().WithBothEndianNumericFields(), "D;1/", 44);

        new Action(() => builder.Build()).Should().Throw<NotSupportedException>()
            .WithMessage("*D;1*2092*2048*");
    }

    [Fact]
    public void DirectoryIdentifiers_CollidingAfterVersionStripping_UnderOneParent_AreRejected()
    {
        var builder = new SyntheticIsoImageBuilder().WithBothEndianNumericFields()
            .AddFile("A;1/ONE.BIN;1", Bytes(1, 1))
            .AddFile("A;2/TWO.BIN;1", Bytes(1, 2));

        new Action(() => builder.Build()).Should().Throw<NotSupportedException>()
            .WithMessage("*A;1*A;2*");
    }

    [Fact]
    public void DirectoryIdentifiers_OneWrittenWithoutAVersion_CollideWithAVersionedOne_AreRejected()
    {
        var builder = new SyntheticIsoImageBuilder().WithBothEndianNumericFields()
            .AddFile("A/ONE.BIN;1", Bytes(1, 1))
            .AddFile("A;1/TWO.BIN;1", Bytes(1, 2));

        new Action(() => builder.Build()).Should().Throw<NotSupportedException>()
            .WithMessage("*A;1*");
    }

    [Fact]
    public void DirectoryIdentifiers_SameNameUnderDifferentParents_AreAccepted()
    {
        var disc = SyntheticDiscBuilder.Mode2Form1(new SyntheticIsoImageBuilder().WithBothEndianNumericFields()
            .AddFile("P;1/A;1/X.BIN;1", Bytes(1, 3))
            .AddFile("Q;1/A;2/Y.BIN;1", Bytes(1, 4))
            .Build());

        var walk = Walk(disc);
        walk.Files.Should().ContainKeys("P/A/X.BIN", "Q/A/Y.BIN");
    }

    [Fact]
    public void DirectoryIdentifiers_LegacyLayout_IsNotChecked_AndStillBuilds()
    {
        new Action(() => new SyntheticIsoImageBuilder()
            .AddFile("A;1/ONE.BIN;1", Bytes(1, 1))
            .AddFile("A;2/TWO.BIN;1", Bytes(1, 2))
            .Build()).Should().NotThrow("the legacy layout keeps its versioned identifiers and is unchanged");
    }

    // Review #780 (M2): LEN_DR is one byte. A 221-character identifier gives a 33 + 221 = 254 byte record (accepted);
    // a 222-character one gives 255, padded to 256 (rejected, since WriteDirectoryRecord would truncate it to 0).
    [Fact]
    public void RecordLength_254Bytes_IsAccepted_ForAFileAndForADirectory()
    {
        new Action(() => new SyntheticIsoImageBuilder().WithBothEndianNumericFields()
            .AddFile(new string('C', 219) + ";1", Bytes(1, 1))
            .AddFile(new string('E', 221) + ";1/F.BIN;1", Bytes(1, 2))
            .Build()).Should().NotThrow("both records are 254 bytes");
    }

    [Fact]
    public void RecordLength_256Bytes_IsRejected_ForARootFile()
    {
        var name = new string('B', 220) + ";1";
        new Action(() => new SyntheticIsoImageBuilder().WithBothEndianNumericFields()
            .AddFile(name, Bytes(1, 1)).Build())
            .Should().Throw<NotSupportedException>()
            .WithMessage($"*{name}*256*");
    }

    [Fact]
    public void RecordLength_256Bytes_IsRejected_ForARootDirectory()
    {
        var directory = new string('A', 222) + ";1";
        new Action(() => new SyntheticIsoImageBuilder().WithBothEndianNumericFields()
            .AddFile(directory + "/F.BIN;1", Bytes(1, 1)).Build())
            .Should().Throw<NotSupportedException>()
            .WithMessage($"*{directory}*256*");
    }

    [Fact]
    public void RecordLength_256Bytes_IsRejected_ForASubdirectoryFile()
    {
        var name = new string('D', 220) + ";1";
        new Action(() => new SyntheticIsoImageBuilder().WithBothEndianNumericFields()
            .AddFile("S;1/" + name, Bytes(1, 1)).Build())
            .Should().Throw<NotSupportedException>()
            .WithMessage($"*{name}*256*");
    }

    [Fact]
    public void RecordLength_256Bytes_IsRejected_ForASubdirectoryDirectory()
    {
        var directory = new string('G', 222) + ";1";
        new Action(() => new SyntheticIsoImageBuilder().WithBothEndianNumericFields()
            .AddFile("S;1/" + directory + "/F.BIN;1", Bytes(1, 1)).Build())
            .Should().Throw<NotSupportedException>()
            .WithMessage($"*{directory}*256*");
    }

    // Review #780 (M2): the same file path added twice wrote two records with one identifier, and the sector map kept
    // only the second extent, so the first content was lost without an error.
    [Fact]
    public void FileIdentifiers_DuplicatePath_UnderOneParent_IsRejected()
    {
        var builder = new SyntheticIsoImageBuilder().WithBothEndianNumericFields()
            .AddFile("X.BIN;1", Bytes(1, 1))
            .AddFile("X.BIN;1", Bytes(1, 2));

        new Action(() => builder.Build()).Should().Throw<NotSupportedException>()
            .WithMessage("*X.BIN;1*");
    }

    [Fact]
    public void FileIdentifiers_DuplicatePath_InASubdirectory_IsRejected()
    {
        var builder = new SyntheticIsoImageBuilder().WithBothEndianNumericFields()
            .AddFile("S;1/X.BIN;1", Bytes(1, 1))
            .AddFile("S;1/X.BIN;1", Bytes(1, 2));

        new Action(() => builder.Build()).Should().Throw<NotSupportedException>()
            .WithMessage("*S;1/X.BIN;1*");
    }

    [Fact]
    public void FileIdentifiers_DifferentNamesUnderOneParent_AreAccepted()
    {
        var disc = SyntheticDiscBuilder.Mode2Form1(new SyntheticIsoImageBuilder().WithBothEndianNumericFields()
            .AddFile("X.BIN;1", Bytes(1, 1))
            .AddFile("X.BIN;2", Bytes(1, 2))
            .Build());

        // Walk strips versions, so check the raw root records: both identifiers must be present, each with its own extent.
        var records = Records(User(disc, 18));
        records.Select(r => r.Name).Should().Contain(new[] { "X.BIN;1", "X.BIN;2" });
        var first = records.Single(r => r.Name == "X.BIN;1");
        var second = records.Single(r => r.Name == "X.BIN;2");
        first.Extent.Should().NotBe(second.Extent);
    }

    [Fact]
    public void FileIdentifiers_SameNameUnderDifferentParents_AreAccepted_WithTheirOwnContent()
    {
        var disc = SyntheticDiscBuilder.Mode2Form1(new SyntheticIsoImageBuilder().WithBothEndianNumericFields()
            .AddFile("P;1/X.BIN;1", Bytes(1, 1))
            .AddFile("Q;1/X.BIN;1", Bytes(1, 2))
            .Build());

        var walk = Walk(disc);
        User(disc, (int)walk.Files["P/X.BIN"].Extent)[0].Should().Be(1);
        User(disc, (int)walk.Files["Q/X.BIN"].Extent)[0].Should().Be(2);
    }

    // A directory and a file in one directory share the normalised name "A", so the reader could not tell them apart.
    [Fact]
    public void FileAndDirectoryIdentifiers_CollidingAfterNormalisation_AreRejected()
    {
        var builder = new SyntheticIsoImageBuilder().WithBothEndianNumericFields()
            .AddFile("A;1/ONE.BIN;1", Bytes(1, 1))
            .AddFile("A;1", Bytes(1, 2));

        new Action(() => builder.Build()).Should().Throw<NotSupportedException>()
            .WithMessage("*A;1*");
    }

    private static void AssertEveryFileIntact(byte[] disc, int expectedFiles)
    {
        var walk = Walk(disc);
        var files = walk.Files.Where(f => f.Key.Contains("ABCD")).ToList();
        files.Should().HaveCount(expectedFiles);
        foreach (var (_, (extent, size)) in files)
        {
            size.Should().Be(1u);
            User(disc, (int)extent)[0].Should().Be(7, "the file's sector was not overwritten by a directory record");
        }
    }
}
