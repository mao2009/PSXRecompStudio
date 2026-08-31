using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Formats decoded MIPS R3000A instructions into human-readable mnemonic + operands strings.
/// </summary>
[Domain]
public static class MipsInstructionFormatter
{
    private static readonly string[] RegisterNames =
    [
        "$zero", "$at", "$v0", "$v1", "$a0", "$a1", "$a2", "$a3",
        "$t0", "$t1", "$t2", "$t3", "$t4", "$t5", "$t6", "$t7",
        "$s0", "$s1", "$s2", "$s3", "$s4", "$s5", "$s6", "$s7",
        "$t8", "$t9", "$k0", "$k1", "$gp", "$sp", "$fp", "$ra",
    ];

    public static string FormatMnemonic(R3000aOpcode opcode)
    {
        return opcode switch
        {
            R3000aOpcode.Add => "add",
            R3000aOpcode.Addu => "addu",
            R3000aOpcode.Addi => "addi",
            R3000aOpcode.Addiu => "addiu",
            R3000aOpcode.Sub => "sub",
            R3000aOpcode.Subu => "subu",
            R3000aOpcode.Slt => "slt",
            R3000aOpcode.Sltu => "sltu",
            R3000aOpcode.Slti => "slti",
            R3000aOpcode.Sltiu => "sltiu",
            R3000aOpcode.And => "and",
            R3000aOpcode.Or => "or",
            R3000aOpcode.Xor => "xor",
            R3000aOpcode.Nor => "nor",
            R3000aOpcode.Andi => "andi",
            R3000aOpcode.Ori => "ori",
            R3000aOpcode.Xori => "xori",
            R3000aOpcode.Lui => "lui",
            R3000aOpcode.Sll => "sll",
            R3000aOpcode.Srl => "srl",
            R3000aOpcode.Sra => "sra",
            R3000aOpcode.Sllv => "sllv",
            R3000aOpcode.Srlv => "srlv",
            R3000aOpcode.Srav => "srav",
            R3000aOpcode.Mult => "mult",
            R3000aOpcode.Multu => "multu",
            R3000aOpcode.Div => "div",
            R3000aOpcode.Divu => "divu",
            R3000aOpcode.Mfhi => "mfhi",
            R3000aOpcode.Mthi => "mthi",
            R3000aOpcode.Mflo => "mflo",
            R3000aOpcode.Mtlo => "mtlo",
            R3000aOpcode.Lb => "lb",
            R3000aOpcode.Lbu => "lbu",
            R3000aOpcode.Lh => "lh",
            R3000aOpcode.Lhu => "lhu",
            R3000aOpcode.Lw => "lw",
            R3000aOpcode.Lwl => "lwl",
            R3000aOpcode.Lwr => "lwr",
            R3000aOpcode.Lwc2 => "lwc2",
            R3000aOpcode.Sb => "sb",
            R3000aOpcode.Sh => "sh",
            R3000aOpcode.Sw => "sw",
            R3000aOpcode.Swl => "swl",
            R3000aOpcode.Swr => "swr",
            R3000aOpcode.Swc2 => "swc2",
            R3000aOpcode.J => "j",
            R3000aOpcode.Jal => "jal",
            R3000aOpcode.Jr => "jr",
            R3000aOpcode.Jalr => "jalr",
            R3000aOpcode.Beq => "beq",
            R3000aOpcode.Bne => "bne",
            R3000aOpcode.Blez => "blez",
            R3000aOpcode.Bgtz => "bgtz",
            R3000aOpcode.Bltz => "bltz",
            R3000aOpcode.Bgez => "bgez",
            R3000aOpcode.Bltzal => "bltzal",
            R3000aOpcode.Bgezal => "bgezal",
            R3000aOpcode.Syscall => "syscall",
            R3000aOpcode.Break => "break",
            R3000aOpcode.Mfc0 => "mfc0",
            R3000aOpcode.Mtc0 => "mtc0",
            R3000aOpcode.Rfe => "rfe",
            R3000aOpcode.Cop2Command => "cop2",
            R3000aOpcode.Cop1Unusable => "cop1",
            R3000aOpcode.Cop3Unusable => "cop3",
            R3000aOpcode.Reserved => "???",
            _ => "???",
        };
    }

    public static string FormatOperands(R3000aInstruction instruction)
    {
        var opcode = instruction.Opcode;
        int count = instruction.OperandCount;
        if (count == 0)
        {
            if (opcode == R3000aOpcode.Syscall || opcode == R3000aOpcode.Break)
            {
                return string.Empty;
            }
            if (opcode == R3000aOpcode.Rfe)
            {
                return string.Empty;
            }
        }

        return opcode switch
        {
            R3000aOpcode.J or R3000aOpcode.Jal => FormatJumpTarget(instruction),
            R3000aOpcode.Jr => FormatRegisterOnly(instruction),
            R3000aOpcode.Jalr => FormatJalr(instruction),
            R3000aOpcode.Beq or R3000aOpcode.Bne => FormatBranch2Reg(instruction),
            R3000aOpcode.Blez or R3000aOpcode.Bgtz => FormatBranch1Reg(instruction),
            R3000aOpcode.Bltz or R3000aOpcode.Bgez or R3000aOpcode.Bltzal or R3000aOpcode.Bgezal
                => FormatBranchZero(instruction),
            R3000aOpcode.Lb or R3000aOpcode.Lbu or R3000aOpcode.Lh or R3000aOpcode.Lhu
                or R3000aOpcode.Lw or R3000aOpcode.Lwl or R3000aOpcode.Lwr
                or R3000aOpcode.Lwc2
                => FormatLoad(instruction),
            R3000aOpcode.Sb or R3000aOpcode.Sh or R3000aOpcode.Sw
                or R3000aOpcode.Swl or R3000aOpcode.Swr
                or R3000aOpcode.Swc2
                => FormatStore(instruction),
            R3000aOpcode.Mfhi or R3000aOpcode.Mflo => FormatMoveFromHiLo(instruction),
            R3000aOpcode.Mthi or R3000aOpcode.Mtlo => FormatMoveToHiLo(instruction),
            R3000aOpcode.Mult or R3000aOpcode.Multu or R3000aOpcode.Div or R3000aOpcode.Divu
                => FormatMultiplyDivide(instruction),
            R3000aOpcode.Mfc0 => FormatMfc0(instruction),
            R3000aOpcode.Mtc0 => FormatMtc0(instruction),
            R3000aOpcode.Cop2Command => FormatCop2(instruction),
            _ => FormatDefault(instruction),
        };
    }

    public static string FormatInstruction(R3000aInstruction instruction, uint address)
    {
        var mnemonic = FormatMnemonic(instruction.Opcode);
        var operands = FormatOperands(instruction);
        return string.IsNullOrEmpty(operands) ? mnemonic : $"{mnemonic} {operands}";
    }

    private static string FormatRegister(R3000aOperand operand)
    {
        return RegisterNames[operand.Register];
    }

    private static string FormatImmediate(uint value)
    {
        return $"0x{value & 0xFFFF:X4}";
    }

    private static string FormatSignedImmediate(ushort value)
    {
        short signed = (short)value;
        return signed >= 0 ? $"0x{value:X4}" : $"-0x{(-signed):X4}";
    }

    private static string FormatJumpTarget(R3000aInstruction instruction)
    {
        var target = instruction.Operand0.Value << 2;
        return $"0x{target:X8}";
    }

    private static string FormatRegisterOnly(R3000aInstruction instruction)
    {
        return FormatRegister(instruction.Operand0);
    }

    private static string FormatJalr(R3000aInstruction instruction)
    {
        var rd = instruction.Operand0.Register;
        var rs = instruction.Operand1.Register;
        if (rd == 31)
        {
            return FormatRegister(instruction.Operand1);
        }
        return $"{RegisterNames[rd]}, {RegisterNames[rs]}";
    }

    private static string FormatBranch2Reg(R3000aInstruction instruction)
    {
        var rs = FormatRegister(instruction.Operand0);
        var rt = FormatRegister(instruction.Operand1);
        short offset = (short)instruction.Operand2.Value;
        var target = (uint)((int)instruction.GetOperand(2).Value << 2);
        return $"{rs}, {rt}, 0x{target:X8}";
    }

    private static string FormatBranch1Reg(R3000aInstruction instruction)
    {
        var rs = FormatRegister(instruction.Operand0);
        short offset = (short)instruction.Operand1.Value;
        var target = (uint)((int)instruction.Operand1.Value << 2);
        return $"{rs}, 0x{target:X8}";
    }

    private static string FormatBranchZero(R3000aInstruction instruction)
    {
        var rs = FormatRegister(instruction.Operand0);
        short offset = (short)instruction.Operand1.Value;
        var target = (uint)((int)instruction.Operand1.Value << 2);
        return $"{rs}, 0x{target:X8}";
    }

    private static string FormatLoad(R3000aInstruction instruction)
    {
        var rt = FormatRegister(instruction.Operand0);
        var mem = instruction.Operand1;
        var baseReg = RegisterNames[mem.BaseRegister];
        var offset = (short)mem.Value;
        return $"{rt}, {offset}(0x{baseReg})";
    }

    private static string FormatStore(R3000aInstruction instruction)
    {
        var rt = FormatRegister(instruction.Operand0);
        var mem = instruction.Operand1;
        var baseReg = RegisterNames[mem.BaseRegister];
        var offset = (short)mem.Value;
        return $"{rt}, {offset}(0x{baseReg})";
    }

    private static string FormatMoveFromHiLo(R3000aInstruction instruction)
    {
        return FormatRegister(instruction.Operand0);
    }

    private static string FormatMoveToHiLo(R3000aInstruction instruction)
    {
        return FormatRegister(instruction.Operand0);
    }

    private static string FormatMultiplyDivide(R3000aInstruction instruction)
    {
        var rs = FormatRegister(instruction.Operand0);
        var rt = FormatRegister(instruction.Operand1);
        return $"{rs}, {rt}";
    }

    private static string FormatMfc0(R3000aInstruction instruction)
    {
        return FormatRegister(instruction.Operand0);
    }

    private static string FormatMtc0(R3000aInstruction instruction)
    {
        return FormatRegister(instruction.Operand0);
    }

    private static string FormatCop2(R3000aInstruction instruction)
    {
        if (instruction.OperandCount == 0)
        {
            return string.Empty;
        }

        if (instruction.Operand0.Kind == R3000aOperandKind.CopReg)
        {
            return $"v{instruction.Operand0.Register}";
        }

        return string.Empty;
    }

    private static string FormatDefault(R3000aInstruction instruction)
    {
        if (instruction.OperandCount == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        for (int i = 0; i < instruction.OperandCount; i++)
        {
            var op = instruction.GetOperand(i);
            parts.Add(op.Kind switch
            {
                R3000aOperandKind.Register => FormatRegister(op),
                R3000aOperandKind.Immediate => FormatImmediate(op.Value),
                R3000aOperandKind.Shamt => $"0x{op.Value:X}",
                R3000aOperandKind.MemoryOffset => FormatMemoryOffset(op),
                R3000aOperandKind.JumpIndex => $"0x{op.Value << 2:X8}",
                R3000aOperandKind.CopReg => $"v{op.Register}",
                _ => $"0x{op.Value:X}",
            });
        }
        return string.Join(", ", parts);
    }

    private static string FormatMemoryOffset(R3000aOperand op)
    {
        var baseReg = RegisterNames[op.BaseRegister];
        var offset = (short)op.Value;
        return $"{offset}({baseReg})";
    }
}
