using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.DiscImageTests;

/// <summary>
/// Focused tests that hostile CHD V5 size fields are rejected before they can
/// drive large allocations.
/// </summary>
[Test]
public class ChdReaderMalformedHeaderTests
{
    private static readonly byte[] ValidMagic = [
        0x4D, 0x43, 0x6F, 0x6D, 0x70, 0x72, 0x48, 0x44,
    ];

    private static byte[] BuildV5Header(
        uint hunkBytes,
        uint unitBytes,
        ulong logicalBytes = 0,
        ulong mapOffset = 0,
        uint compressor0 = 0)
    {
        var header = new byte[ChdHeader.V5HeaderSize];
        Array.Copy(ValidMagic, header, ValidMagic.Length);

        PutUInt32(header, 8, ChdHeader.V5HeaderSize);
        PutUInt32(header, 12, 5);
        PutUInt32(header, 16, compressor0);
        PutUInt64(header, 32, logicalBytes);
        PutUInt64(header, 40, mapOffset);
        PutUInt32(header, 56, hunkBytes);
        PutUInt32(header, 60, unitBytes);
        return header;
    }

    [Fact]
    public void Open_HunkBytesZero_ThrowsInvalidDataException()
    {
        using var stream = new MemoryStream(BuildV5Header(hunkBytes: 0, unitBytes: 2352));
        stream.Invoking(s => ChdReader.Open(s)).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Open_UnitBytesZero_ThrowsInvalidDataException()
    {
        using var stream = new MemoryStream(BuildV5Header(hunkBytes: 2352 * 8, unitBytes: 0));
        stream.Invoking(s => ChdReader.Open(s)).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Open_BothGeometryZero_ThrowsInvalidDataException()
    {
        using var stream = new MemoryStream(BuildV5Header(hunkBytes: 0, unitBytes: 0));
        stream.Invoking(s => ChdReader.Open(s)).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Open_ValidGeometry_DoesNotThrowForHeaderParsing()
    {
        using var stream = new MemoryStream(BuildV5Header(hunkBytes: 2352 * 8, unitBytes: 2352));
        stream.Invoking(s => ChdReader.Open(s)).Should().NotThrow();
    }

    [Fact]
    public void Open_HunkBytesAboveSafetyCeiling_RejectsBeforeAllocation()
    {
        using var stream = new MemoryStream(BuildV5Header(
            hunkBytes: 16U * 1024U * 1024U + 1U,
            unitBytes: 2352));

        stream.Invoking(s => ChdReader.Open(s))
            .Should().Throw<InvalidDataException>()
            .WithMessage("*hunkBytes*ceiling*");
    }

    [Fact]
    public void Open_LogicalBytesAbovePs1Ceiling_RejectsBeforeMapAllocation()
    {
        using var stream = new MemoryStream(BuildV5Header(
            hunkBytes: 2352 * 8,
            unitBytes: 2352,
            logicalBytes: 2UL * 1024UL * 1024UL * 1024UL + 1UL));

        stream.Invoking(s => ChdReader.Open(s))
            .Should().Throw<InvalidDataException>()
            .WithMessage("*logicalBytes*ceiling*");
    }

    [Fact]
    public void Open_TooManyHunks_RejectsBeforeExpandedMapAllocation()
    {
        using var stream = new MemoryStream(BuildV5Header(
            hunkBytes: 1,
            unitBytes: 1,
            logicalBytes: 500_001));

        stream.Invoking(s => ChdReader.Open(s))
            .Should().Throw<InvalidDataException>()
            .WithMessage("*hunks*ceiling*");
    }

    [Fact]
    public void Open_CompressedMapLengthLargerThanContainer_RejectsBeforeAllocation()
    {
        var header = BuildV5Header(
            hunkBytes: 2352 * 8,
            unitBytes: 2352,
            logicalBytes: 2352 * 8,
            mapOffset: ChdHeader.V5HeaderSize,
            compressor0: 1);

        var image = new byte[ChdHeader.V5HeaderSize + 16];
        Array.Copy(header, image, header.Length);
        PutUInt32(image, ChdHeader.V5HeaderSize, uint.MaxValue);

        using var stream = new MemoryStream(image);
        stream.Invoking(s => ChdReader.Open(s))
            .Should().Throw<InvalidDataException>()
            .WithMessage("*compressed map length*ceiling*");
    }

    [Fact]
    public void Open_UncompressedMapOutsideContainer_RejectsBeforeAllocation()
    {
        var image = BuildV5Header(
            hunkBytes: 2352 * 8,
            unitBytes: 2352,
            logicalBytes: 2352 * 8,
            mapOffset: ChdHeader.V5HeaderSize);

        using var stream = new MemoryStream(image);
        stream.Invoking(s => ChdReader.Open(s))
            .Should().Throw<InvalidDataException>()
            .WithMessage("*uncompressed map*outside*");
    }

    private static void PutUInt32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static void PutUInt64(byte[] data, int offset, ulong value)
    {
        PutUInt32(data, offset, (uint)(value >> 32));
        PutUInt32(data, offset + 4, (uint)value);
    }
}
