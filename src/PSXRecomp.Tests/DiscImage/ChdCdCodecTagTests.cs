using System.Text;
using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.DiscImageTests;

/// <summary>
/// Verifies CHD CD codec tag constants encode the correct ASCII codec strings
/// ("cdlz" / "cdzl") under the V5 big-endian file interpretation, matching MAME.
/// </summary>
[Test]
public class ChdCdCodecTagTests
{
    [Fact]
    public void CodecLzma_BigEndian_IsAsciiCdlz()
    {
        // 0x63646C7A as big-endian bytes must spell "cdlz".
        var bytes = new[]
        {
            unchecked((byte)(ChdCdCodec.CodecLzma >> 24)),
            unchecked((byte)(ChdCdCodec.CodecLzma >> 16)),
            unchecked((byte)(ChdCdCodec.CodecLzma >> 8)),
            unchecked((byte)ChdCdCodec.CodecLzma),
        };
        Encoding.ASCII.GetString(bytes).Should().Be("cdlz");
    }

    [Fact]
    public void CodecZlib_BigEndian_IsAsciiCdzl()
    {
        var bytes = new[]
        {
            unchecked((byte)(ChdCdCodec.CodecZlib >> 24)),
            unchecked((byte)(ChdCdCodec.CodecZlib >> 16)),
            unchecked((byte)(ChdCdCodec.CodecZlib >> 8)),
            unchecked((byte)ChdCdCodec.CodecZlib),
        };
        Encoding.ASCII.GetString(bytes).Should().Be("cdzl");
    }

    [Fact]
    public void HeaderCompressionName_RoundTripsTags()
    {
        var header = new ChdHeader
        {
            Version = 5,
            HeaderLength = 124,
            Compressors = new[] { ChdCdCodec.CodecLzma, ChdCdCodec.CodecZlib, 0u, 0u },
            LogicalBytes = 1,
            MapOffset = 1,
            MetaOffset = 1,
            HunkBytes = 2340,
            UnitBytes = 2340,
            RawSha1 = new byte[20],
            Sha1 = new byte[20],
            ParentSha1 = new byte[20],
        };

        header.CompressionName(0).Should().Be("cdlz");
        header.CompressionName(1).Should().Be("cdzl");
        header.CompressionName(2).Should().Be("none");
    }
}
