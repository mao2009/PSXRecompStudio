using System.Collections.ObjectModel;
using PSXRecomp.Architecture;
using PSXRecomp.Core.DiscImage.AnalysisArtifacts;

namespace PSXRecomp.Core.Recompiler;

[Domain]
public enum RecompilerIrOperationKind : byte
{
    Nop,
    Constant,
    ReadGpr,
    WriteGpr,
    Add,
    Subtract,
    And,
    Or,
    Xor,
    Nor,
    ShiftLeftLogical,
    ShiftRightLogical,
    ShiftRightArithmetic,
    /// <summary>
    /// Reads 8 bits from the guest 32-bit address in input A and produces them
    /// zero-extended into the 32-bit result. Signedness is not part of the
    /// operation: a sign-extending guest load is expressed by the lowering as a
    /// <see cref="ShiftLeftLogical"/> / <see cref="ShiftRightArithmetic"/> pair.
    /// </summary>
    Load8,

    /// <summary>Reads 16 bits from the address in input A, zero-extended (see <see cref="Load8"/>).</summary>
    Load16,

    /// <summary>Reads 32 bits from the guest 32-bit address in input A.</summary>
    Load32,

    /// <summary>
    /// Writes the low 8 bits of the value in input B to the guest 32-bit address
    /// in input A, and produces no result.
    /// </summary>
    Store8,

    /// <summary>Writes the low 16 bits of input B to the address in input A.</summary>
    Store16,

    /// <summary>Writes the 32-bit value in input B to the address in input A.</summary>
    Store32,

    /// <summary>Produces 1 when inputs A and B are equal, otherwise 0.</summary>
    CompareEqual,

    /// <summary>Produces 1 when inputs A and B differ, otherwise 0.</summary>
    CompareNotEqual,
}

[Domain]
public enum RecompilerIrTerminationReason : byte
{
    Success,
    UnsupportedInstruction,
    UnsupportedIr,
    UnsupportedMemory,
    UnsupportedMmio,
    UnresolvedIndirectFlow,
    Exception,
    ExecutionBudgetExceeded,
    GenerationFailure,
    HostCompilerFailure,
    StateMismatch,
}

[Domain]
public enum RecompilerIrDiagnosticCode : byte
{
    InvalidRegister,
    InvalidOperationShape,
    MissingOperand,
    InvalidOperandWidth,
    IllegalTermination,
    ZeroRegisterWrite,
    DuplicateBlock,
    UnstableBlockOrder,
    InvalidFlow,
    ReservedFlow,
    InvalidMemoryAccess,
    InvalidMetadata,
    DuplicateFunction,
    InvalidFunction,
}

[Domain]
public enum RecompilerMemoryAccessKind : byte
{
    Read,
    Write,
}

[Domain]
public readonly record struct RecompilerIrValue(int Id)
{
    public bool IsValid => Id >= 0;
}

[Domain]
public sealed record RecompilerIrOperation
{
    public RecompilerIrOperation(
        RecompilerIrOperationKind kind,
        int resultValueId = -1,
        int inputValueA = -1,
        int inputValueB = -1,
        byte register = 0,
        byte shiftAmount = 0,
        uint immediate = 0)
    {
        Kind = kind;
        ResultValueId = resultValueId;
        InputValueA = inputValueA;
        InputValueB = inputValueB;
        Register = register;
        ShiftAmount = shiftAmount;
        Immediate = immediate;
    }

    public RecompilerIrOperationKind Kind { get; }
    public int ResultValueId { get; }
    public int InputValueA { get; }
    public int InputValueB { get; }
    public byte Register { get; }
    public byte ShiftAmount { get; }
    public uint Immediate { get; }
}

/// <summary>
/// Classifies how control flows from a basic block to its successor(s). The
/// sequential case is the existing "success with a next PC" relation; branch,
/// jump and call make control flow explicit. <see cref="Return"/> remains a
/// reserved extension point and is rejected by the validator: it needs a target
/// held in a register, which <see cref="RecompilerIrFlow.Target"/> — a static
/// address — cannot carry.
/// </summary>
[Domain]
public enum RecompilerIrFlowKind : byte
{
    Sequential = 0,
    Branch = 1,
    Jump = 2,
    Call = 3,
    Return = 4,
}

/// <summary>
/// The explicit control-flow transition of a block, carried by
/// <see cref="RecompilerIrExit"/> when the block does not simply fall through.
/// <list type="bullet">
/// <item>Branch: condition value id, taken target; the not-taken successor is the
/// exit's next PC.</item>
/// <item>Jump: unconditional target address.</item>
/// <item>Sequential: matches the existing success-with-next-PC relation.</item>
/// <item>Call: unconditional target address of the callee, with the exit's next
/// PC carrying the address control resumes at when the callee returns. The
/// linked return address itself is an architectural GPR write the lowering
/// emits; the flow states the call relation, not the link register.</item>
/// <item>Return: reserved (not yet supported).</item>
/// </list>
/// </summary>
[Domain]
public sealed record RecompilerIrFlow
{
    public RecompilerIrFlow(
        RecompilerIrFlowKind kind,
        uint? target = null,
        int conditionValueId = -1)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
        Target = target;
        ConditionValueId = conditionValueId;
    }

    public RecompilerIrFlowKind Kind { get; }
    public uint? Target { get; }
    public int ConditionValueId { get; }
}

[Domain]
public sealed record RecompilerIrExit
{
    public RecompilerIrExit(
        RecompilerIrTerminationReason reason,
        uint? nextPc = null,
        RecompilerIrFlow? flow = null)
    {
        Reason = reason;
        NextPc = nextPc;
        Flow = flow;
    }

    public RecompilerIrTerminationReason Reason { get; }
    public uint? NextPc { get; }
    public RecompilerIrFlow? Flow { get; }
}

[Domain]
public sealed record RecompilerIrBlock
{
    public RecompilerIrBlock(uint entryPc, IEnumerable<RecompilerIrOperation> operations, RecompilerIrExit exit)
    {
        ArgumentNullException.ThrowIfNull(operations);
        Exit = exit ?? throw new ArgumentNullException(nameof(exit));
        EntryPc = entryPc;
        Operations = new ReadOnlyCollection<RecompilerIrOperation>(operations.ToArray());
    }

    public uint EntryPc { get; }
    public IReadOnlyList<RecompilerIrOperation> Operations { get; }
    public RecompilerIrExit Exit { get; }
}

/// <summary>
/// A generic, typed key/value metadata slot used to carry PS1/MIPS-specific
/// information (for example endianness, address-space region, or scratchpad base)
/// without leaking that information into the generic IR operation surface. The
/// key is a stable string; exactly one of <see cref="UIntValue"/> or
/// <see cref="StringValue"/> is set.
/// </summary>
[Domain]
public sealed record RecompilerIrMetadataEntry
{
    public RecompilerIrMetadataEntry(string key, uint? uintValue = null, string? stringValue = null)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Metadata key must be non-empty.", nameof(key));
        if (uintValue is not null && stringValue is not null)
            throw new ArgumentException("A metadata entry carries a single typed value, not both.", nameof(key));
        if (uintValue is null && stringValue is null)
            throw new ArgumentException("A metadata entry requires a value.", nameof(key));
        Key = key;
        UIntValue = uintValue;
        StringValue = stringValue;
    }

    public string Key { get; }
    public uint? UIntValue { get; }
    public string? StringValue { get; }
}

/// <summary>
/// A function: an entry address plus the basic blocks reachable from it, and
/// optional PS1/MIPS-scoped metadata. Function blocks are a grouping view over
/// the blocks of a <see cref="RecompilerIrProgram"/>; the program remains the
/// SSOT for block ordering and uniqueness.
/// </summary>
[Domain]
public sealed record RecompilerIrFunction
{
    public RecompilerIrFunction(
        uint entryPc,
        IEnumerable<RecompilerIrBlock> blocks,
        IEnumerable<RecompilerIrMetadataEntry>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        EntryPc = entryPc;
        Blocks = new ReadOnlyCollection<RecompilerIrBlock>(blocks.ToArray());
        Metadata = new ReadOnlyCollection<RecompilerIrMetadataEntry>((metadata ?? Array.Empty<RecompilerIrMetadataEntry>()).ToArray());
    }

    public uint EntryPc { get; }
    public IReadOnlyList<RecompilerIrBlock> Blocks { get; }
    public IReadOnlyList<RecompilerIrMetadataEntry> Metadata { get; }
}

[Domain]
public sealed record RecompilerIrProgram
{
    public RecompilerIrProgram(
        IEnumerable<RecompilerIrBlock> blocks,
        IEnumerable<RecompilerIrFunction>? functions = null)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        Blocks = new ReadOnlyCollection<RecompilerIrBlock>(blocks.OrderBy(block => block.EntryPc).ToArray());
        Functions = new ReadOnlyCollection<RecompilerIrFunction>((functions ?? Array.Empty<RecompilerIrFunction>()).ToArray());
    }

    public IReadOnlyList<RecompilerIrBlock> Blocks { get; }
    public IReadOnlyList<RecompilerIrFunction> Functions { get; }
}

[Domain]
public sealed record RecompilerIrDiagnostic(
    RecompilerIrDiagnosticCode Code,
    string Message,
    int BlockIndex,
    int OperationIndex);

[Domain]
public sealed record RecompilerIrValidationResult(IReadOnlyList<RecompilerIrDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.Count == 0;
}

[Domain]
public static class RecompilerIrValidator
{
    public static RecompilerIrValidationResult Validate(RecompilerIrProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        var diagnostics = new List<RecompilerIrDiagnostic>();
        uint? previousPc = null;

        for (var blockIndex = 0; blockIndex < program.Blocks.Count; blockIndex++)
        {
            var block = program.Blocks[blockIndex];
            if (previousPc == block.EntryPc)
            {
                Add(diagnostics, RecompilerIrDiagnosticCode.DuplicateBlock, "Block entry PCs must be unique.", blockIndex);
            }
            else if (previousPc is not null && previousPc > block.EntryPc)
            {
                Add(diagnostics, RecompilerIrDiagnosticCode.UnstableBlockOrder, "Blocks must be ordered by entry PC.", blockIndex);
            }

            previousPc = block.EntryPc;
            var definedValueIds = new HashSet<int>();
            for (var operationIndex = 0; operationIndex < block.Operations.Count; operationIndex++)
            {
                var operation = block.Operations[operationIndex];
                ValidateOperation(operation, diagnostics, blockIndex, operationIndex);
                ValidateInput(operation.InputValueA, definedValueIds, diagnostics, blockIndex, operationIndex);
                ValidateInput(operation.InputValueB, definedValueIds, diagnostics, blockIndex, operationIndex);
                if (operation.ResultValueId >= 0)
                {
                    definedValueIds.Add(operation.ResultValueId);
                }
            }

            ValidateExit(block.Exit, definedValueIds, diagnostics, blockIndex);
        }

        ValidateFunctions(program, diagnostics);

        return new RecompilerIrValidationResult(new ReadOnlyCollection<RecompilerIrDiagnostic>(diagnostics));
    }

    private static void ValidateExit(RecompilerIrExit exit, HashSet<int> definedValueIds, List<RecompilerIrDiagnostic> diagnostics, int blockIndex)
    {
        if (!Enum.IsDefined(exit.Reason))
        {
            Add(diagnostics, RecompilerIrDiagnosticCode.IllegalTermination, "Termination reason must be a defined value.", blockIndex);
            return;
        }

        var flow = exit.Flow;
        if (flow is null)
        {
            if (exit.Reason == RecompilerIrTerminationReason.Success && exit.NextPc is null)
            {
                Add(diagnostics, RecompilerIrDiagnosticCode.IllegalTermination, "Success exits require a next PC.", blockIndex);
            }
            else if (exit.Reason != RecompilerIrTerminationReason.Success && exit.NextPc is not null)
            {
                Add(diagnostics, RecompilerIrDiagnosticCode.IllegalTermination, "Non-success exits must not provide a next PC.", blockIndex);
            }
            return;
        }

        if (!Enum.IsDefined(flow.Kind))
        {
            Add(diagnostics, RecompilerIrDiagnosticCode.InvalidFlow, "Flow kind must be a defined value.", blockIndex);
            return;
        }

        if (exit.Reason != RecompilerIrTerminationReason.Success)
        {
            Add(diagnostics, RecompilerIrDiagnosticCode.InvalidFlow, "A flow is only valid on a success exit.", blockIndex);
            return;
        }

        switch (flow.Kind)
        {
            case RecompilerIrFlowKind.Sequential:
                if (exit.NextPc is null)
                {
                    Add(diagnostics, RecompilerIrDiagnosticCode.InvalidFlow, "Sequential flow requires a next PC.", blockIndex);
                }
                if (flow.Target is not null || flow.ConditionValueId >= 0)
                {
                    Add(diagnostics, RecompilerIrDiagnosticCode.InvalidFlow, "Sequential flow carries no target or condition.", blockIndex);
                }
                break;
            case RecompilerIrFlowKind.Branch:
                if (flow.Target is null)
                {
                    Add(diagnostics, RecompilerIrDiagnosticCode.InvalidFlow, "Branch flow requires a taken target.", blockIndex);
                }
                if (flow.ConditionValueId < 0)
                {
                    Add(diagnostics, RecompilerIrDiagnosticCode.InvalidFlow, "Branch flow requires a condition value.", blockIndex);
                }
                else if (!definedValueIds.Contains(flow.ConditionValueId))
                {
                    Add(diagnostics, RecompilerIrDiagnosticCode.MissingOperand, "Branch condition value must be defined by an earlier operation in the block.", blockIndex);
                }
                if (exit.NextPc is null)
                {
                    Add(diagnostics, RecompilerIrDiagnosticCode.InvalidFlow, "Branch flow requires a fall-through next PC.", blockIndex);
                }
                break;
            case RecompilerIrFlowKind.Jump:
                if (flow.Target is null)
                {
                    Add(diagnostics, RecompilerIrDiagnosticCode.InvalidFlow, "Jump flow requires a target.", blockIndex);
                }
                if (flow.ConditionValueId >= 0)
                {
                    Add(diagnostics, RecompilerIrDiagnosticCode.InvalidFlow, "Jump flow carries no condition.", blockIndex);
                }
                if (exit.NextPc is not null)
                {
                    Add(diagnostics, RecompilerIrDiagnosticCode.InvalidFlow, "Jump flow must not provide a next PC.", blockIndex);
                }
                break;
            case RecompilerIrFlowKind.Call:
                if (flow.Target is null)
                {
                    Add(diagnostics, RecompilerIrDiagnosticCode.InvalidFlow, "Call flow requires a callee target.", blockIndex);
                }
                if (flow.ConditionValueId >= 0)
                {
                    Add(diagnostics, RecompilerIrDiagnosticCode.InvalidFlow, "Call flow carries no condition.", blockIndex);
                }
                if (exit.NextPc is null)
                {
                    Add(diagnostics, RecompilerIrDiagnosticCode.InvalidFlow, "Call flow requires the return-address next PC.", blockIndex);
                }
                break;
            case RecompilerIrFlowKind.Return:
                Add(diagnostics, RecompilerIrDiagnosticCode.ReservedFlow, $"Flow kind '{flow.Kind}' is reserved and not yet supported.", blockIndex);
                break;
            default:
                Add(diagnostics, RecompilerIrDiagnosticCode.InvalidFlow, "Flow kind must be a defined value.", blockIndex);
                break;
        }
    }

    private static void ValidateFunctions(RecompilerIrProgram program, List<RecompilerIrDiagnostic> diagnostics)
    {
        var blockPcs = program.Blocks.Select(block => block.EntryPc).ToHashSet();
        var seenFunctions = new HashSet<uint>();
        foreach (var function in program.Functions)
        {
            if (!seenFunctions.Add(function.EntryPc))
            {
                Add(diagnostics, RecompilerIrDiagnosticCode.DuplicateFunction, "Function entry PCs must be unique.", -1);
                continue;
            }

            if (!blockPcs.Contains(function.EntryPc))
            {
                Add(diagnostics, RecompilerIrDiagnosticCode.InvalidFunction, "Function entry PC must reference a block in the program.", -1);
                continue;
            }

            foreach (var block in function.Blocks)
            {
                if (!blockPcs.Contains(block.EntryPc))
                {
                    Add(diagnostics, RecompilerIrDiagnosticCode.InvalidFunction, "A function block must exist in the program.", -1);
                }
            }

            foreach (var entry in function.Metadata)
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                {
                    Add(diagnostics, RecompilerIrDiagnosticCode.InvalidMetadata, "Metadata keys must be non-empty.", -1);
                }
            }
        }
    }

    private static void ValidateOperation(RecompilerIrOperation operation, List<RecompilerIrDiagnostic> diagnostics, int blockIndex, int operationIndex)
    {
        if (operation.Register > 31)
        {
            Add(diagnostics, RecompilerIrDiagnosticCode.InvalidRegister, "GPR number must be within [0, 31].", blockIndex, operationIndex);
        }

        var hasResult = operation.ResultValueId >= 0;
        var hasA = operation.InputValueA >= 0;
        var hasB = operation.InputValueB >= 0;
        if (!Enum.IsDefined(operation.Kind))
        {
            Add(diagnostics, RecompilerIrDiagnosticCode.InvalidOperationShape, "Operation kind must be a defined value.", blockIndex, operationIndex);
            return;
        }

        if (operation.ResultValueId < -1 || operation.InputValueA < -1 || operation.InputValueB < -1)
        {
            Add(diagnostics, RecompilerIrDiagnosticCode.InvalidOperandWidth, "Value IDs must be -1 or non-negative.", blockIndex, operationIndex);
        }

        switch (operation.Kind)
        {
            case RecompilerIrOperationKind.Nop:
                Require(!hasResult && !hasA && !hasB, diagnostics, blockIndex, operationIndex);
                break;
            case RecompilerIrOperationKind.Constant:
            case RecompilerIrOperationKind.ReadGpr:
                Require(hasResult && !hasA && !hasB, diagnostics, blockIndex, operationIndex);
                break;
            case RecompilerIrOperationKind.WriteGpr:
                Require(!hasResult && hasA && !hasB, diagnostics, blockIndex, operationIndex);
                if (operation.Register == 0)
                {
                    Add(diagnostics, RecompilerIrDiagnosticCode.ZeroRegisterWrite, "GPR[0] is immutable and cannot be written.", blockIndex, operationIndex);
                }
                break;
            case RecompilerIrOperationKind.Load8:
            case RecompilerIrOperationKind.Load16:
            case RecompilerIrOperationKind.Load32:
                Require(hasResult && hasA && !hasB, diagnostics, blockIndex, operationIndex);
                if (operation.Register != 0 || operation.ShiftAmount != 0)
                {
                    Add(diagnostics, RecompilerIrDiagnosticCode.InvalidMemoryAccess, "Load operations must not carry a register or shift amount.", blockIndex, operationIndex);
                }
                break;
            case RecompilerIrOperationKind.Store8:
            case RecompilerIrOperationKind.Store16:
            case RecompilerIrOperationKind.Store32:
                Require(!hasResult && hasA && hasB, diagnostics, blockIndex, operationIndex);
                if (operation.Register != 0)
                {
                    Add(diagnostics, RecompilerIrDiagnosticCode.InvalidMemoryAccess, "Store operations must not carry a register.", blockIndex, operationIndex);
                }
                break;
            case RecompilerIrOperationKind.CompareEqual:
            case RecompilerIrOperationKind.CompareNotEqual:
                Require(hasResult && hasA && hasB && operation.ShiftAmount == 0, diagnostics, blockIndex, operationIndex);
                break;
            case RecompilerIrOperationKind.ShiftLeftLogical:
            case RecompilerIrOperationKind.ShiftRightLogical:
            case RecompilerIrOperationKind.ShiftRightArithmetic:
                Require(hasResult && hasA && !hasB && operation.ShiftAmount <= 31, diagnostics, blockIndex, operationIndex);
                break;
            default:
                Require(hasResult && hasA && hasB && operation.ShiftAmount == 0, diagnostics, blockIndex, operationIndex);
                break;
        }
    }

    private static void ValidateInput(int valueId, HashSet<int> definedValueIds, List<RecompilerIrDiagnostic> diagnostics, int blockIndex, int operationIndex)
    {
        if (valueId >= 0 && !definedValueIds.Contains(valueId))
        {
            Add(diagnostics, RecompilerIrDiagnosticCode.MissingOperand, "Input value must be defined by an earlier operation in the block.", blockIndex, operationIndex);
        }
    }

    private static void Require(bool condition, List<RecompilerIrDiagnostic> diagnostics, int blockIndex, int operationIndex)
    {
        if (!condition)
        {
            Add(diagnostics, RecompilerIrDiagnosticCode.InvalidOperationShape, "Operation has an invalid operand shape.", blockIndex, operationIndex);
        }
    }

    private static void Add(List<RecompilerIrDiagnostic> diagnostics, RecompilerIrDiagnosticCode code, string message, int blockIndex, int operationIndex = -1) =>
        diagnostics.Add(new RecompilerIrDiagnostic(code, message, blockIndex, operationIndex));
}

[Domain]
public sealed record RecompilerLoadDelayState
{
    public RecompilerLoadDelayState(bool isPending = false, byte targetRegister = 0, uint value = 0)
    {
        if (targetRegister > 31) throw new ArgumentOutOfRangeException(nameof(targetRegister));
        IsPending = isPending;
        TargetRegister = targetRegister;
        Value = value;
    }

    public bool IsPending { get; }
    public byte TargetRegister { get; }
    public uint Value { get; }
}

[Domain]
public sealed record RecompilerExceptionState
{
    public RecompilerExceptionState(bool isRaised = false, uint code = 0, uint faultPc = 0, bool inDelaySlot = false)
    {
        IsRaised = isRaised;
        Code = code;
        FaultPc = faultPc;
        InDelaySlot = inDelaySlot;
    }

    public bool IsRaised { get; }
    public uint Code { get; }
    public uint FaultPc { get; }
    public bool InDelaySlot { get; }
}

[Domain]
public sealed record RecompilerMemoryObservation
{
    public RecompilerMemoryObservation(uint address, uint value, byte width, RecompilerMemoryAccessKind access)
    {
        if (width is not (1 or 2 or 4)) throw new ArgumentOutOfRangeException(nameof(width));
        if (!Enum.IsDefined(access)) throw new ArgumentOutOfRangeException(nameof(access));
        Address = address;
        Value = value;
        Width = width;
        Access = access;
    }

    public uint Address { get; }
    public uint Value { get; }
    public byte Width { get; }
    public RecompilerMemoryAccessKind Access { get; }
}

[Domain]
public sealed record RecompilerStateSnapshot
{
    public RecompilerStateSnapshot(
        IEnumerable<uint> gpr,
        uint hi,
        uint lo,
        uint pc,
        RecompilerLoadDelayState? loadDelay = null,
        RecompilerExceptionState? exception = null,
        RecompilerIrTerminationReason termination = RecompilerIrTerminationReason.Success,
        IEnumerable<RecompilerMemoryObservation>? memory = null)
    {
        ArgumentNullException.ThrowIfNull(gpr);
        if (!Enum.IsDefined(termination)) throw new ArgumentOutOfRangeException(nameof(termination));
        var registers = gpr.ToArray();
        if (registers.Length != 32) throw new ArgumentException("A state snapshot must contain exactly 32 GPR values.", nameof(gpr));
        registers[0] = 0;
        Gpr = new ReadOnlyCollection<uint>(registers);
        HI = hi;
        LO = lo;
        PC = pc;
        LoadDelay = loadDelay ?? new RecompilerLoadDelayState();
        Exception = exception ?? new RecompilerExceptionState();
        Termination = termination;
        Memory = new ReadOnlyCollection<RecompilerMemoryObservation>((memory ?? Array.Empty<RecompilerMemoryObservation>()).ToArray());
    }

    public IReadOnlyList<uint> Gpr { get; }
    public uint HI { get; }
    public uint LO { get; }
    public uint PC { get; }
    public RecompilerLoadDelayState LoadDelay { get; }
    public RecompilerExceptionState Exception { get; }
    public RecompilerIrTerminationReason Termination { get; }
    public IReadOnlyList<RecompilerMemoryObservation> Memory { get; }
}

[Domain]
public static class RecompilerIrSerializer
{
    public static string Serialize(RecompilerIrProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        var validation = RecompilerIrValidator.Validate(program);
        if (!validation.IsValid) throw new ArgumentException("IR must validate before serialization.", nameof(program));
        return ArtifactJson.Serialize(program);
    }

    public static string Serialize(RecompilerStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return ArtifactJson.Serialize(snapshot);
    }
}
