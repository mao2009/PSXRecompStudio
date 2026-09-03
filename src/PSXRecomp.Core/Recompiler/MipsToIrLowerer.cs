using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Core.Recompiler;

[Domain]
public static class MipsToIrLowerer
{
    private const uint InstructionSize = 4;

    public static MipsToIrLoweringResult Lower(R3000aInstruction instruction, uint entryPc)
    {
        var nextPc = entryPc + InstructionSize;
        return instruction.Opcode switch
        {
            R3000aOpcode.Sll when IsNop(instruction) => MipsToIrLoweringResult.Success(LowerNop(entryPc, nextPc)),
            R3000aOpcode.Sll => MipsToIrLoweringResult.Success(LowerShift(entryPc, nextPc, instruction, RecompilerIrOperationKind.ShiftLeftLogical)),
            R3000aOpcode.Srl => MipsToIrLoweringResult.Success(LowerShift(entryPc, nextPc, instruction, RecompilerIrOperationKind.ShiftRightLogical)),
            R3000aOpcode.Sra => MipsToIrLoweringResult.Success(LowerShift(entryPc, nextPc, instruction, RecompilerIrOperationKind.ShiftRightArithmetic)),
            R3000aOpcode.Addu => MipsToIrLoweringResult.Success(LowerThreeRegisterArithmetic(entryPc, nextPc, instruction, RecompilerIrOperationKind.Add)),
            R3000aOpcode.Subu => MipsToIrLoweringResult.Success(LowerThreeRegisterArithmetic(entryPc, nextPc, instruction, RecompilerIrOperationKind.Subtract)),
            R3000aOpcode.And => MipsToIrLoweringResult.Success(LowerThreeRegisterArithmetic(entryPc, nextPc, instruction, RecompilerIrOperationKind.And)),
            R3000aOpcode.Or => MipsToIrLoweringResult.Success(LowerThreeRegisterArithmetic(entryPc, nextPc, instruction, RecompilerIrOperationKind.Or)),
            R3000aOpcode.Xor => MipsToIrLoweringResult.Success(LowerThreeRegisterArithmetic(entryPc, nextPc, instruction, RecompilerIrOperationKind.Xor)),
            R3000aOpcode.Nor => MipsToIrLoweringResult.Success(LowerThreeRegisterArithmetic(entryPc, nextPc, instruction, RecompilerIrOperationKind.Nor)),
            R3000aOpcode.Addiu => MipsToIrLoweringResult.Success(LowerAddiu(entryPc, nextPc, instruction)),
            R3000aOpcode.Lui => MipsToIrLoweringResult.Success(LowerLui(entryPc, nextPc, instruction)),
            _ => MipsToIrLoweringResult.Unsupported(
                instruction.Opcode,
                RecompilerIrDiagnosticCode.InvalidOperationShape,
                $"Opcode '{instruction.Opcode}' is not supported in Phase 2A lowering."),
        };
    }

    public static RecompilerIrProgram LowerProgram(IReadOnlyList<(R3000aInstruction Instruction, uint EntryPc)> instructions)
    {
        var blocks = new List<RecompilerIrBlock>();
        for (var i = 0; i < instructions.Count; i++)
        {
            var (instruction, entryPc) = instructions[i];
            var result = Lower(instruction, entryPc);
            if (!result.IsSupported || result.Block is null)
            {
                throw new InvalidOperationException(
                    $"Cannot build program: instruction at PC 0x{entryPc:X8} ({instruction.Opcode}) is not supported. " +
                    $"Diagnostic: [{result.DiagnosticCode}] {result.DiagnosticMessage}");
            }

            blocks.Add(result.Block);
        }

        return new RecompilerIrProgram(blocks);
    }

    private static bool IsNop(R3000aInstruction instruction) =>
        instruction.Operand0.Register == 0 &&
        instruction.Operand1.Register == 0 &&
        instruction.Operand2.Value == 0;

    private static RecompilerIrBlock LowerNop(uint entryPc, uint nextPc) =>
        new(
            entryPc,
            new[] { new RecompilerIrOperation(RecompilerIrOperationKind.Nop) },
            new RecompilerIrExit(RecompilerIrTerminationReason.Success, nextPc));

    private static RecompilerIrBlock LowerThreeRegisterArithmetic(
        uint entryPc, uint nextPc, R3000aInstruction instruction, RecompilerIrOperationKind operationKind)
    {
        var rd = instruction.Operand0.Register;
        var rs = instruction.Operand1.Register;
        var rt = instruction.Operand2.Register;

        var ops = new List<RecompilerIrOperation>();
        int nextValueId = 0;

        int ReadSource(byte register)
        {
            var id = nextValueId++;
            ops.Add(new RecompilerIrOperation(
                RecompilerIrOperationKind.ReadGpr,
                resultValueId: id,
                register: register));
            return id;
        }

        var rsId = ReadSource(rs);
        var rtId = ReadSource(rt);

        var resultId = nextValueId++;
        ops.Add(new RecompilerIrOperation(
            operationKind,
            resultValueId: resultId,
            inputValueA: rsId,
            inputValueB: rtId));

        if (rd != 0)
        {
            ops.Add(new RecompilerIrOperation(
                RecompilerIrOperationKind.WriteGpr,
                inputValueA: resultId,
                register: rd));
        }

        return new RecompilerIrBlock(entryPc, ops, new RecompilerIrExit(RecompilerIrTerminationReason.Success, nextPc));
    }

    private static RecompilerIrBlock LowerAddiu(uint entryPc, uint nextPc, R3000aInstruction instruction)
    {
        var rt = instruction.Operand0.Register;
        var rs = instruction.Operand1.Register;
        var imm = (ushort)instruction.Operand2.Value;
        var signExtended = SignExtend16To32(imm);

        var ops = new List<RecompilerIrOperation>();
        int nextValueId = 0;

        var rsId = nextValueId++;
        ops.Add(new RecompilerIrOperation(
            RecompilerIrOperationKind.ReadGpr,
            resultValueId: rsId,
            register: rs));

        var immId = nextValueId++;
        ops.Add(new RecompilerIrOperation(
            RecompilerIrOperationKind.Constant,
            resultValueId: immId,
            immediate: signExtended));

        var resultId = nextValueId++;
        ops.Add(new RecompilerIrOperation(
            RecompilerIrOperationKind.Add,
            resultValueId: resultId,
            inputValueA: rsId,
            inputValueB: immId));

        if (rt != 0)
        {
            ops.Add(new RecompilerIrOperation(
                RecompilerIrOperationKind.WriteGpr,
                inputValueA: resultId,
                register: rt));
        }

        return new RecompilerIrBlock(entryPc, ops, new RecompilerIrExit(RecompilerIrTerminationReason.Success, nextPc));
    }

    private static RecompilerIrBlock LowerLui(uint entryPc, uint nextPc, R3000aInstruction instruction)
    {
        var rt = instruction.Operand0.Register;
        var imm = (uint)(ushort)instruction.Operand1.Value << 16;

        var ops = new List<RecompilerIrOperation>();
        int nextValueId = 0;

        var immId = nextValueId++;
        ops.Add(new RecompilerIrOperation(
            RecompilerIrOperationKind.Constant,
            resultValueId: immId,
            immediate: imm));

        if (rt != 0)
        {
            ops.Add(new RecompilerIrOperation(
                RecompilerIrOperationKind.WriteGpr,
                inputValueA: immId,
                register: rt));
        }

        return new RecompilerIrBlock(entryPc, ops, new RecompilerIrExit(RecompilerIrTerminationReason.Success, nextPc));
    }

    private static RecompilerIrBlock LowerShift(
        uint entryPc, uint nextPc, R3000aInstruction instruction, RecompilerIrOperationKind operationKind)
    {
        var rd = instruction.Operand0.Register;
        var rt = instruction.Operand1.Register;
        var shamt = (byte)instruction.Operand2.Value;

        var ops = new List<RecompilerIrOperation>();
        int nextValueId = 0;

        var rtId = nextValueId++;
        ops.Add(new RecompilerIrOperation(
            RecompilerIrOperationKind.ReadGpr,
            resultValueId: rtId,
            register: rt));

        var resultId = nextValueId++;
        ops.Add(new RecompilerIrOperation(
            operationKind,
            resultValueId: resultId,
            inputValueA: rtId,
            shiftAmount: shamt));

        if (rd != 0)
        {
            ops.Add(new RecompilerIrOperation(
                RecompilerIrOperationKind.WriteGpr,
                inputValueA: resultId,
                register: rd));
        }

        return new RecompilerIrBlock(entryPc, ops, new RecompilerIrExit(RecompilerIrTerminationReason.Success, nextPc));
    }

    private static uint SignExtend16To32(ushort value) =>
        (value & 0x8000) != 0 ? (uint)(value | 0xFFFF0000) : value;
}
