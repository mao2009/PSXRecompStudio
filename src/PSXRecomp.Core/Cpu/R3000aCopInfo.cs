using System.Runtime.InteropServices;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Cpu;

[Domain]
public enum R3000aCopOperationKind : byte
{
    None = 0,
    MoveFromCoprocessor = 1,
    MoveToCoprocessor = 2,
    MoveControlFromCoprocessor = 3,
    MoveControlToCoprocessor = 4,
    ExecuteCommand = 5,
    ReturnFromException = 6,
}

[Domain]
[StructLayout(LayoutKind.Sequential)]
public readonly record struct R3000aCopInfo
{
    public const byte MaxCoprocessorId = 3;
    public const byte MaxCopRegisterNumber = 31;
    public const uint MaxCommandValue = 0x01FFFFFF;

    private readonly byte _coprocessorId;
    private readonly byte _operation;
    private readonly byte _copRegisterNumber;
    private readonly uint _command;

    private R3000aCopInfo(byte coprocessorId, R3000aCopOperationKind operation, byte copRegisterNumber, uint command)
    {
        _coprocessorId = coprocessorId;
        _operation = (byte)operation;
        _copRegisterNumber = copRegisterNumber;
        _command = command;
    }

    public byte CoprocessorId => _coprocessorId;
    public R3000aCopOperationKind Operation => (R3000aCopOperationKind)_operation;
    public byte CopRegisterNumber => _copRegisterNumber;
    public uint Command => _command;

    public static R3000aCopInfo None => default;

    public static R3000aCopInfo CreateMoveFromCoprocessor(byte coprocessorId, byte copRegisterNumber)
    {
        EnsureCoprocessorIdInRange(coprocessorId);
        EnsureCopRegisterNumberInRange(copRegisterNumber);
        return new R3000aCopInfo(coprocessorId, R3000aCopOperationKind.MoveFromCoprocessor, copRegisterNumber, 0);
    }

    public static R3000aCopInfo CreateMoveToCoprocessor(byte coprocessorId, byte copRegisterNumber)
    {
        EnsureCoprocessorIdInRange(coprocessorId);
        EnsureCopRegisterNumberInRange(copRegisterNumber);
        return new R3000aCopInfo(coprocessorId, R3000aCopOperationKind.MoveToCoprocessor, copRegisterNumber, 0);
    }

    public static R3000aCopInfo CreateMoveControlFromCoprocessor(byte coprocessorId, byte copRegisterNumber)
    {
        EnsureCoprocessorIdInRange(coprocessorId);
        EnsureCopRegisterNumberInRange(copRegisterNumber);
        return new R3000aCopInfo(coprocessorId, R3000aCopOperationKind.MoveControlFromCoprocessor, copRegisterNumber, 0);
    }

    public static R3000aCopInfo CreateMoveControlToCoprocessor(byte coprocessorId, byte copRegisterNumber)
    {
        EnsureCoprocessorIdInRange(coprocessorId);
        EnsureCopRegisterNumberInRange(copRegisterNumber);
        return new R3000aCopInfo(coprocessorId, R3000aCopOperationKind.MoveControlToCoprocessor, copRegisterNumber, 0);
    }

    public static R3000aCopInfo CreateExecuteCommand(byte coprocessorId, uint cofun)
    {
        EnsureCoprocessorIdInRange(coprocessorId);
        if (cofun > MaxCommandValue)
        {
            throw new ArgumentOutOfRangeException(nameof(cofun), cofun, "The cofun field must be a 25-bit value.");
        }

        return new R3000aCopInfo(coprocessorId, R3000aCopOperationKind.ExecuteCommand, 0, cofun);
    }

    public static R3000aCopInfo CreateReturnFromException()
    {
        return new R3000aCopInfo(0, R3000aCopOperationKind.ReturnFromException, 0, 0);
    }

    private static void EnsureCoprocessorIdInRange(byte coprocessorId)
    {
        if (coprocessorId > MaxCoprocessorId)
        {
            throw new ArgumentOutOfRangeException(nameof(coprocessorId), coprocessorId, "Coprocessor id must be within [0, 3].");
        }
    }

    private static void EnsureCopRegisterNumberInRange(byte copRegisterNumber)
    {
        if (copRegisterNumber > MaxCopRegisterNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(copRegisterNumber), copRegisterNumber, "Coprocessor register number must be within [0, 31].");
        }
    }
}
