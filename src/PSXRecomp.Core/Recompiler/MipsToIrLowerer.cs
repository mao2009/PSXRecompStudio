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
/// operand reads, condition and link write, then the delay-slot instruction's
/// operations, then the exit that carries the flow. Reading the branch operands
/// first is what makes a delay slot that overwrites a condition register
/// harmless, exactly as on hardware.
/// </para>
/// <para>
/// A load owns a load delay slot in the same sense: its target register keeps its
/// previous value for exactly one instruction. When that is architecturally
/// observable, <see cref="LowerProgram"/> fuses the load with the instruction in
/// its load-delay slot and places the register commit at that instruction's
/// retirement point, so the observer reads the pre-load value. Neither delay is
/// left for a backend to synthesize.
/// </para>
/// </summary>
[Domain]
public static class MipsToIrLowerer
{
    private const uint InstructionSize = 4;

    /// <summary>
    /// CAUSE Excode for the BREAK instruction (Bp, docs/cpu/exceptions.md).
    /// Shared with the interpreter executor so both execution paths agree on
    /// which trap exception the lowering models (Issue #481).
    /// </summary>
    public const uint BreakExcode = 0x09;

    /// <summary>
    /// CAUSE Excode for the SYSCALL instruction (Sys, docs/cpu/exceptions.md).
    /// SYSCALL is lowered exactly like BREAK apart from this code (Issue #628).
    /// </summary>
    public const uint SyscallExcode = 0x08;

    /// <summary>
    /// Returns the CAUSE Excode of a synchronous trap instruction (SYSCALL/BREAK)
    /// this stage lowers as an exception exit, or <see langword="null"/>.
    /// </summary>
    public static uint? TrapExcode(R3000aOpcode opcode) => opcode switch
    {
        R3000aOpcode.Syscall => SyscallExcode,
        R3000aOpcode.Break => BreakExcode,
        _ => null,
    };

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

        if (TrapExcode(instruction.Opcode) is { } trapExcode)
        {
            // BREAK/SYSCALL raise a synchronous exception (Bp 0x09 / Sys 0x08) before any
            // later instruction retires; it reads and writes no registers, so an
            // empty operation list fully describes it. The block carries the
            // exception details so the host code generator can reproduce EPC/BD
            // exactly instead of leaving the runtime to re-derive them from the
            // dispatch PC (Issue #481).
            return MipsToIrLoweringResult.Success(new RecompilerIrBlock(
                entryPc,
                Array.Empty<RecompilerIrOperation>(),
                new RecompilerIrExit(
                    RecompilerIrTerminationReason.Exception,
                    exception: CreateTrapException(trapExcode, entryPc, inDelaySlot: false))));
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
    /// <item>JAL — the link write (<c>$ra = entryPc + 8</c>, from
    /// <see cref="R3000aLinkSemantics"/>) precedes the delay slot, exactly as the
    /// interpreter links before the transfer applies, and the block exits with a
    /// <see cref="RecompilerIrFlowKind.Call"/> flow whose target is the callee and
    /// whose next PC is the return address.</item>
    /// <item>BLEZ / BGTZ / BLTZ / BGEZ — a compare-with-zero condition built from
    /// <see cref="RecompilerIrOperationKind.CompareLessThanSigned"/> (and, for
    /// BGEZ/BLEZ, negated via <see cref="RecompilerIrOperationKind.CompareEqual"/>
    /// against 0) drives the same <see cref="RecompilerIrFlowKind.Branch"/> flow as
    /// BEQ/BNE.</item>
    /// <item>BLTZAL / BGEZAL — the same compare-with-zero condition as BLTZ/BGEZ,
    /// plus the unconditional <c>$ra = entryPc + 8</c> link write before the delay
    /// slot (the branch decision does not gate the link, per
    /// <c>PSXCpu::ExecBltzal</c>/<c>ExecBgezal</c>).</item>
    /// <item>JR / JALR — the target is a runtime register value, which
    /// <see cref="RecompilerIrFlow.Target"/> (a static address) cannot express, so
    /// the register is read before the delay slot retires and the block exits with
    /// <see cref="RecompilerIrTerminationReason.Success"/> and that value as
    /// <see cref="RecompilerIrExit.TargetValueId"/> (Issue #635). JALR performs its
    /// link write after reading the target register, so that <c>JALR rd, rd</c>
    /// keeps the pre-link target.</item>
    /// </list>
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

        var builder = new BlockBuilder();
        var failure = TryEmitControlTransfer(builder, control, entryPc, delaySlot, pendingLoad: null, out var exit);
        return failure ?? MipsToIrLoweringResult.Success(
            new RecompilerIrBlock(entryPc, builder.Operations, exit, retiredInstructionCount: 2));
    }

    /// <summary>
    /// Lowers a linear instruction stream into a program.
    /// <para>
    /// A control-transfer instruction consumes the following entry as its delay
    /// slot, so the pair yields one block. A load whose load-delay slot observes
    /// its target register consumes that instruction too (see
    /// <see cref="LowerObservedLoadDelay"/>). Every other instruction yields one
    /// block. The stream is rejected — never silently approximated — when a
    /// control transfer has no delay-slot entry, when the delay-slot entry is not
    /// at <c>pc + 4</c>, when an instruction is unsupported, or when a load delay
    /// falls outside what a fused block can represent.
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
                var delaySlot = RequireDelaySlot(instructions, i);
                blocks.Add(Require(LowerControlTransfer(instruction, entryPc, delaySlot), instruction, entryPc));
                i += 2;
                continue;
            }

            if (IsLoadDelayObservedByItsDelaySlot(instructions, i))
            {
                blocks.Add(LowerObservedLoadDelay(instructions, i, out var consumed));
                i += consumed;
                continue;
            }

            blocks.Add(Require(Lower(instruction, entryPc), instruction, entryPc));
            i++;
        }

        EnsureStaticTargetsAreBlockEntries(blocks, instructions);
        return new RecompilerIrProgram(blocks);
    }

    /// <summary>
    /// Rejects a stream in which a resolved transfer target lands inside a fused
    /// block instead of on its entry — a branch into a delay slot, or into the
    /// load-delay slot of a fused load. Such a target has no block to enter, and
    /// an execution boundary would read that as "control left the program" and
    /// silently stop. A target outside the lowered stream is a legitimate exit and
    /// is left alone.
    /// </summary>
    private static void EnsureStaticTargetsAreBlockEntries(
        IReadOnlyList<RecompilerIrBlock> blocks,
        IReadOnlyList<(R3000aInstruction Instruction, uint EntryPc)> instructions)
    {
        var loweredPcs = instructions.Select(entry => entry.EntryPc).ToHashSet();
        var entryPcs = blocks.Select(block => block.EntryPc).ToHashSet();

        foreach (var block in blocks)
        {
            var target = block.Exit.Flow?.Target;
            if (target is null || !loweredPcs.Contains(target.Value) || entryPcs.Contains(target.Value))
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Cannot build program: the transfer in the block at PC 0x{block.EntryPc:X8} targets PC " +
                $"0x{target.Value:X8}, which is lowered but is not a block entry. Diagnostic: " +
                $"[{RecompilerIrDiagnosticCode.InvalidFlow}] the target is inside a fused block — a delay slot or " +
                "a load-delay slot — which this lowering stage cannot enter.");
        }
    }

    /// <summary>
    /// Fuses a load with the instruction in its load-delay slot into one block,
    /// entered at the load's PC.
    /// <para>
    /// The R3000A commits a load into its target register at the retirement point
    /// of the following instruction (<c>UpdateLoadDelay</c> in
    /// <c>src/PSXRecomp.Native/src/psx_cpu_pipeline.cpp</c>), so that instruction reads the
    /// pre-load value. The fused block reproduces that ordering literally: the
    /// load's access, then the observer's operations, then the commit. When the
    /// observer itself writes the target register the commit is <em>omitted</em>,
    /// because on hardware that write cancels the pending load.
    /// </para>
    /// <para>
    /// When the observer is a control transfer, its own delay slot joins the block
    /// as well and the commit is placed between the transfer's operations and the
    /// delay slot's — the transfer reads the pre-load value, the delay-slot
    /// instruction (which retires after the commit) reads the loaded one.
    /// </para>
    /// </summary>
    private static RecompilerIrBlock LowerObservedLoadDelay(
        IReadOnlyList<(R3000aInstruction Instruction, uint EntryPc)> instructions, int index, out int consumed)
    {
        var (load, loadPc) = instructions[index];
        var (observer, observerPc) = instructions[index + 1];

        var builder = new BlockBuilder();
        var failure = TryEmitLoadValue(builder, load, mergeSource: null, out var loadedValue);
        if (failure is not null)
        {
            throw Unsupported(failure, load, loadPc);
        }

        // A write to the load's target register in the load-delay slot cancels the
        // pending load on hardware (PSXCpu::SetGPR / WriteRegDelayed), so the
        // commit is dropped rather than reordered.
        var target = load.LoadDelayInfo.TargetRegister;
        var writesTarget = TryGetDestinationRegister(observer, out var destination) && destination == target;
        var pendingLoad = writesTarget ? (PendingLoadCommit?)null : new PendingLoadCommit(target, loadedValue);

        if (observer.DelaySlot != R3000aDelaySlotKind.None)
        {
            var observerDelaySlot = RequireDelaySlot(instructions, index + 1);
            var transferFailure = TryEmitControlTransfer(
                builder, observer, observerPc, observerDelaySlot, pendingLoad, out var transferExit);
            if (transferFailure is not null)
            {
                throw Unsupported(transferFailure, observer, observerPc);
            }

            consumed = 3;
            return new RecompilerIrBlock(loadPc, builder.Operations, transferExit, retiredInstructionCount: 3);
        }

        // LWL/LWR to the load's own target merge into the still-pending value
        // (docs/cpu/pipeline.md "Special LWL/LWR Behavior", PSXCpu::ExecLwl/ExecLwr),
        // not into the committed register the other instructions observe.
        var observerFailure = writesTarget && observer.Opcode is R3000aOpcode.Lwl or R3000aOpcode.Lwr
            ? EmitLoad(builder, observer, mergeSource: loadedValue)
            : TryEmitInstruction(builder, observer);
        if (observerFailure is not null)
        {
            throw Unsupported(observerFailure, observer, observerPc);
        }

        pendingLoad?.Emit(builder);
        EnsureChainedLoadDelayIsRepresentable(instructions, index, index + 1);

        consumed = 2;
        var exit = new RecompilerIrExit(
            RecompilerIrTerminationReason.Success,
            unchecked(loadPc + (2 * InstructionSize)));
        return new RecompilerIrBlock(loadPc, builder.Operations, exit, retiredInstructionCount: 2);
    }

    /// <summary>
    /// Reports whether the load at <paramref name="index"/> leaves a value that the
    /// instruction in its load-delay slot reads — the case that only a fused block
    /// can represent. A load into <c>$zero</c> is never observable (GPR[0] is
    /// immutable), and an entry that is not at <c>pc + 4</c> is not the
    /// architectural load-delay slot.
    /// </summary>
    private static bool IsLoadDelayObservedByItsDelaySlot(
        IReadOnlyList<(R3000aInstruction Instruction, uint EntryPc)> instructions, int index)
    {
        var (load, loadPc) = instructions[index];
        if (!load.LoadDelayInfo.ProducesLoadDelay)
        {
            return false;
        }

        var target = load.LoadDelayInfo.TargetRegister;
        if (target == 0 || index + 1 >= instructions.Count)
        {
            return false;
        }

        var (next, nextPc) = instructions[index + 1];
        return RequiresAdjacentLoadDelayPair(load, loadPc, next, nextPc);
    }

    /// <summary>
    /// Reports whether two adjacent instructions require the load-delay fusion
    /// performed by <see cref="LowerProgram"/>. Reachable-code discovery uses this
    /// narrow query to avoid splitting an architecturally observable load pair
    /// across CFG block entries; it does not reproduce load-delay semantics.
    /// </summary>
    internal static bool RequiresAdjacentLoadDelayPair(
        R3000aInstruction load,
        uint loadPc,
        R3000aInstruction next,
        uint nextPc)
    {
        if (!load.LoadDelayInfo.ProducesLoadDelay || load.LoadDelayInfo.TargetRegister == 0)
        {
            return false;
        }

        if (nextPc != unchecked(loadPc + InstructionSize))
        {
            return false;
        }

        return TryGetSourceRegisters(next, out var sources)
            && Array.IndexOf(sources, load.LoadDelayInfo.TargetRegister) >= 0;
    }

    /// <summary>
    /// Rejects a second load delay stacked on the one just fused. The observer of
    /// the fused load may itself be a load; its own commit then belongs at the
    /// retirement point of the instruction after the fused block, which is outside
    /// it. That chained shadow is not representable here, so it fails fast instead
    /// of committing a value one instruction early.
    /// </summary>
    private static void EnsureChainedLoadDelayIsRepresentable(
        IReadOnlyList<(R3000aInstruction Instruction, uint EntryPc)> instructions, int loadIndex, int observerIndex)
    {
        if (!IsLoadDelayObservedByItsDelaySlot(instructions, observerIndex))
        {
            return;
        }

        var (load, loadPc) = instructions[loadIndex];
        var (observer, observerPc) = instructions[observerIndex];
        var (consumer, consumerPc) = instructions[observerIndex + 1];
        throw new InvalidOperationException(
            $"Cannot build program: the load at PC 0x{loadPc:X8} ({load.Opcode}) is fused with the load at PC " +
            $"0x{observerPc:X8} ({observer.Opcode}) in its load-delay slot, whose own loaded value is read by the " +
            $"instruction at PC 0x{consumerPc:X8} ({consumer.Opcode}). Diagnostic: " +
            $"[{RecompilerIrDiagnosticCode.InvalidMemoryAccess}] chained load delays would have to commit outside " +
            "the fused block, which this lowering stage does not represent.");
    }

    private static R3000aInstruction RequireDelaySlot(
        IReadOnlyList<(R3000aInstruction Instruction, uint EntryPc)> instructions, int index)
    {
        var (instruction, entryPc) = instructions[index];
        if (index + 1 >= instructions.Count)
        {
            throw new InvalidOperationException(
                $"Cannot build program: the control-transfer instruction at PC 0x{entryPc:X8} ({instruction.Opcode}) " +
                "has no delay-slot instruction. Its delay slot always retires, so the pair cannot be lowered apart.");
        }

        var (delaySlot, delaySlotPc) = instructions[index + 1];
        var expectedDelaySlotPc = unchecked(entryPc + InstructionSize);
        if (delaySlotPc != expectedDelaySlotPc)
        {
            throw new InvalidOperationException(
                $"Cannot build program: the delay slot of the instruction at PC 0x{entryPc:X8} ({instruction.Opcode}) " +
                $"must be at PC 0x{expectedDelaySlotPc:X8}, but the next entry is at PC 0x{delaySlotPc:X8}.");
        }

        return delaySlot;
    }

    private static RecompilerIrBlock Require(MipsToIrLoweringResult result, R3000aInstruction instruction, uint entryPc) =>
        result.IsSupported && result.Block is not null
            ? result.Block
            : throw Unsupported(result, instruction, entryPc);

    private static InvalidOperationException Unsupported(
        MipsToIrLoweringResult result, R3000aInstruction instruction, uint entryPc) =>
        new($"Cannot build program: instruction at PC 0x{entryPc:X8} ({instruction.Opcode}) is not supported. " +
            $"Diagnostic: [{result.DiagnosticCode}] {result.DiagnosticMessage}");

    /// <summary>
    /// Emits a control transfer and its delay slot into <paramref name="builder"/>
    /// and produces the block's exit. <paramref name="pendingLoad"/>, when set, is
    /// the load-delay commit owed at the transfer's own retirement point: it is
    /// emitted after the transfer's operand reads, condition and link write, and
    /// before the delay-slot instruction's operations.
    /// </summary>
    private static MipsToIrLoweringResult? TryEmitControlTransfer(
        BlockBuilder builder,
        R3000aInstruction control,
        uint controlPc,
        R3000aInstruction delaySlot,
        PendingLoadCommit? pendingLoad,
        out RecompilerIrExit exit)
    {
        exit = null!;

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

        switch (control.Opcode)
        {
            case R3000aOpcode.Beq:
                return TryEmitConditionalBranch(
                    builder, control, controlPc, delaySlot, pendingLoad, RecompilerIrOperationKind.CompareEqual, out exit);
            case R3000aOpcode.Bne:
                return TryEmitConditionalBranch(
                    builder, control, controlPc, delaySlot, pendingLoad, RecompilerIrOperationKind.CompareNotEqual, out exit);
            case R3000aOpcode.J:
                return TryEmitDirectJump(builder, control, controlPc, delaySlot, pendingLoad, out exit);
            case R3000aOpcode.Jal:
                return TryEmitCall(builder, control, controlPc, delaySlot, pendingLoad, out exit);
            case R3000aOpcode.Jr:
            case R3000aOpcode.Jalr:
                return TryEmitRegisterIndirectTransfer(builder, control, controlPc, delaySlot, pendingLoad, out exit);
            case R3000aOpcode.Blez:
            case R3000aOpcode.Bgtz:
            case R3000aOpcode.Bltz:
            case R3000aOpcode.Bgez:
            case R3000aOpcode.Bltzal:
            case R3000aOpcode.Bgezal:
                return TryEmitCompareWithZeroBranch(builder, control, controlPc, delaySlot, pendingLoad, out exit);
            default:
                return MipsToIrLoweringResult.Unsupported(
                    control.Opcode,
                    RecompilerIrDiagnosticCode.InvalidFlow,
                    $"Control-transfer opcode '{control.Opcode}' is not supported by this lowering stage.");
        }
    }

    private static MipsToIrLoweringResult? TryEmitConditionalBranch(
        BlockBuilder builder,
        R3000aInstruction control,
        uint controlPc,
        R3000aInstruction delaySlot,
        PendingLoadCommit? pendingLoad,
        RecompilerIrOperationKind compareKind,
        out RecompilerIrExit exit)
    {
        exit = null!;
        if (!R3000aBranchSemantics.TryGetBranchTarget(control, controlPc, out var target))
        {
            return UnresolvedTarget(control, controlPc, "branch");
        }

        // The condition is evaluated from the register values as they stand
        // before the delay slot retires, so these reads must precede it.
        var left = builder.ReadGpr(control.Operand0.Register);
        var right = builder.ReadGpr(control.Operand1.Register);
        var condition = builder.Binary(compareKind, left, right);

        var failure = TryEmitDelaySlot(builder, control, controlPc, delaySlot, pendingLoad, out var trapExit);
        if (failure is not null)
        {
            return failure;
        }

        if (trapExit is not null)
        {
            // The delay slot raised a trap (BREAK/SYSCALL): the pending transfer never applies,
            // so the block stops at the exception instead of flowing.
            exit = trapExit;
            return null;
        }

        exit = new RecompilerIrExit(
            RecompilerIrTerminationReason.Success,
            nextPc: unchecked(controlPc + (2 * InstructionSize)),
            flow: new RecompilerIrFlow(RecompilerIrFlowKind.Branch, target, condition));
        return null;
    }

    /// <summary>
    /// Lowers BLEZ/BGTZ/BLTZ/BGEZ and their link variants BLTZAL/BGEZAL. All six
    /// compare <c>rs</c> against zero; the *AL pair additionally links <c>$ra</c>
    /// unconditionally, from the same pre-delay-slot read of <c>rs</c>, before the
    /// delay slot (<c>PSXCpu::ExecBltzal</c>/<c>ExecBgezal</c>: "the decision uses
    /// rs before linking" — the link is not gated by the branch outcome).
    /// </summary>
    private static MipsToIrLoweringResult? TryEmitCompareWithZeroBranch(
        BlockBuilder builder,
        R3000aInstruction control,
        uint controlPc,
        R3000aInstruction delaySlot,
        PendingLoadCommit? pendingLoad,
        out RecompilerIrExit exit)
    {
        exit = null!;
        if (!R3000aBranchSemantics.TryGetBranchTarget(control, controlPc, out var target))
        {
            return UnresolvedTarget(control, controlPc, "branch");
        }

        // The condition is evaluated from rs as it stands before the delay slot
        // retires, matching BEQ/BNE.
        var rs = builder.ReadGpr(control.Operand0.Register);
        var zero = builder.Constant(0);
        var condition = EmitCompareWithZeroCondition(builder, control.Opcode, rs, zero);

        if (control.LinkInfo.WritesLink)
        {
            if (!R3000aLinkSemantics.TryGetLinkValue(control, controlPc, out var returnAddress))
            {
                return MipsToIrLoweringResult.Unsupported(
                    control.Opcode,
                    RecompilerIrDiagnosticCode.InvalidFlow,
                    $"'{control.Opcode}' at PC 0x{controlPc:X8} was decoded without link information, so its return " +
                    "address cannot be lowered.");
            }

            EmitLinkWrite(builder, control, returnAddress);
        }

        var failure = TryEmitDelaySlot(builder, control, controlPc, delaySlot, pendingLoad, out var trapExit);
        if (failure is not null)
        {
            return failure;
        }

        if (trapExit is not null)
        {
            // The *AL link write above still retires before a delay-slot trap (BREAK/SYSCALL), the
            // same ordering TryEmitCall uses for JAL.
            exit = trapExit;
            return null;
        }

        exit = new RecompilerIrExit(
            RecompilerIrTerminationReason.Success,
            nextPc: unchecked(controlPc + (2 * InstructionSize)),
            flow: new RecompilerIrFlow(RecompilerIrFlowKind.Branch, target, condition));
        return null;
    }

    /// <summary>
    /// Builds the 0/1 branch condition for a compare-with-zero opcode by composing
    /// the existing signed less-than and equality operations, rather than adding a
    /// new IR operation kind that every consumer (evaluator, host codegen) would
    /// also have to learn: <c>rs &gt;= 0</c> is <c>!(rs &lt; 0)</c> and
    /// <c>rs &lt;= 0</c> is <c>!(0 &lt; rs)</c>, each negated with
    /// <see cref="RecompilerIrOperationKind.CompareEqual"/> against 0.
    /// </summary>
    private static int EmitCompareWithZeroCondition(BlockBuilder builder, R3000aOpcode opcode, int rs, int zero)
    {
        switch (opcode)
        {
            case R3000aOpcode.Bltz:
            case R3000aOpcode.Bltzal:
                return builder.Binary(RecompilerIrOperationKind.CompareLessThanSigned, rs, zero);
            case R3000aOpcode.Bgez:
            case R3000aOpcode.Bgezal:
                var lessThanZero = builder.Binary(RecompilerIrOperationKind.CompareLessThanSigned, rs, zero);
                return builder.Binary(RecompilerIrOperationKind.CompareEqual, lessThanZero, zero);
            case R3000aOpcode.Bgtz:
                return builder.Binary(RecompilerIrOperationKind.CompareLessThanSigned, zero, rs);
            case R3000aOpcode.Blez:
                var greaterThanZero = builder.Binary(RecompilerIrOperationKind.CompareLessThanSigned, zero, rs);
                return builder.Binary(RecompilerIrOperationKind.CompareEqual, greaterThanZero, zero);
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(opcode), opcode, "Not a compare-with-zero branch opcode.");
        }
    }

    private static MipsToIrLoweringResult? TryEmitDirectJump(
        BlockBuilder builder,
        R3000aInstruction control,
        uint controlPc,
        R3000aInstruction delaySlot,
        PendingLoadCommit? pendingLoad,
        out RecompilerIrExit exit)
    {
        exit = null!;
        if (!R3000aJumpSemantics.TryGetJumpTarget(control, controlPc, out var target))
        {
            return UnresolvedTarget(control, controlPc, "jump");
        }

        var failure = TryEmitDelaySlot(builder, control, controlPc, delaySlot, pendingLoad, out var trapExit);
        if (failure is not null)
        {
            return failure;
        }

        if (trapExit is not null)
        {
            exit = trapExit;
            return null;
        }

        exit = new RecompilerIrExit(
            RecompilerIrTerminationReason.Success,
            nextPc: null,
            flow: new RecompilerIrFlow(RecompilerIrFlowKind.Jump, target));
        return null;
    }

    /// <summary>
    /// Lowers JAL. The interpreter links before the transfer applies
    /// (<c>PSXCpu::ExecJal</c>), so the link write precedes the delay slot and the
    /// delay-slot instruction observes the new link register — the same ordering
    /// that lets <c>lw $ra, 0($sp)</c> followed by <c>jal</c> keep the linked
    /// address (docs/cpu/pipeline.md).
    /// </summary>
    private static MipsToIrLoweringResult? TryEmitCall(
        BlockBuilder builder,
        R3000aInstruction control,
        uint controlPc,
        R3000aInstruction delaySlot,
        PendingLoadCommit? pendingLoad,
        out RecompilerIrExit exit)
    {
        exit = null!;
        if (!R3000aJumpSemantics.TryGetJumpTarget(control, controlPc, out var target))
        {
            return UnresolvedTarget(control, controlPc, "jump");
        }

        if (!R3000aLinkSemantics.TryGetLinkValue(control, controlPc, out var returnAddress))
        {
            return MipsToIrLoweringResult.Unsupported(
                control.Opcode,
                RecompilerIrDiagnosticCode.InvalidFlow,
                $"'{control.Opcode}' at PC 0x{controlPc:X8} was decoded without link information, so its return " +
                "address cannot be lowered.");
        }

        EmitLinkWrite(builder, control, returnAddress);

        var failure = TryEmitDelaySlot(builder, control, controlPc, delaySlot, pendingLoad, out var trapExit);
        if (failure is not null)
        {
            return failure;
        }

        if (trapExit is not null)
        {
            // JAL's link write above still retires before the delay slot raises
            // its trap (BREAK/SYSCALL) — on hardware the branch links, then the fault applies.
            exit = trapExit;
            return null;
        }

        exit = new RecompilerIrExit(
            RecompilerIrTerminationReason.Success,
            nextPc: returnAddress,
            flow: new RecompilerIrFlow(RecompilerIrFlowKind.Call, target));
        return null;
    }

    /// <summary>
    /// Lowers JR and JALR. The target register is read first — before JALR's link
    /// write (so <c>JALR rd, rd</c> uses the pre-link value, <c>PSXCpu::ExecJalr</c>)
    /// and before the delay slot — then the delay slot retires and the block exits
    /// with that runtime value as its next PC (<see cref="RecompilerIrExit.TargetValueId"/>,
    /// Issue #635). Whether the address is a compiled block, a BIOS vector or
    /// nothing at all is decided at runtime by the dispatch loop and host transfer,
    /// not here. A trapping delay slot (BREAK/SYSCALL) still suppresses the transfer.
    /// </summary>
    private static MipsToIrLoweringResult? TryEmitRegisterIndirectTransfer(
        BlockBuilder builder,
        R3000aInstruction control,
        uint controlPc,
        R3000aInstruction delaySlot,
        PendingLoadCommit? pendingLoad,
        out RecompilerIrExit exit)
    {
        exit = null!;
        int target;
        if (control.LinkInfo.WritesLink)
        {
            if (!R3000aLinkSemantics.TryGetLinkValue(control, controlPc, out var returnAddress))
            {
                return MipsToIrLoweringResult.Unsupported(
                    control.Opcode,
                    RecompilerIrDiagnosticCode.InvalidFlow,
                    $"'{control.Opcode}' at PC 0x{controlPc:X8} was decoded without link information, so its return " +
                    "address cannot be lowered.");
            }

            // Reading the target register first is architectural, not decorative:
            // the link write below may target the same register.
            target = builder.ReadGpr(control.Operand1.Register);
            EmitLinkWrite(builder, control, returnAddress);
        }
        else
        {
            target = builder.ReadGpr(control.Operand0.Register);
        }

        var failure = TryEmitDelaySlot(builder, control, controlPc, delaySlot, pendingLoad, out var trapExit);
        if (failure is not null)
        {
            return failure;
        }

        if (trapExit is not null)
        {
            exit = trapExit;
            return null;
        }

        exit = new RecompilerIrExit(RecompilerIrTerminationReason.Success, targetValueId: target);
        return null;
    }

    private static void EmitLinkWrite(BlockBuilder builder, R3000aInstruction control, uint returnAddress)
    {
        var linked = builder.Constant(returnAddress);
        builder.WriteGpr(control.LinkInfo.LinkRegister, linked);
    }

    private static MipsToIrLoweringResult UnresolvedTarget(R3000aInstruction control, uint controlPc, string kind) =>
        MipsToIrLoweringResult.Unsupported(
            control.Opcode,
            RecompilerIrDiagnosticCode.InvalidFlow,
            $"The {kind} target of '{control.Opcode}' at PC 0x{controlPc:X8} could not be resolved from the decoded operands.");

    private static MipsToIrLoweringResult? TryEmitDelaySlot(
        BlockBuilder builder,
        R3000aInstruction control,
        uint controlPc,
        R3000aInstruction delaySlot,
        PendingLoadCommit? pendingLoad,
        out RecompilerIrExit? trapExit)
    {
        trapExit = null;

        // The transfer has retired at this point, so an owed load-delay commit
        // lands here — before the delay-slot instruction, which therefore reads
        // the loaded value while the transfer read the pre-load one.
        pendingLoad?.Emit(builder);

        // A BREAK/SYSCALL delay slot raises the exception at its own retirement, before
        // the pending transfer applies. It reads no registers, so nothing else is
        // emitted; the exception carries the owning branch's PC and BD=1, and the
        // caller suppresses the transfer flow (Issue #481).
        if (TrapExcode(delaySlot.Opcode) is { } trapExcode)
        {
            trapExit = new RecompilerIrExit(
                RecompilerIrTerminationReason.Exception,
                exception: CreateTrapException(trapExcode, controlPc, inDelaySlot: true));
            return null;
        }

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

    private static RecompilerExceptionState CreateTrapException(uint excode, uint faultPc, bool inDelaySlot) =>
        new(isRaised: true, code: excode, faultPc: faultPc, inDelaySlot: inDelaySlot);

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
            case R3000aOpcode.Jal:
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
            case R3000aOpcode.Slt:
            case R3000aOpcode.Sltu:
                sources = new[] { instruction.Operand1.Register, instruction.Operand2.Register };
                return true;
            case R3000aOpcode.Addiu:
            case R3000aOpcode.Andi:
            case R3000aOpcode.Ori:
            case R3000aOpcode.Xori:
            case R3000aOpcode.Slti:
            case R3000aOpcode.Sltiu:
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
            case R3000aOpcode.Swl:
            case R3000aOpcode.Swr:
            case R3000aOpcode.Lwl:
            case R3000aOpcode.Lwr:
                // LWL/LWR also read rt: they merge into it.
                sources = new[] { instruction.Operand1.BaseRegister, instruction.Operand0.Register };
                return true;
            case R3000aOpcode.Beq:
            case R3000aOpcode.Bne:
                sources = new[] { instruction.Operand0.Register, instruction.Operand1.Register };
                return true;
            case R3000aOpcode.Blez:
            case R3000aOpcode.Bgtz:
            case R3000aOpcode.Bltz:
            case R3000aOpcode.Bgez:
            case R3000aOpcode.Bltzal:
            case R3000aOpcode.Bgezal:
                sources = new[] { instruction.Operand0.Register };
                return true;
            case R3000aOpcode.Jr:
                sources = new[] { instruction.Operand0.Register };
                return true;
            case R3000aOpcode.Jalr:
                sources = new[] { instruction.Operand1.Register };
                return true;
            case R3000aOpcode.Sllv:
            case R3000aOpcode.Srlv:
            case R3000aOpcode.Srav:
                // Operand1=rt (value), Operand2=rs (shift amount) — DecodeShiftByRegister.
                sources = new[] { instruction.Operand1.Register, instruction.Operand2.Register };
                return true;
            case R3000aOpcode.Mult:
            case R3000aOpcode.Multu:
            case R3000aOpcode.Div:
            case R3000aOpcode.Divu:
                // Operand0=rs, Operand1=rt, no rd — DecodeMultiplyDivide. Both feed
                // the HI/LO write; neither is a GPR destination.
                sources = new[] { instruction.Operand0.Register, instruction.Operand1.Register };
                return true;
            case R3000aOpcode.Mthi:
            case R3000aOpcode.Mtlo:
                // Operand0=rs — DecodeMoveToHiLo. The GPR is read; HI/LO is written,
                // not a GPR, so it never appears as a destination register.
                sources = new[] { instruction.Operand0.Register };
                return true;
            case R3000aOpcode.Mfhi:
            case R3000aOpcode.Mflo:
                // Operand0=rd — DecodeMoveFromHiLo. Reads HI/LO (not a GPR); no GPR source.
                sources = Array.Empty<byte>();
                return true;
            default:
                sources = Array.Empty<byte>();
                return false;
        }
    }

    /// <summary>
    /// Reports the GPR an instruction writes, for the opcodes this stage lowers.
    /// A load's write is architecturally delayed, but it still cancels an older
    /// pending load to the same register (<c>PSXCpu::WriteRegDelayed</c>), so it is
    /// reported here alongside the immediate writes.
    /// </summary>
    private static bool TryGetDestinationRegister(R3000aInstruction instruction, out byte destination)
    {
        switch (instruction.Opcode)
        {
            case R3000aOpcode.Sll when IsNop(instruction):
            case R3000aOpcode.Sb:
            case R3000aOpcode.Sh:
            case R3000aOpcode.Sw:
            case R3000aOpcode.Swl:
            case R3000aOpcode.Swr:
            case R3000aOpcode.Beq:
            case R3000aOpcode.Bne:
            case R3000aOpcode.Blez:
            case R3000aOpcode.Bgtz:
            case R3000aOpcode.Bltz:
            case R3000aOpcode.Bgez:
            case R3000aOpcode.J:
            case R3000aOpcode.Jr:
            case R3000aOpcode.Mult:
            case R3000aOpcode.Multu:
            case R3000aOpcode.Div:
            case R3000aOpcode.Divu:
            case R3000aOpcode.Mthi:
            case R3000aOpcode.Mtlo:
                // Mult/Multu/Div/Divu write HI/LO, not a GPR; Mthi/Mtlo write HI/LO
                // from a GPR they only read. None of the six has a GPR destination.
                destination = 0;
                return false;
            case R3000aOpcode.Sll:
            case R3000aOpcode.Srl:
            case R3000aOpcode.Sra:
            case R3000aOpcode.Sllv:
            case R3000aOpcode.Srlv:
            case R3000aOpcode.Srav:
            case R3000aOpcode.Mfhi:
            case R3000aOpcode.Mflo:
            case R3000aOpcode.Addu:
            case R3000aOpcode.Subu:
            case R3000aOpcode.And:
            case R3000aOpcode.Or:
            case R3000aOpcode.Xor:
            case R3000aOpcode.Nor:
            case R3000aOpcode.Slt:
            case R3000aOpcode.Sltu:
            case R3000aOpcode.Addiu:
            case R3000aOpcode.Andi:
            case R3000aOpcode.Ori:
            case R3000aOpcode.Xori:
            case R3000aOpcode.Slti:
            case R3000aOpcode.Sltiu:
            case R3000aOpcode.Lui:
            case R3000aOpcode.Lb:
            case R3000aOpcode.Lbu:
            case R3000aOpcode.Lh:
            case R3000aOpcode.Lhu:
            case R3000aOpcode.Lw:
            case R3000aOpcode.Lwl:
            case R3000aOpcode.Lwr:
                destination = instruction.Operand0.Register;
                return true;
            case R3000aOpcode.Jal:
            case R3000aOpcode.Jalr:
            case R3000aOpcode.Bltzal:
            case R3000aOpcode.Bgezal:
                destination = instruction.LinkInfo.LinkRegister;
                return instruction.LinkInfo.WritesLink;
            default:
                destination = 0;
                return false;
        }
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
            case R3000aOpcode.Andi:
                EmitImmediateArithmetic(builder, instruction, RecompilerIrOperationKind.And);
                return null;
            case R3000aOpcode.Ori:
                EmitImmediateArithmetic(builder, instruction, RecompilerIrOperationKind.Or);
                return null;
            case R3000aOpcode.Xori:
                EmitImmediateArithmetic(builder, instruction, RecompilerIrOperationKind.Xor);
                return null;
            case R3000aOpcode.Slt:
                EmitThreeRegisterArithmetic(builder, instruction, RecompilerIrOperationKind.CompareLessThanSigned);
                return null;
            case R3000aOpcode.Sltu:
                EmitThreeRegisterArithmetic(builder, instruction, RecompilerIrOperationKind.CompareLessThanUnsigned);
                return null;
            case R3000aOpcode.Slti:
                EmitSlti(builder, instruction, compareSigned: true);
                return null;
            case R3000aOpcode.Sltiu:
                EmitSlti(builder, instruction, compareSigned: false);
                return null;
            case R3000aOpcode.Addiu:
                EmitAddiu(builder, instruction);
                return null;
            case R3000aOpcode.Addi:
                EmitAddi(builder, instruction);
                return null;
            case R3000aOpcode.Lui:
                EmitLui(builder, instruction);
                return null;
            case R3000aOpcode.Lb:
            case R3000aOpcode.Lbu:
            case R3000aOpcode.Lh:
            case R3000aOpcode.Lhu:
            case R3000aOpcode.Lw:
            case R3000aOpcode.Lwl:
            case R3000aOpcode.Lwr:
                return EmitLoad(builder, instruction);
            case R3000aOpcode.Swl:
            case R3000aOpcode.Swr:
                return EmitUnalignedStore(builder, instruction);
            case R3000aOpcode.Sb:
                return EmitStore(builder, instruction, RecompilerIrOperationKind.Store8);
            case R3000aOpcode.Sh:
                return EmitStore(builder, instruction, RecompilerIrOperationKind.Store16);
            case R3000aOpcode.Sw:
                return EmitStore(builder, instruction, RecompilerIrOperationKind.Store32);
            case R3000aOpcode.Sllv:
                // Decoded operand layout (DecodeShiftByRegister): Operand0=rd, Operand1=rt
                // (value), Operand2=rs (runtime shift amount) — the same rd/left/right
                // shape EmitThreeRegisterArithmetic already reads, just with a variable-
                // shift IR kind instead of an ALU one.
                EmitThreeRegisterArithmetic(builder, instruction, RecompilerIrOperationKind.ShiftLeftLogicalVariable);
                return null;
            case R3000aOpcode.Srlv:
                EmitThreeRegisterArithmetic(builder, instruction, RecompilerIrOperationKind.ShiftRightLogicalVariable);
                return null;
            case R3000aOpcode.Srav:
                EmitThreeRegisterArithmetic(builder, instruction, RecompilerIrOperationKind.ShiftRightArithmeticVariable);
                return null;
            case R3000aOpcode.Mult:
                EmitMultiplyOrDivide(builder, instruction, RecompilerIrOperationKind.MultiplySigned);
                return null;
            case R3000aOpcode.Multu:
                EmitMultiplyOrDivide(builder, instruction, RecompilerIrOperationKind.MultiplyUnsigned);
                return null;
            case R3000aOpcode.Div:
                EmitMultiplyOrDivide(builder, instruction, RecompilerIrOperationKind.DivideSigned);
                return null;
            case R3000aOpcode.Divu:
                EmitMultiplyOrDivide(builder, instruction, RecompilerIrOperationKind.DivideUnsigned);
                return null;
            case R3000aOpcode.Mfhi:
                builder.WriteGpr(instruction.Operand0.Register, builder.ReadHi());
                return null;
            case R3000aOpcode.Mflo:
                builder.WriteGpr(instruction.Operand0.Register, builder.ReadLo());
                return null;
            case R3000aOpcode.Mthi:
                builder.WriteHi(builder.ReadGpr(instruction.Operand0.Register));
                return null;
            case R3000aOpcode.Mtlo:
                builder.WriteLo(builder.ReadGpr(instruction.Operand0.Register));
                return null;
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

    /// <summary>
    /// MULT/MULTU/DIV/DIVU (<c>DecodeMultiplyDivide</c>: Operand0=rs, Operand1=rt,
    /// no rd). Writes HI/LO directly; there is no GPR destination and no SSA
    /// result to write back (see <see cref="RecompilerIrOperationKind.MultiplySigned"/>).
    /// </summary>
    private static void EmitMultiplyOrDivide(
        BlockBuilder builder, R3000aInstruction instruction, RecompilerIrOperationKind operationKind)
    {
        var rs = builder.ReadGpr(instruction.Operand0.Register);
        var rt = builder.ReadGpr(instruction.Operand1.Register);
        builder.MultiplyOrDivide(operationKind, rs, rt);
    }

    private static void EmitImmediateArithmetic(
        BlockBuilder builder, R3000aInstruction instruction, RecompilerIrOperationKind operationKind)
    {
        var left = builder.ReadGpr(instruction.Operand1.Register);
        var immediate = builder.Constant((uint)(ushort)instruction.Operand2.Value);
        var result = builder.Binary(operationKind, left, immediate);
        builder.WriteGpr(instruction.Operand0.Register, result);
    }

    private static void EmitSlti(BlockBuilder builder, R3000aInstruction instruction, bool compareSigned)
    {
        var left = builder.ReadGpr(instruction.Operand1.Register);
        var immediate = builder.Constant(SignExtend16To32((ushort)instruction.Operand2.Value));
        var result = builder.Binary(
            compareSigned ? RecompilerIrOperationKind.CompareLessThanSigned : RecompilerIrOperationKind.CompareLessThanUnsigned,
            left, immediate);
        builder.WriteGpr(instruction.Operand0.Register, result);
    }

    private static void EmitAddiu(BlockBuilder builder, R3000aInstruction instruction)
    {
        var left = builder.ReadGpr(instruction.Operand1.Register);
        var immediate = builder.Constant(SignExtend16To32((ushort)instruction.Operand2.Value));
        var result = builder.Binary(RecompilerIrOperationKind.Add, left, immediate);
        builder.WriteGpr(instruction.Operand0.Register, result);
    }

    private static void EmitAddi(BlockBuilder builder, R3000aInstruction instruction)
    {
        var _left = builder.ReadGpr(instruction.Operand1.Register);
        var _immediate = builder.Constant(SignExtend16To32((ushort)instruction.Operand2.Value));
        var _result = builder.Binary(RecompilerIrOperationKind.AddSigned, _left, _immediate);
        builder.WriteGpr(instruction.Operand0.Register, _result);
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
    /// Lowers a load and commits it immediately. That is the shape used when the
    /// load delay is not architecturally observable; the observable case commits
    /// through <see cref="LowerObservedLoadDelay"/> instead.
    /// </summary>
    private static MipsToIrLoweringResult? EmitLoad(
        BlockBuilder builder, R3000aInstruction instruction, int? mergeSource = null)
    {
        var failure = TryEmitLoadValue(builder, instruction, mergeSource, out var value);
        if (failure is not null)
        {
            return failure;
        }

        builder.WriteGpr(instruction.Operand0.Register, value);
        return null;
    }

    /// <summary>
    /// Emits the effective address, the load itself, and — for the sign-extending
    /// forms — the shift pair that widens the accessed value, producing the value
    /// the target register will receive. The register commit is deliberately left
    /// to the caller, because the load delay decides where it belongs.
    /// <paramref name="mergeSource"/> is the value LWL/LWR merge into when a load
    /// to the same register is still pending; otherwise they read <c>rt</c>.
    /// </summary>
    private static MipsToIrLoweringResult? TryEmitLoadValue(
        BlockBuilder builder, R3000aInstruction instruction, int? mergeSource, out int value)
    {
        value = -1;
        if (instruction.Opcode is R3000aOpcode.Lwl or R3000aOpcode.Lwr)
        {
            return TryEmitUnalignedLoadValue(builder, instruction, mergeSource, out value);
        }

        // The shift amount is the sign-extension width: 0 for the zero-extending
        // forms (LBU/LHU) and for LW, which use the loaded value directly.
        (RecompilerIrOperationKind Kind, byte SignExtendShift)? shape = instruction.Opcode switch
        {
            R3000aOpcode.Lb => (RecompilerIrOperationKind.Load8, (byte)24),
            R3000aOpcode.Lbu => (RecompilerIrOperationKind.Load8, (byte)0),
            R3000aOpcode.Lh => (RecompilerIrOperationKind.Load16, (byte)16),
            R3000aOpcode.Lhu => (RecompilerIrOperationKind.Load16, (byte)0),
            R3000aOpcode.Lw => (RecompilerIrOperationKind.Load32, (byte)0),
            _ => null,
        };

        if (shape is null)
        {
            return MipsToIrLoweringResult.Unsupported(
                instruction.Opcode,
                RecompilerIrDiagnosticCode.InvalidOperationShape,
                $"Opcode '{instruction.Opcode}' is not a load lowered by this stage.");
        }

        var (loadKind, signExtendShift) = shape.Value;
        var memory = instruction.Operand1;
        if (memory.Kind != R3000aOperandKind.MemoryOffset)
        {
            return UnsupportedMemoryOperand(instruction);
        }

        var address = EmitEffectiveAddress(builder, memory);
        var loaded = builder.Load(loadKind, address);

        if (signExtendShift != 0)
        {
            // The IR load yields the accessed byte/halfword zero-extended into a
            // 32-bit value; MIPS sign extension is expressed with the generic
            // shift operations rather than a signedness flag on the load.
            var shiftedLeft = builder.Shift(RecompilerIrOperationKind.ShiftLeftLogical, loaded, signExtendShift);
            loaded = builder.Shift(RecompilerIrOperationKind.ShiftRightArithmetic, shiftedLeft, signExtendShift);
        }

        value = loaded;
        return null;
    }

    /// <summary>
    /// LWL/LWR: reads the aligned word containing the effective address and
    /// merges it into <paramref name="mergeSource"/> (or <c>rt</c>), preserving
    /// the bytes the access does not cover. The byte-merge table is the one in
    /// <c>src/PSXRecomp.Native/rust/src/cpu_unaligned.rs</c>, with
    /// <c>s = (addr &amp; 3) * 8</c>:
    /// LWL = <c>(reg &amp; (0x00FFFFFF &gt;&gt; s)) | (mem &lt;&lt; (24 - s))</c>,
    /// LWR = <c>(reg &amp; ~(0xFFFFFFFF &gt;&gt; s)) | (mem &gt;&gt; s)</c>.
    /// Composed from existing operations, so no backend learns a new kind.
    /// </summary>
    private static MipsToIrLoweringResult? TryEmitUnalignedLoadValue(
        BlockBuilder builder, R3000aInstruction instruction, int? mergeSource, out int value)
    {
        value = -1;
        if (instruction.Operand1.Kind != R3000aOperandKind.MemoryOffset)
        {
            return UnsupportedMemoryOperand(instruction);
        }

        var address = EmitEffectiveAddress(builder, instruction.Operand1);
        var (aligned, shift) = EmitUnalignedAddressParts(builder, address);
        var register = mergeSource ?? builder.ReadGpr(instruction.Operand0.Register);
        var memory = builder.Load(RecompilerIrOperationKind.Load32, aligned);

        int keptMask, merged;
        if (instruction.Opcode == R3000aOpcode.Lwl)
        {
            keptMask = builder.Binary(
                RecompilerIrOperationKind.ShiftRightLogicalVariable, builder.Constant(0x00FFFFFFu), shift);
            merged = builder.Binary(
                RecompilerIrOperationKind.ShiftLeftLogicalVariable, memory, EmitTwentyFourMinus(builder, shift));
        }
        else
        {
            keptMask = EmitNot(builder, builder.Binary(
                RecompilerIrOperationKind.ShiftRightLogicalVariable, builder.Constant(0xFFFFFFFFu), shift));
            merged = builder.Binary(RecompilerIrOperationKind.ShiftRightLogicalVariable, memory, shift);
        }

        var kept = builder.Binary(RecompilerIrOperationKind.And, register, keptMask);
        value = builder.Binary(RecompilerIrOperationKind.Or, kept, merged);
        return null;
    }

    /// <summary>
    /// SWL/SWR: a read-modify-write of the aligned word containing the
    /// effective address, per <c>cpu_unaligned.rs</c> (<c>s = (addr &amp; 3) * 8</c>):
    /// SWL = <c>(mem &amp; ~(0xFFFFFFFF &gt;&gt; (24 - s))) | (reg &gt;&gt; (24 - s))</c>,
    /// SWR = <c>(mem &amp; ~(0xFFFFFFFF &lt;&lt; s)) | (reg &lt;&lt; s)</c>.
    /// </summary>
    private static MipsToIrLoweringResult? EmitUnalignedStore(BlockBuilder builder, R3000aInstruction instruction)
    {
        if (instruction.Operand1.Kind != R3000aOperandKind.MemoryOffset)
        {
            return UnsupportedMemoryOperand(instruction);
        }

        var address = EmitEffectiveAddress(builder, instruction.Operand1);
        var (aligned, shift) = EmitUnalignedAddressParts(builder, address);
        var register = builder.ReadGpr(instruction.Operand0.Register);
        var memory = builder.Load(RecompilerIrOperationKind.Load32, aligned);

        // SWL shifts right by 24 - s, SWR left by s — mask and value alike.
        var (shiftKind, amount) = instruction.Opcode == R3000aOpcode.Swl
            ? (RecompilerIrOperationKind.ShiftRightLogicalVariable, EmitTwentyFourMinus(builder, shift))
            : (RecompilerIrOperationKind.ShiftLeftLogicalVariable, shift);

        var keptMask = EmitNot(builder, builder.Binary(shiftKind, builder.Constant(0xFFFFFFFFu), amount));
        var kept = builder.Binary(RecompilerIrOperationKind.And, memory, keptMask);
        var stored = builder.Binary(shiftKind, register, amount);
        builder.Store(RecompilerIrOperationKind.Store32, aligned, builder.Binary(RecompilerIrOperationKind.Or, kept, stored));
        return null;
    }

    /// <summary>Emits <c>addr &amp; ~3</c> and the byte shift <c>(addr &amp; 3) * 8</c>.</summary>
    private static (int Aligned, int Shift) EmitUnalignedAddressParts(BlockBuilder builder, int address)
    {
        var aligned = builder.Binary(RecompilerIrOperationKind.And, address, builder.Constant(~3u));
        var byteOffset = builder.Binary(RecompilerIrOperationKind.And, address, builder.Constant(3u));
        return (aligned, builder.Shift(RecompilerIrOperationKind.ShiftLeftLogical, byteOffset, 3));
    }

    private static int EmitTwentyFourMinus(BlockBuilder builder, int shift) =>
        builder.Binary(RecompilerIrOperationKind.Subtract, builder.Constant(24u), shift);

    private static int EmitNot(BlockBuilder builder, int value) =>
        builder.Binary(RecompilerIrOperationKind.Nor, value, value);

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
    /// A load-delay commit owed at a later retirement point: the target register
    /// and the block-local value the load produced.
    /// </summary>
    private readonly record struct PendingLoadCommit(byte Register, int ValueId)
    {
        public void Emit(BlockBuilder builder) => builder.WriteGpr(Register, ValueId);
    }

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

        public int ReadHi() =>
            AddWithResult(id => new RecompilerIrOperation(RecompilerIrOperationKind.ReadHi, resultValueId: id));

        public int ReadLo() =>
            AddWithResult(id => new RecompilerIrOperation(RecompilerIrOperationKind.ReadLo, resultValueId: id));

        public void WriteHi(int value) =>
            _operations.Add(new RecompilerIrOperation(RecompilerIrOperationKind.WriteHi, inputValueA: value));

        public void WriteLo(int value) =>
            _operations.Add(new RecompilerIrOperation(RecompilerIrOperationKind.WriteLo, inputValueA: value));

        /// <summary>
        /// MULT/MULTU/DIV/DIVU: a two-input operation with no SSA result — it
        /// writes both HI and LO directly, the same "computes, then stores"
        /// shape <see cref="Store"/> uses for guest memory.
        /// </summary>
        public void MultiplyOrDivide(RecompilerIrOperationKind kind, int inputValueA, int inputValueB) =>
            _operations.Add(new RecompilerIrOperation(kind, inputValueA: inputValueA, inputValueB: inputValueB));

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
