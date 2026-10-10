using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

[Test]
// Issue #732: ahead-of-time build of a code image the guest loads into RAM, at its destination address, from explicit
// roots only; each block carries the words it was compiled from, and what cannot be lowered keeps no block.
public sealed class LoadedImageBuildTests
{
    private const uint Dest = 0x80010000u;
    private const byte T0 = 8, Ra = 31;
    private const uint Reserved = 0xFC000000u; // primary opcode 0x3F: not an R3000A instruction
    private static readonly IReadOnlySet<uint> None = new HashSet<uint>();

    [Fact]
    public void OnlyCodeReachableFromTheRoots_IsCompiled_AndEachBlockCarriesItsWords()
    {
        var ori = MipsEncoding.I(0x0D, T0, 0, 1);
        var jr = MipsEncoding.JumpRegister(Ra);
        // Two instructions, a return, its delay slot, then data that must never be decoded.
        var build = ReachableProgramBuilder.BuildLoadedImage(Dest, [ori, ori, jr, MipsEncoding.Nop, Reserved], [Dest], None);

        build.Blocks.Select(static b => b.Block.EntryPc).Should().Equal(Dest, Dest + 4, Dest + 8);
        build.Blocks.Single(static b => b.Block.EntryPc == Dest + 8).Words.Should().Equal(jr, MipsEncoding.Nop);
        build.NativeInstructionCount.Should().Be(4);
        build.SkippedEntries.Should().BeEmpty();
    }

    [Fact]
    public void TheSameBytes_AtTwoDestinations_AreTwoBuilds_WithDestinationRelativeTargets()
    {
        // A PC-relative branch to the word after its delay slot: its target moves with the code.
        var words = new[] { MipsEncoding.Branch(0x04, 0, 0, 0, 12), MipsEncoding.Nop, MipsEncoding.Nop, MipsEncoding.JumpRegister(Ra), MipsEncoding.Nop };

        var low = ReachableProgramBuilder.BuildLoadedImage(0x00000500u, words, [0x00000500u], None);
        var high = ReachableProgramBuilder.BuildLoadedImage(Dest, words, [Dest], None);

        low.Blocks[0].Block.Exit.Flow!.Target.Should().Be(0x0000050Cu);
        high.Blocks[0].Block.Exit.Flow!.Target.Should().Be(Dest + 12);
        low.Blocks[0].Words.Should().Equal(high.Blocks[0].Words);
    }

    [Fact]
    public void MutableDelaySlotLoad_DoesNotUseAnImmutableSuccessorProof()
    {
        var words = new[] { MipsEncoding.Branch(0x04, 0, 0, Dest, Dest + 12),
            MipsEncoding.I(0x23, T0, 4, 0), MipsEncoding.Nop, MipsEncoding.Nop,
            MipsEncoding.JumpRegister(Ra), MipsEncoding.Nop };
        var build = ReachableProgramBuilder.BuildLoadedImage(Dest, words, [Dest], None);
        build.Blocks.Should().NotContain(b => b.Block.EntryPc == Dest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void InterpretPoint_IsNeverCoveredByANativeUnit(int kind)
    {
        uint[] words = kind switch
        {
            0 => [MipsEncoding.I(0x0D, T0, 0, 1), MipsEncoding.I(0x0D, T0, 0, 2), MipsEncoding.JumpRegister(Ra), MipsEncoding.Nop],
            1 => [MipsEncoding.I(0x23, T0, 4, 0), MipsEncoding.I(0x0D, 16, T0, 0), MipsEncoding.JumpRegister(Ra), MipsEncoding.Nop],
            _ => [MipsEncoding.Branch(0x04, 0, 0, Dest, Dest + 8), MipsEncoding.I(0x0D, T0, 0, 1), MipsEncoding.JumpRegister(Ra), MipsEncoding.Nop],
        };
        var point = Dest + 4;
        var build = ReachableProgramBuilder.BuildLoadedImage(Dest, words, [Dest], new HashSet<uint> { point });
        build.Blocks.Should().NotContain(b => point >= b.Block.EntryPc && point < b.Block.EntryPc + b.Words.Count * 4u);
    }

    [Fact]
    public void AnUnsupportedRoot_AndAnExcludedEntry_KeepNoBlock()
    {
        var ori = MipsEncoding.I(0x0D, T0, 0, 2);
        var jr = MipsEncoding.JumpRegister(Ra);
        var build = ReachableProgramBuilder.BuildLoadedImage(
            Dest, [ori, jr, MipsEncoding.Nop, Reserved, ori, jr, MipsEncoding.Nop], [Dest, Dest + 12, Dest + 16, Dest + 20], new HashSet<uint> { Dest + 16 });

        build.Blocks.Select(static b => b.Block.EntryPc).Should().Equal(Dest, Dest + 4, Dest + 20);
        build.SkippedEntries.Should().Equal(Dest + 12);
    }
}
