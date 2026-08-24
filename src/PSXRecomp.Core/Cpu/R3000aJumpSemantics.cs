using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Cpu;

[Domain]
public static class R3000aJumpSemantics
{
    private const uint RegionMask = 0xF0000000;

    public static bool TryGetJumpTarget(in R3000aInstruction instruction, uint pc, out uint target)
    {
        switch (instruction.Opcode)
        {
            case R3000aOpcode.J:
            case R3000aOpcode.Jal:
                break;
            default:
                target = 0;
                return false;
        }

        var indexOperand = instruction.GetOperand(instruction.OperandCount - 1);
        if (indexOperand.Kind != R3000aOperandKind.JumpIndex)
        {
            target = 0;
            return false;
        }

        var delaySlotAddress = unchecked(pc + 4u);
        target = (delaySlotAddress & RegionMask) | (indexOperand.Value << 2);
        return true;
    }
}
