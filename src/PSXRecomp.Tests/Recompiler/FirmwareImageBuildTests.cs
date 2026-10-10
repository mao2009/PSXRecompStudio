using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;
using static PSXRecomp.Tests.Recompiler.RecompilerCop0Tests;

namespace PSXRecomp.Tests.Recompiler;

[Test]
// Issue #732: reachable-program discovery over a firmware ROM image — COP0 moves
// fall through, a branch-delay-slot load is lowered only with a proof that no
// successor observes its shadow, and BuildFirmwareImage reports native coverage
// and the static targets that are left to the interpreter fallback.
public sealed class FirmwareImageBuildTests
{
    private const byte T0 = 8, T1 = 9, T2 = 10, T3 = 11;

    [Fact]
    public void ReachableProgram_Cop0MovesDoNotEndDiscovery()
    {
        var program = ReachableProgramBuilder.Build(EntryPc,
        [
            Mtc0(T0, RecompilerCop0.Status),
            Rfe,
            Ori(T1, 0, 1),
            MipsEncoding.JumpRegister(31),
            MipsEncoding.Nop,
        ], EntryPc);

        program.Blocks.Select(block => block.EntryPc).Should().Contain([EntryPc + 4, EntryPc + 8, EntryPc + 12]);
    }

    [Fact]
    public void ReachableProgram_DelaySlotLoad_WhoseShadowNoSuccessorReads_IsLowered()
    {
        var program = ReachableProgramBuilder.Build(EntryPc, DelaySlotLoadProgram(targetReadsLoadedRegister: false), EntryPc);

        var branch = program.Blocks.Single(block => block.EntryPc == EntryPc);
        branch.Operations.Should().Contain(op => op.Kind == RecompilerIrOperationKind.Load32);
        branch.Operations.Should().Contain(op => op.Kind == RecompilerIrOperationKind.WriteGpr && op.Register == T2);
    }

    [Fact]
    public void ReachableProgram_DelaySlotLoad_ReadByASuccessor_StillFailsClosed()
    {
        var build = () => ReachableProgramBuilder.Build(EntryPc, DelaySlotLoadProgram(targetReadsLoadedRegister: true), EntryPc);

        build.Should().Throw<InvalidOperationException>().WithMessage("*delay slot*");
    }

    [Fact]
    public void FirmwareImage_ReportsNativeCoverageAndTheStaticTargetsLeftToFallback()
    {
        const uint RomBase = 0xBFC00000u;
        var image = new uint[]
        {
            MipsEncoding.JumpAndLink(RomBase + 0x1000),   // outside the 6-word image
            MipsEncoding.Nop,
            Mtc0(0, RecompilerCop0.Status),
            MipsEncoding.JumpRegister(31),
            MipsEncoding.Nop,
            0x6E65704Fu,                                   // data: never reached
        };

        var firmware = ReachableProgramBuilder.BuildFirmwareImage(RomBase, image, RomBase, additionalRoots: []);

        firmware.ImageInstructionCount.Should().Be(6);
        firmware.NativeInstructionCount.Should().Be(5);
        firmware.FallbackTargets.Should().Equal(RomBase + 0x1000);
        firmware.Program.Blocks.Select(block => block.EntryPc).Should().Equal(RomBase, RomBase + 8, RomBase + 12);
    }

    /// <summary>BEQ $zero,$zero with LW $t2 in its delay slot; both successors either ignore $t2 or read it.</summary>
    private static uint[] DelaySlotLoadProgram(bool targetReadsLoadedRegister) =>
    [
        MipsEncoding.Branch(0x04, rs: 0, rt: 0, pc: EntryPc, target: EntryPc + 16),
        MipsEncoding.Load(R3000aOpcode.Lw, rt: T2, baseRegister: 4, offset: 0),
        Ori(T3, 0, 1),                                                           // fall-through
        MipsEncoding.Nop,
        targetReadsLoadedRegister
            ? MipsEncoding.R(0x21, rd: T3, rs: T2, rt: 0, shamt: 0)              // target reads $t2
            : Ori(T3, 0, 2),
        MipsEncoding.JumpRegister(31),
        MipsEncoding.Nop,
    ];
}
