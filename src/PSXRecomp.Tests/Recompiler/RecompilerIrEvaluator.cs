using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

/// <summary>
/// A guest RAM window addressed exactly like the native core: KUSEG is physical,
/// KSEG0/KSEG1 mask off the region bits (<c>src/PSXRecomp.Native/src/psx_cpu.cpp</c>).
/// Access is little-endian, matching the PS1.
/// </summary>
[Test]
internal sealed class RecompilerGuestMemory
{
    public const uint RamSize = 2 * 1024 * 1024;

    private readonly byte[] _ram = new byte[RamSize];

    public static uint Translate(uint virtualAddress)
    {
        if (virtualAddress <= 0x7FFFFFFF) return virtualAddress;
        if (virtualAddress <= 0xBFFFFFFF) return virtualAddress & 0x1FFFFFFF;
        throw new ArgumentOutOfRangeException(
            nameof(virtualAddress), virtualAddress, "The test memory model maps KUSEG/KSEG0/KSEG1 only.");
    }

    public byte Read8(uint virtualAddress) => _ram[Translate(virtualAddress)];

    public ushort Read16(uint virtualAddress)
    {
        var physical = Translate(virtualAddress);
        return (ushort)(_ram[physical] | (_ram[physical + 1] << 8));
    }

    public uint Read32(uint virtualAddress)
    {
        var physical = Translate(virtualAddress);
        return (uint)(_ram[physical]
            | (_ram[physical + 1] << 8)
            | (_ram[physical + 2] << 16)
            | (_ram[physical + 3] << 24));
    }

    public void Write8(uint virtualAddress, byte value) => _ram[Translate(virtualAddress)] = value;

    public void Write16(uint virtualAddress, ushort value)
    {
        var physical = Translate(virtualAddress);
        _ram[physical] = (byte)value;
        _ram[physical + 1] = (byte)(value >> 8);
    }

    public void Write32(uint virtualAddress, uint value)
    {
        var physical = Translate(virtualAddress);
        _ram[physical] = (byte)value;
        _ram[physical + 1] = (byte)(value >> 8);
        _ram[physical + 2] = (byte)(value >> 16);
        _ram[physical + 3] = (byte)(value >> 24);
    }
}

[Test]
internal sealed record RecompilerIrEvaluationResult(
    IReadOnlyList<uint> Gpr,
    uint Pc,
    RecompilerIrTerminationReason Termination,
    uint BlocksRetired);

/// <summary>
/// A reference evaluator for the IR, used only by the lowering tests as an
/// oracle: it executes a <see cref="RecompilerIrProgram"/> directly, so a test
/// can compare the lowering's meaning against the native R3000A interpreter
/// rather than only against the shape of the emitted operations.
/// </summary>
/// <remarks>
/// It is deliberately a test asset, not production code: the host code generator
/// remains the supported backend, and this evaluator exists so a lowering change
/// that is shaped correctly but means something else still fails a test.
/// </remarks>
[Test]
internal static class RecompilerIrEvaluator
{
    public static RecompilerIrEvaluationResult Run(
        RecompilerIrProgram program,
        uint entryPc,
        IReadOnlyList<uint> initialGpr,
        RecompilerGuestMemory memory,
        uint blockBudget)
    {
        var gpr = initialGpr.ToArray();
        gpr[0] = 0;

        var blocks = program.Blocks.ToDictionary(block => block.EntryPc);
        var pc = entryPc;
        uint retired = 0;

        while (true)
        {
            if (!blocks.TryGetValue(pc, out var block))
            {
                // Control left the lowered program; the run completed.
                return new RecompilerIrEvaluationResult(gpr, pc, RecompilerIrTerminationReason.Success, retired);
            }

            if (retired >= blockBudget)
            {
                return new RecompilerIrEvaluationResult(
                    gpr, pc, RecompilerIrTerminationReason.ExecutionBudgetExceeded, retired);
            }

            var values = new Dictionary<int, uint>();
            foreach (var operation in block.Operations)
            {
                Execute(operation, gpr, values, memory);
            }

            retired++;

            var exit = block.Exit;
            if (exit.Reason != RecompilerIrTerminationReason.Success)
            {
                return new RecompilerIrEvaluationResult(gpr, pc, exit.Reason, retired);
            }

            pc = NextPc(exit, values);
        }
    }

    private static uint NextPc(RecompilerIrExit exit, Dictionary<int, uint> values)
    {
        var flow = exit.Flow;
        if (flow is null || flow.Kind == RecompilerIrFlowKind.Sequential)
        {
            return exit.NextPc!.Value;
        }

        return flow.Kind switch
        {
            RecompilerIrFlowKind.Branch => values[flow.ConditionValueId] != 0 ? flow.Target!.Value : exit.NextPc!.Value,
            RecompilerIrFlowKind.Jump => flow.Target!.Value,
            _ => throw new NotSupportedException($"Flow kind '{flow.Kind}' is reserved and has no evaluation."),
        };
    }

    private static void Execute(
        RecompilerIrOperation operation,
        uint[] gpr,
        Dictionary<int, uint> values,
        RecompilerGuestMemory memory)
    {
        switch (operation.Kind)
        {
            case RecompilerIrOperationKind.Nop:
                return;
            case RecompilerIrOperationKind.Constant:
                values[operation.ResultValueId] = operation.Immediate;
                return;
            case RecompilerIrOperationKind.ReadGpr:
                values[operation.ResultValueId] = gpr[operation.Register];
                return;
            case RecompilerIrOperationKind.WriteGpr:
                gpr[operation.Register] = values[operation.InputValueA];
                return;
            case RecompilerIrOperationKind.Store8:
                memory.Write8(values[operation.InputValueA], (byte)values[operation.InputValueB]);
                return;
            case RecompilerIrOperationKind.Store16:
                memory.Write16(values[operation.InputValueA], (ushort)values[operation.InputValueB]);
                return;
            case RecompilerIrOperationKind.Store32:
                memory.Write32(values[operation.InputValueA], values[operation.InputValueB]);
                return;
            default:
                values[operation.ResultValueId] = Evaluate(operation, values, memory);
                return;
        }
    }

    private static uint Evaluate(
        RecompilerIrOperation operation, Dictionary<int, uint> values, RecompilerGuestMemory memory)
    {
        var a = operation.InputValueA >= 0 ? values[operation.InputValueA] : 0u;
        var b = operation.InputValueB >= 0 ? values[operation.InputValueB] : 0u;

        return operation.Kind switch
        {
            RecompilerIrOperationKind.Add => unchecked(a + b),
            RecompilerIrOperationKind.Subtract => unchecked(a - b),
            RecompilerIrOperationKind.And => a & b,
            RecompilerIrOperationKind.Or => a | b,
            RecompilerIrOperationKind.Xor => a ^ b,
            RecompilerIrOperationKind.Nor => ~(a | b),
            RecompilerIrOperationKind.ShiftLeftLogical => a << (operation.ShiftAmount & 31),
            RecompilerIrOperationKind.ShiftRightLogical => a >> (operation.ShiftAmount & 31),
            RecompilerIrOperationKind.ShiftRightArithmetic => unchecked((uint)((int)a >> (operation.ShiftAmount & 31))),
            RecompilerIrOperationKind.Load8 => memory.Read8(a),
            RecompilerIrOperationKind.Load16 => memory.Read16(a),
            RecompilerIrOperationKind.Load32 => memory.Read32(a),
            RecompilerIrOperationKind.CompareEqual => a == b ? 1u : 0u,
            RecompilerIrOperationKind.CompareNotEqual => a != b ? 1u : 0u,
            _ => throw new NotSupportedException($"Operation kind '{operation.Kind}' has no evaluation."),
        };
    }
}
