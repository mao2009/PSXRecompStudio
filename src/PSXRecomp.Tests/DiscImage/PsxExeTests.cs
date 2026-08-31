using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.DiscImageTests;

[Test]
public class PsxExeTests
{
    [Fact]
    public void Parse_ValidHeader_ExtractsFields()
    {
        var header = new byte[PsxExeHeader.HeaderSize];

        // Write magic "PS-X EXE"
        System.Text.Encoding.ASCII.GetBytes("PS-X EXE").CopyTo(header, 0);

        // Write entry point at offset 0x10
        BitConverter.GetBytes(0x80010000u).CopyTo(header, 0x10);

        // Write text start at offset 0x18
        BitConverter.GetBytes(0x80010800u).CopyTo(header, 0x18);

        // Write text size at offset 0x1C
        BitConverter.GetBytes(0x1000u).CopyTo(header, 0x1C);

        // Write SP at offset 0x30
        BitConverter.GetBytes(0x801FFF00u).CopyTo(header, 0x30);

        var result = PsxExeHeader.Parse(header);

        result.EntryPoint.Should().Be(0x80010000u);
        result.TextStart.Should().Be(0x80010800u);
        result.TextSize.Should().Be(0x1000u);
        result.TextEnd.Should().Be(0x80011800u);
        result.SpInitial.Should().Be(0x801FFF00u);
    }

    [Fact]
    public void Parse_InvalidMagic_ThrowsInvalidData()
    {
        var header = new byte[PsxExeHeader.HeaderSize];
        BitConverter.GetBytes(0xDEADBEEFu).CopyTo(header, 0);

        var act = () => PsxExeHeader.Parse(header);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*Invalid PS-X EXE magic*");
    }

    [Fact]
    public void Parse_TooShortHeader_ThrowsInvalidData()
    {
        var header = new byte[64];

        var act = () => PsxExeHeader.Parse(header);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*too short*");
    }

    [Fact]
    public void Load_WithTextSegment_ReturnsCorrectData()
    {
        var fileContent = new byte[PsxExeHeader.HeaderSize + 16];

        // Magic
        System.Text.Encoding.ASCII.GetBytes("PS-X EXE").CopyTo(fileContent, 0);

        // Entry point
        BitConverter.GetBytes(0x80010000u).CopyTo(fileContent, 0x10);

        // Text start
        BitConverter.GetBytes(0x80010000u).CopyTo(fileContent, 0x18);

        // Text size
        BitConverter.GetBytes(16u).CopyTo(fileContent, 0x1C);

        // Text data (4 instructions)
        BitConverter.GetBytes(0x03E00008u).CopyTo(fileContent, PsxExeHeader.HeaderSize + 0);  // jr ra
        BitConverter.GetBytes(0x00000000u).CopyTo(fileContent, PsxExeHeader.HeaderSize + 4);  // nop
        BitConverter.GetBytes(0x3C018001u).CopyTo(fileContent, PsxExeHeader.HeaderSize + 8);  // lui $1, 0x8001
        BitConverter.GetBytes(0x03E00008u).CopyTo(fileContent, PsxExeHeader.HeaderSize + 12); // jr ra

        var exe = PsxExe.Load(fileContent, "TEST.EXE");

        exe.FileName.Should().Be("TEST.EXE");
        exe.Header.EntryPoint.Should().Be(0x80010000u);
        exe.TextSegment.Should().HaveCount(16);
        exe.GetInstructionWord(0x80010000u).Should().Be(0x03E00008u);
        exe.GetInstructionWord(0x80010004u).Should().Be(0x00000000u);
    }
}
