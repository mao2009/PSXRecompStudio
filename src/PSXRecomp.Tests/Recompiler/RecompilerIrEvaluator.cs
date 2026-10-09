using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Tests.Recompiler;

/// <summary>
/// A guest RAM window addressed exactly like the native core: KUSEG is physical,
/// KSEG0/KSEG1 mask off the region bits (<c>src/PSXRecomp.Native/src/psx_cpu_memory_access.cpp</c>).
/// Access is little-endian, matching the PS1.
/// </summary>
[Test]
internal sealed class RecompilerGuestMemory
{
    public const uint RamSize = 2 * 1024 * 1024;

    private readonly byte[] _ram = new byte[RamSize];

    public static uint Translate(uint virtualAddress)
    {
        if (!Ps1AddressTranslation.TryTranslate(virtualAddress, out var physical))
        {
            throw new ArgumentOutOfRangeException(
                nameof(virtualAddress), virtualAddress, "The test memory model maps KUSEG/KSEG0/KSEG1 only.");
        }
        return physical;
    }

    public byte Read8(uint virtualAddress)
    {
        var physical = Translate(virtualAddress);
        if (physical >= RamSize) return 0;
        return _ram[physical];
    }

    public ushort Read16(uint virtualAddress)
    {
        var physical = Translate(virtualAddress);
        if (physical > RamSize - 2) return 0;
        return (ushort)(_ram[physical] | (_ram[physical + 1] << 8));
    }

    public uint Read32(uint virtualAddress)
    {
        var physical = Translate(virtualAddress);
        if (physical > RamSize - 4) return 0;
        return (uint)(_ram[physical]
            | (_ram[physical + 1] << 8)
            | (_ram[physical + 2] << 16)
            | (_ram[physical + 3] << 24));
    }

    public void Write8(uint virtualAddress, byte value)
    {
        var physical = Translate(virtualAddress);
        if (physical >= RamSize) return;
        _ram[physical] = value;
    }

    public void Write16(uint virtualAddress, ushort value)
    {
        var physical = Translate(virtualAddress);
        if (physical > RamSize - 2) return;
        _ram[physical] = (byte)value;
        _ram[physical + 1] = (byte)(value >> 8);
    }

    public void Write32(uint virtualAddress, uint value)
    {
        var physical = Translate(virtualAddress);
        if (physical > RamSize - 4) return;
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
    uint BlocksRetired,
    uint Hi = 0,
    uint Lo = 0,
    RecompilerExceptionState? Exception = null,
    IReadOnlyList<uint>? Cop0 = null);

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
        uint blockBudget,
        uint[]? cop0 = null)
    {
        // COP0 registers by number (PSXCpu::cop0_), threaded like hiLo (Issue #732).
        cop0 ??= new uint[32];
        var gpr = initialGpr.ToArray();
        gpr[0] = 0;
        // [0] = HI, [1] = LO — architectural state distinct from the 32 GPRs
        // (see RecompilerIrOperationKind.ReadHi), threaded the same way gpr is.
        var hiLo = new uint[2];

        var blocks = program.Blocks.ToDictionary(block => block.EntryPc);
        var pc = entryPc;
        uint retired = 0;

        while (true)
        {
            if (!blocks.TryGetValue(pc, out var block))
            {
                // Control left the lowered program; the run completed.
                return new RecompilerIrEvaluationResult(
                    gpr, pc, RecompilerIrTerminationReason.Success, retired, hiLo[0], hiLo[1], Cop0: cop0);
            }

            if (retired >= blockBudget)
            {
                return new RecompilerIrEvaluationResult(
                    gpr, pc, RecompilerIrTerminationReason.ExecutionBudgetExceeded, retired, hiLo[0], hiLo[1], Cop0: cop0);
            }

            var values = new Dictionary<int, uint>();
            foreach (var operation in block.Operations)
            {
                if (!Execute(operation, gpr, hiLo, cop0, values, memory))
                {
                    return new RecompilerIrEvaluationResult(
                        gpr, pc, RecompilerIrTerminationReason.Exception, retired + 1, hiLo[0], hiLo[1], Cop0: cop0);
                }
            }

            retired++;

            var exit = block.Exit;
            if (exit.Reason != RecompilerIrTerminationReason.Success)
            {
                return new RecompilerIrEvaluationResult(
                    gpr, pc, exit.Reason, retired, hiLo[0], hiLo[1], exit.Exception, cop0);
            }

            pc = NextPc(exit, values);
        }
    }

    private static uint NextPc(RecompilerIrExit exit, Dictionary<int, uint> values)
    {
        if (exit.TargetValueId is { } targetValueId)
        {
            // JR/JALR: the runtime target value is the next PC (Issue #635).
            return values[targetValueId];
        }

        var flow = exit.Flow;
        if (flow is null || flow.Kind == RecompilerIrFlowKind.Sequential)
        {
            return exit.NextPc!.Value;
        }

        return flow.Kind switch
        {
            RecompilerIrFlowKind.Branch => values[flow.ConditionValueId] != 0 ? flow.Target!.Value : exit.NextPc!.Value,
            RecompilerIrFlowKind.Jump => flow.Target!.Value,
            // A call transfers to the callee; the exit's next PC is the return
            // address, which the linked GPR carries and a later return resolves.
            RecompilerIrFlowKind.Call => flow.Target!.Value,
            _ => throw new NotSupportedException($"Flow kind '{flow.Kind}' is reserved and has no evaluation."),
        };
    }

    private static bool Execute(
        RecompilerIrOperation operation,
        uint[] gpr,
        uint[] hiLo,
        uint[] cop0,
        Dictionary<int, uint> values,
        RecompilerGuestMemory memory)
    {
        if (operation.Kind is RecompilerIrOperationKind.Store8 or RecompilerIrOperationKind.Store16 or RecompilerIrOperationKind.Store32
            && RecompilerCop0.StoreIsCacheIsolated(cop0[RecompilerCop0.Status], values[operation.InputValueA]))
        {
            return true;
        }

        switch (operation.Kind)
        {
            case RecompilerIrOperationKind.ReadCop0:
                values[operation.ResultValueId] = cop0[operation.Register];
                return true;
            case RecompilerIrOperationKind.WriteCop0:
                cop0[operation.Register] = operation.Register == RecompilerCop0.Cause
                    ? RecompilerCop0.WriteCause(cop0[RecompilerCop0.Cause], values[operation.InputValueA])
                    : values[operation.InputValueA];
                return true;
            case RecompilerIrOperationKind.ReturnFromException:
                cop0[RecompilerCop0.Status] = RecompilerCop0.ReturnFromException(cop0[RecompilerCop0.Status]);
                return true;
            case RecompilerIrOperationKind.Nop:
                return true;
            case RecompilerIrOperationKind.Constant:
                values[operation.ResultValueId] = operation.Immediate;
                return true;
            case RecompilerIrOperationKind.ReadGpr:
                values[operation.ResultValueId] = gpr[operation.Register];
                return true;
            case RecompilerIrOperationKind.WriteGpr:
                gpr[operation.Register] = values[operation.InputValueA];
                return true;
            case RecompilerIrOperationKind.Store8:
                memory.Write8(values[operation.InputValueA], (byte)values[operation.InputValueB]);
                return true;
            case RecompilerIrOperationKind.Store16:
                memory.Write16(values[operation.InputValueA], (ushort)values[operation.InputValueB]);
                return true;
            case RecompilerIrOperationKind.Store32:
                memory.Write32(values[operation.InputValueA], values[operation.InputValueB]);
                return true;
            case RecompilerIrOperationKind.ReadHi:
                values[operation.ResultValueId] = hiLo[0];
                return true;
            case RecompilerIrOperationKind.ReadLo:
                values[operation.ResultValueId] = hiLo[1];
                return true;
            case RecompilerIrOperationKind.WriteHi:
                hiLo[0] = values[operation.InputValueA];
                return true;
            case RecompilerIrOperationKind.WriteLo:
                hiLo[1] = values[operation.InputValueA];
                return true;
            case RecompilerIrOperationKind.MultiplySigned:
            {
                var product = (long)(int)values[operation.InputValueA] * (int)values[operation.InputValueB];
                hiLo[0] = unchecked((uint)(product >> 32));
                hiLo[1] = unchecked((uint)(product & 0xFFFFFFFFu));
                return true;
            }
            case RecompilerIrOperationKind.MultiplyUnsigned:
            {
                var product = (ulong)values[operation.InputValueA] * values[operation.InputValueB];
                hiLo[0] = unchecked((uint)(product >> 32));
                hiLo[1] = unchecked((uint)(product & 0xFFFFFFFFu));
                return true;
            }
            case RecompilerIrOperationKind.DivideSigned:
            {
                var n = (int)values[operation.InputValueA];
                var d = (int)values[operation.InputValueB];
                if (d == 0)
                {
                    hiLo[1] = n >= 0 ? uint.MaxValue : 1u;
                    hiLo[0] = unchecked((uint)n);
                }
                else if (n == int.MinValue && d == -1)
                {
                    hiLo[0] = 0u;
                    hiLo[1] = 0x80000000u;
                }
                else
                {
                    hiLo[0] = unchecked((uint)(n % d));
                    hiLo[1] = unchecked((uint)(n / d));
                }
                return true;
            }
            case RecompilerIrOperationKind.DivideUnsigned:
            {
                var dividend = values[operation.InputValueA];
                var divisor = values[operation.InputValueB];
                if (divisor == 0)
                {
                    hiLo[0] = dividend;
                    hiLo[1] = uint.MaxValue;
                }
                else
                {
                    hiLo[0] = dividend % divisor;
                    hiLo[1] = dividend / divisor;
                }
                return true;
            }
            default:
                if (operation.Kind == RecompilerIrOperationKind.AddSigned)
                {
                    var left = values[operation.InputValueA];
                    var right = values[operation.InputValueB];
                    var result = unchecked(left + right);
                    if (((left ^ result) & (right ^ result) & 0x80000000u) != 0)
                    {
                        return false;
                    }

                    values[operation.ResultValueId] = result;
                    return true;
                }

                values[operation.ResultValueId] = Evaluate(operation, values, memory);
                return true;
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
            RecompilerIrOperationKind.ShiftLeftLogicalVariable => a << (int)(b & 31),
            RecompilerIrOperationKind.ShiftRightLogicalVariable => a >> (int)(b & 31),
            RecompilerIrOperationKind.ShiftRightArithmeticVariable => unchecked((uint)((int)a >> (int)(b & 31))),
            RecompilerIrOperationKind.Load8 => memory.Read8(a),
            RecompilerIrOperationKind.Load16 => memory.Read16(a),
            RecompilerIrOperationKind.Load32 => memory.Read32(a),
            RecompilerIrOperationKind.CompareEqual => a == b ? 1u : 0u,
            RecompilerIrOperationKind.CompareNotEqual => a != b ? 1u : 0u,
            RecompilerIrOperationKind.CompareLessThanSigned => unchecked((uint)((int)a < (int)b ? 1 : 0)),
            RecompilerIrOperationKind.CompareLessThanUnsigned => a < b ? 1u : 0u,
            _ => throw new NotSupportedException($"Operation kind '{operation.Kind}' has no evaluation."),
        };
    }
}
