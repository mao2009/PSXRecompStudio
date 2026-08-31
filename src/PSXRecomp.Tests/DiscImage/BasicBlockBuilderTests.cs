using PSXRecomp.Core.Analysis.Contracts;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.DiscImageTests;

/// <summary>
/// Unit tests for <see cref="BasicBlockBuilder"/> covering block construction,
/// edge resolution, and delay-slot handling for MIPS R3000A instructions.
/// </summary>
[Test]
public class BasicBlockBuilderTests
{
    private const uint BaseAddress = 0x80010000;

    [Fact]
    public void Build_EmptyList_ReturnsNoBlocksNoEdges()
    {
        var (blocks, edges) = BasicBlockBuilder.Build([], BaseAddress, 0);

        blocks.Should().BeEmpty();
        edges.Should().BeEmpty();
    }

    [Fact]
    public void Build_AllSequential_OneBlock()
    {
        var instructions = new[]
        {
            MakeDecoded(0, 0x00000000),
            MakeDecoded(1, 0x00000000),
            MakeDecoded(2, 0x00000000),
        };

        var (blocks, edges) = BasicBlockBuilder.Build(instructions, BaseAddress, 3);

        blocks.Should().HaveCount(1);
        blocks[0].StartAddress.Should().Be(BaseAddress);
        blocks[0].EndAddress.Should().Be(BaseAddress + 8);
        blocks[0].InstructionCount.Should().Be(3);

        edges.Should().HaveCount(0);
    }

    [Fact]
    public void Build_Jump_SplitsIntoTwoBlocks()
    {
        // nop, j target, nop (delay slot), nop
        var instructions = new[]
        {
            MakeDecoded(0, 0x00000000),
            MakeDecoded(1, EncodeJ(0x80010020)),
            MakeDecoded(2, 0x00000000),
            MakeDecoded(3, 0x00000000),
        };

        var (blocks, edges) = BasicBlockBuilder.Build(instructions, BaseAddress, 4);

        blocks.Should().HaveCount(2);
        blocks[0].InstructionCount.Should().Be(3);
        blocks[1].InstructionCount.Should().Be(1);

        edges.Should().Contain(e => e.Kind == "jump" && e.TargetAddress == 0x80010020);
    }

    [Fact]
    public void Build_Beconditional_SplitsIntoTwoBlocksWithBranchAndFallthrough()
    {
        // nop, beq $1,$2,+8 (target=Base+12), nop (delay), nop
        var instructions = new[]
        {
            MakeDecoded(0, 0x00000000),
            MakeDecoded(1, EncodeBeq(1, 2, 2)),
            MakeDecoded(2, 0x00000000),
            MakeDecoded(3, 0x00000000),
        };

        var (blocks, edges) = BasicBlockBuilder.Build(instructions, BaseAddress, 4);

        blocks.Count.Should().BeGreaterThanOrEqualTo(2);

        edges.Should().Contain(e => e.Kind == "branch");
        edges.Should().Contain(e => e.Kind == "fallthrough");
    }

    [Fact]
    public void Build_BranchTarget_WithinWindow_BecomesLeader()
    {
        // nop, beq $1,$2,+8 (target=Base+12), nop (delay), nop
        // Target is at instruction index 3 (BaseAddress + 12)
        var instructions = new[]
        {
            MakeDecoded(0, 0x00000000),
            MakeDecoded(1, EncodeBeq(1, 2, 2)),
            MakeDecoded(2, 0x00000000),
            MakeDecoded(3, 0x00000000),
        };

        var (blocks, edges) = BasicBlockBuilder.Build(instructions, BaseAddress, 4);

        blocks.Should().Contain(b => b.StartAddress == BaseAddress + 12);
    }

    [Fact]
    public void Build_BranchTarget_OutsideWindow_NoEdgeToTarget()
    {
        // 2 instructions: nop, beq with target outside window
        var instructions = new[]
        {
            MakeDecoded(0, 0x00000000),
            MakeDecoded(1, EncodeBeq(1, 2, 100)),
        };

        var (blocks, edges) = BasicBlockBuilder.Build(instructions, BaseAddress, 2);

        edges.Should().Contain(e => e.Kind == "branch");
    }

    [Fact]
    public void Build_Jal_IsJumpWithCallCandidate()
    {
        // nop, jal target, nop (delay), nop
        var instructions = new[]
        {
            MakeDecoded(0, 0x00000000),
            MakeDecoded(1, EncodeJal(0x80010040)),
            MakeDecoded(2, 0x00000000),
            MakeDecoded(3, 0x00000000),
        };

        var (blocks, edges) = BasicBlockBuilder.Build(instructions, BaseAddress, 4);

        edges.Should().Contain(e => e.Kind == "jump" && e.TargetAddress == 0x80010040);
    }

    [Fact]
    public void Build_Jr_IndirectEdge()
    {
        // nop, jr $ra, nop (delay)
        var instructions = new[]
        {
            MakeDecoded(0, 0x00000000),
            MakeDecoded(1, EncodeJr(31)),
            MakeDecoded(2, 0x00000000),
        };

        var (blocks, edges) = BasicBlockBuilder.Build(instructions, BaseAddress, 3);

        edges.Should().Contain(e => e.Kind == "indirect" && e.SourceAddress == BaseAddress + 4);
    }

    [Fact]
    public void Build_SequentialBlock_FallthroughEdgeToNext()
    {
        // 4 nops = one block, no edges
        var instructions = new[]
        {
            MakeDecoded(0, 0x00000000),
            MakeDecoded(1, 0x00000000),
            MakeDecoded(2, 0x00000000),
            MakeDecoded(3, 0x00000000),
        };

        var (blocks, edges) = BasicBlockBuilder.Build(instructions, BaseAddress, 4);

        blocks.Should().HaveCount(1);
        edges.Should().BeEmpty();
    }

    [Fact]
    public void Build_DelaySlot_IsPartOfBranchBlock()
    {
        // j target, nop (delay), nop (after delay slot)
        var instructions = new[]
        {
            MakeDecoded(0, EncodeJ(0x80010020)),
            MakeDecoded(1, 0x00000000),
            MakeDecoded(2, 0x00000000),
        };

        var (blocks, edges) = BasicBlockBuilder.Build(instructions, BaseAddress, 3);

        blocks.Should().HaveCount(2);
        blocks[0].InstructionCount.Should().Be(2);
        blocks[0].EndAddress.Should().Be(BaseAddress + 4);
    }

    private static DecodedInstruction MakeDecoded(int index, uint rawWord)
    {
        return new DecodedInstruction
        {
            Address = BaseAddress + (uint)(index * 4),
            RawWord = rawWord,
            Mnemonic = "",
            Operands = "",
            Format = "",
            ControlFlow = "",
        };
    }

    private static uint EncodeJ(uint target)
    {
        uint index = (target >> 2) & 0x03FFFFFF;
        return (2u << 26) | index;
    }

    private static uint EncodeJal(uint target)
    {
        uint index = (target >> 2) & 0x03FFFFFF;
        return (3u << 26) | index;
    }

    private static uint EncodeBeq(int rs, int rt, short offset)
    {
        return (4u << 26) | ((uint)rs << 21) | ((uint)rt << 16) | (uint)(ushort)offset;
    }

    private static uint EncodeJr(int rs)
    {
        return ((uint)rs << 21) | 0x08;
    }
}
