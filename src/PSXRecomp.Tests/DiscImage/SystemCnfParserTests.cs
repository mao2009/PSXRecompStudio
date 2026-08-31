using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.DiscImageTests;

[Test]
public class SystemCnfParserTests
{
    [Fact]
    public void Parse_StandardSystemCnf_ExtractsBootPath()
    {
        var content = """
            BOOT=cdrom:\SLUS_011.47;1
            VMODE=NTSC
            """;

        var result = SystemCnfParser.Parse(content);

        result.BootPath.Should().Be("cdrom:\\SLUS_011.47;1");
    }

    [Fact]
    public void Parse_QuotedBootPath_ExtractsPath()
    {
        var content = """
            BOOT="SLPM_861.47;1"
            """;

        var result = SystemCnfParser.Parse(content);

        result.BootPath.Should().Be("SLPM_861.47;1");
    }

    [Fact]
    public void Parse_CaseInsensitiveBootKey_Parses()
    {
        var content = """
            boot=SLUS_011.47;1
            """;

        var result = SystemCnfParser.Parse(content);

        result.BootPath.Should().Be("SLUS_011.47;1");
    }

    [Fact]
    public void Parse_CommentsIgnored()
    {
        var content = """
            // This is a comment
            BOOT=SLUS_011.47;1
            // Another comment
            VMODE=NTSC
            """;

        var result = SystemCnfParser.Parse(content);

        result.BootPath.Should().Be("SLUS_011.47;1");
    }

    [Fact]
    public void Parse_Boot2_Parses()
    {
        var content = """
            BOOT2=SLUS_011.47;1
            """;

        var result = SystemCnfParser.Parse(content);

        result.BootPath.Should().Be("SLUS_011.47;1");
    }

    [Fact]
    public void Parse_NoBoot_ThrowsInvalidData()
    {
        var content = """
            VMODE=NTSC
            """;

        var act = () => SystemCnfParser.Parse(content);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*BOOT*");
    }

    [Fact]
    public void Parse_EmptyContent_ThrowsInvalidData()
    {
        var act = () => SystemCnfParser.Parse(string.Empty);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Parse_ByteContent_Parses()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("BOOT=SLUS_011.47;1\n");

        var result = SystemCnfParser.Parse(bytes);

        result.BootPath.Should().Be("SLUS_011.47;1");
    }
}
