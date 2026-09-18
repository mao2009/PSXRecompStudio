using System.Text;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.DiscImageTests;

/// <summary>
/// Regression tests for Issue #394: a length or size field read out of untrusted
/// disc-image content must be bounded before it sizes a heap allocation.
///
/// Every case here feeds a deliberately hostile size field and asserts that the
/// reader fails closed with a classified error. A test that merely "did not crash"
/// would prove nothing, so each one pins the failure to a specific exception type
/// or, where the pipeline is involved, to a specific classified
/// <see cref="RomAnalysisOutcome"/> stage — the contract
/// <see cref="RomAnalysisPipeline"/> already guarantees for every other
/// malformed-input case. None of them may be satisfied by catching
/// <see cref="OutOfMemoryException"/>: the oversized allocation must never be
/// attempted in the first place.
/// </summary>
[Test]
public class DiscImageHostileSizeTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string BootValue = @"cdrom:\SLPS_TEST.01;1";
    private const string ExeIsoName = "SLPS_TEST.01;1";

    private const int SectorSize = Iso9660Reader.SectorSize;
    private const uint CdFrameSize = 2448;
    private const uint ValidUnitBytes = CdFrameSize;
    private const uint ValidHunkBytes = CdFrameSize * 8; // chdman's 8 frames per hunk
    private const int MapOffset = ChdHeader.V5HeaderSize;
    private const uint Cdlz = 0x63646C7A; // "cdlz"

    /// <summary>Mirrors ChdReader's compressed-map ceiling for a one-hunk image.</summary>
    private const int OneHunkMapLimit = ChdHeader.MapEntrySize + 64;

    // ------------------------------------------------------------------ CHD header

    [Fact]
    public void Open_LogicalBytesAboveTheImageLimit_ThrowsBeforeAllocating()
    {
        // 4 GiB of declared content in a 124-byte file.
        var image = BuildChd(logicalBytes: 4UL * 1024 * 1024 * 1024);

        OpenChd(image).Should().Throw<InvalidDataException>()
            .WithMessage("*logicalBytes*");
    }

    [Fact]
    public void Open_HunkHoldingMoreFramesThanTheLimit_ThrowsBeforeAllocating()
    {
        // One hunk claiming 8192 CD frames would decompress into a ~20 MB buffer.
        var image = BuildChd(logicalBytes: ValidHunkBytes, hunkBytes: CdFrameSize * 8192);

        OpenChd(image).Should().Throw<InvalidDataException>()
            .WithMessage("*frames per hunk*");
    }

    [Fact]
    public void Open_HunkCountAboveTheLimit_ThrowsBeforeAllocating()
    {
        // A sub-frame hunk size turns a 2 GiB logical image into ~4.2 million map
        // entries. This is the path that used to reach `new byte[hunkCount * 12]`.
        var image = BuildChd(logicalBytes: 2UL * 1024 * 1024 * 1024, hunkBytes: 512);

        OpenChd(image).Should().Throw<InvalidDataException>()
            .WithMessage("*hunks*");
    }

    [Fact]
    public void TotalHunks_NarrowingOutOfIntRange_ThrowsInsteadOfWrapping()
    {
        var header = BuildHeaderRecord(logicalBytes: ulong.MaxValue, hunkBytes: 1);

        header.Invoking(h => h.TotalHunks).Should().Throw<OverflowException>();
    }

    [Fact]
    public void TotalHunks_InRange_RoundsUpWithoutOverflowingTheAddition()
    {
        // The old `LogicalBytes + HunkBytes - 1` rounding wraps to 0 here; the count
        // must still be the real one.
        var header = BuildHeaderRecord(logicalBytes: ValidHunkBytes * 3 + 1, hunkBytes: ValidHunkBytes);

        header.TotalHunks.Should().Be(4);
    }

    // --------------------------------------------------------------------- CHD map

    [Fact]
    public void Open_CompressedMapBytesPastEndOfFile_ThrowsBeforeAllocating()
    {
        // Within the resource ceiling for one hunk, so this pins the file-length check
        // specifically: 64 bytes of map declared in an image that ends at the map header.
        var image = BuildChd(
            logicalBytes: ValidHunkBytes,
            compressor0: Cdlz,
            trailing: BuildCompressedMapHeader(mapBytes: 64));

        OpenChd(image).Should().Throw<InvalidDataException>()
            .WithMessage("*compressed map data*");
    }

    [Fact]
    public void Open_CompressedMapBytesAtUIntMaxValue_IsRejectedByTheResourceBound()
    {
        // 4 GiB of declared map in a 140-byte image. Both checks would reject it, so
        // the message pins the order: the resource ceiling runs ahead of SeekChecked's
        // file-length check and therefore ahead of `new byte[mapBytes]`.
        var image = BuildChd(
            logicalBytes: ValidHunkBytes,
            compressor0: Cdlz,
            trailing: BuildCompressedMapHeader(mapBytes: uint.MaxValue));

        OpenChd(image).Should().Throw<InvalidDataException>()
            .Which.Message.Should().Contain("compressed map bytes").And.NotContain("compressed map data");
    }

    [Fact]
    public void Open_CompressedMapBytesAboveTheRawMapLimit_ThrowsBeforeAllocating()
    {
        // One byte over the ceiling, and the image really does contain those bytes, so
        // SeekChecked would accept it. Only the resource bound rejects it.
        var image = BuildCompressedMapChd(
            compressedLength: ValidHunkBytes, mapBytesTarget: OneHunkMapLimit + 1);

        OpenChd(image).Should().Throw<InvalidDataException>()
            .WithMessage("*compressed map bytes*");
    }

    [Fact]
    public void Open_CompressedMapBytesAtTheRawMapLimit_StillParsesTheMap()
    {
        // Guards the ceiling against over-rejection at its exact value. The decoder
        // stops after the single entry it needs, so the trailing padding is inert.
        var image = BuildCompressedMapChd(
            compressedLength: ValidHunkBytes, mapBytesTarget: OneHunkMapLimit);

        using var stream = new MemoryStream(image);
        using var reader = ChdReader.Open(stream);

        reader.ReadSector(0)[0].Should().Be(0xA5);
    }

    [Fact]
    public void Open_UncompressedMapPastEndOfFile_ThrowsBeforeAllocating()
    {
        // TotalHunks says 4096 map entries (16 KB) but the file stops after the header.
        var image = BuildChd(logicalBytes: ValidHunkBytes * 4096);

        OpenChd(image).Should().Throw<InvalidDataException>()
            .WithMessage("*uncompressed hunk map*");
    }

    // -------------------------------------------------------------------- CHD hunk

    [Fact]
    public void ReadSector_HunkDataExtendingPastEndOfFile_ThrowsBeforeAllocating()
    {
        // The map entry resolves to a hunk whose data starts inside the file but runs
        // past its end. This is the bound every per-hunk CompressedLength passes
        // through before it sizes a buffer.
        var image = BuildUncompressedChd(hunkFileOffsetUnits: 1, totalLength: (int)ValidHunkBytes + 16);

        using var stream = new MemoryStream(image);
        using var reader = ChdReader.Open(stream);

        reader.Invoking(r => r.ReadSector(0)).Should().Throw<InvalidDataException>()
            .WithMessage("*hunk data*");
    }

    [Fact]
    public void ReadSector_HunkDataInsideTheFile_StillReadsTheSector()
    {
        // Guards the bound above against over-rejection: a hunk that genuinely fits
        // must still be readable.
        var image = BuildUncompressedChd(hunkFileOffsetUnits: 1, totalLength: (int)ValidHunkBytes * 2);
        image[ValidHunkBytes] = 0xA5;
        image[ValidHunkBytes + 1] = 0x5A;

        using var stream = new MemoryStream(image);
        using var reader = ChdReader.Open(stream);

        var sector = reader.ReadSector(0);

        sector.Should().HaveCount(ChdCdCodec.CdSectorDataSize);
        sector[0].Should().Be(0xA5);
        sector[1].Should().Be(0x5A);
    }

    [Fact]
    public void ReadSector_CompressedLengthAtTheHunkLimit_StillReadsTheSector()
    {
        // Guards the compressed-length ceiling against over-rejection at its exact
        // value: a hunk stored raw is always exactly HunkBytes long.
        var image = BuildCompressedMapChd(compressedLength: ValidHunkBytes);

        using var stream = new MemoryStream(image);
        using var reader = ChdReader.Open(stream);

        var sector = reader.ReadSector(0);

        sector.Should().HaveCount(ChdCdCodec.CdSectorDataSize);
        sector[0].Should().Be(0xA5);
        sector[1].Should().Be(0x5A);
    }

    [Fact]
    public void ReadSector_CompressedLengthAboveTheHunkLimit_ThrowsBeforeAllocating()
    {
        // One byte over, and the image really does contain that byte, so SeekChecked
        // would accept it. Only the resource bound rejects it.
        var image = BuildCompressedMapChd(compressedLength: ValidHunkBytes + 1);

        using var stream = new MemoryStream(image);
        using var reader = ChdReader.Open(stream);

        reader.Invoking(r => r.ReadSector(0)).Should().Throw<InvalidDataException>()
            .WithMessage("*compressed length*");
    }

    [Theory]
    // Near int range, where `new byte[length]` would still be a legal 2 GiB request.
    [InlineData((uint)int.MaxValue)]
    // The largest value the map's 32-bit length field can encode.
    [InlineData(uint.MaxValue)]
    public void ReadSector_CompressedLengthFarAboveTheHunkLimit_IsRejectedByTheResourceBound(
        uint hostileLength)
    {
        // These also run past the end of the file, so the message pins the order: the
        // ceiling runs ahead of SeekChecked ("hunk data") and therefore ahead of the
        // `new byte[length]` allocation inside ReadBytesAt.
        var image = BuildCompressedMapChd(compressedLength: hostileLength);

        using var stream = new MemoryStream(image);
        using var reader = ChdReader.Open(stream);

        reader.Invoking(r => r.ReadSector(0)).Should().Throw<InvalidDataException>()
            .Which.Message.Should().Contain("compressed length").And.NotContain("hunk data");
    }

    [Fact]
    public void RunFromChd_HostileHunkLength_IsClassifiedInsteadOfExhaustingMemory()
    {
        // End to end: a hostile per-hunk length must reach the pipeline's classified
        // failure contract, never an OutOfMemoryException.
        var outcome = RomAnalysisPipeline.RunFromChd(
            BuildCompressedMapChd(compressedLength: uint.MaxValue), Sha);

        outcome.Status.Should().Be(RomAnalysisStatus.Fail);
    }

    [Fact]
    public void RunFromChd_HostileHeader_IsClassifiedAtChdOpen()
    {
        var outcome = RomAnalysisPipeline.RunFromChd(
            BuildChd(logicalBytes: 4UL * 1024 * 1024 * 1024), Sha);

        outcome.Status.Should().Be(RomAnalysisStatus.Fail);
        outcome.FailedStage.Should().Be(RomAnalysisStage.ChdOpen);
        outcome.FailureKind.Should().Be("ChdOpenFailure");
    }

    // ------------------------------------------------------------------------- ISO

    [Theory]
    // Rounded up to whole sectors this is exactly 4 GiB — past int range.
    [InlineData(uint.MaxValue)]
    // 1,200,000 sectors * 2048: the old unchecked int multiplication wrapped negative.
    [InlineData(1_200_000u * SectorSize)]
    public void RunFromIsoImage_BootExecutableSizeOverflowsIntArithmetic_IsClassified(uint hostileSize)
    {
        var image = BuildIsoDisc();
        PatchRootDirectoryEntrySize(image, ExeIsoName, hostileSize);

        var outcome = RomAnalysisPipeline.RunFromIsoImage(image, Sha);

        outcome.Status.Should().Be(RomAnalysisStatus.Fail);
        outcome.FailedStage.Should().Be(RomAnalysisStage.BootExecutable);
        outcome.FailureKind.Should().Be("BootExecutableUnreadable");
        outcome.FailureReason.Should().Contain("not addressable");
    }

    [Fact]
    public void RunFromIsoImage_BootExecutableSizePastEndOfImage_IsClassified()
    {
        // In int range, so no overflow — but ~10 MB of extent in a ~50 KB image.
        var image = BuildIsoDisc();
        PatchRootDirectoryEntrySize(image, ExeIsoName, 10 * 1024 * 1024);

        var outcome = RomAnalysisPipeline.RunFromIsoImage(image, Sha);

        outcome.Status.Should().Be(RomAnalysisStatus.Fail);
        outcome.FailedStage.Should().Be(RomAnalysisStage.BootExecutable);
        outcome.FailureKind.Should().Be("BootExecutableUnreadable");
    }

    [Fact]
    public void RunFromIsoImage_RootDirectorySizeOverflowsIntArithmetic_IsClassified()
    {
        var image = BuildIsoDisc();
        PatchPrimaryVolumeDescriptorRootSize(image, uint.MaxValue);

        var outcome = RomAnalysisPipeline.RunFromIsoImage(image, Sha);

        outcome.Status.Should().Be(RomAnalysisStatus.Fail);
        outcome.FailureReason.Should().Contain("not addressable");
    }

    [Fact]
    public void RunFromIsoImage_UnmodifiedDisc_StillReachesTheReportStage()
    {
        // The ISO bound must not reject a well-formed extent.
        var outcome = RomAnalysisPipeline.RunFromIsoImage(BuildIsoDisc(), Sha, instructionCount: 16);

        outcome.Status.Should().Be(RomAnalysisStatus.Pass);
        outcome.LastSuccessfulStage.Should().Be(RomAnalysisStage.Report);
    }

    // ------------------------------------------------------------------- CHD input

    private static Action OpenChd(byte[] image) => () =>
    {
        using var stream = new MemoryStream(image);
        using var reader = ChdReader.Open(stream);
    };

    /// <summary>
    /// Builds a CHD V5 header, optionally followed by <paramref name="trailing"/> bytes
    /// (the map region). Only the fields this reader parses are written.
    /// </summary>
    private static byte[] BuildChd(
        ulong logicalBytes,
        uint hunkBytes = ValidHunkBytes,
        uint unitBytes = ValidUnitBytes,
        uint compressor0 = 0,
        byte[]? trailing = null)
    {
        var image = new byte[ChdHeader.V5HeaderSize + (trailing?.Length ?? 0)];
        Encoding.ASCII.GetBytes("MComprHD").CopyTo(image, 0);
        PutUInt32BE(image, 8, ChdHeader.V5HeaderSize);
        PutUInt32BE(image, 12, 5); // version
        PutUInt32BE(image, 16, compressor0);
        PutUInt64BE(image, 32, logicalBytes);
        PutUInt64BE(image, 40, MapOffset);
        PutUInt32BE(image, 56, hunkBytes);
        PutUInt32BE(image, 60, unitBytes);
        trailing?.CopyTo(image, ChdHeader.V5HeaderSize);
        return image;
    }

    /// <summary>
    /// Builds a single-hunk, uncompressed-map CHD whose one map entry points at
    /// <paramref name="hunkFileOffsetUnits"/> hunks into the file, padded to
    /// <paramref name="totalLength"/> bytes overall.
    /// </summary>
    private static byte[] BuildUncompressedChd(uint hunkFileOffsetUnits, int totalLength)
    {
        var map = new byte[4];
        PutUInt32BE(map, 0, hunkFileOffsetUnits);

        var image = BuildChd(logicalBytes: ValidHunkBytes, trailing: map);
        Array.Resize(ref image, totalLength);
        return image;
    }

    /// <summary>
    /// Builds a one-hunk CHD carrying a real V5 <em>compressed</em> map, which is the only
    /// route by which an attacker-chosen <c>CompressedLength</c> or <c>mapBytes</c> reaches
    /// the reader. The single map entry uses compression type 1, whose compressor slot is
    /// left at 0, so ChdReader takes its raw-hunk path and no codec is exercised.
    /// The hunk data is marked 0xA5 0x5A so a successful read is identifiable.
    /// </summary>
    /// <param name="compressedLength">The hunk length written into the map's length field.</param>
    /// <param name="mapBytesTarget">
    /// Declared (and physically present) compressed-map size; defaults to the encoded map.
    /// </param>
    private static byte[] BuildCompressedMapChd(uint compressedLength, int? mapBytesTarget = null)
    {
        var bits = new BitWriter();

        // Flat 16-symbol Huffman tree: every code length is 4, so the canonical codes
        // are 0..15 and symbol N is simply the 4-bit value N.
        for (int i = 0; i < 16; i++)
        {
            bits.Write(4, 4);
        }

        bits.Write(1, 4);                  // compression type 1 => codec slot 1 (== 0, raw)
        bits.Write(compressedLength, 32);  // lengthBits = 32, set in the map header below
        bits.Write(0, 16);                 // per-hunk CRC, not verified by this reader

        var mapData = bits.ToArray();
        int mapBytes = mapBytesTarget ?? mapData.Length;
        if (mapBytes > mapData.Length)
        {
            Array.Resize(ref mapData, mapBytes);
        }

        // The physical map region is always as long as the declared mapBytes, so a
        // rejected case is never rejected merely for running past the end of the file.
        int hunkDataOffset = MapOffset + 16 + Math.Max(mapBytes, mapData.Length);
        var hunkData = new byte[ValidHunkBytes + 16];
        hunkData[0] = 0xA5;
        hunkData[1] = 0x5A;

        var mapHeader = BuildCompressedMapHeader((uint)mapBytes);
        PutUInt48BE(mapHeader, 4, (ulong)hunkDataOffset);
        mapHeader[12] = 32; // lengthBits
        mapHeader[13] = 8;  // selfBits
        mapHeader[14] = 8;  // parentBits

        var image = BuildChd(
            logicalBytes: ValidHunkBytes,
            compressor0: Cdlz,
            trailing: [.. mapHeader, .. mapData, .. hunkData]);
        return image;
    }

    /// <summary>Minimal big-endian bit writer matching ChdBitstream's read order.</summary>
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _acc;
        private int _accBits;

        public void Write(uint value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                _acc = (_acc << 1) | (int)((value >> i) & 1);
                if (++_accBits == 8)
                {
                    _bytes.Add((byte)_acc);
                    _acc = 0;
                    _accBits = 0;
                }
            }
        }

        public byte[] ToArray()
        {
            var result = new List<byte>(_bytes);
            if (_accBits > 0)
            {
                result.Add((byte)(_acc << (8 - _accBits)));
            }
            return [.. result];
        }
    }

    /// <summary>Builds the 16-byte V5 compressed-map header, of which only mapBytes matters here.</summary>
    private static byte[] BuildCompressedMapHeader(uint mapBytes)
    {
        var mapHeader = new byte[16];
        PutUInt32BE(mapHeader, 0, mapBytes);
        return mapHeader;
    }

    private static ChdHeader BuildHeaderRecord(ulong logicalBytes, uint hunkBytes) => new()
    {
        Version = 5,
        HeaderLength = ChdHeader.V5HeaderSize,
        Compressors = [0, 0, 0, 0],
        LogicalBytes = logicalBytes,
        MapOffset = MapOffset,
        MetaOffset = 0,
        HunkBytes = hunkBytes,
        UnitBytes = ValidUnitBytes,
        RawSha1 = new byte[20],
        Sha1 = new byte[20],
        ParentSha1 = new byte[20],
    };

    private static void PutUInt32BE(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void PutUInt48BE(byte[] buffer, int offset, ulong value)
    {
        for (int i = 0; i < 6; i++)
        {
            buffer[offset + i] = (byte)(value >> (8 * (5 - i)));
        }
    }

    private static void PutUInt64BE(byte[] buffer, int offset, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            buffer[offset + i] = (byte)(value >> (8 * (7 - i)));
        }
    }

    // ------------------------------------------------------------------- ISO input

    private static byte[] BuildIsoDisc() =>
        new SyntheticIsoImageBuilder()
            .AddSystemCnf(BootValue)
            .AddFile(ExeIsoName, SyntheticPsxExeBuilder.BuildValid(16))
            .Build();

    /// <summary>Overwrites the size field of a named root-directory record.</summary>
    private static void PatchRootDirectoryEntrySize(byte[] image, string isoName, uint size)
    {
        int offset = (int)RootDirectorySector(image) * SectorSize;
        int end = offset + SectorSize;

        while (offset < end)
        {
            byte recordLength = image[offset];
            if (recordLength == 0)
            {
                break;
            }

            byte nameLength = image[offset + 32];
            if (Encoding.ASCII.GetString(image, offset + 33, nameLength) == isoName)
            {
                BitConverter.GetBytes(size).CopyTo(image, offset + 10);
                return;
            }

            offset += recordLength;
        }

        throw new InvalidOperationException($"Directory entry '{isoName}' not present in the synthetic image.");
    }

    /// <summary>Overwrites the root directory record's size inside the Primary Volume Descriptor.</summary>
    private static void PatchPrimaryVolumeDescriptorRootSize(byte[] image, uint size) =>
        BitConverter.GetBytes(size).CopyTo(image, 16 * SectorSize + 156 + 10);

    private static uint RootDirectorySector(byte[] image) =>
        BitConverter.ToUInt32(image, 16 * SectorSize + 156 + 2);
}
