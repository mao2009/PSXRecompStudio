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

    /// <summary>Mirrors ChdReader's independent ceiling for one hunk's compressed data.</summary>
    private const uint MaxCompressedHunkBytes = 4096 * CdFrameSize;

    // A hostile header pair: HunkBytes / UnitBytes = 200 stays under the frames-per-hunk
    // ceiling (4096), so ReadHeader accepts it, yet HunkBytes itself (20,000,000) is well
    // above MaxCompressedHunkBytes. This is only possible because HunkBytes carries no
    // ceiling of its own — exactly the gap the independent cap closes.
    private const uint HostileUnitBytes = 100_000;
    private const uint HostileHunkBytes = HostileUnitBytes * 200;

    /// <summary>Mirrors Iso9660Reader's independent ceiling for a single file's extent.</summary>
    private const long IsoMaxReadableExtentBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>Frames per hunk in the hostile-extent CHD (matches ValidHunkBytes / ValidUnitBytes).</summary>
    private const int ChdFramesPerHunk = 8;

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
    public void ReadSector_CompressedLengthAboveSupportedLimit_ThrowsBeforeAllocating()
    {
        // HunkBytes (20,000,000) alone would not catch this: it only rejects a length
        // above the *declared* hunk size, and this length is well under that. Only the
        // independent absolute cap does, because HunkBytes itself carries no ceiling of
        // its own (a crafted UnitBytes lets it grow arbitrarily; see HostileHunkBytes).
        var image = BuildCompressedMapChd(
            compressedLength: MaxCompressedHunkBytes + 1,
            hunkBytes: HostileHunkBytes,
            unitBytes: HostileUnitBytes,
            includeHunkData: false);

        using var stream = new MemoryStream(image);
        using var reader = ChdReader.Open(stream);

        reader.Invoking(r => r.ReadSector(0)).Should().Throw<InvalidDataException>()
            .WithMessage("*compressed length*");
    }

    [Fact]
    public void ReadSector_CompressedLengthAtSupportedLimit_PassesSizeValidation()
    {
        // Exactly at the cap: the size validation must not reject it. The image carries
        // no physical hunk data, so the read still fails, but only at the file-length
        // check that runs after the cap — proving the cap itself let the value through.
        var image = BuildCompressedMapChd(
            compressedLength: MaxCompressedHunkBytes,
            hunkBytes: HostileHunkBytes,
            unitBytes: HostileUnitBytes,
            includeHunkData: false);

        using var stream = new MemoryStream(image);
        using var reader = ChdReader.Open(stream);

        reader.Invoking(r => r.ReadSector(0)).Should().Throw<InvalidDataException>()
            .Which.Message.Should().Contain("hunk data").And.NotContain("compressed length");
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

    [Fact]
    public void RunFromChd_BootExecutableExtentAboveSupportedLimit_IsClassifiedBeforeAllocating()
    {
        // End to end through the CHD-backed sector path, which the plain-ISO entry point
        // cannot exercise: a ~28 KB CHD whose map marks every hunk beyond its single real
        // 8-sector block as a free CompressionParent hunk and aliases the claimed extent's
        // final sector back to that block via CompressionSelf. Under the pre-cap
        // implementation (int.MaxValue addressability only), the last-sector probe would
        // have succeeded here and ReadRawFile would have allocated the full ~1 GiB extent;
        // an OutOfMemoryException escapes IsClassifiableFailure and would break the
        // classified-outcome contract. The extent cap must therefore reject the file
        // before the probe runs, reaching the classified BootExecutableUnreadable failure.
        var (chd, lastSector) = BuildHostileExtentChd((uint)(IsoMaxReadableExtentBytes + SectorSize));

        // A few kilobytes on disk for a ~1 GiB logical extent claim.
        chd.Length.Should().BeLessThan(1 << 20);

        // Proof of the mechanism the plain-ISO tests cannot show: the tiny CHD really does
        // serve the claimed extent's final sector (mode byte 1 => a valid ISO sector), so
        // the probe succeeds against a zero-cost hunk and the pre-cap code reaches the
        // allocation rather than failing at the probe.
        using (var stream = new MemoryStream(chd))
        using (var reader = ChdReader.Open(stream))
        {
            reader.ReadSector((int)lastSector)[15].Should().Be((byte)1);
        }

        var outcome = RomAnalysisPipeline.RunFromChd(chd, Sha);

        outcome.Status.Should().Be(RomAnalysisStatus.Fail);
        outcome.LastSuccessfulStage.Should().Be(RomAnalysisStage.SystemCnf);
        outcome.FailedStage.Should().Be(RomAnalysisStage.BootExecutable);
        outcome.FailureKind.Should().Be("BootExecutableUnreadable");
        outcome.FailureReason.Should().Contain("not addressable");
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
    public void RunFromIsoImage_BootExecutableSizeAboveSupportedExtentLimit_IsClassified()
    {
        // Comfortably below int.MaxValue (~2 GiB), so the old addressability-only check
        // would have accepted it and gone on to allocate ~1 GiB. Only the independent
        // resource cap rejects it, before any allocation is attempted.
        var image = BuildIsoDisc();
        PatchRootDirectoryEntrySize(image, ExeIsoName, (uint)(IsoMaxReadableExtentBytes + SectorSize));

        var outcome = RomAnalysisPipeline.RunFromIsoImage(image, Sha);

        outcome.Status.Should().Be(RomAnalysisStatus.Fail);
        outcome.FailedStage.Should().Be(RomAnalysisStage.BootExecutable);
        outcome.FailureKind.Should().Be("BootExecutableUnreadable");
        outcome.FailureReason.Should().Contain("not addressable");
    }

    [Fact]
    public void RunFromIsoImage_BootExecutableSizeAtSupportedExtentLimit_PassesSizeValidation()
    {
        // Exactly at the cap: the resource limit must not reject it. The synthetic image is
        // far smaller than 1 GiB, so the read still fails - but only because the declared
        // sector is outside the real (tiny) image, proving the cap itself let the value
        // through before any allocation was attempted.
        var image = BuildIsoDisc();
        PatchRootDirectoryEntrySize(image, ExeIsoName, (uint)IsoMaxReadableExtentBytes);

        var outcome = RomAnalysisPipeline.RunFromIsoImage(image, Sha);

        outcome.Status.Should().Be(RomAnalysisStatus.Fail);
        outcome.FailedStage.Should().Be(RomAnalysisStage.BootExecutable);
        outcome.FailureKind.Should().Be("BootExecutableUnreadable");
        outcome.FailureReason.Should().Contain("outside the").And.NotContain("not addressable");
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
    /// <param name="hunkBytes">Declared header HunkBytes; defaults to <see cref="ValidHunkBytes"/>.</param>
    /// <param name="unitBytes">Declared header UnitBytes; defaults to <see cref="ValidUnitBytes"/>.</param>
    /// <param name="logicalBytes">Declared header LogicalBytes; defaults to <paramref name="hunkBytes"/>.</param>
    /// <param name="includeHunkData">
    /// Whether to physically append hunk-data bytes after the map. False leaves the image
    /// ending at the map, so a read that reaches past the resource bound hits SeekChecked's
    /// file-length check instead of allocating <paramref name="compressedLength"/> bytes.
    /// </param>
    private static byte[] BuildCompressedMapChd(
        uint compressedLength,
        int? mapBytesTarget = null,
        uint hunkBytes = ValidHunkBytes,
        uint unitBytes = ValidUnitBytes,
        ulong? logicalBytes = null,
        bool includeHunkData = true)
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
        var hunkData = Array.Empty<byte>();
        if (includeHunkData)
        {
            hunkData = new byte[hunkBytes + 16];
            hunkData[0] = 0xA5;
            hunkData[1] = 0x5A;
        }

        var mapHeader = BuildCompressedMapHeader((uint)mapBytes);
        PutUInt48BE(mapHeader, 4, (ulong)hunkDataOffset);
        mapHeader[12] = 32; // lengthBits
        mapHeader[13] = 8;  // selfBits
        mapHeader[14] = 8;  // parentBits

        var image = BuildChd(
            logicalBytes: logicalBytes ?? hunkBytes,
            hunkBytes: hunkBytes,
            unitBytes: unitBytes,
            compressor0: Cdlz,
            trailing: [.. mapHeader, .. mapData, .. hunkData]);
        return image;
    }

    /// <summary>
    /// Builds a CHD whose boot-executable extent claims <paramref name="exeSize"/> bytes above
    /// the reader's file-extent cap. It is the end-to-end fixture for the CHD-backed sector
    /// path the plain-ISO tests cannot reach: the ISO volume descriptor, root directory, and
    /// SYSTEM.CNF all live in one real 8-frame raw hunk (hunk 2, frames = ISO sectors 16..23),
    /// every other hunk is a zero-cost <see cref="ChdReader"/> CompressionParent hunk, and the
    /// hunk holding the claimed extent's final sector is CompressionSelf referencing hunk 2 —
    /// so the reader serves that far sector from the few kilobytes on disk without any file
    /// data, and a last-sector probe against it succeeds under the pre-cap implementation.
    /// </summary>
    /// <returns>The CHD bytes and the claimed extent's final sector index.</returns>
    private static (byte[] Chd, long LastSector) BuildHostileExtentChd(uint exeSize)
    {
        var iso = BuildIsoDisc();
        PatchRootDirectoryEntrySize(iso, ExeIsoName, exeSize);
        long exeLocation = RootDirectoryEntryLocation(iso, ExeIsoName);

        long totalSectors = ((long)exeSize + SectorSize - 1) / SectorSize;
        long lastSector = exeLocation + totalSectors - 1;
        long lastHunk = lastSector / ChdFramesPerHunk;
        int totalHunks = (int)(lastHunk + 1);
        ulong logicalBytes = (ulong)totalHunks * ValidHunkBytes;

        var bits = new BitWriter();

        // Flat 16-symbol Huffman tree, the same encoding the other hostile-map helpers use.
        for (int i = 0; i < 16; i++)
        {
            bits.Write(4, 4);
        }

        // Per-hunk compression types (first decode pass in DecompressV5Map).
        bits.Write(6, 4);                                             // hunk 0: CompressionParent
        bits.Write(6, 4);                                             // hunk 1: CompressionParent
        bits.Write(1, 4);                                             // hunk 2: codec slot 1 (== raw)
        bits.Write(6, 4);                                             // hunk 3: parent, anchors the RLE run
        WriteParentRleRun(bits, totalHunks - 5);                      // hunks 4..totalHunks-2: all parent
        bits.Write(5, 4);                                             // last hunk: CompressionSelf

        // Per-hunk auxiliary data (second decode pass, in hunk order). parentBits = 1, so a
        // parent entry costs a single bit; the codec entry carries length + CRC and the self
        // entry an 8-bit back-reference to hunk 2.
        bits.Write(0, 1);
        bits.Write(0, 1);
        bits.Write(ValidHunkBytes, 32);
        bits.Write(0, 16);
        for (int i = 0; i < totalHunks - 4; i++)
        {
            bits.Write(0, 1);
        }
        bits.Write(2, 8);

        var mapData = bits.ToArray();
        var hunkData = BuildFrameHunks(iso);
        var firstOffs = (ulong)(MapOffset + 16 + mapData.Length);

        var mapHeader = BuildCompressedMapHeader((uint)mapData.Length);
        PutUInt48BE(mapHeader, 4, firstOffs);
        mapHeader[12] = 32; // lengthBits
        mapHeader[13] = 8;  // selfBits
        mapHeader[14] = 1;  // parentBits

        var image = BuildChd(
            logicalBytes: logicalBytes,
            hunkBytes: ValidHunkBytes,
            unitBytes: ValidUnitBytes,
            compressor0: Cdlz,
            trailing: [.. mapHeader, .. mapData, .. hunkData]);
        return (image, lastSector);
    }

    /// <summary>
    /// Emits RLE runs (<c>RleLarge</c>, then possibly one <c>RleSmall</c>) that repeat the last
    /// explicit compression type <paramref name="count"/> more times, exactly as DecompressV5Map
    /// consumes them. The reader counts the event hunk itself, so a run covers
    /// <c>repCount + 1</c> hunks: <c>RleLarge</c> (value <c>v</c>) covers <c>19 + v</c> hunks for
    /// <c>v</c> in 0..255, and <c>RleSmall</c> (value <c>v</c>) covers <c>3 + v</c> hunks for
    /// <c>v</c> in 0..15.
    /// </summary>
    private static void WriteParentRleRun(BitWriter bits, int count)
    {
        while (count >= 19)
        {
            int chunk = Math.Min(count, 274);
            int value = chunk - 19;
            bits.Write(8, 4);              // RleLarge
            bits.Write((uint)(value >> 4), 4);
            bits.Write((uint)(value & 0xF), 4);
            count -= chunk;
        }

        if (count >= 3)
        {
            bits.Write(7, 4);              // RleSmall
            bits.Write((uint)(count - 3), 4);
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                bits.Write(6, 4);          // too short for RLE: emit explicit parent entries
            }
        }
    }

    /// <summary>
    /// Builds the one 8-frame raw hunk that physically exists in the hostile CHD. Frames are
    /// CD-ROM Mode 1 sectors whose 2048 bytes of user data are ISO sector <c>16 + frame</c>, the
    /// layout <see cref="DiscImageAnalyzer.CreateIsoReader"/> expects (mode byte at offset 15).
    /// </summary>
    private static byte[] BuildFrameHunks(byte[] isoImage)
    {
        var hunk = new byte[ValidHunkBytes];
        for (int frame = 0; frame < ChdFramesPerHunk; frame++)
        {
            var raw = new byte[CdFrameSize];
            raw[15] = 1; // CD-ROM Mode 1: user data at offset 16
            int src = (16 + frame) * SectorSize;
            if (src + SectorSize <= isoImage.Length)
            {
                Buffer.BlockCopy(isoImage, src, raw, 16, SectorSize);
            }

            Buffer.BlockCopy(raw, 0, hunk, frame * (int)CdFrameSize, (int)CdFrameSize);
        }

        return hunk;
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
        int offset = FindRootEntryOffset(image, isoName);
        BitConverter.GetBytes(size).CopyTo(image, offset + 10);
    }

    /// <summary>Returns the image-file offset of a named root-directory record.</summary>
    private static int FindRootEntryOffset(byte[] image, string isoName)
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
                return offset;
            }

            offset += recordLength;
        }

        throw new InvalidOperationException($"Directory entry '{isoName}' not present in the synthetic image.");
    }

    /// <summary>Returns the on-disc sector of a named root-directory record.</summary>
    private static uint RootDirectoryEntryLocation(byte[] image, string isoName) =>
        BitConverter.ToUInt32(image, FindRootEntryOffset(image, isoName) + 2);

    /// <summary>Overwrites the root directory record's size inside the Primary Volume Descriptor.</summary>
    private static void PatchPrimaryVolumeDescriptorRootSize(byte[] image, uint size) =>
        BitConverter.GetBytes(size).CopyTo(image, 16 * SectorSize + 156 + 10);

    private static uint RootDirectorySector(byte[] image) =>
        BitConverter.ToUInt32(image, 16 * SectorSize + 156 + 2);
}
