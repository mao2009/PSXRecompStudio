using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Core.DiscImage;

/// <summary>How a recognized BIOS call site's function number was established.</summary>
[Domain]
public enum BiosCallResolution : byte
{
    /// <summary>
    /// The function number came from a constant materialized into R9/<c>$t1</c> by the
    /// transferring instruction's delay slot. This is the canonical PS1 stub shape, and
    /// it is only correct because the delay slot executes *before* control reaches the
    /// jump-table entry.
    /// </summary>
    DelaySlotConstant = 0,

    /// <summary>
    /// The function number came from a constant materialized into R9/<c>$t1</c> earlier
    /// in the same basic block, with no instruction in between that could clobber it.
    /// </summary>
    BlockConstant = 1,

    /// <summary>
    /// The BIOS jump-table vector was recognized, but the function number was not
    /// statically resolvable — no constant reached R9, or something that may write R9
    /// intervened. The site is still recorded: an unresolved call is evidence, and
    /// dropping it would understate a ROM's BIOS surface.
    /// </summary>
    Unresolved = 2,
}

/// <summary>
/// One guest instruction that transfers control into a PS1 BIOS jump table.
/// </summary>
[Domain]
public sealed record BiosCallSite
{
    /// <summary>Guest PC of the transferring instruction (the <c>jr</c>/<c>jalr</c>/<c>j</c>/<c>jal</c> itself).</summary>
    public required uint GuestPc { get; init; }

    /// <summary>The jump table this site dispatches through.</summary>
    public required BiosCallFamily Family { get; init; }

    /// <summary>The resolved function number, or <see langword="null"/> when unresolved.</summary>
    public byte? FunctionNumber { get; init; }

    /// <summary>The verified service name, or <see langword="null"/> when the identity is unverified or unresolved.</summary>
    public string? ServiceName { get; init; }

    /// <summary>How <see cref="FunctionNumber"/> was established.</summary>
    public required BiosCallResolution Resolution { get; init; }

    /// <summary>Start address of the basic block containing this site.</summary>
    public required uint BasicBlockStartAddress { get; init; }

    /// <summary>Entry address of the discovered function containing this site, when function discovery covered it.</summary>
    public uint? ContainingFunctionAddress { get; init; }
}

/// <summary>One aggregated <c>(family, function number)</c> identity and how many sites call it.</summary>
[Domain]
public sealed record BiosCallSummaryEntry
{
    public required BiosCallFamily Family { get; init; }

    /// <summary>The function number, or <see langword="null"/> for the family's unresolved bucket.</summary>
    public byte? FunctionNumber { get; init; }

    public string? ServiceName { get; init; }

    public required int CallSiteCount { get; init; }
}

/// <summary>
/// Structured BIOS call evidence for one analyzed executable: every recognized call site
/// plus the per-identity aggregation, both in a canonical order.
/// </summary>
[Domain]
public sealed record BiosCallEvidence
{
    /// <summary>Recognized call sites, ordered by guest PC ascending.</summary>
    public required IReadOnlyList<BiosCallSite> Sites { get; init; }

    /// <summary>
    /// Per-identity aggregation, ordered by family ordinal, then function number
    /// ascending, with each family's unresolved bucket last within that family.
    /// </summary>
    public required IReadOnlyList<BiosCallSummaryEntry> Summary { get; init; }

    /// <summary>Evidence with no recognized site.</summary>
    public static BiosCallEvidence Empty { get; } = new()
    {
        Sites = Array.Empty<BiosCallSite>(),
        Summary = Array.Empty<BiosCallSummaryEntry>(),
    };
}

/// <summary>
/// Recognizes PS1 BIOS jump-table call sites in an already decoded instruction stream.
///
/// PS1 software does not reach the BIOS through the MIPS <c>syscall</c> instruction: it
/// transfers control to the jump-table vector at physical <c>0xA0</c>, <c>0xB0</c> or
/// <c>0xC0</c> with the function number in R9 (<c>$t1</c>). A <c>j</c> cannot reach those
/// addresses from kernel-segment code (J preserves the top four PC bits), so real stubs
/// materialize the vector into a register and use <c>jr</c>/<c>jalr</c>:
///
/// <code>
///     addiu $t2, $zero, 0xA0
///     jr    $t2
///     addiu $t1, $zero, 0x3C   # delay slot: executes before the jump takes effect
/// </code>
///
/// This is a projection of the existing decode/basic-block analysis in the same spirit as
/// <see cref="FunctionDiscovery"/>: it re-decodes the recorded raw words through
/// <see cref="R3000aDecoder"/>, reuses <see cref="R3000aJumpSemantics"/>,
/// <see cref="R3000aImmediateSemantics"/> and <see cref="Ps1AddressTranslation"/> for
/// every semantic question, and emulates nothing.
///
/// Constant tracking is deliberately block-local and forward-only: each basic block starts
/// with every register unknown, and any instruction that *might* write a tracked register
/// without materializing a known constant makes it unknown again. That is why an
/// unrecognized opcode, a load (whose R3000A write is delayed), or an indirect vector with
/// no local constant yields <see cref="BiosCallResolution.Unresolved"/> instead of a
/// guessed identity. A missing fact is recorded; it is never invented.
/// </summary>
[Domain]
public static class BiosCallRecognizer
{
    /// <summary>R9 (<c>$t1</c>) carries the BIOS function number at the jump-table entry.</summary>
    private const byte FunctionNumberRegister = (byte)R3000aRegister.T1;

    /// <summary>Physical addresses of the three BIOS jump-table vectors.</summary>
    private const uint VectorA0 = 0x000000A0;
    private const uint VectorB0 = 0x000000B0;
    private const uint VectorC0 = 0x000000C0;

    /// <summary>Recognizes BIOS call sites in an analysis report's decoded instruction stream.</summary>
    public static BiosCallEvidence Recognize(DiscImageAnalysisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return Recognize(report.DecodedInstructions, report.BasicBlocks, report.FunctionDiscovery);
    }

    /// <summary>
    /// Recognizes BIOS call sites in a decoded instruction stream partitioned into basic
    /// blocks. <paramref name="functions"/> is optional; when supplied, each site is
    /// attributed to the discovered function containing it.
    /// </summary>
    public static BiosCallEvidence Recognize(
        IReadOnlyList<DecodedInstruction> instructions,
        IReadOnlyList<BasicBlock> blocks,
        FunctionDiscoveryArtifact? functions = null)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(blocks);

        if (instructions.Count == 0)
        {
            return BiosCallEvidence.Empty;
        }

        var ordered = instructions.OrderBy(static instruction => instruction.Address).ToArray();
        var leaders = new HashSet<uint>(blocks.Select(static block => block.StartAddress));
        var functionByBlockStart = BuildFunctionOwnership(functions);

        var state = new ConstantRegisterState();
        var sites = new List<BiosCallSite>();
        uint blockStart = ordered[0].Address;
        var haveBlock = false;

        for (var index = 0; index < ordered.Length; index++)
        {
            var address = ordered[index];

            // A block leader, or a gap in the linear stream, invalidates every tracked
            // constant: this analysis joins no dataflow across blocks by design.
            if (!haveBlock || leaders.Contains(address.Address)
                || (index > 0 && unchecked(ordered[index - 1].Address + 4u) != address.Address))
            {
                state.Clear();
                blockStart = address.Address;
                haveBlock = true;
            }

            var raw = R3000aDecoder.Decode(address.RawWord);

            if (TryGetBiosVectorFamily(raw, address.Address, state, out var family))
            {
                // The delay slot executes before control reaches the jump-table entry, so
                // the function number is read from the state *after* applying it.
                var atEntry = state.Clone();
                var resolution = BiosCallResolution.BlockConstant;

                if (index + 1 < ordered.Length
                    && unchecked(address.Address + 4u) == ordered[index + 1].Address)
                {
                    var delaySlot = R3000aDecoder.Decode(ordered[index + 1].RawWord);
                    var beforeDelaySlot = atEntry.TryGet(FunctionNumberRegister, out var carried);
                    Apply(atEntry, delaySlot);

                    var afterDelaySlot = atEntry.TryGet(FunctionNumberRegister, out var settled);
                    if (afterDelaySlot && (!beforeDelaySlot || settled != carried))
                    {
                        resolution = BiosCallResolution.DelaySlotConstant;
                    }
                }

                sites.Add(CreateSite(
                    address.Address,
                    family,
                    atEntry,
                    resolution,
                    blockStart,
                    functionByBlockStart));
            }

            Apply(state, raw);
        }

        sites.Sort(static (left, right) => left.GuestPc.CompareTo(right.GuestPc));
        return new BiosCallEvidence { Sites = sites, Summary = Aggregate(sites) };
    }

    private static BiosCallSite CreateSite(
        uint guestPc,
        BiosCallFamily family,
        ConstantRegisterState atEntry,
        BiosCallResolution resolution,
        uint blockStart,
        IReadOnlyDictionary<uint, uint> functionByBlockStart)
    {
        byte? functionNumber = null;
        string? serviceName = null;

        // A jump-table index wider than a byte means the recognized shape is not the
        // pattern this recognizer models, so the number is not trusted.
        if (atEntry.TryGet(FunctionNumberRegister, out var value) && value <= byte.MaxValue)
        {
            functionNumber = (byte)value;
            if (BiosCallNames.TryResolve(family, functionNumber.Value, out var name))
            {
                serviceName = name;
            }
        }
        else
        {
            resolution = BiosCallResolution.Unresolved;
        }

        return new BiosCallSite
        {
            GuestPc = guestPc,
            Family = family,
            FunctionNumber = functionNumber,
            ServiceName = serviceName,
            Resolution = resolution,
            BasicBlockStartAddress = blockStart,
            ContainingFunctionAddress = functionByBlockStart.TryGetValue(blockStart, out var owner)
                ? owner
                : null,
        };
    }

    /// <summary>
    /// Determines whether a control transfer lands on a BIOS jump-table vector. A direct
    /// <c>j</c>/<c>jal</c> target is resolved by <see cref="R3000aJumpSemantics"/>; an
    /// indirect <c>jr</c>/<c>jalr</c> target is resolved only when the jump register holds
    /// a locally known constant. Anything else — an ordinary call, an unresolved indirect
    /// jump, a return — is not a BIOS call site.
    /// </summary>
    private static bool TryGetBiosVectorFamily(
        in R3000aInstruction instruction,
        uint address,
        ConstantRegisterState state,
        out BiosCallFamily family)
    {
        uint target;
        switch (instruction.ControlFlow)
        {
            case R3000aControlFlowKind.JumpAbsolute:
                if (!R3000aJumpSemantics.TryGetJumpTarget(instruction, address, out target))
                {
                    family = default;
                    return false;
                }

                break;

            case R3000aControlFlowKind.JumpRegister:
                // Jr encodes the jump register as operand 0; Jalr encodes rd first and the
                // jump register as operand 1.
                var jumpRegisterOperand = instruction.Opcode == R3000aOpcode.Jalr
                    ? instruction.Operand1
                    : instruction.Operand0;

                if (jumpRegisterOperand.Kind != R3000aOperandKind.Register
                    || !state.TryGet(jumpRegisterOperand.Register, out target))
                {
                    family = default;
                    return false;
                }

                break;

            default:
                family = default;
                return false;
        }

        return TryMapVector(target, out family);
    }

    /// <summary>
    /// Maps a guest virtual address onto a BIOS jump-table family. The vectors live in the
    /// first page of RAM, so the KUSEG/KSEG0/KSEG1 aliases of <c>0xA0</c>/<c>0xB0</c>/
    /// <c>0xC0</c> are the same location and are accepted through the canonical
    /// translation; an untranslatable address (KSEG2 and above) is not.
    /// </summary>
    private static bool TryMapVector(uint target, out BiosCallFamily family)
    {
        if (Ps1AddressTranslation.TryTranslate(target, out var physical))
        {
            switch (physical)
            {
                case VectorA0:
                    family = BiosCallFamily.A0;
                    return true;
                case VectorB0:
                    family = BiosCallFamily.B0;
                    return true;
                case VectorC0:
                    family = BiosCallFamily.C0;
                    return true;
            }
        }

        family = default;
        return false;
    }

    /// <summary>
    /// Applies one instruction's effect on the tracked constants. Only the immediate
    /// materialization forms a BIOS stub actually uses produce a constant; every other
    /// possible GPR write invalidates its destination, and an instruction whose write
    /// target cannot be determined invalidates everything.
    /// </summary>
    private static void Apply(ConstantRegisterState state, in R3000aInstruction instruction)
    {
        switch (instruction.Opcode)
        {
            // li rt, imm  ==  addi/addiu/ori rt, $zero, imm
            case R3000aOpcode.Addi:
            case R3000aOpcode.Addiu:
            case R3000aOpcode.Ori:
                if (instruction.OperandCount == 3
                    && instruction.Operand0.Kind == R3000aOperandKind.Register
                    && instruction.Operand1.Kind == R3000aOperandKind.Register
                    && R3000aImmediateSemantics.TryGetImmediate(instruction, out var immediate))
                {
                    var destination = instruction.Operand0.Register;
                    var source = instruction.Operand1.Register;

                    if (source == (byte)R3000aRegister.Zero)
                    {
                        state.Set(destination, unchecked((uint)immediate));
                        return;
                    }

                    // ori rt, rt, imm completes the lui/ori pair that materializes a
                    // 32-bit constant such as a KSEG0-aliased vector.
                    if (instruction.Opcode == R3000aOpcode.Ori
                        && source == destination
                        && state.TryGet(source, out var upper))
                    {
                        state.Set(destination, upper | unchecked((uint)immediate));
                        return;
                    }

                    state.Invalidate(destination);
                    return;
                }

                state.InvalidateAll();
                return;

            case R3000aOpcode.Lui:
                if (instruction.OperandCount == 2
                    && instruction.Operand0.Kind == R3000aOperandKind.Register
                    && instruction.Operand1.Kind == R3000aOperandKind.Immediate)
                {
                    state.Set(instruction.Operand0.Register, instruction.Operand1.Value << 16);
                    return;
                }

                state.InvalidateAll();
                return;

            default:
                if (TryGetWrittenRegister(instruction, out var written, out var known))
                {
                    state.Invalidate(written);
                    return;
                }

                if (!known)
                {
                    state.InvalidateAll();
                }

                return;
        }
    }

    /// <summary>
    /// Reports which GPR an instruction writes. <paramref name="known"/> distinguishes
    /// "writes nothing" from "effect not modeled here"; the caller must treat an unmodeled
    /// instruction as capable of writing any register. This deliberately does not reuse the
    /// lowering stage's destination lookup, which answers the opposite question — it
    /// reports no write for the opcodes it does not lower, which would be unsound here.
    /// </summary>
    private static bool TryGetWrittenRegister(in R3000aInstruction instruction, out byte register, out bool known)
    {
        known = true;
        switch (instruction.Opcode)
        {
            // Writes rd (operand 0).
            case R3000aOpcode.Add:
            case R3000aOpcode.Addu:
            case R3000aOpcode.Sub:
            case R3000aOpcode.Subu:
            case R3000aOpcode.Slt:
            case R3000aOpcode.Sltu:
            case R3000aOpcode.And:
            case R3000aOpcode.Or:
            case R3000aOpcode.Xor:
            case R3000aOpcode.Nor:
            case R3000aOpcode.Sll:
            case R3000aOpcode.Srl:
            case R3000aOpcode.Sra:
            case R3000aOpcode.Sllv:
            case R3000aOpcode.Srlv:
            case R3000aOpcode.Srav:
            case R3000aOpcode.Mfhi:
            case R3000aOpcode.Mflo:
            // Writes rt (operand 0). Loads are architecturally delayed, but a delayed
            // write is still a write for this analysis: the old constant is gone.
            case R3000aOpcode.Slti:
            case R3000aOpcode.Sltiu:
            case R3000aOpcode.Andi:
            case R3000aOpcode.Xori:
            case R3000aOpcode.Lb:
            case R3000aOpcode.Lbu:
            case R3000aOpcode.Lh:
            case R3000aOpcode.Lhu:
            case R3000aOpcode.Lw:
            case R3000aOpcode.Lwl:
            case R3000aOpcode.Lwr:
                if (instruction.OperandCount > 0 && instruction.Operand0.Kind == R3000aOperandKind.Register)
                {
                    register = instruction.Operand0.Register;
                    return true;
                }

                break;

            case R3000aOpcode.Jal:
            case R3000aOpcode.Jalr:
            case R3000aOpcode.Bltzal:
            case R3000aOpcode.Bgezal:
                if (instruction.LinkInfo.WritesLink)
                {
                    register = instruction.LinkInfo.LinkRegister;
                    return true;
                }

                break;

            // Writes no GPR.
            case R3000aOpcode.Sb:
            case R3000aOpcode.Sh:
            case R3000aOpcode.Sw:
            case R3000aOpcode.Swl:
            case R3000aOpcode.Swr:
            case R3000aOpcode.Swc2:
            case R3000aOpcode.Lwc2:
            case R3000aOpcode.Mult:
            case R3000aOpcode.Multu:
            case R3000aOpcode.Div:
            case R3000aOpcode.Divu:
            case R3000aOpcode.Mthi:
            case R3000aOpcode.Mtlo:
            case R3000aOpcode.J:
            case R3000aOpcode.Jr:
            case R3000aOpcode.Beq:
            case R3000aOpcode.Bne:
            case R3000aOpcode.Blez:
            case R3000aOpcode.Bgtz:
            case R3000aOpcode.Bltz:
            case R3000aOpcode.Bgez:
                register = 0;
                return false;

            // Every coprocessor move shares one opcode per coprocessor, so the operation
            // kind — not the opcode — decides whether a GPR is written. A move *from* a
            // coprocessor writes GPR rt, which the operand model does not expose (the
            // decoder builds these with no operands and records only the coprocessor
            // register), so it is reported as unmodeled and invalidates everything. Moves
            // *to* a coprocessor, GTE commands and rfe write no GPR.
            case R3000aOpcode.Mfc0:
            case R3000aOpcode.Mtc0:
            case R3000aOpcode.Rfe:
            case R3000aOpcode.Cop2Command:
                switch (instruction.CopInfo.Operation)
                {
                    case R3000aCopOperationKind.MoveToCoprocessor:
                    case R3000aCopOperationKind.MoveControlToCoprocessor:
                    case R3000aCopOperationKind.ExecuteCommand:
                    case R3000aCopOperationKind.ReturnFromException:
                        register = 0;
                        return false;
                }

                break;

            // Traps, coprocessor-unusable and reserved encodings transfer to an exception
            // handler whose register effects this analysis does not model.
            default:
                break;
        }

        known = false;
        register = 0;
        return false;
    }

    /// <summary>
    /// Aggregates sites into one entry per <c>(family, function number)</c> identity, with
    /// each family's unresolved sites collected into a single trailing bucket so an
    /// unresolved call is visible in the summary rather than absent from it.
    /// </summary>
    private static IReadOnlyList<BiosCallSummaryEntry> Aggregate(IReadOnlyList<BiosCallSite> sites)
    {
        var counts = new Dictionary<(BiosCallFamily Family, int FunctionNumber), int>();
        for (var index = 0; index < sites.Count; index++)
        {
            var site = sites[index];
            var key = (site.Family, site.FunctionNumber ?? -1);
            counts[key] = counts.TryGetValue(key, out var existing) ? existing + 1 : 1;
        }

        return counts
            .OrderBy(static pair => (byte)pair.Key.Family)
            // -1 (unresolved) sorts after every real function number within its family.
            .ThenBy(static pair => pair.Key.FunctionNumber < 0 ? int.MaxValue : pair.Key.FunctionNumber)
            .Select(static pair =>
            {
                byte? functionNumber = pair.Key.FunctionNumber < 0 ? null : (byte)pair.Key.FunctionNumber;
                string? serviceName = null;
                if (functionNumber is byte number
                    && BiosCallNames.TryResolve(pair.Key.Family, number, out var name))
                {
                    serviceName = name;
                }

                return new BiosCallSummaryEntry
                {
                    Family = pair.Key.Family,
                    FunctionNumber = functionNumber,
                    ServiceName = serviceName,
                    CallSiteCount = pair.Value,
                };
            })
            .ToList();
    }

    /// <summary>
    /// Maps each basic-block start address onto the entry address of the discovered
    /// function containing it. A block reachable from several functions is attributed to
    /// the lowest entry address, so the mapping does not depend on enumeration order.
    /// </summary>
    private static IReadOnlyDictionary<uint, uint> BuildFunctionOwnership(FunctionDiscoveryArtifact? functions)
    {
        var ownership = new Dictionary<uint, uint>();
        if (functions is null)
        {
            return ownership;
        }

        foreach (var function in functions.Functions.OrderBy(static function => function.EntryAddress))
        {
            foreach (var block in function.BasicBlocks)
            {
                ownership.TryAdd(block.StartAddress, function.EntryAddress);
            }
        }

        return ownership;
    }

    /// <summary>
    /// Block-local known-constant GPR values. Register 0 is never tracked: it reads as
    /// zero architecturally, and treating a write to it as meaningful would be wrong.
    /// </summary>
    private sealed class ConstantRegisterState
    {
        private const int RegisterCount = 32;

        private readonly uint[] _values = new uint[RegisterCount];
        private readonly bool[] _known = new bool[RegisterCount];

        public bool TryGet(byte register, out uint value)
        {
            if (register < RegisterCount && _known[register])
            {
                value = _values[register];
                return true;
            }

            value = 0;
            return false;
        }

        public void Set(byte register, uint value)
        {
            if (register == 0 || register >= RegisterCount)
            {
                return;
            }

            _values[register] = value;
            _known[register] = true;
        }

        public void Invalidate(byte register)
        {
            if (register < RegisterCount)
            {
                _known[register] = false;
            }
        }

        public void InvalidateAll() => Array.Clear(_known);

        public void Clear() => InvalidateAll();

        public ConstantRegisterState Clone()
        {
            var clone = new ConstantRegisterState();
            Array.Copy(_values, clone._values, RegisterCount);
            Array.Copy(_known, clone._known, RegisterCount);
            return clone;
        }
    }
}
