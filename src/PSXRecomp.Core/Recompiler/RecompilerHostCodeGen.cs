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
    private const string Sra32Helper = "recompiler_sra32";
    private const int IndentSpaces = 2;
    private const string IndentUnit = "  ";

    public static RecompilerHostCodeGenResult Generate(RecompilerIrProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);

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
            }

            if (!Enum.IsDefined(program.Blocks[i].Exit.Reason))
            {
                return new RecompilerHostCodeGenResult(
                    false, null,
                    "UNSUPPORTED_TERMINATION_REASON",
                    $"Termination reason {(byte)program.Blocks[i].Exit.Reason} is not a defined enum value.");
            }
        }

        var source = EmitSource(program);
        return new RecompilerHostCodeGenResult(true, source, null, null);
    }

    private static string EmitSource(RecompilerIrProgram program)
    {
        var sb = new StringBuilder();

        sb.AppendLine("#include <stdint.h>");
        sb.AppendLine();
        EmitStateStruct(sb);
        EmitSra32Helper(sb);

        foreach (var block in program.Blocks)
        {
            EmitBlockFunction(sb, block);
        }

        EmitDispatchFunction(sb, program);

        return sb.ToString();
    }

    private static void EmitStateStruct(StringBuilder sb)
    {
        sb.AppendLine("typedef struct {");
        sb.AppendLine("  uint32_t gpr[32];");
        sb.AppendLine("  uint32_t hi;");
        sb.AppendLine("  uint32_t lo;");
        sb.AppendLine("  uint32_t pc;");
        sb.AppendLine("  int32_t termination_reason;");
        sb.AppendLine("  uint32_t next_pc;");
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

    private static string EmitOperation(
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

            default:
                return null;
        }
    }

    private static string EmitBinaryOp(
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
        if (exit.Reason == RecompilerIrTerminationReason.Success && exit.NextPc.HasValue)
        {
            return $"{StateParam}->next_pc = {FormatImmediate(exit.NextPc.Value)}; return 0;";
        }

        return $"return (int32_t){(byte)exit.Reason}u;";
    }

    private static string FormatImmediate(uint value)
    {
        if (value <= 9)
            return value.ToString();

        return $"({value}u)";
    }

    private static void EmitDispatchFunction(StringBuilder sb, RecompilerIrProgram program)
    {
        sb.AppendLine("int32_t recompiler_dispatch(" + StateStruct + "* " + StateParam + ") {");

        foreach (var block in program.Blocks)
        {
            var functionName = $"recompiler_block_0x{block.EntryPc:X8}";
            sb.AppendLine(IndentUnit + $"return {functionName}({StateParam});");
        }

        sb.AppendLine("}");
        sb.AppendLine();
    }
}
