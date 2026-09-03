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

[Domain]
public sealed record RecompilerIrExit
{
    public RecompilerIrExit(RecompilerIrTerminationReason reason, uint? nextPc = null)
    {
        Reason = reason;
        NextPc = nextPc;
    }

    public RecompilerIrTerminationReason Reason { get; }
    public uint? NextPc { get; }
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

[Domain]
public sealed record RecompilerIrProgram
{
    public RecompilerIrProgram(IEnumerable<RecompilerIrBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        Blocks = new ReadOnlyCollection<RecompilerIrBlock>(blocks.OrderBy(block => block.EntryPc).ToArray());
    }

    public IReadOnlyList<RecompilerIrBlock> Blocks { get; }
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
            ValidateExit(block.Exit, diagnostics, blockIndex);
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
        }

        return new RecompilerIrValidationResult(new ReadOnlyCollection<RecompilerIrDiagnostic>(diagnostics));
    }

    private static void ValidateExit(RecompilerIrExit exit, List<RecompilerIrDiagnostic> diagnostics, int blockIndex)
    {
        if (!Enum.IsDefined(exit.Reason))
        {
            Add(diagnostics, RecompilerIrDiagnosticCode.IllegalTermination, "Termination reason must be a defined value.", blockIndex);
        }
        else if (exit.Reason == RecompilerIrTerminationReason.Success && exit.NextPc is null)
        {
            Add(diagnostics, RecompilerIrDiagnosticCode.IllegalTermination, "Success exits require a next PC.", blockIndex);
        }
        else if (exit.Reason != RecompilerIrTerminationReason.Success && exit.NextPc is not null)
        {
            Add(diagnostics, RecompilerIrDiagnosticCode.IllegalTermination, "Non-success exits must not provide a next PC.", blockIndex);
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

        var binary = operation.Kind is RecompilerIrOperationKind.Add or RecompilerIrOperationKind.Subtract
            or RecompilerIrOperationKind.And or RecompilerIrOperationKind.Or or RecompilerIrOperationKind.Xor or RecompilerIrOperationKind.Nor;
        var shift = operation.Kind is RecompilerIrOperationKind.ShiftLeftLogical or RecompilerIrOperationKind.ShiftRightLogical or RecompilerIrOperationKind.ShiftRightArithmetic;

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
            default:
                Require(hasResult && hasA && (binary ? hasB : !hasB) && (shift ? operation.ShiftAmount <= 31 : operation.ShiftAmount == 0), diagnostics, blockIndex, operationIndex);
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
