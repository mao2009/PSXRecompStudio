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
    private const string MultSignedHelper = "recompiler_mult_signed";
    private const string MultUnsignedHelper = "recompiler_mult_unsigned";
    private const string DivSignedHelper = "recompiler_div_signed";
    private const string DivUnsignedHelper = "recompiler_div_unsigned";
    private const string TerminationField = "termination_reason";
    private const string NextPcField = "next_pc";
    private const string ExceptionRaisedField = "exception_raised";
    private const string ExceptionCodeField = "exception_code";
    private const string ExceptionFaultPcField = "exception_fault_pc";
    private const string ExceptionInDelaySlotField = "exception_in_delay_slot";
    private const string PcField = "pc";
    private const string HostTransferField = "host_transfer";
    private const uint SyscallExcode = MipsToIrLowerer.SyscallExcode;
    private const string HostSyscallField = "host_syscall";
    private const string Cop0SrField = "cop0_sr";
    private const string HostTransferFnType = "recompiler_host_transfer_fn";
    private const string HostRetiredFnType = "recompiler_host_retired_fn";
    private const string HostRetiredField = "host_retired";
    private const string HostInterruptFnType = "recompiler_host_interrupt_fn";
    private const string HostInterruptField = "host_interrupt";
    private const string Cop0CauseField = "cop0_cause";
    private const string Cop0EpcField = "cop0_epc";
    private const string IrqLineField = "irq_line";
    private const string IndirectTargetField = "indirect_target";
    private const string RetiredTotalField = "retired_total";
    private const string RetiredReportedField = "retired_reported";
    private const string Cop0OtherField = "cop0_other";
    private const string GuestExceptionsField = "guest_exceptions";
    private const string Cop0SlotHelper = "recompiler_cop0_slot";
    private const string Cop0WriteHelper = "recompiler_cop0_write";
    private const string StoreIsolatedHelper = "recompiler_store_isolated";
    private const string ExceptionEntryHelper = "recompiler_exception_entry";
    private const string CodeGuardHelper = "recompiler_code_current";
    private const string UnknownPcLabel = "recompiler_unknown_pc";

    // Set for the duration of one Generate call (single-threaded codegen); false emits the historical text.
    [ThreadStatic] private static bool s_guardStats;
    private const int IndentSpaces = 2;
    private const string IndentUnit = "  ";

    public static RecompilerHostCodeGenResult Generate(RecompilerIrProgram program) => Generate(program, loadedCode: null);

    /// <summary>
    /// <see cref="Generate(RecompilerIrProgram)"/>, plus the pre-generated versions of RAM-placed code (Issue #732). At a
    /// PC of <paramref name="loadedCode"/> the dispatcher asks <c>recompiler_code_current</c> (a host-provided helper)
    /// which version guest memory holds now and runs that version's block; with none it takes the unknown-PC boundary
    /// (the host transfer) exactly as for a PC without a block. Null or empty generates the same source as the
    /// single-argument overload.
    /// </summary>
    public static RecompilerHostCodeGenResult Generate(RecompilerIrProgram program, LoadedCodeTable? loadedCode)
        => Generate(program, loadedCode, traceUnits: false, guardStats: false);

    /// <summary>
    /// <see cref="Generate(RecompilerIrProgram, LoadedCodeTable?)"/>, plus the opt-in unit retirement trace (Issue #774).
    /// With <paramref name="traceUnits"/> the generated dispatcher calls the state's <c>host_unit_trace</c> hook once per
    /// dispatch unit: at its retirement credit, or at a stop, with the unit's entry PC, next PC, the cumulative native
    /// total before and after the unit (so their difference is the instructions it actually completed), flags
    /// (interrupted, guest exception) and termination. The same build also contains <c>recompiler_trace_attach</c>, which
    /// writes the trace to the file named by the PSX_UNIT_TRACE_FILE environment variable. False generates the same
    /// source byte for byte as the overload without the flag, so the default artifact is unchanged.
    /// With <paramref name="guardStats"/> the dispatcher also counts, per RAM-placed entry PC, arrivals, guard hits per version
    /// and guard misses, and writes them to the file named by PSX_GUARD_STATS_FILE (measurement only, Issue #773). Off, the
    /// emitted text is exactly what it was.
    /// </summary>
    public static RecompilerHostCodeGenResult Generate(
        RecompilerIrProgram program, LoadedCodeTable? loadedCode, bool traceUnits = false, bool guardStats = false, bool allowEmptyProgram = false)
    {
        s_guardStats = guardStats;
        ArgumentNullException.ThrowIfNull(program);
        loadedCode ??= LoadedCodeTable.Empty;
        var entries = program.Blocks.Select(static block => block.EntryPc).ToHashSet();
        if (loadedCode.Blocks.FirstOrDefault(v => entries.Contains(v.Block.EntryPc) || v.Words.Count != v.Block.RetiredInstructionCount) is { } invalid)
        {
            return new RecompilerHostCodeGenResult(
                false, null,
                "INVALID_LOADED_CODE",
                $"The loaded-code version at PC 0x{invalid.Block.EntryPc:X8} collides with a static block or does not carry exactly its unit's words.");
        }

        if (loadedCode.Blocks.Select(static v => RecompilerIrValidator.Validate(new RecompilerIrProgram([v.Block])))
                .FirstOrDefault(static r => !r.IsValid) is { } invalidVersion)
        {
            return new RecompilerHostCodeGenResult(
                false, null,
                "IR_VALIDATION_FAILED",
                $"IR validation failed with {invalidVersion.Diagnostics.Count} diagnostic(s): {invalidVersion.Diagnostics[0].Code}");
        }

        var allBlocks = program.Blocks.Concat(loadedCode.Blocks.Select(static v => v.Block)).ToArray();
        // allowEmptyProgram (Issue #B2): a firmware distribution whose BIOS is never translated may hold no block at all;
        // every PC then runs in the interpreter fallback. Other callers keep the guard.
        if (allBlocks.Length == 0 && !allowEmptyProgram)
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

        for (var i = 0; i < allBlocks.Length; i++)
        {
            var definedResultIds = new HashSet<int>();
            for (var j = 0; j < allBlocks[i].Operations.Count; j++)
            {
                var id = allBlocks[i].Operations[j].ResultValueId;
                if (id >= 0 && !definedResultIds.Add(id))
                {
                    return new RecompilerHostCodeGenResult(
                        false, null,
                        "DUPLICATE_RESULT_VALUE_ID",
                        $"Result value id {id} is produced by more than one operation.");
                }
            }
        }

        for (var i = 0; i < allBlocks.Length; i++)
        {
            for (var j = 0; j < allBlocks[i].Operations.Count; j++)
            {
                var op = allBlocks[i].Operations[j];
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

            if (!Enum.IsDefined(allBlocks[i].Exit.Reason))
            {
                return new RecompilerHostCodeGenResult(
                    false, null,
                    "UNSUPPORTED_TERMINATION_REASON",
                    $"Termination reason {(byte)allBlocks[i].Exit.Reason} is not a defined enum value.");
            }

            var flow = allBlocks[i].Exit.Flow;
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

        var source = EmitSource(program, loadedCode, traceUnits);
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
        RecompilerIrOperationKind.ShiftLeftLogicalVariable => true,
        RecompilerIrOperationKind.ShiftRightLogicalVariable => true,
        RecompilerIrOperationKind.ShiftRightArithmeticVariable => true,
        RecompilerIrOperationKind.CompareEqual => true,
        RecompilerIrOperationKind.CompareNotEqual => true,
        RecompilerIrOperationKind.CompareLessThanSigned => true,
        RecompilerIrOperationKind.CompareLessThanUnsigned => true,
        RecompilerIrOperationKind.AddSigned => true,
        RecompilerIrOperationKind.Load8 => true,
        RecompilerIrOperationKind.Load16 => true,
        RecompilerIrOperationKind.Load32 => true,
        RecompilerIrOperationKind.Store8 => true,
        RecompilerIrOperationKind.Store16 => true,
        RecompilerIrOperationKind.Store32 => true,
        RecompilerIrOperationKind.ReadHi => true,
        RecompilerIrOperationKind.ReadLo => true,
        RecompilerIrOperationKind.WriteHi => true,
        RecompilerIrOperationKind.WriteLo => true,
        RecompilerIrOperationKind.MultiplySigned => true,
        RecompilerIrOperationKind.MultiplyUnsigned => true,
        RecompilerIrOperationKind.DivideSigned => true,
        RecompilerIrOperationKind.DivideUnsigned => true,
        RecompilerIrOperationKind.ReadCop0 => true,
        RecompilerIrOperationKind.WriteCop0 => true,
        RecompilerIrOperationKind.ReturnFromException => true,
        RecompilerIrOperationKind.ReadCop2 => true,
        RecompilerIrOperationKind.WriteCop2 => true,
        RecompilerIrOperationKind.Cop2Command => true,
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

    private static string EmitSource(RecompilerIrProgram program, LoadedCodeTable loadedCode, bool traceUnits)
    {
        var sb = new StringBuilder();

        sb.AppendLine("#include <stdint.h>");
        sb.AppendLine("#ifdef RECOMPILER_CHECKPOINTS");
        sb.AppendLine("#include <stdio.h>");
        sb.AppendLine("#endif");
        sb.AppendLine();
        EmitTerminationReasonMacros(sb);
        EmitMemoryHelperDeclarations(sb);
        if (loadedCode.Blocks.Count != 0)
        {
            sb.AppendLine("/* Loaded-code identity (Issue #732) — provided by the host: nonzero when guest memory at pc holds exactly");
            sb.AppendLine("   words[0..count). *seen is the host's memory generation at the last successful check (UINT64_MAX: never). */");
            sb.AppendLine($"extern int {CodeGuardHelper}(void* core, uint32_t pc, const uint32_t* words, uint32_t count, uint64_t* seen);");
            sb.AppendLine();
        }
        if (program.Blocks.Concat(loadedCode.Blocks.Select(version => version.Block))
            .Any(block => block.Operations.Any(op => op.Kind is
                RecompilerIrOperationKind.ReadCop2 or RecompilerIrOperationKind.WriteCop2 or RecompilerIrOperationKind.Cop2Command)))
        {
            EmitGteHelperDeclarations(sb);
        }
        EmitStateStruct(sb, traceUnits);
        if (s_guardStats && loadedCode.Blocks.Count != 0) EmitGuardStatsSupport(sb, loadedCode);
        EmitSra32Helper(sb);
        EmitMulDivHelpers(sb);

        foreach (var block in program.Blocks)
        {
            EmitBlockFunction(sb, block);
        }

        foreach (var group in loadedCode.Blocks.GroupBy(static v => v.Block.EntryPc))
        {
            var version = 0;
            foreach (var loaded in group)
            {
                EmitBlockFunction(sb, loaded.Block, $"_v{version++}");
            }
        }

        if (traceUnits)
            EmitUnitTraceWriter(sb);
        EmitDispatchFunction(sb, program, loadedCode, traceUnits);

        return sb.ToString();
    }

    private static void EmitTerminationReasonMacros(StringBuilder sb)
    {
        sb.AppendLine("/* RecompilerIrTerminationReason byte values (RecompilerContract). */");
        sb.AppendLine($"#define RECOMPILER_REASON_SUCCESS {(byte)RecompilerIrTerminationReason.Success}");
        sb.AppendLine($"#define RECOMPILER_REASON_UNSUPPORTED_IR {(byte)RecompilerIrTerminationReason.UnsupportedIr}");
        sb.AppendLine($"#define RECOMPILER_REASON_EXCEPTION {(byte)RecompilerIrTerminationReason.Exception}");
        sb.AppendLine($"#define RECOMPILER_REASON_EXECUTION_BUDGET_EXCEEDED {(byte)RecompilerIrTerminationReason.ExecutionBudgetExceeded}");
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

    /// <summary>
    /// Declares the GTE (COP2) helpers (Issue #447), only for a program that uses COP2. The GTE state is the
    /// runtime's single GTE (the managed <c>GteRegisterBank</c>): the host provides these at link time and
    /// reaches that one instance (the artifact relays to its parent), so generated code holds no GTE copy.
    /// <c>reg</c> is 0-31 for data and 32-63 for control registers; the command helper returns non-zero when
    /// the GTE does not implement the command.
    /// </summary>
    private static void EmitGteHelperDeclarations(StringBuilder sb)
    {
        sb.AppendLine("/* Runtime GTE (COP2) helpers — provided by the host at link time (Issue #447). */");
        sb.AppendLine("extern uint32_t recompiler_gte_read(void* core, uint32_t reg);");
        sb.AppendLine("extern void     recompiler_gte_write(void* core, uint32_t reg, uint32_t value);");
        sb.AppendLine("extern int32_t  recompiler_gte_command(void* core, uint32_t command);");
        sb.AppendLine();
    }

    /// <summary>
    /// Emits the generated program's architectural state, plus the optional host
    /// control-transfer hook (Issue #362).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="HostTransferField"/> is the backend's only concession to a
    /// runtime that owns control transfers this program cannot resolve on its own
    /// — notably the PS1 BIOS A0/B0/C0 trampoline vectors. It is deliberately
    /// generic: the generated code carries no BIOS address, function number, or
    /// service knowledge, and never classifies a PC itself. It simply offers an
    /// unresolved PC to the host and lets the host decide, which keeps BIOS
    /// semantics in the Runtime where ADR-014 requires them.
    /// </para>
    /// <para>
    /// A null hook (the value every zero-initialised state already has) keeps the
    /// pre-existing behavior byte for byte, so a host that does not provide one
    /// needs no change.
    /// </para>
    /// </remarks>
    private static void EmitStateStruct(StringBuilder sb, bool traceUnits)
    {
        sb.AppendLine("struct " + StateStruct + ";");
        sb.AppendLine("/* Optional host control-transfer hook (see recompiler_dispatch). Returns 0 when");
        sb.AppendLine("   the host claims the current pc — having set termination_reason (0 to continue at");
        sb.AppendLine("   next_pc, otherwise a stop reason) — and non-zero when it does not claim it. */");
        sb.AppendLine("typedef int32_t (*" + HostTransferFnType + ")(struct " + StateStruct + "*);");
        sb.AppendLine("/* Optional guest-time hook (Issue #679), called after every dispatch unit that retires. */");
        sb.AppendLine("typedef void (*" + HostRetiredFnType + ")(struct " + StateStruct + "*);");
        sb.AppendLine("/* Optional hardware-interrupt boundary hook (Issue #680), called at the top of every dispatch");
        sb.AppendLine("   iteration — an instruction-fetch boundary outside any branch delay slot. When it accepts an");
        sb.AppendLine("   interrupt it performs the INT exception entry in the state and leaves pc at the vector. */");
        sb.AppendLine("typedef void (*" + HostInterruptFnType + ")(struct " + StateStruct + "*);");
        if (traceUnits)
        {
            // Issue #774: present only in a trace build, so the default struct layout is unchanged.
            // start_total / end_total bracket the instructions the unit actually completed; flags bit 0 = interrupted,
            // bit 1 = a guest exception was raised inside the unit; termination is 0 for a unit that reached its credit.
            sb.AppendLine("typedef void (*recompiler_unit_trace_fn)(struct " + StateStruct + "*, uint32_t entry_pc, uint32_t next_pc,");
            sb.AppendLine("    uint64_t start_total, uint64_t end_total, uint32_t flags, int32_t termination);");
        }
        sb.AppendLine("typedef struct " + StateStruct + " {");
        sb.AppendLine("  uint32_t gpr[32];");
        sb.AppendLine("  uint32_t hi;");
        sb.AppendLine("  uint32_t lo;");
        sb.AppendLine("  uint32_t pc;");
        sb.AppendLine("  int32_t " + TerminationField + ";");
        sb.AppendLine("  uint32_t " + NextPcField + ";");
        sb.AppendLine("  /* Exception resolution carried by an Exception exit (Issue #481):");
        sb.AppendLine("     CAUSE Excode, faulting PC (EPC) and the delay-slot flag of the");
        sb.AppendLine("     instruction that raised. Written by the emitted block exit only");
        sb.AppendLine("     when the IR carries exception state; otherwise left as read. */");
        sb.AppendLine("  uint32_t exception_raised;");
        sb.AppendLine("  uint32_t exception_code;");
        sb.AppendLine("  uint32_t exception_fault_pc;");
        sb.AppendLine("  uint32_t exception_in_delay_slot;");
        sb.AppendLine("  /* COP0 SR (Issue #663), and since Issue #680 CAUSE and EPC. SR is written by a host-completed");
        sb.AppendLine("     SYSCALL (host_syscall) and by a hardware INT entry (host_interrupt); CAUSE/EPC only by the");
        sb.AppendLine("     INT entry; since Issue #732 also by guest MFC0/MTC0/RFE. irq_line mirrors CAUSE.IP2: the aggregate");
        sb.AppendLine("     interrupt-controller line as the host last reported it. */");
        sb.AppendLine("  uint32_t " + Cop0SrField + ";");
        sb.AppendLine("  uint32_t " + Cop0CauseField + ";");
        sb.AppendLine("  uint32_t " + Cop0EpcField + ";");
        sb.AppendLine("  uint32_t " + IrqLineField + ";");
        sb.AppendLine("  /* The runtime target of the most recent register-indirect (JR/JALR) block exit (Issue #693). A");
        sb.AppendLine("     host transfer for a pc equal to it came from an indirect transfer; written only by such exits. */");
        sb.AppendLine("  uint32_t " + IndirectTargetField + ";");
        sb.AppendLine("  void* " + CoreField + ";");
        sb.AppendLine("  " + HostTransferFnType + " " + HostTransferField + ";");
        sb.AppendLine("  /* Optional host SYSCALL hook (Issue #663), same contract as host_transfer: 0 when the");
        sb.AppendLine("     host completed the SYSCALL exception (termination_reason 0 resumes at next_pc),");
        sb.AppendLine("     non-zero when it did not claim it, which leaves the Exception exit unchanged. */");
        sb.AppendLine("  " + HostTransferFnType + " " + HostSyscallField + ";");
        sb.AppendLine("  /* Guest-time accounting (Issue #679): the guest instructions the dispatch units that");
        sb.AppendLine("     completed have retired (RecompilerIrBlock.RetiredInstructionCount), and how many of");
        sb.AppendLine("     them the host has already been told about. A host-claimed transfer, a block that");
        sb.AppendLine("     faults or a budget stop retire no instruction of their own; an already completed");
        sb.AppendLine("     prefix of a fused unit retains its retirement credit. */");
        sb.AppendLine("  uint64_t " + RetiredTotalField + ";");
        sb.AppendLine("  uint64_t event_deadline; uint32_t partial_retired, unit_interrupted;");
        sb.AppendLine("  uint64_t " + RetiredReportedField + ";");
        sb.AppendLine("  " + HostRetiredFnType + " " + HostRetiredField + ";");
        sb.AppendLine("  " + HostInterruptFnType + " " + HostInterruptField + ";");
        if (traceUnits)
            sb.AppendLine("  recompiler_unit_trace_fn host_unit_trace;");
        sb.AppendLine("  /* Issue #732: the COP0 registers other than SR/CAUSE/EPC (which live in the fields above), indexed");
        sb.AppendLine("     by register number, as MFC0/MTC0 read and write them (PSXCpu::cop0_). Slots 12-14 are unused. */");
        sb.AppendLine("  uint32_t " + Cop0OtherField + "[32];");
        sb.AppendLine("  uint32_t (*host_cop0)(struct " + StateStruct + "*, uint32_t, uint32_t, uint32_t);");
        sb.AppendLine("  /* Issue #732: non-zero in firmware mode. A SYSCALL/BREAK exit is then delivered to the guest's own");
        sb.AppendLine("     exception vector (EPC, CAUSE Excode/BD, SR KU/IE push, BEV vector) instead of being offered to");
        sb.AppendLine("     host_syscall or stopping the run. Zero (every zero-initialised state) keeps the HLE behavior. */");
        sb.AppendLine("  uint32_t " + GuestExceptionsField + ";");
        sb.AppendLine("} " + StateStruct + ";");
        sb.AppendLine();
        EmitCop0Helpers(sb);
    }

    /// <summary>
    /// The COP0 helpers every block and the driver share (Issue #732), emitted from <see cref="RecompilerCop0"/>
    /// so the C cannot drift from it. <c>static inline</c>: a program that uses none of them compiles without an
    /// unused-function warning.
    /// </summary>
    private static void EmitCop0Helpers(StringBuilder sb)
    {
        sb.AppendLine("static inline uint32_t* " + Cop0SlotHelper + "(" + StateStruct + "* s, uint32_t r) {");
        sb.AppendLine($"  if (r == {RecompilerCop0.Status}u) return &s->{Cop0SrField};");
        sb.AppendLine($"  if (r == {RecompilerCop0.Cause}u) return &s->{Cop0CauseField};");
        sb.AppendLine($"  if (r == {RecompilerCop0.Epc}u) return &s->{Cop0EpcField};");
        sb.AppendLine($"  if (s->host_cop0) s->{Cop0OtherField}[r & 31u] = s->host_cop0(s, r, 0u, 0u);");
        sb.AppendLine($"  return &s->{Cop0OtherField}[r & 31u];");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("/* MTC0 (PSXCpu::ExecMtc0): CAUSE keeps all but its software IP bits; any other register takes the value. */");
        sb.AppendLine("static inline void " + Cop0WriteHelper + "(" + StateStruct + "* s, uint32_t r, uint32_t v) {");
        sb.AppendLine($"  if (r == {RecompilerCop0.Cause}u) {{ s->{Cop0CauseField} = (s->{Cop0CauseField} & ~{FormatHex(RecompilerCop0.CauseWritableMask)}) | (v & {FormatHex(RecompilerCop0.CauseWritableMask)}); return; }}");
        sb.AppendLine($"  if (r != {RecompilerCop0.Status}u && r != {RecompilerCop0.Epc}u && s->host_cop0) {{ s->host_cop0(s, r, 1u, v); s->{Cop0OtherField}[r & 31u] = v; return; }}");
        sb.AppendLine($"  *{Cop0SlotHelper}(s, r) = v;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("/* SR.IsC (PSXCpu::StoreIsCacheIsolated): a store below KSEG1 is dropped while the data cache is isolated. */");
        sb.AppendLine("static inline int " + StoreIsolatedHelper + "(const " + StateStruct + "* s, uint32_t a) {");
        sb.AppendLine($"  return (s->{Cop0SrField} & {FormatHex(RecompilerCop0.StatusIsolateCache)}) != 0u && a < {FormatHex(RecompilerCop0.CacheIsolationEnd)};");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("/* Exception entry (psx_cpu_exception_resolve): EPC, CAUSE Excode/CE/BD, SR KU/IE push; returns the BEV vector. */");
        sb.AppendLine("static inline uint32_t " + ExceptionEntryHelper + "(" + StateStruct + "* s, uint32_t excode, uint32_t epc, uint32_t bd) {");
        sb.AppendLine($"  uint32_t sr = s->{Cop0SrField};");
        sb.AppendLine($"  s->{Cop0EpcField} = epc;");
        sb.AppendLine($"  s->{Cop0CauseField} = (s->{Cop0CauseField} & ~{FormatHex(RecompilerCop0.CauseEntryMask)}) | ((excode & 31u) << 2) | (bd ? {FormatHex(RecompilerCop0.CauseBranchDelay)} : 0u);");
        sb.AppendLine($"  s->{Cop0SrField} = (sr & ~0x3Fu) | ((sr << 2) & 0x3Cu);");
        sb.AppendLine($"  return (sr & {FormatHex(RecompilerCop0.StatusBootExceptionVectors)}) != 0u ? {FormatHex(RecompilerCop0.RomExceptionVector)} : {FormatHex(RecompilerCop0.RamExceptionVector)};");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static string FormatHex(uint value) => $"0x{value:X8}u";

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

    /// <summary>
    /// MULT/MULTU/DIV/DIVU helpers: each writes both <c>state->hi</c> and
    /// <c>state->lo</c> directly (mirrors <see cref="Store"/>'s "computes, then
    /// stores" shape) rather than returning a value, since a plain C function
    /// cannot return two 32-bit halves without an extra struct type. Every cast
    /// to the signed/64-bit intermediate is explicit (never relying on C's usual
    /// arithmetic promotion), and DIV/DIVU check their divide-by-zero / INT_MIN
    /// special cases before the native <c>/</c>/<c>%</c>, matching
    /// <c>PSXCpu::ExecDiv</c>/<c>ExecDivu</c> / <c>psx_cpu_hilo_div</c>/<c>divu</c>
    /// exactly — plain C division would be undefined behavior for both cases.
    /// </summary>
    private static void EmitMulDivHelpers(StringBuilder sb)
    {
        sb.AppendLine("static void " + MultSignedHelper + "(" + StateStruct + "* " + StateParam + ", uint32_t a, uint32_t b) {");
        sb.AppendLine(IndentUnit + "int64_t product = (int64_t)(int32_t)a * (int64_t)(int32_t)b;");
        sb.AppendLine(IndentUnit + StateParam + "->hi = (uint32_t)(product >> 32);");
        sb.AppendLine(IndentUnit + StateParam + "->lo = (uint32_t)(product & 0xFFFFFFFFu);");
        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine("static void " + MultUnsignedHelper + "(" + StateStruct + "* " + StateParam + ", uint32_t a, uint32_t b) {");
        sb.AppendLine(IndentUnit + "uint64_t product = (uint64_t)a * (uint64_t)b;");
        sb.AppendLine(IndentUnit + StateParam + "->hi = (uint32_t)(product >> 32);");
        sb.AppendLine(IndentUnit + StateParam + "->lo = (uint32_t)(product & 0xFFFFFFFFu);");
        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine("static void " + DivSignedHelper + "(" + StateStruct + "* " + StateParam + ", uint32_t dividend, uint32_t divisor) {");
        sb.AppendLine(IndentUnit + "int32_t n = (int32_t)dividend;");
        sb.AppendLine(IndentUnit + "int32_t d = (int32_t)divisor;");
        sb.AppendLine(IndentUnit + "if (d == 0) {");
        sb.AppendLine(IndentUnit + "  " + StateParam + "->lo = (n >= 0) ? 0xFFFFFFFFu : 1u;");
        sb.AppendLine(IndentUnit + "  " + StateParam + "->hi = (uint32_t)n;");
        sb.AppendLine(IndentUnit + "  return;");
        sb.AppendLine(IndentUnit + "}");
        sb.AppendLine(IndentUnit + "if (n == INT32_MIN && d == -1) {");
        sb.AppendLine(IndentUnit + "  " + StateParam + "->hi = 0u;");
        sb.AppendLine(IndentUnit + "  " + StateParam + "->lo = 0x80000000u;");
        sb.AppendLine(IndentUnit + "  return;");
        sb.AppendLine(IndentUnit + "}");
        sb.AppendLine(IndentUnit + StateParam + "->hi = (uint32_t)(n % d);");
        sb.AppendLine(IndentUnit + StateParam + "->lo = (uint32_t)(n / d);");
        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine("static void " + DivUnsignedHelper + "(" + StateStruct + "* " + StateParam + ", uint32_t dividend, uint32_t divisor) {");
        sb.AppendLine(IndentUnit + "if (divisor == 0) {");
        sb.AppendLine(IndentUnit + "  " + StateParam + "->hi = dividend;");
        sb.AppendLine(IndentUnit + "  " + StateParam + "->lo = 0xFFFFFFFFu;");
        sb.AppendLine(IndentUnit + "  return;");
        sb.AppendLine(IndentUnit + "}");
        sb.AppendLine(IndentUnit + StateParam + "->hi = dividend % divisor;");
        sb.AppendLine(IndentUnit + StateParam + "->lo = dividend / divisor;");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void EmitBlockFunction(StringBuilder sb, RecompilerIrBlock block, string suffix = "")
    {
        var functionName = $"recompiler_block_0x{block.EntryPc:X8}{suffix}";
        var valueNames = new Dictionary<int, string>();

        sb.AppendLine($"static int32_t {functionName}({StateStruct}* {StateParam}) {{");

        for (var operationIndex = 0; operationIndex < block.Operations.Count; operationIndex++)
        {
            var op = block.Operations[operationIndex];
            if (block.InstructionBoundaries.Contains(operationIndex))
                sb.AppendLine(IndentUnit + "if (state->host_retired != 0) { state->retired_total++; state->partial_retired++; state->host_retired(state); }");
            if (block.HasLoadDelay && operationIndex == block.InstructionBoundaries[0] && block.InterruptLoadCommit is { } commit)
            {
                sb.AppendLine(IndentUnit + "if (state->host_interrupt != 0 && state->irq_line && (state->cop0_sr & 0x401u) == 0x401u) {");
                sb.AppendLine(IndentUnit + IndentUnit + EmitOperation(commit, valueNames));
                sb.AppendLine(IndentUnit + IndentUnit + $"state->pc = {FormatImmediate(unchecked(block.EntryPc + 4))}; state->host_interrupt(state); state->next_pc = state->pc; state->unit_interrupted = 1u; return RECOMPILER_REASON_SUCCESS;");
                sb.AppendLine(IndentUnit + "}");
            }
            foreach (var site in block.MemoryFaultSites.Where(site => site.OperationIndex == operationIndex))
                EmitMemoryFaultGuard(sb, op, site, valueNames);
            var stmt = EmitOperation(op, valueNames);
            if (stmt != null)
            {
                sb.AppendLine(IndentUnit + stmt);
            }
        }

        if (block.InstructionBoundaries.Contains(block.Operations.Count))
            sb.AppendLine(IndentUnit + "if (state->host_retired != 0) { state->retired_total++; state->partial_retired++; state->host_retired(state); }");
        sb.AppendLine(IndentUnit + EmitExit(block.Exit));
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void EmitMemoryFaultGuard(StringBuilder sb, RecompilerIrOperation op,
        RecompilerMemoryFaultSite site, Dictionary<int, string> valueNames)
    {
        var mask = op.Kind is RecompilerIrOperationKind.Load16 or RecompilerIrOperationKind.Store16 ? 1u : 3u;
        var excode = op.Kind is RecompilerIrOperationKind.Store16 or RecompilerIrOperationKind.Store32 ? 5u : 4u;
        var address = ResolveValue(op.InputValueA, valueNames);
        sb.AppendLine(IndentUnit + $"if (({address} & {mask}u) != 0u) {{");
        if (site.PendingLoadRegister > 0)
            sb.AppendLine(IndentUnit + IndentUnit + $"state->gpr[{site.PendingLoadRegister}] = {ResolveValue(site.PendingLoadValueId, valueNames)};");
        // A fused prefix has retired even though this memory instruction has not. Downstream
        // per-instruction accounting may already have credited it; charge only the remainder.
        sb.AppendLine(IndentUnit + IndentUnit + $"state->retired_total += {site.RetiredPrefix}u - state->partial_retired;");
        sb.AppendLine(IndentUnit + IndentUnit + $"state->partial_retired = {site.RetiredPrefix}u;");
        sb.AppendLine(IndentUnit + IndentUnit + $"if (state->host_retired) state->host_retired(state);");
        sb.AppendLine(IndentUnit + IndentUnit + $"{Cop0WriteHelper}(state, 8u, {address});");
        sb.AppendLine(IndentUnit + IndentUnit + $"state->{ExceptionRaisedField} = 1u; state->{ExceptionCodeField} = {excode}u;");
        sb.AppendLine(IndentUnit + IndentUnit + $"state->{ExceptionFaultPcField} = {FormatImmediate(site.FaultPc)}; state->{ExceptionInDelaySlotField} = {(site.InDelaySlot ? 1 : 0)}u;");
        sb.AppendLine(IndentUnit + IndentUnit + "state->termination_reason = RECOMPILER_REASON_EXCEPTION;");
        sb.AppendLine(IndentUnit + IndentUnit + "return (int32_t)RECOMPILER_REASON_EXCEPTION;");
        sb.AppendLine(IndentUnit + "}");
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

            case RecompilerIrOperationKind.AddSigned:
                if (result == null) return null;
                valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
                var _left = ResolveValue(op.InputValueA, valueNames);
                var _right = ResolveValue(op.InputValueB, valueNames);
                return $"uint32_t v{op.ResultValueId}_left = {_left}; " +
                       $"uint32_t v{op.ResultValueId}_right = {_right}; " +
                       $"{result} = v{op.ResultValueId}_left + v{op.ResultValueId}_right; " +
                       $"if (((v{op.ResultValueId}_left ^ v{op.ResultValueId}) & " +
                       $"(v{op.ResultValueId}_right ^ v{op.ResultValueId}) & 0x80000000u) != 0u) " +
                       $"{{ {StateParam}->{TerminationField} = RECOMPILER_REASON_EXCEPTION; " +
                       $"return (int32_t)RECOMPILER_REASON_EXCEPTION; }}";

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

            case RecompilerIrOperationKind.ShiftLeftLogicalVariable:
                // SLLV: the amount is a runtime GPR value, so it is masked explicitly
                // here (plain C's << on a uint32_t by a count >= 32 is undefined
                // behavior, unlike the immediate ShiftLeftLogical case above where the
                // decoded shamt is already 0-31).
                if (result == null) return null;
                valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
                return $"{result} = (uint32_t){ResolveValue(op.InputValueA, valueNames)} << ({ResolveValue(op.InputValueB, valueNames)} & 31u);";

            case RecompilerIrOperationKind.ShiftRightLogicalVariable:
                if (result == null) return null;
                valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
                return $"{result} = (uint32_t){ResolveValue(op.InputValueA, valueNames)} >> ({ResolveValue(op.InputValueB, valueNames)} & 31u);";

            case RecompilerIrOperationKind.ShiftRightArithmeticVariable:
                if (result == null) return null;
                valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
                return $"{result} = {Sra32Helper}({ResolveValue(op.InputValueA, valueNames)}, {ResolveValue(op.InputValueB, valueNames)} & 31u);";

            case RecompilerIrOperationKind.CompareEqual:
                if (result == null) return null;
                valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
                return $"{result} = ({ResolveValue(op.InputValueA, valueNames)} == {ResolveValue(op.InputValueB, valueNames)}) ? 1u : 0u;";

            case RecompilerIrOperationKind.CompareNotEqual:
                if (result == null) return null;
                valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
                return $"{result} = ({ResolveValue(op.InputValueA, valueNames)} != {ResolveValue(op.InputValueB, valueNames)}) ? 1u : 0u;";

            case RecompilerIrOperationKind.CompareLessThanSigned:
                if (result == null) return null;
                valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
                // Portable 32-bit signed ordering via unsigned comparison: flipping the
                // sign bit maps two's-complement signed order onto unsigned order without
                // the implementation-defined uint32_t -> int32_t conversion C11 would
                // require for out-of-range values (e.g. 0x80000000).
                return $"{result} = (({ResolveValue(op.InputValueA, valueNames)} ^ 0x80000000u) < ({ResolveValue(op.InputValueB, valueNames)} ^ 0x80000000u)) ? 1u : 0u;";

            case RecompilerIrOperationKind.CompareLessThanUnsigned:
                if (result == null) return null;
                valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
                return $"{result} = ({ResolveValue(op.InputValueA, valueNames)} < {ResolveValue(op.InputValueB, valueNames)}) ? 1u : 0u;";

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

            // Every store carries the SR.IsC guard (RecompilerCop0.StoreIsCacheIsolated, Issue #732) on its own
            // line, so the access itself still reads as the bare helper call.
            case RecompilerIrOperationKind.Store8:
                return StoreGuard(op, valueNames) + $"recompiler_write_mem8({StateParam}->{CoreField}, {ResolveValue(op.InputValueA, valueNames)}, (uint8_t){ResolveValue(op.InputValueB, valueNames)});";

            case RecompilerIrOperationKind.Store16:
                return StoreGuard(op, valueNames) + $"recompiler_write_mem16({StateParam}->{CoreField}, {ResolveValue(op.InputValueA, valueNames)}, (uint16_t){ResolveValue(op.InputValueB, valueNames)});";

            case RecompilerIrOperationKind.Store32:
                return StoreGuard(op, valueNames) + $"recompiler_write_mem32({StateParam}->{CoreField}, {ResolveValue(op.InputValueA, valueNames)}, {ResolveValue(op.InputValueB, valueNames)});";

            case RecompilerIrOperationKind.ReadCop0:
                if (result == null) return null;
                valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
                return $"{result} = *{Cop0SlotHelper}({StateParam}, {op.Register}u);";

            case RecompilerIrOperationKind.WriteCop0:
                return $"{Cop0WriteHelper}({StateParam}, {op.Register}u, {ResolveValue(op.InputValueA, valueNames)});";

            case RecompilerIrOperationKind.ReturnFromException:
                return $"{StateParam}->{Cop0SrField} = ({StateParam}->{Cop0SrField} & ~0xFu) | (({StateParam}->{Cop0SrField} >> 2) & 0xFu);";

            // COP2 (Issue #447): SR.CU2 clear stops the block like a trapping AddSigned (fail closed).
            case RecompilerIrOperationKind.ReadCop2:
                if (result == null) return null;
                valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
                return Cop2Guard() + $"{result} = recompiler_gte_read({StateParam}->{CoreField}, {op.Register + (op.Immediate * 32u)}u);";

            case RecompilerIrOperationKind.WriteCop2:
                return Cop2Guard() + $"recompiler_gte_write({StateParam}->{CoreField}, {op.Register + (op.Immediate * 32u)}u, {ResolveValue(op.InputValueA, valueNames)});";

            case RecompilerIrOperationKind.Cop2Command:
                return Cop2Guard() + $"if (recompiler_gte_command({StateParam}->{CoreField}, {FormatImmediate(op.Immediate)}) != 0) " +
                       $"{{ {StateParam}->{TerminationField} = RECOMPILER_REASON_EXCEPTION; return (int32_t)RECOMPILER_REASON_EXCEPTION; }}";

            case RecompilerIrOperationKind.ReadHi:
                if (result == null) return null;
                valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
                return $"{result} = {StateParam}->hi;";

            case RecompilerIrOperationKind.ReadLo:
                if (result == null) return null;
                valueNames[op.ResultValueId] = $"v{op.ResultValueId}";
                return $"{result} = {StateParam}->lo;";

            case RecompilerIrOperationKind.WriteHi:
                if (op.InputValueA < 0) return null;
                return $"{StateParam}->hi = {ResolveValue(op.InputValueA, valueNames)};";

            case RecompilerIrOperationKind.WriteLo:
                if (op.InputValueA < 0) return null;
                return $"{StateParam}->lo = {ResolveValue(op.InputValueA, valueNames)};";

            case RecompilerIrOperationKind.MultiplySigned:
                return $"{MultSignedHelper}({StateParam}, {ResolveValue(op.InputValueA, valueNames)}, {ResolveValue(op.InputValueB, valueNames)});";

            case RecompilerIrOperationKind.MultiplyUnsigned:
                return $"{MultUnsignedHelper}({StateParam}, {ResolveValue(op.InputValueA, valueNames)}, {ResolveValue(op.InputValueB, valueNames)});";

            case RecompilerIrOperationKind.DivideSigned:
                return $"{DivSignedHelper}({StateParam}, {ResolveValue(op.InputValueA, valueNames)}, {ResolveValue(op.InputValueB, valueNames)});";

            case RecompilerIrOperationKind.DivideUnsigned:
                return $"{DivUnsignedHelper}({StateParam}, {ResolveValue(op.InputValueA, valueNames)}, {ResolveValue(op.InputValueB, valueNames)});";

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

    private static string StoreGuard(RecompilerIrOperation op, Dictionary<int, string> valueNames) =>
        $"if (!{StoreIsolatedHelper}({StateParam}, {ResolveValue(op.InputValueA, valueNames)}))\n{IndentUnit}{IndentUnit}";

    private static string ResolveValue(int valueId, Dictionary<int, string> valueNames)
    {
        return valueNames.TryGetValue(valueId, out var name) ? name : $"v{valueId}";
    }

    private static string EmitExit(RecompilerIrExit exit)
    {
        // A register-indirect transfer (Issue #635) hands its runtime target to
        // the dispatch loop, which enters a compiled block or asks host_transfer.
        if (exit.TargetValueId is { } targetValueId)
        {
            return $"{StateParam}->{IndirectTargetField} = v{targetValueId}; " +
                   $"{StateParam}->{NextPcField} = v{targetValueId}; {StateParam}->{TerminationField} = 0; return 0;";
        }

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
                    return EmitCallExit(flow);
            }
        }

        var reason = (byte)exit.Reason;
        var termination = $"{StateParam}->{TerminationField} = {reason}; return (int32_t){reason}u;";
        if (exit.Exception is { } exception)
        {
            // An exception exit that carries IR exception state reproduces its
            // resolution (Excode, EPC, BD) in the state so the runtime sees the
            // faulting instruction without re-deriving it from the parked PC
            // (Issue #481). The generic Exception exits produced by trapping
            // operations such as AddSigned overflow carry no state and keep
            // writing termination only, exactly as before.
            return $"{StateParam}->{ExceptionRaisedField} = {(exception.IsRaised ? "1u" : "0u")}; " +
                   $"{StateParam}->{ExceptionCodeField} = {FormatImmediate(exception.Code)}; " +
                   $"{StateParam}->{ExceptionFaultPcField} = {FormatImmediate(exception.FaultPc)}; " +
                   $"{StateParam}->{ExceptionInDelaySlotField} = {(exception.InDelaySlot ? "1u" : "0u")}; " + termination;
        }

        return termination;
    }

    private static string Cop2Guard() =>
        $"if (({StateParam}->{Cop0SrField} & {FormatImmediate(RecompilerCop2.StatusCop2Usable)}) == 0u) " +
        $"{{ {StateParam}->{TerminationField} = RECOMPILER_REASON_EXCEPTION; return (int32_t)RECOMPILER_REASON_EXCEPTION; }} ";

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

    private static string EmitCallExit(RecompilerIrFlow flow)
    {
        var calleeTarget = FormatImmediate(flow.Target!.Value);
        return $"{StateParam}->{NextPcField} = {calleeTarget}; {StateParam}->{TerminationField} = 0; return 0;";
    }

    private static string FormatImmediate(uint value)
    {
        if (value <= 9)
            return value.ToString();

        return $"({value}u)";
    }

    private static void EmitBudgetExceededReturn(StringBuilder sb, int indentLevel)
    {
        var indent = string.Concat(Enumerable.Repeat(IndentUnit, indentLevel));
        sb.AppendLine(indent + $"{StateParam}->{TerminationField} = RECOMPILER_REASON_EXECUTION_BUDGET_EXCEEDED;");
        sb.AppendLine(indent + "return (int32_t)RECOMPILER_REASON_EXECUTION_BUDGET_EXCEEDED;");
    }


    private static void EmitGuardStatsSupport(StringBuilder sb, LoadedCodeTable loadedCode)
    {
        var groups = loadedCode.Blocks.GroupBy(static v => v.Block.EntryPc).OrderBy(static g => g.Key).ToList();
        // Per-version tables are flat (one cell per registered version, indexed by psx_gs_off[slot] + version): a slot with
        // many versions no longer pads every other slot to the same width.
        var versions = groups.SelectMany(static g => g).ToList();
        var offsets = new List<int> { 0 };
        foreach (var g in groups) offsets.Add(offsets[^1] + g.Count());
        string Cells(Func<GuardedBlock, string> cell) => string.Join(", ", versions.Select(cell));
        sb.AppendLine("/* Measurement only (PSX_GUARD_STATS_FILE): per RAM-placed entry, arrivals, guard hits per version, guard misses. */");
        sb.AppendLine("#include <stdio.h>");
        sb.AppendLine("#include <stdlib.h>");
        sb.AppendLine("#include <time.h>");
        sb.AppendLine($"#define PSX_GS_N {groups.Count}u");
        sb.AppendLine($"#define PSX_GS_T {versions.Count}u");
        sb.AppendLine($"static const uint32_t psx_gs_pc[PSX_GS_N] = {{ {string.Join(", ", groups.Select(g => FormatHex(g.Key)))} }};");
        sb.AppendLine($"static const uint32_t psx_gs_off[PSX_GS_N + 1u] = {{ {string.Join(", ", offsets.Select(o => o.ToString(System.Globalization.CultureInfo.InvariantCulture) + "u"))} }};");
        sb.AppendLine($"static const uint32_t psx_gs_len[PSX_GS_T] = {{ {Cells(v => v.Words.Count + "u")} }};");
        sb.AppendLine($"static const uint32_t psx_gs_w0[PSX_GS_T] = {{ {Cells(v => v.Words.Count != 0 ? FormatHex(v.Words[0]) : "0u")} }};");
        sb.AppendLine("static uint64_t psx_gs_arrive[PSX_GS_N], psx_gs_hit[PSX_GS_T], psx_gs_miss[PSX_GS_N], psx_gs_refuse[PSX_GS_N];");
        sb.AppendLine("static uint32_t psx_gs_missprev[PSX_GS_N], psx_gs_missram[PSX_GS_N];");
        // CPU state (32 GPRs, pc, prev, slot, RAM word) of the first miss of each entry, at most 16 records in all. A first miss
        // beyond that is only counted in psx_gs_snapdropped and has no CPU registers in the report.
        sb.AppendLine("static uint32_t psx_gs_snap[16][36];");
        sb.AppendLine("static int psx_gs_nsnap = 0, psx_gs_registered = 0;");
        sb.AppendLine("static uint64_t psx_gs_snapdropped = 0u;");
        sb.AppendLine("static uint32_t psx_gs_cur = 0u, psx_gs_prev = 0u;");
        sb.AppendLine("static uint64_t psx_gs_iter = 0u;");
        // Periodic interval and abort point (Issue #797 test hooks), read from the environment the scope hands to the child.
        sb.AppendLine("static uint64_t psx_gs_period = 0x400000u, psx_gs_abort = 0u;");
        sb.AppendLine("static unsigned long long psx_gs_runid = 0ull;");
        // The file is written whole to <path>.tmp and then replaced over <path>. The previous report is never removed first, so
        // a refused replacement keeps the report that was there and removes the .tmp. The content is complete before the rename
        // (fclose is checked). Replacement is platform specific: POSIX rename() replaces atomically; Windows
        // MoveFileExA(MOVEFILE_REPLACE_EXISTING) is a same-volume rename in practice but not documented as atomic, so a reader
        // may not see <path> for an instant, and it is refused (keeping the old file) while the destination is open without
        // FILE_SHARE_DELETE. A killed process leaves the report of an earlier run or of this one (told apart by the run line)
        // and may leave the .tmp of the dump in progress; durability across power loss is not promised (no fsync).
        // The last line says whether this is the process's final dump; a file without "end final 1" comes from a periodic
        // dump of a run that did not exit normally and is incomplete.
        sb.AppendLine("#ifdef _WIN32");
        // Declared by hand: <windows.h> (excpt.h) #defines exception_code, which the generated state uses as a member name.
        sb.AppendLine("__declspec(dllimport) int __stdcall MoveFileExA(const char*, const char*, unsigned long);");
        sb.AppendLine("static int psx_gs_replace(const char* tmp, const char* path) { return MoveFileExA(tmp, path, 0x1ul /* MOVEFILE_REPLACE_EXISTING */ | 0x8ul /* MOVEFILE_WRITE_THROUGH */) ? 0 : -1; }");
        sb.AppendLine("#else");
        sb.AppendLine("static int psx_gs_replace(const char* tmp, const char* path) { return rename(tmp, path); }");
        sb.AppendLine("#endif");
        sb.AppendLine("static void psx_gs_dump(int final_dump) {");
        sb.AppendLine("  const char* path = getenv(\"PSX_GUARD_STATS_FILE\");");
        sb.AppendLine("  char tmp[1024]; FILE* f; unsigned i, v; int ok;");
        sb.AppendLine("  if (path == (const char*)0 || path[0] == '\\0') return;");
        sb.AppendLine("  if (snprintf(tmp, sizeof tmp, \"%s.tmp\", path) >= (int)sizeof tmp) return;");
        sb.AppendLine("  f = fopen(tmp, \"w\");");
        sb.AppendLine("  if (f == (FILE*)0) return;");
        // The run id is fixed at the first dump of the process: a report of this run and its periodic dumps share it, and a
        // report left by an earlier run (not yet replaced) has another one (Issue #797).
        // The parent scope passes a random id per child (PSX_GUARD_STATS_RUN_ID). The time/clock form is only the fallback for a
        // report named by hand, where two processes in the same second can collide (Issue #804).
        sb.AppendLine("  if (psx_gs_runid == 0ull) {");
        sb.AppendLine("    const char* rid = getenv(\"PSX_GUARD_STATS_RUN_ID\");");
        sb.AppendLine("    psx_gs_runid = (rid != (const char*)0 && rid[0] != '\\0') ? strtoull(rid, (char**)0, 16)");
        sb.AppendLine("      : (((unsigned long long)time(0) << 20) ^ (unsigned long long)clock() ^ 1ull);");
        sb.AppendLine("  }");
        sb.AppendLine("  fprintf(f, \"guardstats 1\\nrun %016llx\\niter %llu\\n\", psx_gs_runid, (unsigned long long)psx_gs_iter);");
        sb.AppendLine("  for (i = 0u; i < PSX_GS_N; i++) {");
        sb.AppendLine("    if (psx_gs_arrive[i] == 0u) continue;");
        sb.AppendLine("    fprintf(f, \"slot %08X nver %u arrive %llu miss %llu refuse %llu missprevdispatch %08X missram %08X\", (unsigned)psx_gs_pc[i], (unsigned)(psx_gs_off[i + 1u] - psx_gs_off[i]), (unsigned long long)psx_gs_arrive[i], (unsigned long long)psx_gs_miss[i], (unsigned long long)psx_gs_refuse[i], (unsigned)psx_gs_missprev[i], (unsigned)psx_gs_missram[i]);");
        sb.AppendLine("    for (v = psx_gs_off[i]; v < psx_gs_off[i + 1u]; v++) fprintf(f, \" v%u hit %llu len %u w0 %08X\", v - psx_gs_off[i], (unsigned long long)psx_gs_hit[v], (unsigned)psx_gs_len[v], (unsigned)psx_gs_w0[v]);");
        sb.AppendLine("    fprintf(f, \"\\n\");");
        sb.AppendLine("  }");
        sb.AppendLine("  for (i = 0u; i < (unsigned)psx_gs_nsnap; i++) {");
        sb.AppendLine("    fprintf(f, \"snap slot %08X prevdispatch %08X ram %08X pc %08X gpr\", (unsigned)psx_gs_pc[psx_gs_snap[i][34]], (unsigned)psx_gs_snap[i][33], (unsigned)psx_gs_snap[i][35], (unsigned)psx_gs_snap[i][32]);");
        sb.AppendLine("    for (v = 0u; v < 32u; v++) fprintf(f, \" %08X\", (unsigned)psx_gs_snap[i][v]);");
        sb.AppendLine("    fprintf(f, \"\\n\");");
        sb.AppendLine("  }");
        sb.AppendLine("  fprintf(f, \"snapdropped %llu\\nend final %d\\n\", (unsigned long long)psx_gs_snapdropped, final_dump ? 1 : 0);");
        sb.AppendLine("  ok = ferror(f) == 0;");
        sb.AppendLine("  if (fclose(f) != 0) ok = 0;");
        sb.AppendLine("  if (!ok || psx_gs_replace(tmp, path) != 0) remove(tmp);");
        sb.AppendLine("}");
        sb.AppendLine("static void psx_gs_dump_final(void) { psx_gs_dump(1); }");
        sb.AppendLine("/* psx_gs_prev is the entry PC of the previous dispatch-loop iteration, not necessarily the guest source of the transfer. */");
        sb.AppendLine("static void psx_gs_init_timing(void) {");
        sb.AppendLine("  const char* e = getenv(\"PSX_GUARD_STATS_PERIOD\");");
        sb.AppendLine("  unsigned long long v;");
        sb.AppendLine("  if (e != (const char*)0 && (v = strtoull(e, (char**)0, 10)) != 0ull) psx_gs_period = v;");
        sb.AppendLine("  e = getenv(\"PSX_GUARD_STATS_ABORT_AFTER\");");
        sb.AppendLine("  if (e != (const char*)0) psx_gs_abort = strtoull(e, (char**)0, 10);");
        sb.AppendLine("}");
        // The period is a run-time value (the default 0x400000 is unchanged), so the tick uses a modulo. An abort point ends the
        // child with _Exit, which skips atexit: the final dump does not run, and the report left is the last periodic one.
        sb.AppendLine("#define PSX_GS_TICK(st) do { if (!psx_gs_registered) { psx_gs_registered = 1; psx_gs_init_timing(); atexit(psx_gs_dump_final); } psx_gs_prev = psx_gs_cur; psx_gs_cur = (st)->pc; if ((++psx_gs_iter % psx_gs_period) == 0u) psx_gs_dump(0); if (psx_gs_abort != 0u && psx_gs_iter >= psx_gs_abort) _Exit(86); } while (0)");
        sb.AppendLine("#define PSX_GS_ARRIVE(slot) (psx_gs_arrive[slot]++)");
        sb.AppendLine("#define PSX_GS_REFUSE(slot) (psx_gs_refuse[slot]++)");
        sb.AppendLine("#define PSX_GS_HIT(slot, ver) (psx_gs_hit[psx_gs_off[slot] + (ver)]++)");
        sb.AppendLine("#define PSX_GS_MISS(slot, st) do { unsigned k_; if (psx_gs_miss[slot]++ == 0u) { psx_gs_missprev[slot] = psx_gs_prev; psx_gs_missram[slot] = recompiler_read_mem32((st)->core, (st)->pc); if (psx_gs_nsnap < 16) { for (k_ = 0u; k_ < 32u; k_++) psx_gs_snap[psx_gs_nsnap][k_] = (st)->gpr[k_]; psx_gs_snap[psx_gs_nsnap][32] = (st)->pc; psx_gs_snap[psx_gs_nsnap][33] = psx_gs_prev; psx_gs_snap[psx_gs_nsnap][34] = (uint32_t)(slot); psx_gs_snap[psx_gs_nsnap][35] = psx_gs_missram[slot]; psx_gs_nsnap++; } else { psx_gs_snapdropped++; } } } while (0)");
        sb.AppendLine();
    }

    // Issue #774: the unit trace writer, emitted only in a trace build. One JSON object per line:
    //   {"v":1,"kind":"header",...} once, {"v":1,"kind":"unit",...} per unit, then either
    //   {"v":1,"kind":"end","complete":true} at a normal exit, or {"v":1,"kind":"truncated",...} when the output
    //   cap is reached and {"v":1,"kind":"end","complete":false,...} after it. A file without an "end" line,
    //   or with a last line that does not parse, comes from a run that did not exit normally (for example a kill).
    // It writes only to its own file: stdout and the RHOST_* protocol are untouched. Open failure leaves the hook
    // null, so the run continues exactly as an untraced one.
    private const string UnitTraceWriterSource = """
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#define RECOMPILER_TRACE_SCHEMA_VERSION 1
#define RECOMPILER_TRACE_DEFAULT_CAP 67108864ull
#define RECOMPILER_TRACE_RESERVE 512ull
#define RECOMPILER_TRACE_MIN_CAP (RECOMPILER_TRACE_RESERVE + 128ull)
static FILE* recompiler_trace_file = 0;
static unsigned long long recompiler_trace_seq = 0ull;
static unsigned long long recompiler_trace_bytes = 0ull;
static unsigned long long recompiler_trace_cap = 0ull;
static int recompiler_trace_mode = 0; /* 0 off, 1 writing, 2 truncated (output cap reached) */
static int recompiler_trace_exit_registered = 0;
/* Issue #774 (B-7): optional window over the native retirement total. A unit is written only when its [start, end)
   range lies in [from, to); the default window is everything, so the default trace is unchanged. */
static unsigned long long recompiler_trace_from = 0ull;
static unsigned long long recompiler_trace_to = ~0ull;

static void recompiler_trace_close(void) {
    if (recompiler_trace_mode == 0 || recompiler_trace_file == 0) return;
    if (recompiler_trace_mode == 1) {
        fprintf(recompiler_trace_file, "{\"v\":%d,\"kind\":\"end\",\"seq\":%llu,\"complete\":true}\n",
            RECOMPILER_TRACE_SCHEMA_VERSION, recompiler_trace_seq);
    } else {
        fprintf(recompiler_trace_file, "{\"v\":%d,\"kind\":\"end\",\"seq\":%llu,\"complete\":false,\"reason\":\"output_cap\"}\n",
            RECOMPILER_TRACE_SCHEMA_VERSION, recompiler_trace_seq);
    }
    fclose(recompiler_trace_file);
    recompiler_trace_file = 0;
    recompiler_trace_mode = 0;
}

static void recompiler_trace_line(const char* line, size_t length) {
    if (recompiler_trace_mode != 1) return;
    if (recompiler_trace_bytes + (unsigned long long)length + RECOMPILER_TRACE_RESERVE > recompiler_trace_cap) {
        fprintf(recompiler_trace_file, "{\"v\":%d,\"kind\":\"truncated\",\"seq\":%llu,\"reason\":\"output_cap\"}\n",
            RECOMPILER_TRACE_SCHEMA_VERSION, recompiler_trace_seq);
        recompiler_trace_mode = 2;
        return;
    }
    if (fwrite(line, 1, length, recompiler_trace_file) != length) {
        /* A failed write stops tracing quietly; the missing end line marks the output incomplete. */
        fclose(recompiler_trace_file);
        recompiler_trace_file = 0;
        recompiler_trace_mode = 0;
        return;
    }
    recompiler_trace_bytes += (unsigned long long)length;
    recompiler_trace_seq++;
}

static void recompiler_trace_unit(struct RecompilerState* state, uint32_t entry_pc, uint32_t next_pc,
    uint64_t start_total, uint64_t end_total, uint32_t flags, int32_t termination) {
    char line[1536];
    char gpr_text[512];
    int length;
    int gpr_length = 0;
    int i;
    if (recompiler_trace_mode != 1) return;
    if (end_total < recompiler_trace_from || start_total > recompiler_trace_to) return;
    /* Issue #774 (B-6b): the CPU state at the unit's end (the successor pc, the registers and COP0 as the unit left them). */
    for (i = 0; i < 32; i++) {
        gpr_length += snprintf(gpr_text + gpr_length, sizeof gpr_text - (size_t)gpr_length,
            "%s\"0x%08X\"", i == 0 ? "" : ",", (unsigned)state->gpr[i]);
    }
    length = snprintf(line, sizeof line,
        "{\"v\":%d,\"kind\":\"unit\",\"seq\":%llu,\"entry\":\"0x%08X\",\"next\":\"0x%08X\",\"start\":%llu,\"end\":%llu,"
        "\"retired\":%llu,\"interrupted\":%u,\"exception\":%u,\"termination\":%d,"
        "\"state\":{\"pc\":\"0x%08X\",\"gpr\":[%s],\"hi\":\"0x%08X\",\"lo\":\"0x%08X\","
        "\"sr\":\"0x%08X\",\"cause\":\"0x%08X\",\"epc\":\"0x%08X\"}}\n",
        RECOMPILER_TRACE_SCHEMA_VERSION, recompiler_trace_seq, (unsigned)entry_pc, (unsigned)next_pc,
        (unsigned long long)start_total, (unsigned long long)end_total,
        (unsigned long long)(end_total - start_total), (unsigned)(flags & 1u), (unsigned)((flags >> 1) & 1u),
        (int)termination, (unsigned)next_pc, gpr_text, (unsigned)state->hi, (unsigned)state->lo,
        (unsigned)state->cop0_sr, (unsigned)state->cop0_cause, (unsigned)state->cop0_epc);
    if (length > 0) recompiler_trace_line(line, (size_t)length);
}

/* The session id links this trace to the parent's fallback trace of the same run (Issue #774). It comes from
   PSX_TRACE_SESSION and is kept only when it is a short [0-9A-Za-z-] token, so it can be written unescaped. */
static char recompiler_trace_session[65] = "";
static unsigned long long recompiler_trace_handoffs = 0ull;

static void recompiler_trace_read_session(void) {
    const char* text = getenv("PSX_TRACE_SESSION");
    size_t i, n;
    recompiler_trace_session[0] = '\0';
    if (text == 0) return;
    n = strlen(text);
    if (n == 0 || n > 64) return;
    for (i = 0; i < n; i++) {
        char c = text[i];
        if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-')) return;
    }
    memcpy(recompiler_trace_session, text, n + 1);
}

/* One handoff record per transfer to the host, numbered in the order the host sees them. The handoff counts even when
   the record cannot be written (cap reached), so a later record never reuses or skips a number. native_total is the
   retired total at the transfer: every unit before it is credited and no unit after it has started. */
static void recompiler_trace_handoff(RecompilerState* state) {
    char line[320];
    int length;
    recompiler_trace_handoffs++;
    if (recompiler_trace_mode != 1) return;
    length = snprintf(line, sizeof line,
        "{\"v\":%d,\"kind\":\"handoff\",\"seq\":%llu,\"handoff\":%llu,\"native_total\":%llu,\"pc\":\"0x%08X\"}\n",
        RECOMPILER_TRACE_SCHEMA_VERSION, recompiler_trace_seq, recompiler_trace_handoffs,
        (unsigned long long)state->retired_total, (unsigned)state->pc);
    if (length > 0) recompiler_trace_line(line, (size_t)length);
}

/* Opens the trace from the environment and attaches it to the state. Returns 1 when attached, 0 when no path is
   configured, -1 when the file cannot be created and -2 when PSX_UNIT_TRACE_MAX_BYTES is not a valid size. In every
   non-1 case the hook stays null and the run is untraced. */
int recompiler_trace_attach(struct RecompilerState* state) {
    const char* path = getenv("PSX_UNIT_TRACE_FILE");
    const char* cap_text = getenv("PSX_UNIT_TRACE_MAX_BYTES");
    const char* from_text = getenv("PSX_UNIT_TRACE_FROM");
    const char* to_text = getenv("PSX_UNIT_TRACE_TO");
    unsigned long long cap = RECOMPILER_TRACE_DEFAULT_CAP;
    char* end = 0;
    if (path == 0 || path[0] == '\0') return 0;
    if (cap_text != 0 && cap_text[0] != '\0') {
        cap = strtoull(cap_text, &end, 10);
        if (end == cap_text || *end != '\0' || cap < RECOMPILER_TRACE_MIN_CAP) return -2;
    }
    recompiler_trace_from = 0ull;
    recompiler_trace_to = ~0ull;
    if (from_text != 0 && from_text[0] != '\0') {
        recompiler_trace_from = strtoull(from_text, &end, 10);
        if (end == from_text || *end != '\0') return -2;
    }
    if (to_text != 0 && to_text[0] != '\0') {
        recompiler_trace_to = strtoull(to_text, &end, 10);
        if (end == to_text || *end != '\0') return -2;
    }
    if (recompiler_trace_mode != 0) return 0;
    recompiler_trace_read_session();
    recompiler_trace_file = fopen(path, "w");
    if (recompiler_trace_file == 0) return -1;
    recompiler_trace_cap = cap;
    recompiler_trace_seq = 0ull;
    recompiler_trace_bytes = 0ull;
    recompiler_trace_mode = 1;
    if (recompiler_trace_session[0] != '\0') {
        fprintf(recompiler_trace_file, "{\"v\":%d,\"kind\":\"header\",\"session\":\"%s\",\"cap\":%llu}\n",
            RECOMPILER_TRACE_SCHEMA_VERSION, recompiler_trace_session, cap);
    } else {
        fprintf(recompiler_trace_file, "{\"v\":%d,\"kind\":\"header\",\"cap\":%llu}\n", RECOMPILER_TRACE_SCHEMA_VERSION, cap);
    }
    recompiler_trace_bytes = (unsigned long long)ftell(recompiler_trace_file);
    if (!recompiler_trace_exit_registered) {
        atexit(recompiler_trace_close);
        recompiler_trace_exit_registered = 1;
    }
    state->host_unit_trace = recompiler_trace_unit;
    return 1;
}
""";

    private static void EmitUnitTraceWriter(StringBuilder sb) => sb.AppendLine(UnitTraceWriterSource);

    private static void EmitDispatchFunction(
        StringBuilder sb, RecompilerIrProgram program, LoadedCodeTable loadedCode, bool traceUnits)
    {
        sb.AppendLine($"int32_t recompiler_dispatch({StateStruct}* {StateParam}, uint32_t budget) {{");
        sb.AppendLine(IndentUnit + "uint32_t steps = 0;");
        // Issue #774: a trace build attaches its trace on the first dispatch of the process. Emitted only with traceUnits.
        if (traceUnits)
            sb.AppendLine(IndentUnit + "{ static int trace_started = 0; if (!trace_started) { trace_started = 1; recompiler_trace_attach(state); } }");
        sb.AppendLine(IndentUnit + "for (;;) {");
        sb.AppendLine(IndentUnit + IndentUnit + "uint32_t retired = 0;");
        if (s_guardStats && loadedCode.Blocks.Count != 0) sb.AppendLine(IndentUnit + IndentUnit + $"PSX_GS_TICK({StateParam});");
        sb.AppendLine(IndentUnit + IndentUnit + "state->partial_retired = 0u; state->unit_interrupted = 0u;");
        // Issue #680: the interrupt boundary. Every iteration starts between dispatch units, and a unit
        // fuses a branch with its delay slot, so this is never inside a branch + delay-slot pair —
        // where the interpreter's CPU takes INT too (not while branch_pending_). Gated by the same
        // budget guard as the blocks below, so budget == 0 still executes nothing. A null hook (every
        // state that does not set one) keeps the pre-existing behavior.
        sb.AppendLine(IndentUnit + IndentUnit + $"if ({StateParam}->{HostInterruptField} != 0 && steps < budget) {{ {StateParam}->{HostInterruptField}({StateParam}); }}");
        // Issue #774: the unit's entry PC, captured after the interrupt boundary (which may move pc to the vector). The
        // credit below cannot recover it: an interrupted unit has already set pc to its resume address.
        if (traceUnits)
            sb.AppendLine(IndentUnit + IndentUnit + $"uint32_t unit_entry_pc = {StateParam}->{PcField};");
        if (traceUnits)
            sb.AppendLine(IndentUnit + IndentUnit + $"uint64_t unit_start_total = {StateParam}->{RetiredTotalField};");

        // A generated-host budget is a strict upper bound on retired dispatch
        // units. Known generated blocks and host-claimed transfers both spend one
        // unit. The guard therefore runs before either callback can mutate guest
        // state; budget == 0 executes nothing.
        // Clang -O0 lowers a sparse cross-image switch to thousands of linear comparisons.
        // Bound each inner switch to a 4 KiB PC page; preserve full addresses and version guards.
        // Measurement only: the slot numbering EmitGuardStatsSupport uses (entry PCs ascending).
        var guardSlots = s_guardStats
            ? loadedCode.Blocks.Select(static v => v.Block.EntryPc).Distinct().Order().Select(static (pc, i) => (pc, i)).ToDictionary(static t => t.pc, static t => t.i)
            : [];
        var splitDispatch = loadedCode.Blocks.Count != 0;
        var pages = splitDispatch
            ? program.Blocks.Select(static b => b.EntryPc >> 12).Concat(loadedCode.Blocks.Select(static b => b.Block.EntryPc >> 12)).Distinct().Order().ToArray()
            : new uint[] { 0 };
        if (splitDispatch) sb.AppendLine(IndentUnit + IndentUnit + $"switch ({StateParam}->{PcField} >> 12) {{");
        foreach (var page in pages)
        {
            if (splitDispatch) sb.AppendLine(IndentUnit + IndentUnit + $"case {FormatImmediate(page)}: {{");
            sb.AppendLine(IndentUnit + IndentUnit + $"switch ({StateParam}->{PcField}) {{");
            foreach (var block in program.Blocks.Where(b => !splitDispatch || (b.EntryPc >> 12) == page))
            {
                var functionName = $"recompiler_block_0x{block.EntryPc:X8}";
                var bodyLine = $"{StateParam}->{TerminationField} = {functionName}({StateParam});";
                sb.AppendLine(IndentUnit + IndentUnit + $"case {FormatImmediate(block.EntryPc)}: {{");
                sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + "if (steps >= budget) {");
                EmitBudgetExceededReturn(sb, 4);
                sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + "}");
                // B1 checkpoint: under -DRECOMPILER_CHECKPOINTS the generated binary
                // prints the guest PC of every retired block, so the harness can compare
                // the recompiled block trace against the interpreter's instruction trace.
                // Guaranteed emitted only when the matching block actually retires, so a
                // normal no-block Success exit never prints a spurious trailing PC.
                sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + "#ifdef RECOMPILER_CHECKPOINTS");
                sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + IndentUnit + $"printf(\"CKPT 0x%08X\\n\", {StateParam}->{PcField});");
                sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + "#endif");
                sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + $"retired = {block.RetiredInstructionCount}u;");
                sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + bodyLine);
                sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + "break;");
                sb.AppendLine(IndentUnit + IndentUnit + "}");
            }

            // Issue #732: RAM-placed code. Each pre-generated version of the code at this PC runs only while guest memory
            // holds exactly the words it was compiled from; the first version that matches is selected. With none, the PC is
            // treated exactly like one without a block (the host transfer). The identity check retires nothing.
            foreach (var group in loadedCode.Blocks.Where(v => (v.Block.EntryPc >> 12) == page).GroupBy(static v => v.Block.EntryPc))
            {
                var indent = IndentUnit + IndentUnit + IndentUnit;
                sb.AppendLine(IndentUnit + IndentUnit + $"case {FormatImmediate(group.Key)}: {{");
                var guardSlot = s_guardStats ? guardSlots[group.Key] : -1;
                // arrive = hit + miss + refuse: an arrival whose budget check refuses it ran nothing and is counted as a refusal.
                if (s_guardStats) sb.AppendLine(indent + $"PSX_GS_ARRIVE({guardSlot});");
                var version = 0;
                foreach (var loaded in group)
                {
                    sb.AppendLine(indent + $"static const uint32_t code{version}[] = {{ {string.Join(", ", loaded.Words.Select(FormatHex))} }};");
                    sb.AppendLine(indent + $"static uint64_t seen{version} = UINT64_MAX;");
                    sb.AppendLine(indent + $"if ({CodeGuardHelper}({StateParam}->{CoreField}, {StateParam}->{PcField}, code{version}, {loaded.Words.Count}u, &seen{version})) {{");
                    sb.AppendLine(indent + IndentUnit + "if (steps >= budget) {");
                    if (s_guardStats) sb.AppendLine(indent + IndentUnit + IndentUnit + $"PSX_GS_REFUSE({guardSlot});");
                    EmitBudgetExceededReturn(sb, 5);
                    sb.AppendLine(indent + IndentUnit + "}");
                    // Counted after the budget check: a unit the budget refuses did not run and is not a hit (Issue #797).
                    if (s_guardStats) sb.AppendLine(indent + IndentUnit + $"PSX_GS_HIT({guardSlot}, {version});");
                    sb.AppendLine(indent + IndentUnit + "#ifdef RECOMPILER_CHECKPOINTS");
                    sb.AppendLine(indent + IndentUnit + IndentUnit + $"printf(\"CKPT 0x%08X\\n\", {StateParam}->{PcField});");
                    sb.AppendLine(indent + IndentUnit + "#endif");
                    sb.AppendLine(indent + IndentUnit + $"retired = {loaded.Block.RetiredInstructionCount}u;");
                    sb.AppendLine(indent + IndentUnit + $"{StateParam}->{TerminationField} = recompiler_block_0x{group.Key:X8}_v{version}({StateParam});");
                    sb.AppendLine(indent + IndentUnit + "break;");
                    sb.AppendLine(indent + "}");
                    version++;
                }

                if (s_guardStats) sb.AppendLine(indent + $"PSX_GS_MISS({guardSlot}, {StateParam});");
                sb.AppendLine(indent + $"goto {UnknownPcLabel};");
                sb.AppendLine(IndentUnit + IndentUnit + "}");
            }

            if (splitDispatch)
            {
                sb.AppendLine(IndentUnit + IndentUnit + $"default: goto {UnknownPcLabel};");
                sb.AppendLine(IndentUnit + IndentUnit + "}");
                sb.AppendLine(IndentUnit + IndentUnit + "break;");
                sb.AppendLine(IndentUnit + IndentUnit + "}");
            }
        }

        // The unknown-PC boundary. A null hook has no side effect, so the existing
        // normal fall-off / unsupported-entry distinction can be resolved without
        // spending budget. A real host callback may mutate state and therefore must
        // not be invoked once the strict dispatch budget is exhausted.
        sb.AppendLine(IndentUnit + IndentUnit + (loadedCode.Blocks.Count == 0 ? "default: {" : $"default: {UnknownPcLabel}: {{"));
        sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + $"if ({StateParam}->{HostTransferField} == 0) {{");
        sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + IndentUnit + $"if (steps > 0) {{ {StateParam}->{TerminationField} = RECOMPILER_REASON_SUCCESS; return 0; }}");
        sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + IndentUnit + $"{StateParam}->{TerminationField} = RECOMPILER_REASON_UNSUPPORTED_IR; return (int32_t)RECOMPILER_REASON_UNSUPPORTED_IR;");
        sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + "}");
        sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + "if (steps >= budget) {");
        EmitBudgetExceededReturn(sb, 4);
        sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + "}");
        // Issue #774: every transfer to the host is one handoff, whatever serves it (fallback, BIOS, exception).
        if (traceUnits)
            sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + $"recompiler_trace_handoff({StateParam});");
        sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + $"int32_t hosted = {StateParam}->{HostTransferField}({StateParam});");
        // A decline the budget guard above let through is definitive: this pc has
        // no block and no host claim, regardless of steps. Unlike the null-hook
        // case, "steps == 0" here can just mean a segment restart landed back on
        // this same pc with a fresh budget, not an invalid entry — so it must not
        // gate this outcome the way it gates the null-hook branch above.
        sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + "if (hosted != 0) {");
        sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + IndentUnit + $"{StateParam}->{TerminationField} = RECOMPILER_REASON_SUCCESS; return 0;");
        sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + "}");
        // A claimed transfer retires like a block, so it appears in the checkpoint
        // trace at the PC it was claimed for and spends a step from the same budget.
        sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + "#ifdef RECOMPILER_CHECKPOINTS");
        sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + IndentUnit + $"printf(\"CKPT 0x%08X\\n\", {StateParam}->{PcField});");
        sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + "#endif");
        sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + "break;");
        sb.AppendLine(IndentUnit + IndentUnit + "}");
        sb.AppendLine(IndentUnit + IndentUnit + "}");

        // Issue #774: whether this unit raised a guest exception, read before the exception handling below consumes it.
        if (traceUnits)
            sb.AppendLine(IndentUnit + IndentUnit + $"uint32_t unit_raised = ({StateParam}->{TerminationField} == RECOMPILER_REASON_EXCEPTION && {StateParam}->{ExceptionRaisedField} != 0u) ? 1u : 0u;");

        // Issue #732: in firmware mode a SYSCALL/BREAK exit enters the guest's own exception vector, exactly as
        // the interpreter's RaiseException does, and execution continues there (typically the kernel handler at
        // 0x80000080). The trapping instruction retires nothing. Emitted only for a program with a trap exit.
        if (program.Blocks.Concat(loadedCode.Blocks.Select(static v => v.Block)).Any(static block => block.Exit.Exception is { IsRaised: true } || block.MemoryFaultSites.Count != 0))
        {
            sb.AppendLine(IndentUnit + IndentUnit + $"if ({StateParam}->{TerminationField} == RECOMPILER_REASON_EXCEPTION && " +
                $"{StateParam}->{ExceptionRaisedField} != 0u && {StateParam}->{GuestExceptionsField} != 0u) {{");
            sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + $"{StateParam}->{NextPcField} = {ExceptionEntryHelper}({StateParam}, " +
                $"{StateParam}->{ExceptionCodeField}, {StateParam}->{ExceptionFaultPcField}, {StateParam}->{ExceptionInDelaySlotField});");
            sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + $"{StateParam}->{ExceptionRaisedField} = 0u; " +
                $"{StateParam}->{ExceptionCodeField} = 0u; {StateParam}->{ExceptionFaultPcField} = 0u; {StateParam}->{ExceptionInDelaySlotField} = 0u;");
            sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + $"{StateParam}->{TerminationField} = RECOMPILER_REASON_SUCCESS; retired = state->partial_retired;");
            sb.AppendLine(IndentUnit + IndentUnit + "}");
        }

        // Issue #663: a SYSCALL exception exit (Excode 8, not in a delay slot) is offered
        // to the host's syscall hook. A host that completes it resumes the guest; the
        // exception state it raised is consumed, so the snapshot does not report a
        // stale exception. Without a hook (or when it declines) the exit is unchanged.
        // Emitted only for a program that has such an exit, so every other program's
        // dispatch text stays byte for byte as it was.
        if (program.Blocks.Concat(loadedCode.Blocks.Select(static v => v.Block)).Any(static block => block.Exit.Exception is { IsRaised: true, Code: SyscallExcode }))
        {
            sb.AppendLine(IndentUnit + IndentUnit + $"if ({StateParam}->{TerminationField} == RECOMPILER_REASON_EXCEPTION && " +
                $"{StateParam}->{ExceptionRaisedField} != 0u && {StateParam}->{ExceptionCodeField} == 8u && " +
                $"{StateParam}->{ExceptionInDelaySlotField} == 0u && {StateParam}->{HostSyscallField} != 0) {{");
            sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + $"if ({StateParam}->{HostSyscallField}({StateParam}) == 0 && " +
                $"{StateParam}->{TerminationField} == RECOMPILER_REASON_SUCCESS) {{");
            sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + IndentUnit + $"{StateParam}->{ExceptionRaisedField} = 0u; " +
                $"{StateParam}->{ExceptionCodeField} = 0u; {StateParam}->{ExceptionFaultPcField} = 0u; {StateParam}->{ExceptionInDelaySlotField} = 0u;");
            sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + "}");
            sb.AppendLine(IndentUnit + IndentUnit + "}");
        }

        // Stop on a non-Success termination from the retired dispatch unit.
        sb.AppendLine(IndentUnit + IndentUnit + $"if ({StateParam}->{TerminationField} != RECOMPILER_REASON_SUCCESS) {{");
        // Issue #774: a unit that stops here completed only the instructions already credited at its in-body boundaries
        // (end - start = that partial count); nothing further is credited, and the record carries the stop reason.
        if (traceUnits)
            sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + $"if ({StateParam}->host_unit_trace != 0) {{ {StateParam}->host_unit_trace({StateParam}, unit_entry_pc, {StateParam}->{NextPcField}, " +
                $"unit_start_total, {StateParam}->{RetiredTotalField}, ({StateParam}->unit_interrupted ? 1u : 0u) | (unit_raised << 1), {StateParam}->{TerminationField}); }}");
        sb.AppendLine(IndentUnit + IndentUnit + IndentUnit + $"return {StateParam}->{TerminationField};");
        sb.AppendLine(IndentUnit + IndentUnit + "}");

        sb.AppendLine(IndentUnit + IndentUnit + "if (state->unit_interrupted) retired = state->partial_retired;");
        // Charge only the remainder: interior retirements were already synchronized.
        // A fault charges no instruction of its own; any preceding successful fused
        // instructions keep their time. An interior load INT retires only the load.
        sb.AppendLine(IndentUnit + IndentUnit + $"{StateParam}->{RetiredTotalField} += retired - {StateParam}->partial_retired;");
        if (traceUnits)
            sb.AppendLine(IndentUnit + IndentUnit + $"if ({StateParam}->host_unit_trace != 0) {{ {StateParam}->host_unit_trace({StateParam}, unit_entry_pc, {StateParam}->{NextPcField}, " +
                $"unit_start_total, {StateParam}->{RetiredTotalField}, ({StateParam}->unit_interrupted ? 1u : 0u) | (unit_raised << 1), 0); }}");
        sb.AppendLine(IndentUnit + IndentUnit + $"if ({StateParam}->{HostRetiredField} != 0) {{ {StateParam}->{HostRetiredField}({StateParam}); }}");
        sb.AppendLine(IndentUnit + IndentUnit + $"{StateParam}->{PcField} = {StateParam}->{NextPcField};");
        sb.AppendLine(IndentUnit + IndentUnit + "steps++;");
        sb.AppendLine(IndentUnit + "}");
        sb.AppendLine("}");
        sb.AppendLine();
    }
}
