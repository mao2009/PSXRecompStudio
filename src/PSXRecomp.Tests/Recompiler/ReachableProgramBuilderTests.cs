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

        // Discovery still stops at the register-indirect transfer and guesses no
        // static target; the exit carries the runtime target value (Issue #635).
        program.Blocks.Should().ContainSingle();
        program.Blocks[0].Exit.Reason.Should().Be(RecompilerIrTerminationReason.Success);
        program.Blocks[0].Exit.TargetValueId.Should().NotBeNull();
        program.Blocks[0].Exit.NextPc.Should().BeNull();
        program.Blocks[0].Exit.Flow.Should().BeNull();
    }

    [Fact]
    public void Jalr_DiscoversReturnSiteAsReachableBlock()
    {
        // JALR at LoadAddress; delay slot NOP at LoadAddress+4;
        // return continuation at LoadAddress+8 must be discovered (Issue #638).
        var program = Build(
            Word.JumpAndLinkRegister(31, 8),
            Word.Nop,
            Word.Addiu(10, 0, 3));

        program.Blocks.Select(block => block.EntryPc)
            .Should().Contain(LoadAddress + 8);
    }

    [Fact]
    public void Jalr_DoesNotGuessCallTarget()
    {
        // The dynamic target of JALR must not be added as a static discovery.
        // Only LoadAddress (the JALR block) and LoadAddress+8 (the return site)
        // may appear in the reachable set (Issue #638).
        var program = Build(
            Word.JumpAndLinkRegister(31, 8),
            Word.Nop,
            Word.Addiu(10, 0, 3));

        program.Blocks.Select(block => block.EntryPc)
            .Should().Equal(LoadAddress, LoadAddress + 8);
    }

    [Fact]
    public void Jr_DoesNotDiscoverContinuation()
    {
        // JR (no link) must not enqueue pc+8; only the JR block is reachable.
        var program = Build(
            Word.JumpRegister(8),
            Word.Nop,
            Word.Addiu(10, 0, 3));

        program.Blocks.Select(block => block.EntryPc)
            .Should().Equal(LoadAddress);
    }

    [Fact]
    public void Jalr_IntoZeroRegister_DoesNotDiscoverContinuation()
    {
        // JALR rd=0 is architecturally JR: GPR[0] is immutable, no link is
        // written, and the return site must not be enqueued (Issue #638 edge case).
        var program = Build(
            Word.JumpAndLinkRegister(0, 8),
            Word.Nop,
            Word.Addiu(10, 0, 3));

        program.Blocks.Select(block => block.EntryPc)
            .Should().Equal(LoadAddress);
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

    [Fact]
    public void ImageEndingAtAddressSpaceLimit_PreservesLastWordAsInImage()
    {
        const uint lastWordAddress = 0xFFFFFFFCu;

        var lower = () => ReachableProgramBuilder.Build(
            lastWordAddress,
            [Word.Cop1Unusable],
            lastWordAddress);

        lower.Should().Throw<InvalidOperationException>()
            .WithMessage("*Cop1Unusable*")
            .And.Message.Should().NotContain("outside the supplied text image");
    }

    // ---- Issue #644: explicit additional roots, discovered in one shared pass ----

    private static readonly uint[] BranchingImage =
    [
        Word.Branch(0x04, rs: 8, rt: 0, pc: LoadAddress, target: LoadAddress + 16),
        Word.Nop,
        Word.Jump(LoadAddress + 20),
        Word.Nop,
        Word.Addiu(9, 0, 1),
        Word.Addiu(10, 0, 2),
    ];

    /// <summary>Entry is a register jump; the words after it are reachable only through a root.</summary>
    private static readonly uint[] IndirectOnlyImage =
    [
        Word.JumpRegister(8),
        Word.Nop,
        Word.Addiu(9, 0, 5),
        Word.Addiu(10, 0, 6),
    ];

    [Fact]
    public void AdditionalRoots_Empty_MatchesEntryOnlyBuildExactly()
    {
        var entryOnly = ReachableProgramBuilder.Build(LoadAddress, BranchingImage, LoadAddress);
        var withEmpty = ReachableProgramBuilder.Build(LoadAddress, BranchingImage, LoadAddress, []);

        withEmpty.Blocks.Select(block => block.EntryPc).Should().Equal(entryOnly.Blocks.Select(block => block.EntryPc));
        RecompilerIrSerializer.Serialize(withEmpty).Should().Be(RecompilerIrSerializer.Serialize(entryOnly));
    }

    [Fact]
    public void IndirectOnlyTarget_IsCompiledOnlyWhenSuppliedAsRoot()
    {
        var without = ReachableProgramBuilder.Build(LoadAddress, IndirectOnlyImage, LoadAddress);
        var with = ReachableProgramBuilder.Build(LoadAddress, IndirectOnlyImage, LoadAddress, [LoadAddress + 8]);

        without.Blocks.Select(block => block.EntryPc).Should().Equal(LoadAddress);
        with.Blocks.Select(block => block.EntryPc)
            .Should().Equal(LoadAddress, LoadAddress + 8, LoadAddress + 12);
    }

    [Fact]
    public void DuplicateEntryAndAlreadyReachableRoots_AreAcceptedWithoutChangingTheProgram()
    {
        var entryOnly = ReachableProgramBuilder.Build(LoadAddress, BranchingImage, LoadAddress);

        var program = ReachableProgramBuilder.Build(
            LoadAddress,
            BranchingImage,
            LoadAddress,
            [LoadAddress, LoadAddress + 16, LoadAddress + 20, LoadAddress + 16]);

        program.Blocks.Select(block => block.EntryPc).Should().OnlyHaveUniqueItems();
        RecompilerIrSerializer.Serialize(program).Should().Be(RecompilerIrSerializer.Serialize(entryOnly));
    }

    [Fact]
    public void RootOrderAndDuplicates_DoNotAffectTheProgram()
    {
        var ascending = ReachableProgramBuilder.Build(
            LoadAddress, IndirectOnlyImage, LoadAddress, [LoadAddress + 8, LoadAddress + 12]);
        var shuffled = ReachableProgramBuilder.Build(
            LoadAddress, IndirectOnlyImage, LoadAddress, [LoadAddress + 12, LoadAddress + 8, LoadAddress + 12]);

        RecompilerIrSerializer.Serialize(shuffled).Should().Be(RecompilerIrSerializer.Serialize(ascending));
    }

    [Fact]
    public void LastWordOfImage_IsAValidRoot()
    {
        var program = ReachableProgramBuilder.Build(
            LoadAddress, IndirectOnlyImage, LoadAddress, [LoadAddress + 12]);

        program.Blocks.Select(block => block.EntryPc).Should().Equal(LoadAddress, LoadAddress + 12);
    }

    [Theory]
    [InlineData(0x80010002u, "not 4-byte aligned")]
    [InlineData(0x80010004u + 0x1000u, "outside the supplied text image")]
    [InlineData(0x8000FFFCu, "outside the supplied text image")]
    [InlineData(0x80010010u, "outside the supplied text image")] // one word past the last (4-word image)
    public void InvalidAdditionalRoot_FailsClosedNamingTheRoot(uint root, string reason)
    {
        var build = () => ReachableProgramBuilder.Build(LoadAddress, IndirectOnlyImage, LoadAddress, [root]);

        build.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain($"0x{root:X8}").And.Contain(reason).And.Contain("additional root");
    }

    [Fact]
    public void RootInsideAnotherPathsDelaySlot_FailsClosedLikeAnyLeaderDelaySlotConflict()
    {
        var build = () => ReachableProgramBuilder.Build(
            LoadAddress, IndirectOnlyImage, LoadAddress, [LoadAddress + 4]);

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*0x80010004*both a discovered block entry and a delay slot*");
    }

    [Fact]
    public void RootBetweenALoadAndItsDelayObserver_FailsClosed()
    {
        var words = new[]
        {
            Word.JumpRegister(8),
            Word.Nop,
            Word.LoadWord(9, 8, 0),
            Word.R(0x21, rd: 10, rs: 9, rt: 0),
        };

        var build = () => ReachableProgramBuilder.Build(
            LoadAddress, words, LoadAddress, [LoadAddress + 8, LoadAddress + 12]);

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*load at PC 0x80010008*0x8001000C*");
    }

    [Fact]
    public void AdditionalRoots_ShareOneDiscoveryPassWithTheEntry()
    {
        // The root at +8 falls through into +12, which the entry-driven path also reaches by a
        // direct jump. One shared pass discovers +12 once, as a leader of both.
        var words = new[]
        {
            Word.Jump(LoadAddress + 12),
            Word.Nop,
            Word.Addiu(9, 0, 1),
            Word.Addiu(10, 0, 2),
        };

        var program = ReachableProgramBuilder.Build(LoadAddress, words, LoadAddress, [LoadAddress + 8]);

        program.Blocks.Select(block => block.EntryPc)
            .Should().Equal(LoadAddress, LoadAddress + 8, LoadAddress + 12);
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
