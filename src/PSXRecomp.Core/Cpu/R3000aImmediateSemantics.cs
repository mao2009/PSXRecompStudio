using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Cpu;

[Domain]
public static class R3000aImmediateSemantics
{
    public static bool TryGetImmediate(in R3000aInstruction instruction, out int immediate)
    {
        switch (instruction.Opcode)
        {
            case R3000aOpcode.Addi:
            case R3000aOpcode.Addiu:
            case R3000aOpcode.Slti:
            case R3000aOpcode.Sltiu:
            case R3000aOpcode.Andi:
            case R3000aOpcode.Ori:
            case R3000aOpcode.Xori:
                break;
            default:
                immediate = 0;
                return false;
        }

        var immediateOperand = instruction.GetOperand(instruction.OperandCount - 1);
        if (immediateOperand.Kind != R3000aOperandKind.Immediate)
        {
            immediate = 0;
            return false;
        }

        var rawImmediate = (ushort)immediateOperand.Value;
        immediate = IsZeroExtension(instruction.Opcode) ? rawImmediate : (short)rawImmediate;
        return true;
    }

    private static bool IsZeroExtension(R3000aOpcode opcode)
    {
        return opcode is R3000aOpcode.Andi or R3000aOpcode.Ori or R3000aOpcode.Xori;
    }
}
