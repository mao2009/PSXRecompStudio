using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Cpu;

[Domain]
public static class R3000aBranchSemantics
{
    public static bool TryGetBranchTarget(in R3000aInstruction instruction, uint pc, out uint target)
    {
        switch (instruction.Opcode)
        {
            case R3000aOpcode.Beq:
            case R3000aOpcode.Bne:
            case R3000aOpcode.Blez:
            case R3000aOpcode.Bgtz:
            case R3000aOpcode.Bltz:
            case R3000aOpcode.Bgez:
            case R3000aOpcode.Bltzal:
            case R3000aOpcode.Bgezal:
                break;
            default:
                target = 0;
                return false;
        }

        var _offsetOperand = instruction.GetOperand(instruction.OperandCount - 1);
        if (_offsetOperand.Kind != R3000aOperandKind.Immediate)
        {
            target = 0;
            return false;
        }

        var _scaledOffset = (uint)((int)(short)(ushort)_offsetOperand.Value << 2);
        target = unchecked(pc + 4u + _scaledOffset);
        return true;
    }
}
