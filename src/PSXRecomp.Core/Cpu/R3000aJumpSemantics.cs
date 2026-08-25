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

        var _indexOperand = instruction.GetOperand(instruction.OperandCount - 1);
        if (_indexOperand.Kind != R3000aOperandKind.JumpIndex)
        {
            target = 0;
            return false;
        }

        var _delaySlotAddress = unchecked(pc + 4u);
        target = (_delaySlotAddress & RegionMask) | (_indexOperand.Value << 2);
        return true;
    }
}
