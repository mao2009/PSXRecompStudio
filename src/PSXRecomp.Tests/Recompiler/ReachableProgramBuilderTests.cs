using FluentAssertions;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

[Test]
public sealed class ReachableProgramBuilderTests
{
    private const uint LoadAddress = 0x80010000u;

    [Fact]
    public void DataBeforeEntry_IsNeverDecodedOrLowered()
    {
        var program = BuildAt(LoadAddress + 8,
            Word.Cop1Unusable,
            Word.Cop1Unusable,
            Word.Addiu(8, 0, 1));

        program.Blocks.Select(block => block.EntryPc).Should().Equal(LoadAddress + 8);
    }

    [Fact]
    public void UnreachableUnsupportedWord_DoesNotPreventCompilation()
    {
        var program = Build(
            Word.Jump(LoadAddress + 0x1000),
            Word.Nop,
            Word.Cop1Unusable,
            Word.Cop1Unusable);

        program.Blocks.Should().ContainSingle();
        var exit = program.Blocks[0].Exit.Flow!;
        exit.Kind.Should().Be(RecompilerIrFlowKind.Jump);
        exit.Target.Should().Be(LoadAddress + 0x1000);
    }

    [Fact]
    public void ReachableUnsupportedInstruction_FailsClosed()
    {
        var lower = () => Build(Word.Cop1Unusable);

        lower.Should().Throw<InvalidOperationException>()
            .WithMessage("*Cop1Unusable*");
    }

    [Fact]
    public void DirectJump_DiscoversTargetAndDelaySlot()
    {
        var program = Build(
            Word.Jump(LoadAddress + 12),
            Word.Addiu(8, 0, 1),
            Word.Cop1Unusable,
            Word.Addiu(9, 0, 2));

        program.Blocks.Select(block => block.EntryPc)
            .Should().Equal(LoadAddress, LoadAddress + 12);
        program.Blocks[0].Operations.Should().Contain(operation =>
            operation.Kind == RecompilerIrOperationKind.WriteGpr && operation.Register == 8);
    }

    [Fact]
    public void ConditionalBranch_DiscoversTargetFallthroughAndDelaySlot()
    {
        var program = Build(
            Word.Branch(0x04, rs: 8, rt: 0, pc: LoadAddress, target: LoadAddress + 12),
            Word.Addiu(9, 0, 1),
            Word.Addiu(10, 0, 2),
            Word.Addiu(11, 0, 3));

        program.Blocks.Select(block => block.EntryPc)
            .Should().Equal(LoadAddress, LoadAddress + 8, LoadAddress + 12);
        var exit = program.Blocks[0].Exit.Flow!;
        exit.Kind.Should().Be(RecompilerIrFlowKind.Branch);
        exit.Target.Should().Be(LoadAddress + 12);
        program.Blocks[0].Exit.NextPc.Should().Be(LoadAddress + 8);
        program.Blocks[0].Operations.Should().Contain(operation =>
            operation.Kind == RecompilerIrOperationKind.WriteGpr && operation.Register == 9);
    }

    [Fact]
    public void Jal_DiscoversCallTargetAndReturnContinuation()
    {
        var program = Build(
            Word.JumpAndLink(LoadAddress + 20),
            Word.Addiu(8, 0, 1),
            Word.Addiu(9, 0, 2),
            Word.Jump(LoadAddress + 0x1000),
            Word.Nop,
            Word.Addiu(10, 0, 3));

        program.Blocks.Select(block => block.EntryPc)
            .Should().Equal(LoadAddress, LoadAddress + 8, LoadAddress + 12, LoadAddress + 20);
        var exit = program.Blocks[0].Exit.Flow!;
        exit.Kind.Should().Be(RecompilerIrFlowKind.Call);
        exit.Target.Should().Be(LoadAddress + 20);
        program.Blocks[0].Exit.NextPc.Should().Be(LoadAddress + 8);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndirectTransfers_PreserveDynamicBoundary(bool link)
    {
        var program = Build(
            link ? Word.JumpAndLinkRegister(8, 9) : Word.JumpRegister(8),
            Word.Nop);

        program.Blocks.Should().ContainSingle();
        program.Blocks[0].Exit.Reason.Should().Be(RecompilerIrTerminationReason.UnresolvedIndirectFlow);
        program.Blocks[0].Exit.Flow.Should().BeNull();
    }

    [Fact]
    public void UnsupportedDelaySlot_FailsClosed()
    {
        var lower = () => Build(Word.Jump(0x90000000u), Word.Cop1Unusable);

        lower.Should().Throw<InvalidOperationException>()
            .WithMessage("*Cop1Unusable*");
    }

    [Fact]
    public void InvalidEntryPoint_FailsClosed()
    {
        var lower = () => ReachableProgramBuilder.Build(LoadAddress, [Word.Nop], LoadAddress + 2);

        lower.Should().Throw<InvalidOperationException>()
            .WithMessage("*not 4-byte aligned*");
    }

    [Fact]
    public void LoadDelayPair_RemainsFusedAcrossReachableLowering()
    {
        var program = Build(
            Word.LoadWord(9, 8, 0),
            Word.R(0x21, rd: 10, rs: 9, rt: 0));

        program.Blocks.Should().ContainSingle();
        program.Blocks[0].EntryPc.Should().Be(LoadAddress);
        program.Blocks[0].Operations.Count(operation =>
            operation.Kind == RecompilerIrOperationKind.WriteGpr && operation.Register == 9)
            .Should().Be(1);
    }

    [Fact]
    public void DeterministicOrderingAndSerialization_AreStable()
    {
        var words = new[]
        {
            Word.Branch(0x04, rs: 8, rt: 0, pc: LoadAddress, target: LoadAddress + 16),
            Word.Nop,
            Word.Jump(LoadAddress + 20),
            Word.Nop,
            Word.Addiu(9, 0, 1),
            Word.Addiu(10, 0, 2),
        };

        var first = Build(words);
        var second = Build(words);

        first.Blocks.Select(block => block.EntryPc).Should()
            .Equal(second.Blocks.Select(block => block.EntryPc));
        RecompilerIrSerializer.Serialize(first).Should().Be(RecompilerIrSerializer.Serialize(second));
    }

    [Fact]
    public void EntryAtTextStart_WithReachableText_RemainsCompatible()
    {
        var program = Build(
            Word.Addiu(8, 0, 1),
            Word.Addiu(9, 0, 2),
            Word.R(0x21, rd: 10, rs: 8, rt: 9));

        program.Blocks.Select(block => block.EntryPc)
            .Should().Equal(LoadAddress, LoadAddress + 4, LoadAddress + 8);
    }

    private static RecompilerIrProgram Build(params uint[] words) =>
        ReachableProgramBuilder.Build(LoadAddress, words, LoadAddress);

    private static RecompilerIrProgram BuildAt(uint entryPc, params uint[] words) =>
        ReachableProgramBuilder.Build(LoadAddress, words, entryPc);

    private static class Word
    {
        public const uint Nop = 0;
        public const uint Cop1Unusable = 0x44000000u;

        public static uint Addiu(byte rt, byte rs, short immediate) =>
            I(0x09, rs, rt, unchecked((ushort)immediate));

        public static uint LoadWord(byte rt, byte baseRegister, short offset) =>
            I(0x23, baseRegister, rt, unchecked((ushort)offset));

        public static uint R(byte function, byte rd, byte rs, byte rt, byte shamt = 0) =>
            (uint)rs << 21 | (uint)rt << 16 | (uint)rd << 11 | (uint)shamt << 6 | function;

        public static uint Jump(uint target) =>
            0x08000000u | ((target & 0x0FFFFFFFu) >> 2);

        public static uint JumpAndLink(uint target) =>
            0x0C000000u | ((target & 0x0FFFFFFFu) >> 2);

        public static uint JumpRegister(byte rs) => R(0x08, 0, rs, 0);

        public static uint JumpAndLinkRegister(byte rd, byte rs) => R(0x09, rd, rs, 0);

        public static uint Branch(byte opcode, byte rs, byte rt, uint pc, uint target)
        {
            var offset = checked((int)(target - (pc + 4)) / 4);
            return I(opcode, rs, rt, unchecked((ushort)(short)offset));
        }

        private static uint I(byte opcode, byte rs, byte rt, uint immediate) =>
            (uint)opcode << 26 | (uint)rs << 21 | (uint)rt << 16 | (immediate & 0xFFFFu);
    }
}
