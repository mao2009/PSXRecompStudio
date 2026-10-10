using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
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
    /// in input A, and produces no result. Every store (Store8/16/32) is dropped while
    /// SR.IsC isolates the data cache and the address is below
    /// <see cref="RecompilerCop0.CacheIsolationEnd"/> (<see cref="RecompilerCop0.StoreIsCacheIsolated"/>):
    /// that guard is part of the store's effect, which every backend must apply.
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

    /// <summary>Produces 1 when the signed (32-bit two's-complement) value of input A
    /// is less than the signed value of input B, otherwise 0.</summary>
    CompareLessThanSigned,

    /// <summary>Produces 1 when the unsigned value of input A is less than the
    /// unsigned value of input B, otherwise 0.</summary>
    CompareLessThanUnsigned,

    /// <summary>
    /// Adds two 32-bit operands as signed R3000A values. On signed overflow the
    /// operation terminates the current execution with an Overflow exception
    /// instead of producing a wrapped result; consumers must not execute later
    /// operations in the block in that case.
    /// </summary>
    AddSigned,

    /// <summary>
    /// SLLV: logical left shift of input A by input B, masked to input B's low
    /// 5 bits. Unlike <see cref="ShiftLeftLogical"/>, the amount is a runtime
    /// value (input B) rather than a compile-time <c>ShiftAmount</c> byte,
    /// because SLLV's amount comes from a GPR at execution time (MIPS I:
    /// only bits 4:0 of the register are architecturally significant).
    /// </summary>
    ShiftLeftLogicalVariable,

    /// <summary>SRLV: logical right shift of input A by input B, masked to input B's low 5 bits (see <see cref="ShiftLeftLogicalVariable"/>).</summary>
    ShiftRightLogicalVariable,

    /// <summary>SRAV: arithmetic (sign-filling) right shift of input A by input B, masked to input B's low 5 bits (see <see cref="ShiftLeftLogicalVariable"/>).</summary>
    ShiftRightArithmeticVariable,

    /// <summary>
    /// Produces the current value of the HI register (the multiply/divide
    /// unit's second 32-bit slot). HI/LO are architectural state distinct from
    /// the 32 GPRs (Issue #497's <c>PSXCpu::hi_</c>/<c>lo_</c>), so they get
    /// their own operation kinds rather than an out-of-range <see cref="ReadGpr"/>
    /// register number.
    /// </summary>
    ReadHi,

    /// <summary>Produces the current value of the LO register (see <see cref="ReadHi"/>).</summary>
    ReadLo,

    /// <summary>Writes input A to the HI register and produces no result (see <see cref="ReadHi"/>).</summary>
    WriteHi,

    /// <summary>Writes input A to the LO register and produces no result (see <see cref="ReadHi"/>).</summary>
    WriteLo,

    /// <summary>
    /// MULT: the signed 64-bit product of inputs A and B (each reinterpreted as
    /// a two's-complement <c>int32</c>), writing HI = the upper 32 bits and
    /// LO = the lower 32 bits directly — mirrors <c>PSXCpu::ExecMult</c> /
    /// <c>psx_cpu_hilo_mult</c>. Produces no SSA result; the new HI/LO values
    /// are read back through <see cref="ReadHi"/>/<see cref="ReadLo"/>, the same
    /// way a guest store is read back through a later load.
    /// </summary>
    MultiplySigned,

    /// <summary>MULTU: the unsigned 64-bit product of inputs A and B, split the same way as <see cref="MultiplySigned"/> (see <c>PSXCpu::ExecMultu</c>).</summary>
    MultiplyUnsigned,

    /// <summary>
    /// DIV: signed division of input A (dividend) by input B (divisor), writing
    /// LO = quotient and HI = remainder, with the PS1/MIPS-I divide-by-zero and
    /// <c>INT_MIN / -1</c> special cases <c>PSXCpu::ExecDiv</c> / <c>psx_cpu_hilo_div</c>
    /// define (never a host trap or UB). Produces no SSA result (see <see cref="MultiplySigned"/>).
    /// </summary>
    DivideSigned,

    /// <summary>DIVU: unsigned division of input A by input B, split the same way as <see cref="DivideSigned"/>, with the divide-by-zero special case <c>PSXCpu::ExecDivu</c> defines.</summary>
    DivideUnsigned,

    /// <summary>
    /// MFC0: produces the value of the COP0 register numbered by <c>Register</c>
    /// (0-31, <c>PSXCpu::cop0_</c>). The read itself has no side effect; MFC0's load
    /// delay is placed by the lowering exactly like a memory load's
    /// (<see cref="RecompilerCop0"/>).
    /// </summary>
    ReadCop0,

    /// <summary>
    /// MTC0: writes input A to the COP0 register numbered by <c>Register</c>, with
    /// <c>PSXCpu::ExecMtc0</c>'s semantics: CAUSE keeps every bit except the software
    /// interrupt bits <see cref="RecompilerCop0.CauseWritableMask"/>; every other
    /// register takes the whole value. Produces no result.
    /// </summary>
    WriteCop0,

    /// <summary>
    /// RFE: pops the SR KU/IE stack (<c>psx_cpu_cop0_rfe</c>:
    /// <c>(sr &amp; ~0xF) | ((sr &gt;&gt; 2) &amp; 0xF)</c>). No operands, no result; the
    /// PC restore is the guest's own JR (ADR-005).
    /// </summary>
    ReturnFromException,
}

/// <summary>
/// COP0 register numbers and bit masks the IR and every backend share (Issue #732),
/// mirroring <c>src/PSXRecomp.Native/src/psx_cpu_cop0.cpp</c> /
/// <c>rust/src/cpu_cop0.rs</c> / <c>rust/src/cpu_exception.rs</c>. One C# source so
/// the IR evaluator, the host code generator and the artifact driver cannot drift.
/// </summary>
[Domain]
public static class RecompilerCop0
{
    /// <summary>BadVAddr (cop0r8).</summary>
    public const byte BadVAddr = 8;

    /// <summary>SR (cop0r12).</summary>
    public const byte Status = 12;

    /// <summary>CAUSE (cop0r13).</summary>
    public const byte Cause = 13;

    /// <summary>EPC (cop0r14).</summary>
    public const byte Epc = 14;

    /// <summary>CAUSE bits MTC0 may write: IP[1:0] (<c>CAUSE_SW_IP_MASK</c>).</summary>
    public const uint CauseWritableMask = 0x300u;

    /// <summary>
    /// SR.IsC (bit 16). While set, a data store to an address below
    /// <see cref="CacheIsolationEnd"/> is dropped (<c>PSXCpu::StoreIsCacheIsolated</c>).
    /// </summary>
    public const uint StatusIsolateCache = 0x00010000u;

    /// <summary>First virtual address (KSEG1) a store reaches even while SR.IsC is set.</summary>
    public const uint CacheIsolationEnd = 0xA0000000u;

    /// <summary>SR.BEV (bit 22): selects the ROM exception vector.</summary>
    public const uint StatusBootExceptionVectors = 0x00400000u;

    /// <summary>CAUSE bits an exception entry replaces: Excode (6:2), CE (29:28) and BD (31).</summary>
    public const uint CauseEntryMask = 0x7Cu | 0x30000000u | 0x80000000u;

    /// <summary>CAUSE.BD (bit 31).</summary>
    public const uint CauseBranchDelay = 0x80000000u;

    /// <summary>General exception vector with SR.BEV = 0.</summary>
    public const uint RamExceptionVector = 0x80000080u;

    /// <summary>General exception vector with SR.BEV = 1.</summary>
    public const uint RomExceptionVector = 0xBFC00180u;

    /// <summary><c>psx_cpu_cop0_write_cause</c>.</summary>
    public static uint WriteCause(uint cause, uint written) =>
        (cause & ~CauseWritableMask) | (written & CauseWritableMask);

    /// <summary><c>psx_cpu_cop0_rfe</c>.</summary>
    public static uint ReturnFromException(uint sr) => (sr & ~0xFu) | ((sr >> 2) & 0xFu);

    /// <summary>Whether a store to <paramref name="address"/> is dropped under <paramref name="sr"/>.</summary>
    public static bool StoreIsCacheIsolated(uint sr, uint address) =>
        (sr & StatusIsolateCache) != 0 && address < CacheIsolationEnd;
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

/// <summary>
/// Classifies the guest-memory effect a Load8/16/32 or Store8/16/32 operation's
/// address carries, when that address can be established. This is the IR-level
/// effect contract (Issue #411): it distinguishes an ordinary guest-RAM access
/// from an MMIO/device-visible one so a consumer — validator, codegen, or a
/// future optimizer — never has to guess.
/// </summary>
[Domain]
public enum RecompilerIrMemoryEffectKind : byte
{
    /// <summary>
    /// The address was not established as ordinary memory or a device register
    /// at IR-construction time. This is the default, and it is what every real
    /// <see cref="MipsToIrLowerer"/> load/store carries: a MIPS base+offset
    /// effective address depends on a guest register value that is only known
    /// at execution time, not at lowering time. An unknown effect must never be
    /// treated as ordinary — it is not reorderable, not dead-store-eliminable,
    /// and not CSE-eligible.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The address is provably ordinary guest memory (RAM or BIOS ROM, per
    /// <see cref="Runtime.Ps1AddressTranslation"/> and
    /// <see cref="Dma.Ps1MemoryMap.ClassifyRegion"/>): no device-visible side
    /// effect. This does not mean idempotent/pure — an ordinary store still
    /// mutates guest state that a later load or aliased store can observe, so
    /// normal memory dependencies apply: a load still needs alias-analysis
    /// proof before CSE, and a store still needs liveness and alias proof
    /// before dead-store elimination or reordering.
    /// </summary>
    Ordinary = 1,

    /// <summary>
    /// The address is provably a PS1 hardware/device register (the
    /// <see cref="Dma.Ps1MemoryMap.HwRegBase"/>..<see cref="Dma.Ps1MemoryMap.HwRegEnd"/>
    /// window). A device read is not idempotent/pure and a device write is not
    /// dead-store-eliminable; the relative order of device operations, and of a
    /// device operation against any other observable effect, must be preserved.
    /// </summary>
    Device = 2,
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
        uint immediate = 0,
        RecompilerIrMemoryEffectKind memoryEffect = RecompilerIrMemoryEffectKind.Unknown)
    {
        Kind = kind;
        ResultValueId = resultValueId;
        InputValueA = inputValueA;
        InputValueB = inputValueB;
        Register = register;
        ShiftAmount = shiftAmount;
        Immediate = immediate;
        MemoryEffect = memoryEffect;
    }

    public RecompilerIrOperationKind Kind { get; }
    public int ResultValueId { get; }
    public int InputValueA { get; }
    public int InputValueB { get; }
    public byte Register { get; }
    public byte ShiftAmount { get; }
    public uint Immediate { get; }

    /// <summary>
    /// The memory-effect classification for a Load8/16/32 or Store8/16/32
    /// operation (see <see cref="RecompilerIrMemoryEffectKind"/>). Meaningless
    /// for any other operation kind, which must leave it at its
    /// <see cref="RecompilerIrMemoryEffectKind.Unknown"/> default — the
    /// validator enforces both.
    /// </summary>
    public RecompilerIrMemoryEffectKind MemoryEffect { get; }
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
        RecompilerIrFlow? flow = null,
        RecompilerExceptionState? exception = null,
        int? targetValueId = null)
    {
        Reason = reason;
        NextPc = nextPc;
        Flow = flow;
        Exception = exception;
        TargetValueId = targetValueId;
    }

    public RecompilerIrTerminationReason Reason { get; }
    public uint? NextPc { get; }
    public RecompilerIrFlow? Flow { get; }

    /// <summary>
    /// The value id holding a runtime next PC (Issue #635): the register-indirect
    /// target of JR/JALR, read before the link write and the delay slot. Only
    /// valid on a flow-less <see cref="RecompilerIrTerminationReason.Success"/>
    /// exit, as the alternative to <see cref="NextPc"/> — exactly one of the two
    /// is set. The runtime decides what the address is (a compiled block, a BIOS
    /// vector through the host transfer, or an unresolved transfer); the IR does
    /// not classify it.
    /// </summary>
    public int? TargetValueId { get; }

    /// <summary>
    /// The exception state an <see cref="RecompilerIrTerminationReason.Exception"/>
    /// exit carries with it (e.g. a BREAK's Excode, faulting PC and delay-slot
    /// flag). Null when the exit terminates without carrying exception details —
    /// the generic Exception exit a trapping operation such as <c>AddSigned</c>
    /// overflow produces, which only traps to the runtime and identifies the
    /// faulting instruction by the dispatch-time PC. Only valid on an Exception
    /// exit (see <see cref="RecompilerIrValidator"/>).
    /// </summary>
    public RecompilerExceptionState? Exception { get; }
}

/// <summary>CPU provenance for an aligned memory primitive that may fault before taking effect.</summary>
[Domain]
public sealed record RecompilerMemoryFaultSite(
    int OperationIndex, uint FaultPc, bool InDelaySlot, int RetiredPrefix,
    int PendingLoadRegister = -1, int PendingLoadValueId = -1);

[Domain]
public sealed record RecompilerIrBlock
{
    public RecompilerIrBlock(
        uint entryPc,
        IEnumerable<RecompilerIrOperation> operations,
        RecompilerIrExit exit,
        int retiredInstructionCount = 1,
        IEnumerable<RecompilerMemoryFaultSite>? memoryFaultSites = null)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentOutOfRangeException.ThrowIfLessThan(retiredInstructionCount, 1);
        Exit = exit ?? throw new ArgumentNullException(nameof(exit));
        EntryPc = entryPc;
        Operations = new ReadOnlyCollection<RecompilerIrOperation>(operations.ToArray());
        RetiredInstructionCount = retiredInstructionCount;
        MemoryFaultSites = new ReadOnlyCollection<RecompilerMemoryFaultSite>((memoryFaultSites ?? []).ToArray());
    }

    public uint EntryPc { get; }

    /// <summary>
    /// The guest instructions this block retires when it completes (Issue #679): one for a
    /// straight-line instruction, two for a control transfer fused with its delay slot or a
    /// load fused with its load-delay observer, three for a load, the control transfer that
    /// observes it, and that transfer's delay slot. It is what a backend reports as elapsed
    /// guest time; a faulting instruction retires nothing, while memory-fault
    /// provenance records any completed prefix.
    /// </summary>
    public int RetiredInstructionCount { get; }
    /// <summary>Ordered aligned-memory fault sites; raw or partial-word IR may omit this CPU provenance.</summary>
    [JsonIgnore]
    public IReadOnlyList<RecompilerMemoryFaultSite> MemoryFaultSites { get; }

    /// <summary>Optional additive serialized provenance; legacy blocks retain their existing schema.</summary>
    [JsonPropertyName("memoryFaultSites")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RecompilerMemoryFaultSite>? SerializedMemoryFaultSites => MemoryFaultSites.Count == 0 ? null : MemoryFaultSites;
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
        Functions = new ReadOnlyCollection<RecompilerIrFunction>((functions ?? Array.Empty<RecompilerIrFunction>()).OrderBy(function => function.EntryPc).ToArray());
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
            if (block.MemoryFaultSites.Select(static site => site.OperationIndex).Distinct().Count() != block.MemoryFaultSites.Count
                || !block.MemoryFaultSites.Select(static site => site.OperationIndex).SequenceEqual(block.MemoryFaultSites.Select(static site => site.OperationIndex).Order()))
                Add(diagnostics, RecompilerIrDiagnosticCode.InvalidOperationShape, "Memory fault sites must be unique and ordered.", blockIndex);
            foreach (var site in block.MemoryFaultSites)
                if (site.OperationIndex < 0 || site.OperationIndex >= block.Operations.Count
                    || site.RetiredPrefix < 0 || site.RetiredPrefix >= block.RetiredInstructionCount || (site.FaultPc & 3u) != 0
                    || (site.InDelaySlot && site.RetiredPrefix == 0)
                    || site.FaultPc != unchecked(block.EntryPc + (uint)(site.RetiredPrefix - (site.InDelaySlot ? 1 : 0)) * 4))
                    Add(diagnostics, RecompilerIrDiagnosticCode.InvalidOperationShape, "Invalid memory fault site location or retirement prefix.", blockIndex);
            var definedValueIds = new HashSet<int>();
            for (var operationIndex = 0; operationIndex < block.Operations.Count; operationIndex++)
            {
                var operation = block.Operations[operationIndex];
                foreach (var site in block.MemoryFaultSites.Where(site => site.OperationIndex == operationIndex))
                {
                    if (operation.Kind is not (RecompilerIrOperationKind.Load16 or RecompilerIrOperationKind.Load32 or RecompilerIrOperationKind.Store16 or RecompilerIrOperationKind.Store32)
                        || site.PendingLoadRegister < -1 || site.PendingLoadRegister == 0 || site.PendingLoadRegister > 31
                        || (site.PendingLoadRegister < 0) != (site.PendingLoadValueId < 0)
                        || (site.PendingLoadRegister >= 0 && site.RetiredPrefix == 0))
                        Add(diagnostics, RecompilerIrDiagnosticCode.InvalidOperationShape, "Invalid aligned memory fault or pending load shape.", blockIndex, operationIndex);
                    if (site.PendingLoadValueId >= 0) ValidateInput(site.PendingLoadValueId, definedValueIds, diagnostics, blockIndex, operationIndex);
                }
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

        if (exit.Exception is not null && exit.Reason != RecompilerIrTerminationReason.Exception)
        {
            Add(diagnostics, RecompilerIrDiagnosticCode.IllegalTermination, "An exit carrying exception state must terminate with the Exception reason.", blockIndex);
            return;
        }

        var flow = exit.Flow;
        if (exit.TargetValueId is { } targetValueId)
        {
            if (exit.Reason != RecompilerIrTerminationReason.Success || flow is not null || exit.NextPc is not null)
            {
                Add(diagnostics, RecompilerIrDiagnosticCode.IllegalTermination, "A runtime target value is only valid on a flow-less success exit without a next PC.", blockIndex);
            }
            else if (!definedValueIds.Contains(targetValueId))
            {
                Add(diagnostics, RecompilerIrDiagnosticCode.MissingOperand, "A runtime target value must be defined by an earlier operation in the block.", blockIndex);
            }
            return;
        }

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
                ValidateMemoryEffect(operation, diagnostics, blockIndex, operationIndex);
                break;
            case RecompilerIrOperationKind.Store8:
            case RecompilerIrOperationKind.Store16:
            case RecompilerIrOperationKind.Store32:
                Require(!hasResult && hasA && hasB, diagnostics, blockIndex, operationIndex);
                if (operation.Register != 0)
                {
                    Add(diagnostics, RecompilerIrDiagnosticCode.InvalidMemoryAccess, "Store operations must not carry a register.", blockIndex, operationIndex);
                }
                ValidateMemoryEffect(operation, diagnostics, blockIndex, operationIndex);
                break;
            case RecompilerIrOperationKind.CompareEqual:
            case RecompilerIrOperationKind.CompareNotEqual:
            case RecompilerIrOperationKind.CompareLessThanSigned:
            case RecompilerIrOperationKind.CompareLessThanUnsigned:
                Require(hasResult && hasA && hasB && operation.ShiftAmount == 0, diagnostics, blockIndex, operationIndex);
                break;
            case RecompilerIrOperationKind.ShiftLeftLogical:
            case RecompilerIrOperationKind.ShiftRightLogical:
            case RecompilerIrOperationKind.ShiftRightArithmetic:
                Require(hasResult && hasA && !hasB && operation.ShiftAmount <= 31, diagnostics, blockIndex, operationIndex);
                break;
            case RecompilerIrOperationKind.ReadHi:
            case RecompilerIrOperationKind.ReadLo:
                Require(hasResult && !hasA && !hasB, diagnostics, blockIndex, operationIndex);
                if (operation.Register != 0 || operation.ShiftAmount != 0)
                {
                    Add(diagnostics, RecompilerIrDiagnosticCode.InvalidRegister, "A HI/LO read must not carry a GPR number or shift amount.", blockIndex, operationIndex);
                }
                break;
            case RecompilerIrOperationKind.WriteHi:
            case RecompilerIrOperationKind.WriteLo:
                Require(!hasResult && hasA && !hasB, diagnostics, blockIndex, operationIndex);
                if (operation.Register != 0 || operation.ShiftAmount != 0)
                {
                    Add(diagnostics, RecompilerIrDiagnosticCode.InvalidRegister, "A HI/LO write must not carry a GPR number or shift amount.", blockIndex, operationIndex);
                }
                break;
            case RecompilerIrOperationKind.MultiplySigned:
            case RecompilerIrOperationKind.MultiplyUnsigned:
            case RecompilerIrOperationKind.DivideSigned:
            case RecompilerIrOperationKind.DivideUnsigned:
                Require(!hasResult && hasA && hasB && operation.ShiftAmount == 0, diagnostics, blockIndex, operationIndex);
                if (operation.Register != 0)
                {
                    Add(diagnostics, RecompilerIrDiagnosticCode.InvalidRegister, "A multiply/divide operation must not carry a GPR number.", blockIndex, operationIndex);
                }
                break;
            case RecompilerIrOperationKind.ReadCop0:
                Require(hasResult && !hasA && !hasB && operation.ShiftAmount == 0, diagnostics, blockIndex, operationIndex);
                break;
            case RecompilerIrOperationKind.WriteCop0:
                Require(!hasResult && hasA && !hasB && operation.ShiftAmount == 0, diagnostics, blockIndex, operationIndex);
                break;
            case RecompilerIrOperationKind.ReturnFromException:
                Require(!hasResult && !hasA && !hasB && operation.ShiftAmount == 0 && operation.Register == 0, diagnostics, blockIndex, operationIndex);
                break;
            default:
                Require(hasResult && hasA && hasB && operation.ShiftAmount == 0, diagnostics, blockIndex, operationIndex);
                break;
        }

        var isMemoryAccess = operation.Kind is RecompilerIrOperationKind.Load8 or RecompilerIrOperationKind.Load16 or RecompilerIrOperationKind.Load32
            or RecompilerIrOperationKind.Store8 or RecompilerIrOperationKind.Store16 or RecompilerIrOperationKind.Store32;
        if (!isMemoryAccess && operation.MemoryEffect != RecompilerIrMemoryEffectKind.Unknown)
        {
            Add(diagnostics, RecompilerIrDiagnosticCode.InvalidMemoryAccess, "Only a memory-access operation may carry a memory effect.", blockIndex, operationIndex);
        }
    }

    /// <summary>
    /// Fails closed on a Load/Store operation whose memory effect is not one of
    /// the defined <see cref="RecompilerIrMemoryEffectKind"/> values, rather than
    /// letting a garbage byte silently pass through as if it meant something
    /// (Issue #411: an unsupported/unclassifiable effect must be an explicit
    /// diagnostic, never a silent fallback to "ordinary").
    /// </summary>
    private static void ValidateMemoryEffect(RecompilerIrOperation operation, List<RecompilerIrDiagnostic> diagnostics, int blockIndex, int operationIndex)
    {
        if (!Enum.IsDefined(operation.MemoryEffect))
        {
            Add(diagnostics, RecompilerIrDiagnosticCode.InvalidMemoryAccess, "Memory effect must be a defined value.", blockIndex, operationIndex);
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
        IEnumerable<RecompilerMemoryObservation>? memory = null,
        IEnumerable<uint>? pcTrace = null)
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
        PcTrace = new ReadOnlyCollection<uint>((pcTrace ?? Array.Empty<uint>()).ToArray());
    }

    public IReadOnlyList<uint> Gpr { get; }
    public uint HI { get; }
    public uint LO { get; }
    public uint PC { get; }
    public RecompilerLoadDelayState LoadDelay { get; }
    public RecompilerExceptionState Exception { get; }
    public RecompilerIrTerminationReason Termination { get; }
    public IReadOnlyList<RecompilerMemoryObservation> Memory { get; }

    /// <summary>
    /// The ordered guest PCs retired during the bounded run. The interpreter
    /// records one PC per retired MIPS instruction (delay slots included); the
    /// recompiled host records one PC per retired block (so a fused branch+delay
    /// block retires a single entry). The host trace is therefore an ordered
    /// subsequence of the interpreter trace for a matching execution (B1).
    /// </summary>
    public IReadOnlyList<uint> PcTrace { get; }
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
