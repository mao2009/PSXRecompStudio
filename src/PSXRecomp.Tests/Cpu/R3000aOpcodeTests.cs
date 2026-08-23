using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Tests.Cpu;

[Test]
public class R3000aOpcodeTests
{
    [Fact]
    public void Opcode_Has67Members()
    {
        Enum.GetValues<R3000aOpcode>().Length.Should().Be(67);
    }

    [Fact]
    public void Opcode_AllMemberValues_AreDistinct()
    {
        Enum.GetValues<R3000aOpcode>().Distinct().Count().Should().Be(67);
    }

    [Fact]
    public void Opcode_UnderlyingType_IsByte()
    {
        Enum.GetUnderlyingType(typeof(R3000aOpcode)).Should().Be(typeof(byte));
    }

    [Theory]
    [InlineData(nameof(R3000aOpcode.Add), 0)]
    [InlineData(nameof(R3000aOpcode.Addiu), 3)]
    [InlineData(nameof(R3000aOpcode.Lui), 17)]
    [InlineData(nameof(R3000aOpcode.Sll), 18)]
    [InlineData(nameof(R3000aOpcode.Srav), 23)]
    [InlineData(nameof(R3000aOpcode.Mult), 24)]
    [InlineData(nameof(R3000aOpcode.Mtlo), 31)]
    [InlineData(nameof(R3000aOpcode.Lb), 32)]
    [InlineData(nameof(R3000aOpcode.Lw), 36)]
    [InlineData(nameof(R3000aOpcode.Lwl), 37)]
    [InlineData(nameof(R3000aOpcode.Lwr), 38)]
    [InlineData(nameof(R3000aOpcode.Lwc2), 39)]
    [InlineData(nameof(R3000aOpcode.Sb), 40)]
    [InlineData(nameof(R3000aOpcode.Swc2), 45)]
    [InlineData(nameof(R3000aOpcode.J), 46)]
    [InlineData(nameof(R3000aOpcode.Jalr), 49)]
    [InlineData(nameof(R3000aOpcode.Beq), 50)]
    [InlineData(nameof(R3000aOpcode.Bltz), 54)]
    [InlineData(nameof(R3000aOpcode.Bgezal), 57)]
    [InlineData(nameof(R3000aOpcode.Syscall), 58)]
    [InlineData(nameof(R3000aOpcode.Break), 59)]
    [InlineData(nameof(R3000aOpcode.Mfc0), 60)]
    [InlineData(nameof(R3000aOpcode.Mtc0), 61)]
    [InlineData(nameof(R3000aOpcode.Rfe), 62)]
    [InlineData(nameof(R3000aOpcode.Cop2Command), 63)]
    [InlineData(nameof(R3000aOpcode.Cop1Unusable), 64)]
    [InlineData(nameof(R3000aOpcode.Cop3Unusable), 65)]
    [InlineData(nameof(R3000aOpcode.Reserved), 66)]
    public void Opcode_DeclarationOrder_MirrorsYamlInstructionOrder(string name, int expectedValue)
    {
        var parsed = Enum.Parse<R3000aOpcode>(name);
        ((int)parsed).Should().Be(expectedValue);
    }
}
