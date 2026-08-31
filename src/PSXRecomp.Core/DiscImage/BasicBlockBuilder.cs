using PSXRecomp.Architecture;
using PSXRecomp.Core.Analysis.Contracts;
using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Builds basic blocks and resolves direct control-flow edges from a linear
/// sequence of decoded MIPS R3000A instructions.
/// </summary>
[Domain]
public static class BasicBlockBuilder
{
    /// <summary>
    /// Constructs basic blocks and CFG edges from the given decoded instructions.
    /// The builder re-decodes each instruction to access full CPU-level metadata
    /// (delay slot kind, operand detail, target resolution).
    /// </summary>
    public static (IReadOnlyList<BasicBlock> Blocks, IReadOnlyList<CfgEdge> Edges)
        Build(IReadOnlyList<DecodedInstruction> decodedInstructions, uint decodeStart, int instructionCount)
    {
        if (decodedInstructions.Count == 0)
        {
            return (Array.Empty<BasicBlock>(), Array.Empty<CfgEdge>());
        }

        var rawInstructions = new R3000aInstruction[decodedInstructions.Count];
        for (int i = 0; i < decodedInstructions.Count; i++)
        {
            rawInstructions[i] = R3000aDecoder.Decode(decodedInstructions[i].RawWord);
        }

        var leaders = new SortedSet<uint>();
        leaders.Add(decodeStart);

        for (int i = 0; i < decodedInstructions.Count; i++)
        {
            var raw = rawInstructions[i];
            var addr = decodedInstructions[i].Address;

            if (raw.DelaySlot == R3000aDelaySlotKind.None)
            {
                continue;
            }

            int afterDelaySlot = i + 2;
            if (afterDelaySlot < decodedInstructions.Count)
            {
                leaders.Add(decodedInstructions[afterDelaySlot].Address);
            }

            if (TryResolveTarget(raw, addr, out var target))
            {
                leaders.Add(target);
            }
        }

        var leaderList = leaders.ToList();
        var addressToIndex = new Dictionary<uint, int>(decodedInstructions.Count);
        for (int i = 0; i < decodedInstructions.Count; i++)
        {
            addressToIndex[decodedInstructions[i].Address] = i;
        }

        var blocks = new List<BasicBlock>();
        for (int l = 0; l < leaderList.Count; l++)
        {
            uint startAddr = leaderList[l];
            if (!addressToIndex.TryGetValue(startAddr, out int startIdx))
            {
                continue;
            }

            int endIdx;
            if (l + 1 < leaderList.Count && addressToIndex.TryGetValue(leaderList[l + 1], out int nextIdx))
            {
                endIdx = nextIdx - 1;
            }
            else
            {
                endIdx = decodedInstructions.Count - 1;
            }

            if (startIdx > endIdx)
            {
                continue;
            }

            blocks.Add(new BasicBlock
            {
                StartAddress = decodedInstructions[startIdx].Address,
                EndAddress = decodedInstructions[endIdx].Address,
                InstructionCount = endIdx - startIdx + 1,
            });
        }

        var edges = new List<CfgEdge>();
        foreach (var block in blocks)
        {
            if (!addressToIndex.TryGetValue(block.StartAddress, out var blockStartIdx))
            {
                continue;
            }

            int blockEndIdx = blockStartIdx + block.InstructionCount - 1;
            int cfIdx = -1;

            for (int i = blockStartIdx; i <= blockEndIdx; i++)
            {
                if (rawInstructions[i].DelaySlot != R3000aDelaySlotKind.None)
                {
                    cfIdx = i;
                    break;
                }
            }

            if (cfIdx >= 0)
            {
                var cfRaw = rawInstructions[cfIdx];
                var cfAddr = decodedInstructions[cfIdx].Address;

                if (TryResolveTarget(cfRaw, cfAddr, out var target))
                {
                    edges.Add(new CfgEdge(cfAddr, target,
                        cfRaw.DelaySlot == R3000aDelaySlotKind.Unconditional ? "jump" : "branch"));
                }
                else
                {
                    edges.Add(new CfgEdge(cfAddr, 0, "indirect"));
                }

                if (cfRaw.DelaySlot == R3000aDelaySlotKind.Conditional)
                {
                    int fallthroughIdx = cfIdx + 2;
                    if (fallthroughIdx < decodedInstructions.Count)
                    {
                        edges.Add(new CfgEdge(cfAddr, decodedInstructions[fallthroughIdx].Address, "fallthrough"));
                    }
                }
            }
            else if (blockEndIdx + 1 < decodedInstructions.Count)
            {
                edges.Add(new CfgEdge(decodedInstructions[blockEndIdx].Address,
                    decodedInstructions[blockEndIdx + 1].Address, "fallthrough"));
            }
        }

        return (blocks, edges);
    }

    private static bool TryResolveTarget(R3000aInstruction instruction, uint address, out uint target)
    {
        return R3000aBranchSemantics.TryGetBranchTarget(instruction, address, out target)
            || R3000aJumpSemantics.TryGetJumpTarget(instruction, address, out target);
    }
}
