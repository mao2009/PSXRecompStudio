using System.Runtime.InteropServices;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Cpu;

[Domain]
[StructLayout(LayoutKind.Sequential)]
public readonly record struct R3000aInstruction
{
    public const int MaxOperandCount = 3;

    private readonly uint _encodedWord;
    private readonly R3000aOpcode _opcode;
    private readonly R3000aInstructionFormat _format;
    private readonly R3000aControlFlowKind _controlFlow;
    private readonly R3000aDelaySlotKind _delaySlot;
    private readonly R3000aOperand _operand0;
    private readonly R3000aOperand _operand1;
    private readonly R3000aOperand _operand2;
    private readonly R3000aLinkInfo _linkInfo;
    private readonly R3000aLoadDelayInfo _loadDelayInfo;
    private readonly byte _operandCount;
    private readonly R3000aCopInfo _copInfo;

    public R3000aInstruction(
        uint encodedWord,
        R3000aOpcode opcode,
        R3000aInstructionFormat format,
        R3000aOperand operand0,
        R3000aOperand operand1,
        R3000aOperand operand2,
        byte operandCount,
        R3000aControlFlowKind controlFlow = R3000aControlFlowKind.Sequential,
        R3000aDelaySlotKind delaySlot = R3000aDelaySlotKind.None,
        R3000aLinkInfo linkInfo = default,
        R3000aLoadDelayInfo loadDelayInfo = default,
        R3000aCopInfo copInfo = default)
    {
        if (operandCount > MaxOperandCount)
        {
            throw new ArgumentOutOfRangeException(nameof(operandCount), operandCount, "An instruction holds at most 3 operands.");
        }

        _encodedWord = encodedWord;
        _opcode = opcode;
        _format = format;
        _controlFlow = controlFlow;
        _delaySlot = delaySlot;
        _operand0 = operand0;
        _operand1 = operand1;
        _operand2 = operand2;
        _linkInfo = linkInfo;
        _loadDelayInfo = loadDelayInfo;
        _operandCount = operandCount;
        _copInfo = copInfo;
    }

    public uint EncodedWord => _encodedWord;
    public R3000aOpcode Opcode => _opcode;
    public R3000aInstructionFormat Format => _format;
    public R3000aControlFlowKind ControlFlow => _controlFlow;
    public R3000aDelaySlotKind DelaySlot => _delaySlot;
    public R3000aOperand Operand0 => _operand0;
    public R3000aOperand Operand1 => _operand1;
    public R3000aOperand Operand2 => _operand2;
    public int OperandCount => _operandCount;
    public R3000aLinkInfo LinkInfo => _linkInfo;
    public R3000aLoadDelayInfo LoadDelayInfo => _loadDelayInfo;
    public R3000aCopInfo CopInfo => _copInfo;

    public R3000aOperand GetOperand(int index)
    {
        if ((uint)index >= (uint)_operandCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Operand index must be less than OperandCount.");
        }

        return index switch
        {
            0 => _operand0,
            1 => _operand1,
            _ => _operand2,
        };
    }
}
