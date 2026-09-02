using PSXRecomp.Core.Analysis.Contracts;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.DiscImageTests;

[Test]
public class FunctionDiscoveryTests
{
    private const uint Base = 0x80010000;

    [Fact]
    public void DiscoversEntryAndDirectJalTarget_WhileKeepingCallFallthrough()
    {
        // A: conditional branch + delay slot, JAL B + delay slot, return.
        // B: conditional branch + delay slot, return.  Bytes between A and B are
        // intentionally unreachable and must not become an inferred function.
        var words = new uint[]
        {
            0x00000000, EncodeBeq(0, 0, 2), 0x00000000, 0x24020001,
            EncodeJal(Base + 0x30), 0x00000000, 0x24020002, EncodeJr(31),
            0x00000000, EncodeJal(Base + 0x44), 0x00000000, 0x00000000,
            EncodeBeq(1, 2, 1), 0x00000000, 0x24020003, EncodeJr(31),
            0x00000000, 0x00000000, EncodeJr(31), 0x00000000,
        };
        var instructions = words.Select((word, index) => new DecodedInstruction
        {
            Address = Base + (uint)(index * 4), RawWord = word,
            Mnemonic = "synthetic", Operands = string.Empty, Format = "RType", ControlFlow = "synthetic",
        }).ToArray();

        var (blocks, edges) = BasicBlockBuilder.Build(instructions, Base, instructions.Length);
        var artifact = FunctionDiscovery.Build(Base, Base, (uint)(words.Length * 4), instructions, blocks, edges);

        artifact.Functions.Select(static f => f.EntryAddress).Should().Equal(Base, Base + 0x30);
        var entry = artifact.Functions[0];
        entry.BasicBlocks.Should().NotBeEmpty();
        entry.DirectCallTargets.Should().Equal(Base + 0x30);
        entry.ReturnAddresses.Should().Contain(Base + 0x1C);
        entry.Edges.Should().Contain(edge => edge.Kind == "jump" && edge.TargetAddress == Base + 0x30);
        entry.Edges.Should().Contain(edge => edge.Kind == "fallthrough" && edge.TargetAddress == Base + 0x18);
        entry.BasicBlocks.Should().Contain(block => block.EndAddress == Base + 0x14,
            "the JAL delay slot belongs to the caller block");
        entry.BasicBlocks.Should().NotContain(block => block.StartAddress == Base + 0x30);
        artifact.Functions.Should().NotContain(function => function.EntryAddress == Base + 0x44,
            "a JAL after the entry return is unreachable and must not seed a function");
    }

    [Fact]
    public void PreservesUnresolvedIndirectFlowAndDoesNotGuessItsTarget()
    {
        var instructions = new[]
        {
            Instruction(Base, EncodeJr(8)),
            Instruction(Base + 4, 0x00000000),
        };
        var (blocks, edges) = BasicBlockBuilder.Build(instructions, Base, instructions.Length);
        var artifact = FunctionDiscovery.Build(Base, Base, 8, instructions, blocks, edges);

        artifact.Functions.Should().ContainSingle();
        artifact.Functions[0].UnresolvedIndirectSources.Should().Equal(Base);
        artifact.Functions[0].DirectCallTargets.Should().BeEmpty();
        artifact.Functions[0].Edges.Should().Contain(edge => edge.Kind == "indirect" && edge.TargetAddress == 0);
    }

    [Fact]
    public void CanonicalArtifactIsStableAcrossRepeatedSerialization()
    {
        var instructions = new[] { Instruction(Base, 0), Instruction(Base + 4, EncodeJr(31)), Instruction(Base + 8, 0) };
        var (blocks, edges) = BasicBlockBuilder.Build(instructions, Base, instructions.Length);
        var first = FunctionDiscovery.Build(Base, Base, 12, instructions, blocks, edges);
        var second = FunctionDiscovery.Build(Base, Base, 12, instructions, blocks, edges);

        first.ToCanonicalJson().Should().Be(second.ToCanonicalJson());
        first.Sha256().Should().Be(second.Sha256());
    }

    [Theory]
    [InlineData(0x11)] // BGEZAL
    [InlineData(0x10)] // BLTZAL
    public void ConditionalLinkBranchHasOneTakenAndOneFallthroughEdge(int linkBranchRt)
    {
        var instructions = new[]
        {
            Instruction(Base, EncodeRegImm(1, linkBranchRt, 2)),
            Instruction(Base + 4, 0),
            Instruction(Base + 8, 0),
            Instruction(Base + 12, 0),
        };
        var (_, edges) = BasicBlockBuilder.Build(instructions, Base, instructions.Length);

        edges.Count(edge => edge.SourceAddress == Base && edge.Kind == "branch").Should().Be(1);
        edges.Count(edge => edge.SourceAddress == Base && edge.Kind == "fallthrough").Should().Be(1);
        edges.Should().OnlyHaveUniqueItems();
    }

    private static DecodedInstruction Instruction(uint address, uint rawWord) => new()
    {
        Address = address, RawWord = rawWord, Mnemonic = "synthetic", Operands = string.Empty,
        Format = "RType", ControlFlow = "synthetic",
    };

    private static uint EncodeBeq(int rs, int rt, short offset) =>
        (4u << 26) | ((uint)rs << 21) | ((uint)rt << 16) | (uint)(ushort)offset;

    private static uint EncodeJal(uint target) => (3u << 26) | ((target >> 2) & 0x03FFFFFF);

    private static uint EncodeJr(int rs) => ((uint)rs << 21) | 0x08;

    private static uint EncodeRegImm(int rs, int rt, short offset) =>
        (1u << 26) | ((uint)rs << 21) | ((uint)rt << 16) | (uint)(ushort)offset;
}
