using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Core.Recompiler;

/// <summary>
/// Discovers and lowers the statically reachable portion of a PS-X EXE text image.
/// Words in the image are decoded only after a guest PC reaches them from the
/// executable entry point or from a caller-supplied explicit root (Issue #644). This keeps text-region data and padding outside the
/// recompiler input while leaving unsupported reachable instructions fail-closed.
/// </summary>
[Domain]
public static class ReachableProgramBuilder
{
    private const uint InstructionSize = 4;

    /// <summary>
    /// Builds a deterministic IR program from a loaded PS-X EXE text image.
    /// </summary>
    /// <param name="loadAddress">Guest address of the first supplied word.</param>
    /// <param name="instructionWords">The complete declared text image, in guest memory order.</param>
    /// <param name="entryPc">The PS-X EXE header entry point.</param>
    /// <exception cref="InvalidOperationException">The entry point or a statically
    /// discovered in-image transfer is structurally invalid.</exception>
    public static RecompilerIrProgram Build(
        uint loadAddress,
        IReadOnlyList<uint> instructionWords,
        uint entryPc) =>
        Build(loadAddress, instructionWords, entryPc, additionalRoots: []);

    /// <summary>
    /// Builds a deterministic IR program from the executable entry point plus
    /// caller-supplied explicit roots, discovered in one shared pass (Issue #644).
    /// Every root feeds the same leader, delay-slot, load-delay and conflict state;
    /// roots are never built separately and merged. The roots are an explicit input,
    /// not a heuristic: nothing in this builder guesses a root from the image.
    /// Without additional roots the result is identical to the entry-only overload.
    /// </summary>
    /// <param name="loadAddress">Guest address of the first supplied word.</param>
    /// <param name="instructionWords">The complete declared text image, in guest memory order.</param>
    /// <param name="entryPc">The PS-X EXE header entry point.</param>
    /// <param name="additionalRoots">Extra block entry PCs. Each must be 4-byte aligned and
    /// name a complete word inside the image. Duplicates, the entry point, and PCs the
    /// entry-driven discovery already reaches are accepted.</param>
    /// <exception cref="InvalidOperationException">The entry point, an additional root, or a
    /// statically discovered in-image transfer is structurally invalid.</exception>
    public static RecompilerIrProgram Build(
        uint loadAddress,
        IReadOnlyList<uint> instructionWords,
        uint entryPc,
        IEnumerable<uint> additionalRoots) =>
        BuildCore(loadAddress, instructionWords, entryPc, additionalRoots, outOfImageTargets: null, out _);

    /// <summary>
    /// Builds a firmware ROM image (Issue #732) — for example the BIOS at <c>0xBFC00000</c> — with exactly the
    /// discovery and lowering of <see cref="Build(uint, IReadOnlyList{uint}, uint, IEnumerable{uint})"/>, and
    /// reports how much of the image is native.
    /// <para>
    /// Only code present in the image at analysis time is compiled. Code the firmware runs from anywhere else
    /// — kernel code it copies to RAM (OpenBIOS <c>.data</c> at <c>0x500</c>), the A0/B0/C0 trampolines it
    /// installs, a KSEG0 alias of the ROM (<c>0x9FC00000</c>), or any register-indirect target without a
    /// block — has no generated block. At run time such a PC reaches the artifact's host transfer and, with
    /// mixed execution enabled, the interpreter fallback (Issue #693), whose
    /// <c>MixedFallbackEvidence</c> counts it as fallback instructions; it is never counted as native.
    /// <see cref="FirmwareImageProgram.FallbackTargets"/> lists the statically known ones.
    /// </para>
    /// </summary>
    /// <param name="loadAddress">Guest address of the first word (the ROM's KSEG1 base).</param>
    /// <param name="instructionWords">The ROM bytes, as little-endian words, in guest order.</param>
    /// <param name="entryPc">The reset vector.</param>
    /// <param name="additionalRoots">Extra in-image entry PCs (for example exception vectors or known
    /// function entries). An explicit input, never guessed.</param>
    public static FirmwareImageProgram BuildFirmwareImage(
        uint loadAddress,
        IReadOnlyList<uint> instructionWords,
        uint entryPc,
        IEnumerable<uint> additionalRoots)
    {
        var outOfImage = new SortedSet<uint>();
        var program = BuildCore(loadAddress, instructionWords, entryPc, additionalRoots, outOfImage, out var native);
        return new FirmwareImageProgram(program, instructionWords.Count, native, outOfImage.ToArray());
    }

    /// <summary>
    /// Ahead-of-time build of a <em>loaded</em> code image (Issue #732): code that the guest itself places in RAM at run
    /// time — kernel code a firmware copies from its ROM, vector stubs, a shell, a PS-X EXE — given here as explicit
    /// input bytes at their <em>destination</em> address. Discovery and lowering are exactly
    /// <see cref="Build(uint, IReadOnlyList{uint}, uint, IEnumerable{uint})"/>'s, from the explicit
    /// <paramref name="roots"/> only, so branch, J/JAL and link targets are those of the destination; the same bytes
    /// meant for two destinations are two separate builds. Nothing is ever compiled at run time.
    /// <para>
    /// Because the guest decides at run time what is really in RAM, every block carries the words it was compiled from
    /// (<see cref="GuardedImageProgram.Guards"/>). The executing engine enters a block only while guest RAM holds exactly
    /// those words and otherwise runs that PC in its counted interpreter fallback, so stale native code never runs. A
    /// root whose code cannot be lowered (an unsupported instruction, an unformable fused unit) keeps no block.
    /// </para>
    /// </summary>
    /// <param name="loadAddress">The guest address the image is loaded at (where it executes).</param>
    /// <param name="words">The image, as little-endian words, in guest order.</param>
    /// <param name="roots">Explicit entry PCs inside the image (for example symbols, an EXE entry point).</param>
    /// <param name="excludedEntries">PCs that must never get a native block (observation points that stay interpreted).</param>
    public static GuardedImageProgram BuildLoadedImage(
        uint loadAddress, IReadOnlyList<uint> words, IEnumerable<uint> roots, IReadOnlySet<uint> excludedEntries)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(excludedEntries);

        var rootList = roots.Where(root => !excludedEntries.Contains(root)).Distinct().Order().ToList();
        if (rootList.Count == 0)
        {
            return new GuardedImageProgram([], []);
        }

        var program = BuildCore(loadAddress, words, rootList[0], rootList, outOfImageTargets: null, out _, out var skipped);
        var blocks = program.Blocks
            .Where(block => !excludedEntries.Contains(block.EntryPc))
            .Select(block =>
            {
                var offset = (int)((block.EntryPc - loadAddress) / InstructionSize);
                return new GuardedBlock(block, Enumerable.Range(offset, block.RetiredInstructionCount).Select(i => words[i]).ToArray());
            })
            .ToArray();
        return new GuardedImageProgram(blocks, skipped.Order().ToArray());
    }

    private static RecompilerIrProgram BuildCore(
        uint loadAddress,
        IReadOnlyList<uint> instructionWords,
        uint entryPc,
        IEnumerable<uint> additionalRoots,
        SortedSet<uint>? outOfImageTargets,
        out int nativeInstructionCount)
        => BuildCore(loadAddress, instructionWords, entryPc, additionalRoots, outOfImageTargets, out nativeInstructionCount, skippedLeaders: null);

    private static RecompilerIrProgram BuildCore(
        uint loadAddress,
        IReadOnlyList<uint> instructionWords,
        uint entryPc,
        IEnumerable<uint> additionalRoots,
        SortedSet<uint>? outOfImageTargets,
        out int nativeInstructionCount,
        out HashSet<uint> skippedLeaders)
    {
        skippedLeaders = [];
        return BuildCore(loadAddress, instructionWords, entryPc, additionalRoots, outOfImageTargets, out nativeInstructionCount, skippedLeaders);
    }

    private static RecompilerIrProgram BuildCore(
        uint loadAddress,
        IReadOnlyList<uint> instructionWords,
        uint entryPc,
        IEnumerable<uint> additionalRoots,
        SortedSet<uint>? outOfImageTargets,
        out int nativeInstructionCount,
        HashSet<uint>? skippedLeaders)
    {
        ArgumentNullException.ThrowIfNull(instructionWords);
        ArgumentNullException.ThrowIfNull(additionalRoots);

        if (instructionWords.Count == 0)
        {
            throw InvalidFlow("the text image contains no instructions.");
        }

        if ((loadAddress & (InstructionSize - 1)) != 0)
        {
            throw InvalidFlow($"text load address 0x{loadAddress:X8} is not 4-byte aligned.");
        }

        var imageBytes = (ulong)instructionWords.Count * InstructionSize;
        var imageEnd = (ulong)loadAddress + imageBytes;
        if (imageEnd > uint.MaxValue + 1UL)
        {
            throw InvalidFlow($"text image at 0x{loadAddress:X8} overflows the 32-bit address space.");
        }

        var endExclusive = imageEnd;
        if ((entryPc & (InstructionSize - 1)) != 0)
        {
            throw InvalidFlow($"entry point 0x{entryPc:X8} is not 4-byte aligned.");
        }

        var image = new InstructionImage(loadAddress, endExclusive, instructionWords);
        if (!image.Contains(entryPc))
        {
            throw InvalidFlow($"entry point 0x{entryPc:X8} is outside the supplied text image.");
        }

        var leaders = new SortedSet<uint> { entryPc };
        var pending = new SortedSet<uint> { entryPc };
        foreach (var root in additionalRoots)
        {
            if ((root & (InstructionSize - 1)) != 0)
            {
                throw InvalidFlow($"additional root 0x{root:X8} is not 4-byte aligned.");
            }

            if (!image.Contains(root))
            {
                throw InvalidFlow($"additional root 0x{root:X8} is outside the supplied text image or has no complete instruction word.");
            }

            leaders.Add(root);
            pending.Add(root);
        }

        var decoded = new Dictionary<uint, R3000aInstruction>();
        var reachable = new HashSet<uint>();
        var delaySlots = new HashSet<uint>();

        while (pending.Count > 0)
        {
            var start = pending.Min;
            pending.Remove(start);
            DiscoverPath(
                start,
                image,
                decoded,
                reachable,
                delaySlots,
                leaders,
                pending,
                outOfImageTargets);
        }

        foreach (var leader in leaders.ToArray())
        {
            if (delaySlots.Contains(leader) && skippedLeaders is not null)
            {
                // A loaded image (BuildLoadedImage): a root that is also a delay slot keeps no block of its own.
                leaders.Remove(leader);
                skippedLeaders.Add(leader);
            }
            else if (delaySlots.Contains(leader))
            {
                throw InvalidFlow($"PC 0x{leader:X8} is both a discovered block entry and a delay slot.");
            }
        }

        var unobservedDelaySlotLoads = FindUnobservedDelaySlotLoads(image, decoded, delaySlots);
        var blocks = new List<RecompilerIrBlock>();
        foreach (var leader in leaders)
        {
            if (!reachable.Contains(leader))
            {
                continue;
            }

            if (skippedLeaders is null)
            {
                var stream = BuildBlock(leader, image, decoded, reachable, leaders);
                blocks.AddRange(MipsToIrLowerer.LowerProgram(stream, unobservedDelaySlotLoads).Blocks);
                continue;
            }

            // Observed run-time code (BuildObservedCode): an unsupported leader keeps no block and stays interpreted.
            try
            {
                var stream = BuildBlock(leader, image, decoded, reachable, leaders);
                blocks.AddRange(MipsToIrLowerer.LowerProgram(stream, unobservedDelaySlotLoads).Blocks);
            }
            catch (InvalidOperationException)
            {
                skippedLeaders.Add(leader);
            }
        }

        nativeInstructionCount = reachable.Count;
        return new RecompilerIrProgram(blocks);
    }

    /// <summary>
    /// COP0 moves and RFE are decoded with <see cref="R3000aControlFlowKind.Coprocessor"/>, but none of them
    /// transfers control: discovery and block formation continue at <c>pc + 4</c> (Issue #732).
    /// </summary>
    private static bool FallsThrough(in R3000aInstruction instruction) =>
        instruction.ControlFlow == R3000aControlFlowKind.Sequential
        || instruction.Opcode is R3000aOpcode.Mfc0 or R3000aOpcode.Mtc0 or R3000aOpcode.Rfe;

    /// <summary>
    /// The control transfers whose delay slot holds a load (or MFC0) whose load-delay shadow provably falls on
    /// no reader (Issue #732): every successor is statically known, inside the image, and does not read the
    /// load's target (<see cref="MipsToIrLowerer.LoadShadowIsUnobserved"/>). A register-indirect transfer has
    /// no static successor and is never in the set, so its delay-slot load still fails closed.
    /// </summary>
    private static HashSet<uint> FindUnobservedDelaySlotLoads(
        InstructionImage image,
        Dictionary<uint, R3000aInstruction> decoded,
        HashSet<uint> delaySlots)
    {
        var proven = new HashSet<uint>();
        foreach (var delayPc in delaySlots)
        {
            var controlPc = delayPc - InstructionSize;
            if (!MipsToIrLowerer.TryGetLoadDelayTarget(decoded[delayPc], out var target))
            {
                continue;
            }

            var control = decoded[controlPc];
            var successors = new List<uint>();
            if (TryGetCheckedBranchTarget(control, controlPc, out var branchTarget))
            {
                successors.Add(branchTarget);
                successors.Add(delayPc + InstructionSize);
            }
            else if (R3000aJumpSemantics.TryGetJumpTarget(control, controlPc, out var jumpTarget))
            {
                successors.Add(jumpTarget);
            }
            else
            {
                continue;
            }

            if (successors.TrueForAll(successor => image.Contains(successor)
                    && MipsToIrLowerer.LoadShadowIsUnobserved(target, Decode(image, decoded, successor))))
            {
                proven.Add(controlPc);
            }
        }

        return proven;
    }

    private static void DiscoverPath(
        uint start,
        InstructionImage image,
        Dictionary<uint, R3000aInstruction> decoded,
        HashSet<uint> reachable,
        HashSet<uint> delaySlots,
        SortedSet<uint> leaders,
        SortedSet<uint> pending,
        SortedSet<uint>? outOfImageTargets)
    {
        var pc = start;
        while (true)
        {
            if (!image.Contains(pc))
            {
                return;
            }

            if (!reachable.Add(pc))
            {
                return;
            }

            var instruction = Decode(image, decoded, pc);
            if (instruction.DelaySlot != R3000aDelaySlotKind.None)
            {
                var delayPc = AddPc(pc, InstructionSize, "delay slot");
                RequireInImage(image, delayPc, "delay slot");
                delaySlots.Add(delayPc);
                reachable.Add(delayPc);
                _ = Decode(image, decoded, delayPc);

                if (TryGetCheckedBranchTarget(instruction, pc, out var branchTarget))
                {
                    QueueStaticTarget(branchTarget, image, leaders, pending, pc, "branch target", outOfImageTargets);
                    QueueContinuation(pc, image, leaders, pending);
                }
                else if (R3000aJumpSemantics.TryGetJumpTarget(instruction, pc, out var jumpTarget))
                {
                    QueueStaticTarget(jumpTarget, image, leaders, pending, pc, "jump target", outOfImageTargets);
                    if (instruction.LinkInfo.WritesLink)
                    {
                        QueueContinuation(pc, image, leaders, pending);
                    }
                }
                else if (instruction.LinkInfo.WritesLink && instruction.LinkInfo.LinkRegister != 0)
                {
                    // JALR with rd != 0: the dynamic target is unknown and must not
                    // be guessed, but the return site (pc + 8) is statically
                    // determined. Enqueue it for reachable-program discovery,
                    // consistent with the JAL handling above.
                    // JALR rd = 0 (architecturally equivalent to JR — GPR[0] is
                    // immutable) is intentionally excluded: no link is written and
                    // no continuation should be queued.
                    QueueContinuation(pc, image, leaders, pending);
                }

                // JR/JALR and any other unresolved transfer deliberately stop
                // discovery here. MipsToIrLowerer preserves their dynamic boundary.
                return;
            }

            if (instruction.Opcode == R3000aOpcode.Syscall)
            {
                // Issue #669: a SYSCALL outside a delay slot is a reachable-program
                // fall-through. Discovery only says the code after it may run; whether
                // the runtime actually completes the SYSCALL and resumes at PC + 4 is
                // the host syscall hook's decision (#663), so an unhandled SYS still
                // fails closed. A delay-slot SYSCALL is decoded by the branch case
                // above and never reaches this point, so it gets no fall-through.
                QueueStaticTarget(AddPc(pc, InstructionSize, "syscall successor"), image, leaders, pending, pc, "syscall successor", outOfImageTargets);
                return;
            }

            if (!FallsThrough(instruction))
            {
                return;
            }

            pc = AddPc(pc, InstructionSize, "sequential successor");
            if (!image.Contains(pc))
            {
                return;
            }
        }
    }

    private static IReadOnlyList<(R3000aInstruction Instruction, uint EntryPc)> BuildBlock(
        uint leader,
        InstructionImage image,
        Dictionary<uint, R3000aInstruction> decoded,
        HashSet<uint> reachable,
        SortedSet<uint> leaders)
    {
        var stream = new List<(R3000aInstruction Instruction, uint EntryPc)>();
        var pc = leader;
        while (reachable.Contains(pc))
        {
            if (stream.Count > 0 && leaders.Contains(pc))
            {
                break;
            }

            var instruction = Decode(image, decoded, pc);
            stream.Add((instruction, pc));

            if (instruction.DelaySlot != R3000aDelaySlotKind.None)
            {
                var delayPc = AddPc(pc, InstructionSize, "delay slot");
                RequireInImage(image, delayPc, "delay slot");
                if (!reachable.Contains(delayPc))
                {
                    throw InvalidFlow($"delay slot at PC 0x{delayPc:X8} was not discovered as reachable.");
                }

                stream.Add((Decode(image, decoded, delayPc), delayPc));
                break;
            }

            if (!FallsThrough(instruction))
            {
                break;
            }

            var nextPc = AddPc(pc, InstructionSize, "sequential successor");
            if (!image.Contains(nextPc))
            {
                break;
            }

            if (MipsToIrLowerer.TryGetLoadDelayTarget(instruction, out _) && leaders.Contains(nextPc))
            {
                var next = Decode(image, decoded, nextPc);
                if (MipsToIrLowerer.RequiresAdjacentLoadDelayPair(instruction, pc, next, nextPc))
                {
                    throw InvalidFlow($"the load at PC 0x{pc:X8} requires its delay-slot observer at 0x{nextPc:X8}, but that PC is also a block entry.");
                }
            }

            pc = nextPc;
        }

        return stream;
    }

    private static R3000aInstruction Decode(
        InstructionImage image,
        Dictionary<uint, R3000aInstruction> decoded,
        uint pc)
    {
        if (decoded.TryGetValue(pc, out var instruction))
        {
            return instruction;
        }

        instruction = R3000aDecoder.Decode(image.GetWord(pc));
        decoded.Add(pc, instruction);
        return instruction;
    }

    private static void QueueContinuation(
        uint controlPc,
        InstructionImage image,
        SortedSet<uint> leaders,
        SortedSet<uint> pending)
    {
        var continuation = AddPc(controlPc, InstructionSize * 2, "control-flow continuation");
        if (image.Contains(continuation))
        {
            leaders.Add(continuation);
            pending.Add(continuation);
        }
    }

    private static void QueueStaticTarget(
        uint target,
        InstructionImage image,
        SortedSet<uint> leaders,
        SortedSet<uint> pending,
        uint sourcePc,
        string kind,
        SortedSet<uint>? outOfImageTargets = null)
    {
        if ((target & (InstructionSize - 1)) != 0)
        {
            throw InvalidFlow($"{kind} 0x{target:X8} from PC 0x{sourcePc:X8} is not 4-byte aligned.");
        }

        // A statically known transfer outside the supplied image is an explicit
        // runtime/BIOS/overlay boundary. Only an address in the image is a
        // discovery candidate, and it must have a complete word available.
        if (!image.ContainsAddress(target))
        {
            outOfImageTargets?.Add(target);
            return;
        }

        RequireInImage(image, target, kind);
        leaders.Add(target);
        pending.Add(target);
    }

    private static bool TryGetCheckedBranchTarget(
        in R3000aInstruction instruction,
        uint pc,
        out uint target)
    {
        if (!R3000aBranchSemantics.TryGetBranchTarget(instruction, pc, out target))
        {
            return false;
        }

        var immediate = (short)(ushort)instruction.GetOperand(instruction.OperandCount - 1).Value;
        var candidate = (long)pc + InstructionSize + ((long)immediate * InstructionSize);
        if (candidate < 0 || candidate > uint.MaxValue)
        {
            throw InvalidFlow($"branch target from PC 0x{pc:X8} overflows the 32-bit address space.");
        }

        target = (uint)candidate;
        return true;
    }

    private static void RequireInImage(InstructionImage image, uint pc, string role)
    {
        if (!image.Contains(pc))
        {
            throw InvalidFlow($"{role} at PC 0x{pc:X8} does not map to a complete instruction in the text image.");
        }
    }

    private static uint AddPc(uint pc, uint amount, string role)
    {
        if ((ulong)pc + amount > uint.MaxValue)
        {
            throw InvalidFlow($"{role} from PC 0x{pc:X8} overflows the 32-bit address space.");
        }

        return pc + amount;
    }

    private static InvalidOperationException InvalidFlow(string detail) =>
        new($"Cannot discover reachable program: {detail} Diagnostic: [{RecompilerIrDiagnosticCode.InvalidFlow}] {detail}");

    private sealed class InstructionImage
    {
        private readonly IReadOnlyList<uint> _words;

        public InstructionImage(uint loadAddress, ulong endExclusive, IReadOnlyList<uint> words)
        {
            LoadAddress = loadAddress;
            EndExclusive = endExclusive;
            _words = words;
        }

        public uint LoadAddress { get; }
        public ulong EndExclusive { get; }

        public bool ContainsAddress(uint pc) => pc >= LoadAddress && (ulong)pc < EndExclusive;

        public bool Contains(uint pc) =>
            ContainsAddress(pc) && (ulong)(pc - LoadAddress) + InstructionSize <= (ulong)_words.Count * InstructionSize;

        public uint GetWord(uint pc)
        {
            if (!Contains(pc))
            {
                throw InvalidFlow($"PC 0x{pc:X8} does not map to a complete instruction in the text image.");
            }

            return _words[(int)((pc - LoadAddress) / InstructionSize)];
        }
    }
}

/// <summary>
/// A firmware ROM image built by <see cref="ReachableProgramBuilder.BuildFirmwareImage"/> (Issue #732).
/// </summary>
/// <param name="Program">The native program: one block per reachable in-image entry.</param>
/// <param name="ImageInstructionCount">Words in the supplied image (code and data alike).</param>
/// <param name="NativeInstructionCount">Distinct image PCs lowered into <paramref name="Program"/> (delay slots
/// included). The rest of the image is unreached data or code only reachable through an indirect transfer.</param>
/// <param name="FallbackTargets">Statically known transfer targets outside the image, ascending. They have no
/// block, so at run time they execute through the interpreter fallback and are reported as fallback.</param>
[Domain]
public sealed record FirmwareImageProgram(
    RecompilerIrProgram Program,
    int ImageInstructionCount,
    int NativeInstructionCount,
    IReadOnlyList<uint> FallbackTargets);
