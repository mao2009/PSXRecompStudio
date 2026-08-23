using System.Runtime.InteropServices;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Cpu;

[Domain]
[StructLayout(LayoutKind.Sequential)]
public readonly record struct R3000aOperand
{
    public const byte MaxRegisterNumber = 31;
    public const byte MaxShamt = 31;
    public const byte MaxCoprocessorId = 3;
    public const uint MaxJumpIndex = 0x03FFFFFF;

    private readonly byte _kind;
    private readonly byte _register;
    private readonly byte _baseRegister;
    private readonly byte _coprocessorId;
    private readonly uint _value;

    private R3000aOperand(R3000aOperandKind kind, byte register, byte baseRegister, byte coprocessorId, uint value)
    {
        _kind = (byte)kind;
        _register = register;
        _baseRegister = baseRegister;
        _coprocessorId = coprocessorId;
        _value = value;
    }

    public R3000aOperandKind Kind => (R3000aOperandKind)_kind;
    public byte Register => _register;
    public byte BaseRegister => _baseRegister;
    public byte CoprocessorId => _coprocessorId;
    public uint Value => _value;

    public static R3000aOperand CreateRegister(byte register)
    {
        EnsureRegisterInRange(register);
        return new R3000aOperand(R3000aOperandKind.Register, register, 0, 0, 0);
    }

    public static R3000aOperand CreateImmediate(ushort immediate)
    {
        return new R3000aOperand(R3000aOperandKind.Immediate, 0, 0, 0, immediate);
    }

    public static R3000aOperand CreateMemoryOffset(byte baseRegister, ushort offset)
    {
        EnsureRegisterInRange(baseRegister);
        return new R3000aOperand(R3000aOperandKind.MemoryOffset, 0, baseRegister, 0, offset);
    }

    public static R3000aOperand CreateShamt(byte shamt)
    {
        if (shamt > MaxShamt)
        {
            throw new ArgumentOutOfRangeException(nameof(shamt), shamt, "Shift amount must be a 5-bit value.");
        }

        return new R3000aOperand(R3000aOperandKind.Shamt, 0, 0, 0, shamt);
    }

    public static R3000aOperand CreateJumpIndex(uint instrIndex)
    {
        if (instrIndex > MaxJumpIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(instrIndex), instrIndex, "Jump index must be a 26-bit value.");
        }

        return new R3000aOperand(R3000aOperandKind.JumpIndex, 0, 0, 0, instrIndex);
    }

    public static R3000aOperand CreateCopReg(byte coprocessorId, byte registerNumber)
    {
        EnsureCoprocessorIdInRange(coprocessorId);
        EnsureRegisterInRange(registerNumber);
        return new R3000aOperand(R3000aOperandKind.CopReg, registerNumber, 0, coprocessorId, 0);
    }

    private static void EnsureRegisterInRange(byte register)
    {
        if (register > MaxRegisterNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(register), register, "GPR number must be within [0, 31].");
        }
    }

    private static void EnsureCoprocessorIdInRange(byte coprocessorId)
    {
        if (coprocessorId > MaxCoprocessorId)
        {
            throw new ArgumentOutOfRangeException(nameof(coprocessorId), coprocessorId, "Coprocessor id must be within [0, 3].");
        }
    }
}
