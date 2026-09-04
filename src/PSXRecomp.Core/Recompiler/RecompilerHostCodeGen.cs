using System.Collections.ObjectModel;
using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Recompiler;

[Domain]
public sealed record RecompilerHostCodeGenResult(
    bool Success,
    string? Source,
    string? DiagnosticCode,
    string? DiagnosticMessage);

[Domain]
public static class RecompilerHostCodeGen
{
    private const string StateStruct = "RecompilerState";
    private const string StateParam = "state";
    private const string CoreField = "core";
    private const string Sra32Helper = "recompiler_sra32";
    private const string TerminationField = "termination_reason";
    private const string NextPcField = "next_pc";
    private const string PcField = "pc";
    private const int IndentSpaces = 2;
    private const string IndentUnit = "  ";

    public static RecompilerHostCodeGenResult Generate(RecompilerIrProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);

        if (program.Blocks.Count == 0)
        {
            return new RecompilerHostCodeGenResult(
                false, null,
                "UNSUPPORTED_EMPTY_PROGRAM",
                "Host code generation requires at least one block; received zero blocks.");
        }

        var validation = RecompilerIrValidator.Validate(program);
        if (!validation.IsValid)
        {
            return new RecompilerHostCodeGenResult(
                false, null,
                "IR_VALIDATION_FAILED",
                $"IR validation failed with {validation.Diagnostics.Count} diagnostic(s): {validation.Diagnostics[0].Code}");
        }

        for (var i = 0; i < program.Blocks.Count; i++)
        {
            var definedResultIds = new HashSet<int>();
            for (var j = 0; j < program.Blocks[i].Operations.Count; j++)
            {
                var id = program.Blocks[i].Operations[j].ResultValueId;
                if (id >= 0 && !definedResultIds.Add(id))
                {
                    return new RecompilerHostCodeGenResult(
                        false, null,
                        "DUPLICATE_RESULT_VALUE_ID",
                        $"Result value id {id} is produced by more than one operation.");
                }
            }
        }

        for (var i = 0; i < program.Blocks.Count; i++)
        {
            for (var j = 0; j < program.Blocks[i].Operations.Count; j++)
            {
                var op = program.Blocks[i].Operations[j];
                if (!Enum.IsDefined(op.Kind))
                {
                    return new RecompilerHostCodeGenResult(
                        false, null,
                        "UNSUPPORTED_OPERATION_KIND",
                        $"Operation kind {(byte)op.Kind} is not a defined enum value.");
                }

                if (!IsEmittable(op.Kind))
                {
                    return new RecompilerHostCodeGenResult(
                        false, null,
                        "UNSUPPORTED_OPERATION_KIND",
                        $"Operation kind '{op.Kind}' has no host emission in this stage; " +
                        "generating a block that silently drops it would produce wrong host code.");
                }
            }

            if (!Enum.IsDefined(program.Blocks[i].Exit.Reason))
            {
                return new RecompilerHostCodeGenResult(
                    false, null,
                    "UNSUPPORTED_TERMINATION_REASON",
                    $"Termination reason {(byte)program.Blocks[i].Exit.Reason} is not a defined enum value.");
            }

            var flow = program.Blocks[i].Exit.Flow;
            if (flow is not null)
            {
                if (!IsEmittableFlow(flow.Kind))
                {
                    return new RecompilerHostCodeGenResult(
                        false, null,
                        "UNSUPPORTED_FLOW_KIND",
                        $"Exit flow kind '{flow.Kind}' has no host emission in this stage; " +
                        "generating a block that silently drops the transfer would produce wrong host code.");
                }
            }
        }

        var source = EmitSource(program);
        return new RecompilerHostCodeGenResult(true, source, null, null);
    }

    /// <summary>
    /// The operation kinds this stage emits host code for. Unsupported operation
    /// kinds are rejected rather than silently dropped, which would produce wrong
    /// host code by omitting a guest-visible side effect.
    /// </summary>
    private static bool IsEmittable(RecompilerIrOperationKind kind) => kind switch
    {
        RecompilerIrOperationKind.Nop => true,
        RecompilerIrOperationKind.Constant => true,
        RecompilerIrOperationKind.ReadGpr => true,
        RecompilerIrOperationKind.WriteGpr => true,
        RecompilerIrOperationKind.Add => true,
        RecompilerIrOperationKind.Subtract => true,
        RecompilerIrOperationKind.And => true,
        RecompilerIrOperationKind.Or => true,
        RecompilerIrOperationKind.Xor => true,
        RecompilerIrOperationKind.Nor => true,
        RecompilerIrOperationKind.ShiftLeftLogical => true,
        RecompilerIrOperationKind.ShiftRightLogical => true,
        RecompilerIrOperationKind.ShiftRightArithmetic => true,
        RecompilerIrOperationKind.CompareEqual => true,
        RecompilerIrOperationKind.CompareNotEqual => true,
        RecompilerIrOperationKind.Load8 => true,
        RecompilerIrOperationKind.Load16 => true,
        RecompilerIrOperationKind.Load32 => true,
        RecompilerIrOperationKind.Store8 => true,
        RecompilerIrOperationKind.Store16 => true,
        RecompilerIrOperationKind.Store32 => true,
        _ => false,
    };

    /// <summary>
    /// The flow kinds this stage can emit host code for. Return and any future
    /// unsupported flow kinds are rejected; they would produce wrong host code by
    /// silently dropping the transfer.
    /// </summary>
    private static bool IsEmittableFlow(RecompilerIrFlowKind kind) => kind switch
    {
        RecompilerIrFlowKind.Sequential => true,
        RecompilerIrFlowKind.Branch => true,
        RecompilerIrFlowKind.Jump => true,
        RecompilerIrFlowKind.Call => true,
        _ => false,
    };

    private static string EmitSource(RecompilerIrProgram program)
    {
        var sb = new StringBuilder();

        sb.AppendLine("#include <stdint.h>");
        sb.AppendLine();
        EmitTerminationReasonMacros(sb);
        EmitMemoryHelperDeclarations(sb);
        EmitStateStruct(sb);
        EmitSra32Helper(sb);

        foreach (var block in program.Blocks)
        {
            EmitBlockFunction(sb, block);
        }

        EmitDispatchFunction(sb, program);

        return sb.ToString();
    }

    private static void EmitTerminationReasonMacros(StringBuilder sb)
    {
        sb.AppendLine("/* RecompilerIrTerminationReason byte values (RecompilerContract). */");
        sb.AppendLine("#define RECOMPILER_REASON_SUCCESS 0");
        sb.AppendLine("#define RECOMPILER_REASON_UNSUPPORTED_IR 2");
        sb.AppendLine("#define RECOMPILER_REASON_EXECUTION_BUDGET_EXCEEDED 7");
        sb.AppendLine();
    }

    /// <summary>
    /// Declares the runtime memory helper functions that block functions call for
    /// guest memory access. The host (or test driver) must provide
    /// implementations of these at link time. Address translation, alignment,
    /// endianness, and bounds checking are the memory/runtime contract's
    /// responsibility, not this backend's.
    /// </summary>
    private static void EmitMemoryHelperDeclarations(StringBuilder sb)
    {
        sb.AppendLine("/* Runtime memory helpers — provided by the host at link time. */");
        sb.AppendLine("extern uint8_t  recompiler_read_mem8(void* core, uint32_t address);");
        sb.AppendLine("extern uint16_t recompiler_read_mem16(void* core, uint32_t address);");
        sb.AppendLine("extern uint32_t recompiler_read_mem32(void* core, uint32_t address);");
        sb.AppendLine("extern void     recompiler_write_mem8(void* core, uint32_t address, uint8_t value);");
        sb.AppendLine("extern void     recompiler_write_mem16(void* core, uint32_t address, uint16_t value);");
        sb.AppendLine("extern void     recompiler_write_mem32(void* core, uint32_t address, uint32_t value);");
        sb.AppendLine();
    }

    private static void EmitStateStruct(StringBuilder sb)
    {
        sb.AppendLine("typedef struct {");
        sb.AppendLine("  uint32_t gpr[32];");
        sb.AppendLine("  uint32_t hi;");
        sb.AppendLine("  uint32_t lo;");
        sb.AppendLine("  uint32_t pc;");
        sb.AppendLine("  int32_t " + TerminationField + ";");
        sb.AppendLine("  uint32_t " + NextPcField + ";");
        sb.AppendLine("  void* " + CoreField + ";");
        sb.AppendLine("} " + StateStruct + ";");
        sb.AppendLine();
    }

    private static void EmitSra32Helper(StringBuilder sb)
    {
        sb.AppendLine("static uint32_t " + Sra32Helper + "(uint32_t a, uint32_t s) {");
        sb.AppendLine(IndentUnit + "uint32_t sh = s & 31u;");
        sb.AppendLine(IndentUnit + "uint32_t result = a >> sh;");
        sb.AppendLine(IndentUnit + "if ((a & 0x80000000u) != 0u && sh != 0u) {");
        sb.AppendLine(IndentUnit + "  result |= (0xFFFFFFFFu << (32u - sh));");
        sb.AppendLine(IndentUnit + "}");
        sb.AppendLine(IndentUnit + "return result;");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void EmitBlockFunction(StringBuilder sb, RecompilerIrBlock block)
    {
        var functionName = $"recompiler_block_0x{block.EntryPc:X8}";
        var valueNames = new Dictionary<int, string>();

        sb.AppendLine($"static int32_t {functionName}({StateStruct}* {StateParam}) {{");

        foreach (var op in block.Operations)
        {
            var stmt = EmitOperation(op, valueNames);
            if (stmt != null)
            {
                sb.AppendLine(IndentUnit + stmt);
            }
        }

        sb.AppendLine(IndentUnit + EmitExit(block.Exit));
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static string? EmitOperation(
        RecompilerIrOperation op,
        Dictionary<int, string> valueNames)
    {
        var result = op.ResultValueId >= 0
            ? $"uint32_t v{op.ResultValueId}"
            : null;

        switch (op.Kind)
        {
            case RecompilerIrOperationKind.Nop:
                return null;

            case RecompilerIrOperationKind.Constant:
                if (result == null) return null;
                valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
                return $"{result} = {FormatImmediate(op.Immediate)};";

            case RecompilerIrOperationKind.ReadGpr:
                if (result == null) return null;
                valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
                return $"{result} = {StateParam}->gpr[{op.Register}];";

            case RecompilerIrOperationKind.WriteGpr:
                if (op.InputValueA < 0) return null;
                return $"{StateParam}->gpr[{op.Register}] = {ResolveValue(op.InputValueA, valueNames)};";

            case RecompilerIrOperationKind.Add:
                return EmitBinaryOp(result, op, "uint32_t", "+", valueNames);

            case RecompilerIrOperationKind.Subtract:
                return EmitBinaryOp(result, op, "uint32_t", "-", valueNames);

            case RecompilerIrOperationKind.And:
                return EmitBinaryOp(result, op, "uint32_t", "&", valueNames);

            case RecompilerIrOperationKind.Or:
                return EmitBinaryOp(result, op, "uint32_t", "|", valueNames);

            case RecompilerIrOperationKind.Xor:
                return EmitBinaryOp(result, op, "uint32_t", "^", valueNames);

            case RecompilerIrOperationKind.Nor:
                if (result == null) return null;
                valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
                return $"{result} = ~({ResolveValue(op.InputValueA, valueNames)} | {ResolveValue(op.InputValueB, valueNames)});";

            case RecompilerIrOperationKind.ShiftLeftLogical:
                if (result == null) return null;
                valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
                return $"{result} = (uint32_t){ResolveValue(op.InputValueA, valueNames)} << ({op.ShiftAmount}u & 31u);";

            case RecompilerIrOperationKind.ShiftRightLogical:
                if (result == null) return null;
                valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
                return $"{result} = (uint32_t){ResolveValue(op.InputValueA, valueNames)} >> ({op.ShiftAmount}u & 31u);";

            case RecompilerIrOperationKind.ShiftRightArithmetic:
                if (result == null) return null;
                valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
                return $"{result} = {Sra32Helper}({ResolveValue(op.InputValueA, valueNames)}, {op.ShiftAmount}u);";

            case RecompilerIrOperationKind.CompareEqual:
                if (result == null) return null;
                valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
                return $"{result} = ({ResolveValue(op.InputValueA, valueNames)} == {ResolveValue(op.InputValueB, valueNames)}) ? 1u : 0u;";

            case RecompilerIrOperationKind.CompareNotEqual:
                if (result == null) return null;
                valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
                return $"{result} = ({ResolveValue(op.InputValueA, valueNames)} != {ResolveValue(op.InputValueB, valueNames)}) ? 1u : 0u;";

            case RecompilerIrOperationKind.Load8:
                if (result == null) return null;
                valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
                return $"{result} = (uint32_t)recompiler_read_mem8({StateParam}->{CoreField}, {ResolveValue(op.InputValueA, valueNames)});";

            case RecompilerIrOperationKind.Load16:
                if (result == null) return null;
                valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
                return $"{result} = (uint32_t)recompiler_read_mem16({StateParam}->{CoreField}, {ResolveValue(op.InputValueA, valueNames)});";

            case RecompilerIrOperationKind.Load32:
                if (result == null) return null;
                valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
                return $"{result} = recompiler_read_mem32({StateParam}->{CoreField}, {ResolveValue(op.InputValueA, valueNames)});";

            case RecompilerIrOperationKind.Store8:
                return $"recompiler_write_mem8({StateParam}->{CoreField}, {ResolveValue(op.InputValueA, valueNames)}, (uint8_t){ResolveValue(op.InputValueB, valueNames)});";

            case RecompilerIrOperationKind.Store16:
                return $"recompiler_write_mem16({StateParam}->{CoreField}, {ResolveValue(op.InputValueA, valueNames)}, (uint16_t){ResolveValue(op.InputValueB, valueNames)});";

            case RecompilerIrOperationKind.Store32:
                return $"recompiler_write_mem32({StateParam}->{CoreField}, {ResolveValue(op.InputValueA, valueNames)}, {ResolveValue(op.InputValueB, valueNames)});";

            default:
                return null;
        }
    }

    private static string? EmitBinaryOp(
        string? result,
        RecompilerIrOperation op,
        string type,
        string opSymbol,
        Dictionary<int, string> valueNames)
    {
        if (result == null) return null;
        valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
        return $"{result} = ({type}){ResolveValue(op.InputValueA, valueNames)} {opSymbol} {ResolveValue(op.InputValueB, valueNames)};";
    }

    private static string ResolveValue(int valueId, Dictionary<int, string> valueNames)
    {
        return valueNames.TryGetValue(valueId, out var name) ? name : $"v{valueId}";
    }

    private static string EmitExit(RecompilerIrExit exit)
    {
        var flow = exit.Flow;
        if (flow is null || flow.Kind == RecompilerIrFlowKind.Sequential)
        {
            if (exit.Reason == RecompilerIrTerminationReason.Success && exit.NextPc.HasValue)
            {
                return $"{StateParam}->{NextPcField} = {FormatImmediate(exit.NextPc.Value)}; {StateParam}->{TerminationField} = 0; return 0;";
            }
        }

        if (flow is not null)
        {
            switch (flow.Kind)
            {
                case RecompilerIrFlowKind.Branch:
                    return EmitBranchExit(flow, exit);

                case RecompilerIrFlowKind.Jump:
                    return EmitJumpExit(flow);

                case RecompilerIrFlowKind.Call:
                    return EmitCallExit(flow, exit);
            }
        }

        var reason = (byte)exit.Reason;
        return $"{StateParam}->{TerminationField} = {reason}; return (int32_t){reason}u;";
    }

    private static string EmitBranchExit(RecompilerIrFlow flow, RecompilerIrExit exit)
    {
        var condVar = $"v{flow.ConditionValueId}";
        var takenTarget = FormatImmediate(flow.Target!.Value);
        var fallthroughTarget = FormatImmediate(exit.NextPc!.Value);
        return $"if ({condVar} != 0u) {{ {StateParam}->{NextPcField} = {takenTarget}; }} else {{ {StateParam}->{NextPcField} = {fallthroughTarget}; }} {StateParam}->{TerminationField} = 0; return 0;";
    }

    private static string EmitJumpExit(RecompilerIrFlow flow)
    {
        var target = FormatImmediate(flow.Target!.Value);
        return $"{StateParam}->{NextPcField} = {target}; {StateParam}->{TerminationField} = 0; return 0;";
    }

    private static string EmitCallExit(RecompilerIrFlow flow, RecompilerIrExit exit)
    {
        var calleeTarget = FormatImmediate(flow.Target!.Value);
        var returnAddress = FormatImmediate(exit.NextPc!.Value);
        return $"{StateParam}->{NextPcField} = {calleeTarget}; {StateParam}->{TerminationField} = 0; return 0;";
    }

    private static string FormatImmediate(uint value)
    {
        if (value <= 9)
            return value.ToString();

        return $"({value}u)";
    }

    private static void EmitDispatchFunction(StringBuilder sb, RecompilerIrProgram program)
    {
        sb.AppendLine($"int32_t recompiler_dispatch({StateStruct}* {StateParam}, uint32_t budget) {{");
        sb.AppendLine(IndentUnit + "uint32_t steps = 0;");
        sb.AppendLine(IndentUnit + "for (;;) {");

        // Resolve the block function for the current PC. A PC that matches no
        // block means the straight-line program has fallen off the end (normal
        // completion once at least one step ran); a PC that matches no block on
        // the very first step means the entry PC cannot start the program.
        for (var i = 0; i < program.Blocks.Count; i++)
        {
            var functionName = $"recompiler_block_0x{program.Blocks[i].EntryPc:X8}";
            var cond = i == 0
                ? $"{StateParam}->{PcField} == {FormatImmediate(program.Blocks[i].EntryPc)}"
                : $"else if ({StateParam}->{PcField} == {FormatImmediate(program.Blocks[i].EntryPc)})";
            var bodyLine = $"{StateParam}->{TerminationField} = {functionName}({StateParam});";
            if (i == 0)
            {
                sb.AppendLine(IndentUnit + IndentUnit + $"if ({cond}) {{");
            }
            else
            {
                sb.AppendLine(IndentUnit + IndentUnit + cond + " {");
            }
            sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + bodyLine);
            sb.AppendLine(IndentUnit + IndentUnit + "}");
        }

        sb.AppendLine(IndentUnit + IndentUnit + "else {");
        sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + $"if (steps > 0) {{ {StateParam}->{TerminationField} = RECOMPILER_REASON_SUCCESS; return 0; }}");
        sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + $"{StateParam}->{TerminationField} = RECOMPILER_REASON_UNSUPPORTED_IR; return (int32_t)RECOMPILER_REASON_UNSUPPORTED_IR;");
        sb.AppendLine(IndentUnit + IndentUnit + "}");

        // Stop on a non-Success termination before enforcing the budget.
        sb.AppendLine(IndentUnit + IndentUnit + $"if ({StateParam}->{TerminationField} != RECOMPILER_REASON_SUCCESS) {{");
        sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + $"return {StateParam}->{TerminationField};");
        sb.AppendLine(IndentUnit + IndentUnit + "}");

        // Bounded execution: refuse to retire more than `budget` instructions.
        sb.AppendLine(IndentUnit + IndentUnit + "if (steps >= budget) {");
        sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + $"{StateParam}->{TerminationField} = RECOMPILER_REASON_EXECUTION_BUDGET_EXCEEDED;");
        sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + "return (int32_t)RECOMPILER_REASON_EXECUTION_BUDGET_EXCEEDED;");
        sb.AppendLine(IndentUnit + IndentUnit + "}");

        // Advance the sequential program counter.
        sb.AppendLine(IndentUnit + IndentUnit + $"{StateParam}->{PcField} = {StateParam}->{NextPcField};");
        sb.AppendLine(IndentUnit + IndentUnit + "steps++;");
        sb.AppendLine(IndentUnit + "}");
        sb.AppendLine("}");
        sb.AppendLine();
    }
}
