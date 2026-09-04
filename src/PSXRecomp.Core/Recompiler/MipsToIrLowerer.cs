using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Core.Recompiler;

/// <summary>
/// Translates decoded R3000A instructions into the IR blocks of
/// <see cref="RecompilerContract"/>-defined types.
/// <para>
/// Straight-line instructions lower to one block each, terminating in a success
/// exit with the sequential next PC (the Phase 2A relation, kept unchanged so an
/// existing program serializes identically).
/// </para>
/// <para>
/// A control-transfer instruction owns a branch delay slot (ADR-004,
/// <c>docs/cpu/pipeline.md</c>): the following instruction always retires before
/// the transfer is applied. Such an instruction therefore never lowers on its
/// own — <see cref="Lower"/> rejects it — and
/// <see cref="LowerControlTransfer"/> fuses the control instruction and its delay
/// slot into a single block whose operations are, in order, the transfer's
/// operand reads and condition, then the delay-slot instruction's operations,
/// then the exit that carries the flow. Reading the branch operands first is what
/// makes a delay slot that overwrites a condition register harmless, exactly as
/// on hardware.
/// </para>
/// </summary>
[Domain]
public static class MipsToIrLowerer
{
    private const uint InstructionSize = 4;

    /// <summary>
    /// Lowers one straight-line instruction into its own block.
    /// An instruction that owns a delay slot is rejected here; use
    /// <see cref="LowerControlTransfer"/> for it.
    /// </summary>
    public static MipsToIrLoweringResult Lower(R3000aInstruction instruction, uint entryPc)
    {
        if (instruction.DelaySlot != R3000aDelaySlotKind.None)
        {
            return MipsToIrLoweringResult.Unsupported(
                instruction.Opcode,
                RecompilerIrDiagnosticCode.InvalidFlow,
                $"Opcode '{instruction.Opcode}' owns a branch delay slot and cannot be lowered as a standalone block; " +
                "lower it together with its delay-slot instruction via LowerControlTransfer.");
        }

        var builder = new BlockBuilder();
        var failure = TryEmitInstruction(builder, instruction);
        if (failure is not null)
        {
            return failure;
        }

        var exit = new RecompilerIrExit(
            RecompilerIrTerminationReason.Success,
            unchecked(entryPc + InstructionSize));
        return MipsToIrLoweringResult.Success(new RecompilerIrBlock(entryPc, builder.Operations, exit));
    }

    /// <summary>
    /// Lowers a control-transfer instruction together with the instruction in its
    /// delay slot into a single block.
    /// <list type="bullet">
    /// <item>BEQ / BNE — <c>CompareEqual</c> / <c>CompareNotEqual</c> over the two
    /// operand registers produces the 0/1 condition consumed by a
    /// <see cref="RecompilerIrFlowKind.Branch"/> flow; the taken target comes from
    /// <see cref="R3000aBranchSemantics"/> and the not-taken successor is the
    /// exit's next PC, <c>entryPc + 8</c> (the instruction after the delay slot).</item>
    /// <item>J — a <see cref="RecompilerIrFlowKind.Jump"/> flow carrying the target
    /// from <see cref="R3000aJumpSemantics"/>; a jump exit carries no next PC.</item>
    /// <item>JR — the delay slot retires and the block terminates with
    /// <see cref="RecompilerIrTerminationReason.UnresolvedIndirectFlow"/>: the
    /// target is a runtime register value, which <see cref="RecompilerIrFlow.Target"/>
    /// (a static address) cannot express. The block states the frontier rather
    /// than inventing a transfer.</item>
    /// </list>
    /// Everything else — JAL, JALR, and the compare-with-zero branches — is
    /// reported unsupported rather than approximated.
    /// </summary>
    /// <param name="control">The control-transfer instruction, at <paramref name="entryPc"/>.</param>
    /// <param name="entryPc">The address of <paramref name="control"/>.</param>
    /// <param name="delaySlot">The instruction decoded at <c>entryPc + 4</c>.</param>
    public static MipsToIrLoweringResult LowerControlTransfer(
        R3000aInstruction control, uint entryPc, R3000aInstruction delaySlot)
    {
        if (control.DelaySlot == R3000aDelaySlotKind.None)
        {
            return MipsToIrLoweringResult.Unsupported(
                control.Opcode,
                RecompilerIrDiagnosticCode.InvalidFlow,
                $"Opcode '{control.Opcode}' does not own a branch delay slot; lower it with Lower instead.");
        }

        if (delaySlot.DelaySlot != R3000aDelaySlotKind.None)
        {
            return MipsToIrLoweringResult.Unsupported(
                delaySlot.Opcode,
                RecompilerIrDiagnosticCode.InvalidFlow,
                $"A control-transfer instruction ('{delaySlot.Opcode}') in the delay slot of '{control.Opcode}' is " +
                "UNPREDICTABLE on MIPS I (docs/cpu/pipeline.md) and is not lowered.");
        }

        if (delaySlot.LoadDelayInfo.ProducesLoadDelay)
        {
            return MipsToIrLoweringResult.Unsupported(
                delaySlot.Opcode,
                RecompilerIrDiagnosticCode.InvalidMemoryAccess,
                $"A load ('{delaySlot.Opcode}') in the delay slot of '{control.Opcode}' leaves its load-delay shadow on a " +
                "successor reached through the transfer, which this lowering stage cannot check or represent.");
        }

        return control.Opcode switch
        {
            R3000aOpcode.Beq => LowerConditionalBranch(control, entryPc, delaySlot, RecompilerIrOperationKind.CompareEqual),
            R3000aOpcode.Bne => LowerConditionalBranch(control, entryPc, delaySlot, RecompilerIrOperationKind.CompareNotEqual),
            R3000aOpcode.J => LowerDirectJump(control, entryPc, delaySlot),
            R3000aOpcode.Jr => LowerJumpRegister(control, entryPc, delaySlot),
            R3000aOpcode.Jal or R3000aOpcode.Jalr => MipsToIrLoweringResult.Unsupported(
                control.Opcode,
                RecompilerIrDiagnosticCode.ReservedFlow,
                $"Opcode '{control.Opcode}' is a call: it links a return address and transfers control. " +
                "RecompilerIrFlowKind.Call is a reserved extension point, and lowering a call to a plain Jump would " +
                "erase the return relation, so it is deferred to the stage that defines Call/Return."),
            _ => MipsToIrLoweringResult.Unsupported(
                control.Opcode,
                RecompilerIrDiagnosticCode.InvalidFlow,
                $"Control-transfer opcode '{control.Opcode}' is not supported by this lowering stage."),
        };
    }

    /// <summary>
    /// Lowers a linear instruction stream into a program.
    /// <para>
    /// A control-transfer instruction consumes the following entry as its delay
    /// slot, so the pair yields one block. Every other instruction yields one
    /// block. The stream is rejected — never silently approximated — when a
    /// control transfer has no delay-slot entry, when the delay-slot entry is not
    /// at <c>pc + 4</c>, when an instruction is unsupported, or when an R3000A
    /// load delay would be architecturally observable (see
    /// <see cref="EnsureNoObservableLoadDelay"/>).
    /// </para>
    /// </summary>
    public static RecompilerIrProgram LowerProgram(IReadOnlyList<(R3000aInstruction Instruction, uint EntryPc)> instructions)
    {
        ArgumentNullException.ThrowIfNull(instructions);

        var blocks = new List<RecompilerIrBlock>();
        for (var i = 0; i < instructions.Count;)
        {
            var (instruction, entryPc) = instructions[i];

            if (instruction.DelaySlot != R3000aDelaySlotKind.None)
            {
                if (i + 1 >= instructions.Count)
                {
                    throw new InvalidOperationException(
                        $"Cannot build program: the control-transfer instruction at PC 0x{entryPc:X8} ({instruction.Opcode}) " +
                        "has no delay-slot instruction. Its delay slot always retires, so the pair cannot be lowered apart.");
                }

                var (delaySlot, delaySlotPc) = instructions[i + 1];
                var expectedDelaySlotPc = unchecked(entryPc + InstructionSize);
                if (delaySlotPc != expectedDelaySlotPc)
                {
                    throw new InvalidOperationException(
                        $"Cannot build program: the delay slot of the instruction at PC 0x{entryPc:X8} ({instruction.Opcode}) " +
                        $"must be at PC 0x{expectedDelaySlotPc:X8}, but the next entry is at PC 0x{delaySlotPc:X8}.");
                }

                var fused = LowerControlTransfer(instruction, entryPc, delaySlot);
                if (!fused.IsSupported || fused.Block is null)
                {
                    throw new InvalidOperationException(
                        $"Cannot build program: the control transfer at PC 0x{entryPc:X8} ({instruction.Opcode}) is not supported. " +
                        $"Diagnostic: [{fused.DiagnosticCode}] {fused.DiagnosticMessage}");
                }

                blocks.Add(fused.Block);
                i += 2;
                continue;
            }

            EnsureNoObservableLoadDelay(instructions, i);

            var result = Lower(instruction, entryPc);
            if (!result.IsSupported || result.Block is null)
            {
                throw new InvalidOperationException(
                    $"Cannot build program: instruction at PC 0x{entryPc:X8} ({instruction.Opcode}) is not supported. " +
                    $"Diagnostic: [{result.DiagnosticCode}] {result.DiagnosticMessage}");
            }

            blocks.Add(result.Block);
            i++;
        }

        return new RecompilerIrProgram(blocks);
    }

    /// <summary>
    /// Rejects a stream in which the R3000A load delay is architecturally
    /// observable. A load's target register keeps its previous value for exactly
    /// one instruction (ADR-004); this stage commits the loaded value with an
    /// immediate <c>WriteGpr</c>, which is equivalent only while the delay-slot
    /// instruction does not read that register. A later write to the same
    /// register — including a second load — cancels the pending value on hardware
    /// too, so those cases stay equivalent and are allowed.
    /// </summary>
    private static void EnsureNoObservableLoadDelay(
        IReadOnlyList<(R3000aInstruction Instruction, uint EntryPc)> instructions, int index)
    {
        var (load, loadPc) = instructions[index];
        if (!load.LoadDelayInfo.ProducesLoadDelay)
        {
            return;
        }

        // GPR[0] is immutable, so a delayed write to it is never observable.
        var target = load.LoadDelayInfo.TargetRegister;
        if (target == 0 || index + 1 >= instructions.Count)
        {
            return;
        }

        var (next, nextPc) = instructions[index + 1];
        if (nextPc != unchecked(loadPc + InstructionSize))
        {
            // The next entry is not the architectural load-delay slot.
            return;
        }

        if (!TryGetSourceRegisters(next, out var sources) || Array.IndexOf(sources, target) < 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Cannot build program: the load at PC 0x{loadPc:X8} ({load.Opcode}) targets GPR{target}, and the " +
            $"load-delay-slot instruction at PC 0x{nextPc:X8} ({next.Opcode}) reads it. Diagnostic: " +
            $"[{RecompilerIrDiagnosticCode.InvalidMemoryAccess}] the R3000A load delay makes that read observe the " +
            "pre-load value, which this lowering stage does not represent.");
    }

    /// <summary>
    /// Reports the GPRs an instruction reads, for the opcodes this stage lowers.
    /// Returns <see langword="false"/> for any other opcode, which then fails on
    /// its own lowering rather than through a load-delay diagnostic.
    /// </summary>
    private static bool TryGetSourceRegisters(R3000aInstruction instruction, out byte[] sources)
    {
        switch (instruction.Opcode)
        {
            case R3000aOpcode.Sll when IsNop(instruction):
            case R3000aOpcode.Lui:
            case R3000aOpcode.J:
                sources = Array.Empty<byte>();
                return true;
            case R3000aOpcode.Sll:
            case R3000aOpcode.Srl:
            case R3000aOpcode.Sra:
                sources = new[] { instruction.Operand1.Register };
                return true;
            case R3000aOpcode.Addu:
            case R3000aOpcode.Subu:
            case R3000aOpcode.And:
            case R3000aOpcode.Or:
            case R3000aOpcode.Xor:
            case R3000aOpcode.Nor:
                sources = new[] { instruction.Operand1.Register, instruction.Operand2.Register };
                return true;
            case R3000aOpcode.Addiu:
                sources = new[] { instruction.Operand1.Register };
                return true;
            case R3000aOpcode.Lb:
            case R3000aOpcode.Lbu:
            case R3000aOpcode.Lh:
            case R3000aOpcode.Lhu:
            case R3000aOpcode.Lw:
                sources = new[] { instruction.Operand1.BaseRegister };
                return true;
            case R3000aOpcode.Sb:
            case R3000aOpcode.Sh:
            case R3000aOpcode.Sw:
                sources = new[] { instruction.Operand1.BaseRegister, instruction.Operand0.Register };
                return true;
            case R3000aOpcode.Beq:
            case R3000aOpcode.Bne:
                sources = new[] { instruction.Operand0.Register, instruction.Operand1.Register };
                return true;
            case R3000aOpcode.Jr:
                sources = new[] { instruction.Operand0.Register };
                return true;
            default:
                sources = Array.Empty<byte>();
                return false;
        }
    }

    private static MipsToIrLoweringResult LowerConditionalBranch(
        R3000aInstruction control, uint entryPc, R3000aInstruction delaySlot, RecompilerIrOperationKind compareKind)
    {
        if (!R3000aBranchSemantics.TryGetBranchTarget(control, entryPc, out var target))
        {
            return MipsToIrLoweringResult.Unsupported(
                control.Opcode,
                RecompilerIrDiagnosticCode.InvalidFlow,
                $"The branch target of '{control.Opcode}' at PC 0x{entryPc:X8} could not be resolved from the decoded operands.");
        }

        var builder = new BlockBuilder();

        // The condition is evaluated from the register values as they stand
        // before the delay slot retires, so these reads must precede it.
        var left = builder.ReadGpr(control.Operand0.Register);
        var right = builder.ReadGpr(control.Operand1.Register);
        var condition = builder.Binary(compareKind, left, right);

        var failure = TryEmitDelaySlot(builder, control, delaySlot);
        if (failure is not null)
        {
            return failure;
        }

        var exit = new RecompilerIrExit(
            RecompilerIrTerminationReason.Success,
            nextPc: unchecked(entryPc + (2 * InstructionSize)),
            flow: new RecompilerIrFlow(RecompilerIrFlowKind.Branch, target, condition));
        return MipsToIrLoweringResult.Success(new RecompilerIrBlock(entryPc, builder.Operations, exit));
    }

    private static MipsToIrLoweringResult LowerDirectJump(
        R3000aInstruction control, uint entryPc, R3000aInstruction delaySlot)
    {
        if (!R3000aJumpSemantics.TryGetJumpTarget(control, entryPc, out var target))
        {
            return MipsToIrLoweringResult.Unsupported(
                control.Opcode,
                RecompilerIrDiagnosticCode.InvalidFlow,
                $"The jump target of '{control.Opcode}' at PC 0x{entryPc:X8} could not be resolved from the decoded operands.");
        }

        var builder = new BlockBuilder();
        var failure = TryEmitDelaySlot(builder, control, delaySlot);
        if (failure is not null)
        {
            return failure;
        }

        var exit = new RecompilerIrExit(
            RecompilerIrTerminationReason.Success,
            nextPc: null,
            flow: new RecompilerIrFlow(RecompilerIrFlowKind.Jump, target));
        return MipsToIrLoweringResult.Success(new RecompilerIrBlock(entryPc, builder.Operations, exit));
    }

    private static MipsToIrLoweringResult LowerJumpRegister(
        R3000aInstruction control, uint entryPc, R3000aInstruction delaySlot)
    {
        var builder = new BlockBuilder();
        var failure = TryEmitDelaySlot(builder, control, delaySlot);
        if (failure is not null)
        {
            return failure;
        }

        // The delay slot retires, then control leaves the program through an
        // address held in a register. No IR flow can carry a runtime target, so
        // the block terminates and says exactly that.
        var exit = new RecompilerIrExit(RecompilerIrTerminationReason.UnresolvedIndirectFlow);
        return MipsToIrLoweringResult.Success(new RecompilerIrBlock(entryPc, builder.Operations, exit));
    }

    private static MipsToIrLoweringResult? TryEmitDelaySlot(
        BlockBuilder builder, R3000aInstruction control, R3000aInstruction delaySlot)
    {
        var failure = TryEmitInstruction(builder, delaySlot);
        if (failure is null)
        {
            return null;
        }

        return MipsToIrLoweringResult.Unsupported(
            delaySlot.Opcode,
            failure.DiagnosticCode ?? RecompilerIrDiagnosticCode.InvalidOperationShape,
            $"The delay-slot instruction of '{control.Opcode}' could not be lowered: {failure.DiagnosticMessage}");
    }

    /// <summary>
    /// Appends the operations of one non-control instruction to the block being
    /// built. Returns <see langword="null"/> on success, or the unsupported
    /// result to propagate.
    /// </summary>
    private static MipsToIrLoweringResult? TryEmitInstruction(BlockBuilder builder, R3000aInstruction instruction)
    {
        switch (instruction.Opcode)
        {
            case R3000aOpcode.Sll when IsNop(instruction):
                builder.Nop();
                return null;
            case R3000aOpcode.Sll:
                EmitShift(builder, instruction, RecompilerIrOperationKind.ShiftLeftLogical);
                return null;
            case R3000aOpcode.Srl:
                EmitShift(builder, instruction, RecompilerIrOperationKind.ShiftRightLogical);
                return null;
            case R3000aOpcode.Sra:
                EmitShift(builder, instruction, RecompilerIrOperationKind.ShiftRightArithmetic);
                return null;
            case R3000aOpcode.Addu:
                EmitThreeRegisterArithmetic(builder, instruction, RecompilerIrOperationKind.Add);
                return null;
            case R3000aOpcode.Subu:
                EmitThreeRegisterArithmetic(builder, instruction, RecompilerIrOperationKind.Subtract);
                return null;
            case R3000aOpcode.And:
                EmitThreeRegisterArithmetic(builder, instruction, RecompilerIrOperationKind.And);
                return null;
            case R3000aOpcode.Or:
                EmitThreeRegisterArithmetic(builder, instruction, RecompilerIrOperationKind.Or);
                return null;
            case R3000aOpcode.Xor:
                EmitThreeRegisterArithmetic(builder, instruction, RecompilerIrOperationKind.Xor);
                return null;
            case R3000aOpcode.Nor:
                EmitThreeRegisterArithmetic(builder, instruction, RecompilerIrOperationKind.Nor);
                return null;
            case R3000aOpcode.Addiu:
                EmitAddiu(builder, instruction);
                return null;
            case R3000aOpcode.Lui:
                EmitLui(builder, instruction);
                return null;
            case R3000aOpcode.Lb:
                return EmitLoad(builder, instruction, RecompilerIrOperationKind.Load8, signExtendShift: 24);
            case R3000aOpcode.Lbu:
                return EmitLoad(builder, instruction, RecompilerIrOperationKind.Load8, signExtendShift: 0);
            case R3000aOpcode.Lh:
                return EmitLoad(builder, instruction, RecompilerIrOperationKind.Load16, signExtendShift: 16);
            case R3000aOpcode.Lhu:
                return EmitLoad(builder, instruction, RecompilerIrOperationKind.Load16, signExtendShift: 0);
            case R3000aOpcode.Lw:
                return EmitLoad(builder, instruction, RecompilerIrOperationKind.Load32, signExtendShift: 0);
            case R3000aOpcode.Sb:
                return EmitStore(builder, instruction, RecompilerIrOperationKind.Store8);
            case R3000aOpcode.Sh:
                return EmitStore(builder, instruction, RecompilerIrOperationKind.Store16);
            case R3000aOpcode.Sw:
                return EmitStore(builder, instruction, RecompilerIrOperationKind.Store32);
            default:
                return MipsToIrLoweringResult.Unsupported(
                    instruction.Opcode,
                    RecompilerIrDiagnosticCode.InvalidOperationShape,
                    $"Opcode '{instruction.Opcode}' is not supported by this lowering stage.");
        }
    }

    private static bool IsNop(R3000aInstruction instruction) =>
        instruction.Operand0.Register == 0 &&
        instruction.Operand1.Register == 0 &&
        instruction.Operand2.Value == 0;

    private static void EmitThreeRegisterArithmetic(
        BlockBuilder builder, R3000aInstruction instruction, RecompilerIrOperationKind operationKind)
    {
        var left = builder.ReadGpr(instruction.Operand1.Register);
        var right = builder.ReadGpr(instruction.Operand2.Register);
        var result = builder.Binary(operationKind, left, right);
        builder.WriteGpr(instruction.Operand0.Register, result);
    }

    private static void EmitAddiu(BlockBuilder builder, R3000aInstruction instruction)
    {
        var left = builder.ReadGpr(instruction.Operand1.Register);
        var immediate = builder.Constant(SignExtend16To32((ushort)instruction.Operand2.Value));
        var result = builder.Binary(RecompilerIrOperationKind.Add, left, immediate);
        builder.WriteGpr(instruction.Operand0.Register, result);
    }

    private static void EmitLui(BlockBuilder builder, R3000aInstruction instruction)
    {
        var immediate = builder.Constant((uint)(ushort)instruction.Operand1.Value << 16);
        builder.WriteGpr(instruction.Operand0.Register, immediate);
    }

    private static void EmitShift(
        BlockBuilder builder, R3000aInstruction instruction, RecompilerIrOperationKind operationKind)
    {
        var source = builder.ReadGpr(instruction.Operand1.Register);
        var result = builder.Shift(operationKind, source, (byte)instruction.Operand2.Value);
        builder.WriteGpr(instruction.Operand0.Register, result);
    }

    /// <summary>
    /// Lowers a load: effective address, the load itself, and — for the
    /// sign-extending forms — the shift pair that widens the accessed value.
    /// <paramref name="signExtendShift"/> is 0 for LW and for the zero-extending
    /// forms (LBU/LHU), which use the loaded value directly.
    /// </summary>
    private static MipsToIrLoweringResult? EmitLoad(
        BlockBuilder builder, R3000aInstruction instruction, RecompilerIrOperationKind loadKind, byte signExtendShift)
    {
        var memory = instruction.Operand1;
        if (memory.Kind != R3000aOperandKind.MemoryOffset)
        {
            return UnsupportedMemoryOperand(instruction);
        }

        var address = EmitEffectiveAddress(builder, memory);
        var loaded = builder.Load(loadKind, address);

        var value = loaded;
        if (signExtendShift != 0)
        {
            // The IR load yields the accessed byte/halfword zero-extended into a
            // 32-bit value; MIPS sign extension is expressed with the generic
            // shift operations rather than a signedness flag on the load.
            var shiftedLeft = builder.Shift(RecompilerIrOperationKind.ShiftLeftLogical, loaded, signExtendShift);
            value = builder.Shift(RecompilerIrOperationKind.ShiftRightArithmetic, shiftedLeft, signExtendShift);
        }

        builder.WriteGpr(instruction.Operand0.Register, value);
        return null;
    }

    private static MipsToIrLoweringResult? EmitStore(
        BlockBuilder builder, R3000aInstruction instruction, RecompilerIrOperationKind storeKind)
    {
        var memory = instruction.Operand1;
        if (memory.Kind != R3000aOperandKind.MemoryOffset)
        {
            return UnsupportedMemoryOperand(instruction);
        }

        var address = EmitEffectiveAddress(builder, memory);
        var value = builder.ReadGpr(instruction.Operand0.Register);
        builder.Store(storeKind, address, value);
        return null;
    }

    /// <summary>
    /// Emits <c>base + sign-extended offset</c>, the guest 32-bit effective
    /// address consumed by a load or store operation.
    /// </summary>
    private static int EmitEffectiveAddress(BlockBuilder builder, R3000aOperand memory)
    {
        var baseValue = builder.ReadGpr(memory.BaseRegister);
        var offset = builder.Constant(SignExtend16To32((ushort)memory.Value));
        return builder.Binary(RecompilerIrOperationKind.Add, baseValue, offset);
    }

    private static MipsToIrLoweringResult UnsupportedMemoryOperand(R3000aInstruction instruction) =>
        MipsToIrLoweringResult.Unsupported(
            instruction.Opcode,
            RecompilerIrDiagnosticCode.InvalidMemoryAccess,
            $"Opcode '{instruction.Opcode}' requires a base+offset memory operand, but the decoder produced " +
            $"'{instruction.Operand1.Kind}'.");

    private static uint SignExtend16To32(ushort value) =>
        (value & 0x8000) != 0 ? (uint)(value | 0xFFFF0000) : value;

    /// <summary>
    /// Accumulates a block's operations and hands out the block-local value ids.
    /// One builder spans one IR block, so a fused control-transfer block numbers
    /// its delay-slot values after the transfer's own.
    /// </summary>
    private sealed class BlockBuilder
    {
        private readonly List<RecompilerIrOperation> _operations = [];
        private int _nextValueId;

        public IReadOnlyList<RecompilerIrOperation> Operations => _operations;

        public void Nop() => _operations.Add(new RecompilerIrOperation(RecompilerIrOperationKind.Nop));

        public int ReadGpr(byte register) =>
            AddWithResult(id => new RecompilerIrOperation(
                RecompilerIrOperationKind.ReadGpr, resultValueId: id, register: register));

        public int Constant(uint value) =>
            AddWithResult(id => new RecompilerIrOperation(
                RecompilerIrOperationKind.Constant, resultValueId: id, immediate: value));

        public int Binary(RecompilerIrOperationKind kind, int inputValueA, int inputValueB) =>
            AddWithResult(id => new RecompilerIrOperation(
                kind, resultValueId: id, inputValueA: inputValueA, inputValueB: inputValueB));

        public int Shift(RecompilerIrOperationKind kind, int inputValueA, byte shiftAmount) =>
            AddWithResult(id => new RecompilerIrOperation(
                kind, resultValueId: id, inputValueA: inputValueA, shiftAmount: shiftAmount));

        public int Load(RecompilerIrOperationKind kind, int address) =>
            AddWithResult(id => new RecompilerIrOperation(kind, resultValueId: id, inputValueA: address));

        public void Store(RecompilerIrOperationKind kind, int address, int value) =>
            _operations.Add(new RecompilerIrOperation(kind, inputValueA: address, inputValueB: value));

        /// <summary>
        /// Writes a GPR, except GPR[0]: it is immutable, so the architectural
        /// write is discarded rather than emitted (the validator rejects a
        /// <c>WriteGpr</c> to register zero).
        /// </summary>
        public void WriteGpr(byte register, int value)
        {
            if (register == 0)
            {
                return;
            }

            _operations.Add(new RecompilerIrOperation(
                RecompilerIrOperationKind.WriteGpr, inputValueA: value, register: register));
        }

        private int AddWithResult(Func<int, RecompilerIrOperation> factory)
        {
            var id = _nextValueId++;
            _operations.Add(factory(id));
            return id;
        }
    }
}
