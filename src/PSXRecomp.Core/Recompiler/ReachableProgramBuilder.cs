using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Core.Recompiler;

/// <summary>
/// Discovers and lowers the statically reachable portion of a PS-X EXE text image.
/// Words in the image are decoded only after a guest PC reaches them from the
/// executable entry point. This keeps text-region data and padding outside the
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
        uint entryPc)
    {
        ArgumentNullException.ThrowIfNull(instructionWords);

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

        var endExclusive = (uint)imageEnd;
        if ((entryPc & (InstructionSize - 1)) != 0)
        {
            throw InvalidFlow($"entry point 0x{entryPc:X8} is not 4-byte aligned.");
        }

        var image = new InstructionImage(loadAddress, endExclusive, instructionWords);
        if (!image.Contains(entryPc))
        {
            throw InvalidFlow($"entry point 0x{entryPc:X8} is outside the supplied text image.");
        }

        var decoded = new Dictionary<uint, R3000aInstruction>();
        var reachable = new HashSet<uint>();
        var delaySlots = new HashSet<uint>();
        var leaders = new SortedSet<uint> { entryPc };
        var pending = new SortedSet<uint> { entryPc };

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
                pending);
        }

        foreach (var leader in leaders)
        {
            if (delaySlots.Contains(leader))
            {
                throw InvalidFlow($"PC 0x{leader:X8} is both a discovered block entry and a delay slot.");
            }
        }

        var blocks = new List<RecompilerIrBlock>();
        foreach (var leader in leaders)
        {
            if (!reachable.Contains(leader))
            {
                continue;
            }

            var stream = BuildBlock(leader, image, decoded, reachable, leaders);
            var program = MipsToIrLowerer.LowerProgram(stream);
            blocks.AddRange(program.Blocks);
        }

        return new RecompilerIrProgram(blocks);
    }

    private static void DiscoverPath(
        uint start,
        InstructionImage image,
        Dictionary<uint, R3000aInstruction> decoded,
        HashSet<uint> reachable,
        HashSet<uint> delaySlots,
        SortedSet<uint> leaders,
        SortedSet<uint> pending)
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
                    QueueStaticTarget(branchTarget, image, leaders, pending, pc, "branch target");
                    QueueContinuation(pc, image, leaders, pending);
                }
                else if (R3000aJumpSemantics.TryGetJumpTarget(instruction, pc, out var jumpTarget))
                {
                    QueueStaticTarget(jumpTarget, image, leaders, pending, pc, "jump target");
                    if (instruction.LinkInfo.WritesLink)
                    {
                        QueueContinuation(pc, image, leaders, pending);
                    }
                }

                // JR/JALR and any other unresolved transfer deliberately stop
                // discovery here. MipsToIrLowerer preserves their dynamic boundary.
                return;
            }

            if (instruction.ControlFlow != R3000aControlFlowKind.Sequential)
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

            if (instruction.ControlFlow != R3000aControlFlowKind.Sequential)
            {
                break;
            }

            var nextPc = AddPc(pc, InstructionSize, "sequential successor");
            if (!image.Contains(nextPc))
            {
                break;
            }

            if (instruction.LoadDelayInfo.ProducesLoadDelay && leaders.Contains(nextPc))
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
        string kind)
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

        public InstructionImage(uint loadAddress, uint endExclusive, IReadOnlyList<uint> words)
        {
            LoadAddress = loadAddress;
            EndExclusive = endExclusive;
            _words = words;
        }

        public uint LoadAddress { get; }
        public uint EndExclusive { get; }

        public bool ContainsAddress(uint pc) => pc >= LoadAddress && pc < EndExclusive;

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
