using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Tests.Cpu;

[Test]
public class R3000aInstructionTests
{
    [Fact]
    public void Construct_RTypeStyle_HoldsEncodedWordAndThreeOperands()
    {
        var rd = R3000aOperand.CreateRegister(16);
        var rs = R3000aOperand.CreateRegister(4);
        var rt = R3000aOperand.CreateRegister(5);

        var instruction = new R3000aInstruction(
            0x00858020u,
            R3000aOpcode.Add,
            R3000aInstructionFormat.R,
            rd,
            rs,
            rt,
            operandCount: 3);

        instruction.EncodedWord.Should().Be(0x00858020u);
        instruction.Opcode.Should().Be(R3000aOpcode.Add);
        instruction.Format.Should().Be(R3000aInstructionFormat.R);
        instruction.OperandCount.Should().Be(3);
        instruction.Operand0.Should().Be(rd);
        instruction.Operand1.Should().Be(rs);
        instruction.Operand2.Should().Be(rt);
        instruction.ControlFlow.Should().Be(R3000aControlFlowKind.Sequential);
        instruction.DelaySlot.Should().Be(R3000aDelaySlotKind.None);
        instruction.LinkInfo.WritesLink.Should().BeFalse();
        instruction.LoadDelayInfo.ProducesLoadDelay.Should().BeFalse();
        instruction.CopInfo.Operation.Should().Be(R3000aCopOperationKind.None);
    }

    [Fact]
    public void Construct_LoadStyle_UsesTwoOperandForm()
    {
        var rt = R3000aOperand.CreateRegister(8);
        var memory = R3000aOperand.CreateMemoryOffset(29, 8);

        var instruction = new R3000aInstruction(
            0x8FA80008u,
            R3000aOpcode.Lw,
            R3000aInstructionFormat.I,
            rt,
            memory,
            default,
            operandCount: 2,
            loadDelayInfo: R3000aLoadDelayInfo.Create(8));

        instruction.OperandCount.Should().Be(2);
        instruction.GetOperand(0).Should().Be(rt);
        instruction.GetOperand(1).Should().Be(memory);
        instruction.LoadDelayInfo.ProducesLoadDelay.Should().BeTrue();
        instruction.LoadDelayInfo.TargetRegister.Should().Be(8);
    }

    [Fact]
    public void Construct_LinkBranch_HoldsConditionalDelaySlotAndRaLink()
    {
        var rs = R3000aOperand.CreateRegister(4);
        var offset = R3000aOperand.CreateImmediate(12);

        var instruction = new R3000aInstruction(
            0x048B000Cu,
            R3000aOpcode.Bgezal,
            R3000aInstructionFormat.Regimm,
            rs,
            offset,
            default,
            operandCount: 2,
            R3000aControlFlowKind.LinkBranch,
            R3000aDelaySlotKind.Conditional,
            linkInfo: R3000aLinkInfo.CreateRa());

        instruction.ControlFlow.Should().Be(R3000aControlFlowKind.LinkBranch);
        instruction.DelaySlot.Should().Be(R3000aDelaySlotKind.Conditional);
        instruction.LinkInfo.WritesLink.Should().BeTrue();
        instruction.LinkInfo.LinkRegister.Should().Be(R3000aLinkInfo.DefaultLinkRegister);
    }

    [Fact]
    public void Construct_CopInstruction_HoldsCopInfoWithoutOperands()
    {
        var copInfo = R3000aCopInfo.CreateMoveFromCoprocessor(0, 12);

        var instruction = new R3000aInstruction(
            0x40086000u,
            R3000aOpcode.Mfc0,
            R3000aInstructionFormat.Cop,
            default,
            default,
            default,
            operandCount: 0,
            controlFlow: R3000aControlFlowKind.Coprocessor,
            copInfo: copInfo);

        instruction.OperandCount.Should().Be(0);
        instruction.ControlFlow.Should().Be(R3000aControlFlowKind.Coprocessor);
        instruction.CopInfo.Should().Be(copInfo);
        instruction.CopInfo.CopRegisterNumber.Should().Be(12);
    }

    [Fact]
    public void Construct_OperandCountAboveThree_Throws()
    {
        var act = () => new R3000aInstruction(
            0u,
            R3000aOpcode.Sll,
            R3000aInstructionFormat.R,
            default,
            default,
            default,
            operandCount: 4);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GetOperand_RejectsIndexBeyondOperandCount()
    {
        var instruction = new R3000aInstruction(
            0u,
            R3000aOpcode.Lui,
            R3000aInstructionFormat.I,
            R3000aOperand.CreateRegister(8),
            R3000aOperand.CreateImmediate(0),
            default,
            operandCount: 2);

        var act = () => instruction.GetOperand(2);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Equality_SameFieldValues_AreEqual()
    {
        var first = new R3000aInstruction(
            0x00858020u,
            R3000aOpcode.Add,
            R3000aInstructionFormat.R,
            R3000aOperand.CreateRegister(16),
            R3000aOperand.CreateRegister(4),
            R3000aOperand.CreateRegister(5),
            operandCount: 3);
        var second = first with { };

        second.Should().Be(first);

        var different = new R3000aInstruction(
            0x00858021u,
            R3000aOpcode.Addu,
            R3000aInstructionFormat.R,
            R3000aOperand.CreateRegister(16),
            R3000aOperand.CreateRegister(4),
            R3000aOperand.CreateRegister(5),
            operandCount: 3);

        different.Should().NotBe(first);
    }

    [Fact]
    public void DefaultInstruction_HasNoOperandsAndSequentialFlow()
    {
        var instruction = default(R3000aInstruction);
        instruction.EncodedWord.Should().Be(0);
        instruction.OperandCount.Should().Be(0);
        instruction.ControlFlow.Should().Be(R3000aControlFlowKind.Sequential);
        instruction.DelaySlot.Should().Be(R3000aDelaySlotKind.None);
        instruction.LinkInfo.WritesLink.Should().BeFalse();
        instruction.LoadDelayInfo.ProducesLoadDelay.Should().BeFalse();
        instruction.CopInfo.Operation.Should().Be(R3000aCopOperationKind.None);
    }
}
