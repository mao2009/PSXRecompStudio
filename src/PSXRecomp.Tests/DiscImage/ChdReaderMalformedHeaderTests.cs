using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.DiscImageTests;

/// <summary>
/// Focused tests that a CHD V5 header with invalid geometry is rejected during
/// header parsing (ReadHeader), rather than failing later with a
/// DivideByZeroException or returning malformed sector geometry.
/// </summary>
[Test]
public class ChdReaderMalformedHeaderTests
{
    private static readonly byte[] ValidMagic = [
        0x4D, 0x43, 0x6F, 0x6D, 0x70, 0x72, 0x48, 0x44, // "MComprHD"
    ];

    /// <summary>
    /// Builds a minimal CHD V5 header honoring the reader's pre-map geometry
    /// validation: a valid magic, locked to V5, followed by the two uint32
    /// geometry fields under test (hunkBytes and unitBytes). Other fields are
    /// zero-filled.
    /// </summary>
    private static byte[] BuildV5Header(uint hunkBytes, uint unitBytes)
    {
        var header = new byte[ChdHeader.V5HeaderSize];
        Array.Copy(ValidMagic, header, ValidMagic.Length);

        void PutUInt32(int offset, uint value)
        {
            header[offset] = (byte)(value >> 24);
            header[offset + 1] = (byte)(value >> 16);
            header[offset + 2] = (byte)(value >> 8);
            header[offset + 3] = (byte)value;
        }

        uint headerLength = ChdHeader.V5HeaderSize;
        PutUInt32(8, headerLength);
        PutUInt32(12, 5); // version 5
        PutUInt32(56, hunkBytes);
        PutUInt32(60, unitBytes);

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
        // A valid geometry causes ReadHeader to pass; map parsing may still
        // fail for the minimal (empty) stream, but it must NOT be a geometry
        // (InvalidDataException) rejection. Use a small logical size so the
        // map region has no hunks to parse.
        using var stream = new MemoryStream(BuildV5Header(hunkBytes: 2352 * 8, unitBytes: 2352));
        stream.Invoking(s => ChdReader.Open(s))
            .Should().NotThrow<InvalidDataException>();
    }
}