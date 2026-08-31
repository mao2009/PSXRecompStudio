using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// A contiguous sequence of instructions with no incoming control-flow edges
/// except at the first instruction (the entry point of the block).
/// </summary>
[Domain]
public sealed record BasicBlock
{
    public required uint StartAddress { get; init; }
    public required uint EndAddress { get; init; }
    public required int InstructionCount { get; init; }

    public string ToTokenString()
    {
        return $"startAddress=0x{StartAddress:X8};endAddress=0x{EndAddress:X8};instructionCount={InstructionCount}";
    }
}
