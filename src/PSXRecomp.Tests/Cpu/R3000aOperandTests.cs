using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Tests.Cpu;

[Test]
public class R3000aOperandTests
{
    [Fact]
    public void CreateRegister_HoldsRegisterNumber()
    {
        var operand = R3000aOperand.CreateRegister(29);
        operand.Kind.Should().Be(R3000aOperandKind.Register);
        operand.Register.Should().Be(29);
        operand.BaseRegister.Should().Be(0);
        operand.Value.Should().Be(0);
    }

    [Fact]
    public void CreateRegister_NumberAbove31_Throws()
    {
        var act = () => R3000aOperand.CreateRegister(32);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CreateImmediate_HoldsRaw16BitValue()
    {
        var operand = R3000aOperand.CreateImmediate(0xFFFF);
        operand.Kind.Should().Be(R3000aOperandKind.Immediate);
        operand.Value.Should().Be(0xFFFF);
    }

    [Fact]
    public void CreateMemoryOffset_HoldsBaseAndRawOffset()
    {
        var operand = R3000aOperand.CreateMemoryOffset(29, 8);
        operand.Kind.Should().Be(R3000aOperandKind.MemoryOffset);
        operand.BaseRegister.Should().Be(29);
        operand.Value.Should().Be(8);
        operand.Register.Should().Be(0);
    }

    [Fact]
    public void CreateMemoryOffset_BaseOutOfRange_Throws()
    {
        var act = () => R3000aOperand.CreateMemoryOffset(32, 8);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CreateShamt_HoldsShiftAmount()
    {
        var operand = R3000aOperand.CreateShamt(31);
        operand.Kind.Should().Be(R3000aOperandKind.Shamt);
        operand.Value.Should().Be(31);
    }

    [Fact]
    public void CreateShamt_Above31_Throws()
    {
        var act = () => R3000aOperand.CreateShamt(32);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CreateJumpIndex_Holds26BitIndex()
    {
        var operand = R3000aOperand.CreateJumpIndex(0x03FFFFFF);
        operand.Kind.Should().Be(R3000aOperandKind.JumpIndex);
        operand.Value.Should().Be(0x03FFFFFF);
    }

    [Fact]
    public void CreateJumpIndex_Above26Bits_Throws()
    {
        var act = () => R3000aOperand.CreateJumpIndex(0x04000000);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CreateCopReg_HoldsCoprocessorIdAndRegister()
    {
        var operand = R3000aOperand.CreateCopReg(2, 15);
        operand.Kind.Should().Be(R3000aOperandKind.CopReg);
        operand.CoprocessorId.Should().Be(2);
        operand.Register.Should().Be(15);
    }

    [Fact]
    public void CreateCopReg_CoprocessorIdAbove3_Throws()
    {
        var act = () => R3000aOperand.CreateCopReg(4, 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CreateCopReg_RegisterAbove31_Throws()
    {
        var act = () => R3000aOperand.CreateCopReg(0, 32);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void OperandKinds_AllFactoryKinds_AreDistinctAndNotNone()
    {
        var kinds = new[]
        {
            R3000aOperand.CreateRegister(0).Kind,
            R3000aOperand.CreateImmediate(0).Kind,
            R3000aOperand.CreateMemoryOffset(0, 0).Kind,
            R3000aOperand.CreateShamt(0).Kind,
            R3000aOperand.CreateJumpIndex(0).Kind,
            R3000aOperand.CreateCopReg(0, 0).Kind,
        };
        kinds.Distinct().Count().Should().Be(6);
        kinds.Should().NotContain(R3000aOperandKind.None);
    }
}
