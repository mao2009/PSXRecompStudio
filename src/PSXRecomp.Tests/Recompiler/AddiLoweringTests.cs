using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

[Test]
public sealed class AddiLoweringTests
{
    private const uint EntryPc = 0x80000000u;

    [Fact]
    public void Addi_PositiveNonOverflow_WritesDestination()
    {
        var result = Run([MipsEncoding.I(0x08, rt: 8, rs: 9, immediate: 1)],
            initialGpr: Gpr(9, 41));

        result.Termination.Should().Be(RecompilerIrTerminationReason.Success);
        result.Gpr[8].Should().Be(42u);
    }

    [Fact]
    public void Addi_NegativeImmediate_IsSignExtended()
    {
        var result = Run([MipsEncoding.I(0x08, rt: 8, rs: 9, immediate: 0xFFFF)],
            initialGpr: Gpr(9, 42));

        result.Termination.Should().Be(RecompilerIrTerminationReason.Success);
        result.Gpr[8].Should().Be(41u);
    }

    [Fact]
    public void Addi_ZeroDestination_IsDiscarded()
    {
        var result = Run([MipsEncoding.I(0x08, rt: 0, rs: 9, immediate: 1)],
            initialGpr: Gpr(9, 41));

        result.Termination.Should().Be(RecompilerIrTerminationReason.Success);
        result.Gpr[0].Should().Be(0u);
    }

    [Fact]
    public void Addi_PositiveOverflow_RaisesExceptionAndSuppressesWrite()
    {
        var result = Run([MipsEncoding.I(0x08, rt: 8, rs: 9, immediate: 1)],
            initialGpr: Gpr(9, 0x7FFFFFFFu));

        result.Termination.Should().Be(RecompilerIrTerminationReason.Exception);
        result.Gpr[8].Should().Be(0u);
    }

    [Fact]
    public void Addi_NegativeOverflow_RaisesExceptionAndSuppressesWrite()
    {
        var result = Run([MipsEncoding.I(0x08, rt: 8, rs: 9, immediate: 0xFFFF)],
            initialGpr: Gpr(9, 0x80000000u));

        result.Termination.Should().Be(RecompilerIrTerminationReason.Exception);
        result.Gpr[8].Should().Be(0u);
    }

    [Fact]
    public void Addiu_StillWrapsWithoutException()
    {
        var result = Run([MipsEncoding.I(0x09, rt: 8, rs: 9, immediate: 1)],
            initialGpr: Gpr(9, 0x7FFFFFFFu));

        result.Termination.Should().Be(RecompilerIrTerminationReason.Success);
        result.Gpr[8].Should().Be(0x80000000u);
    }

    [Fact]
    public void Addi_InJalDelaySlot_WritesBeforeCallExit()
    {
        var result = Run([
            MipsEncoding.JumpAndLink(0x80000020u),
            MipsEncoding.I(0x08, rt: 8, rs: 0, immediate: 7)],
            initialGpr: null);

        result.Termination.Should().Be(RecompilerIrTerminationReason.Success);
        result.Gpr[8].Should().Be(7u);
    }

    [Fact]
    public void Addi_InConditionalBranchDelaySlot_WritesBeforeBranchExit()
    {
        var result = Run([
            MipsEncoding.Branch(0x04, rs: 0, rt: 0, pc: EntryPc, target: EntryPc + 0x20),
            MipsEncoding.I(0x08, rt: 8, rs: 0, immediate: 7)],
            initialGpr: null);

        result.Termination.Should().Be(RecompilerIrTerminationReason.Success);
        result.Gpr[8].Should().Be(7u);
    }

    [Fact]
    public void Addi_InSyntheticPersonaStructuralShape_IsReachableAndLowerable()
    {
        // The leading word represents unreachable text-region data. The entry
        // points at a JAL whose reachable delay slot is ADDI, matching the
        // structural blocker without carrying any real-input bytes.
        var loadAddress = 0x80010000u;
        var entry = loadAddress + 8;
        var program = ReachableProgramBuilder.Build(loadAddress, [
            0x474E445Cu,
            0x00000000u,
            MipsEncoding.JumpAndLink(loadAddress + 0x18),
            MipsEncoding.I(0x08, rt: 8, rs: 0, immediate: 1),
            0x00000000u,
            0x00000000u,
            0x00000000u,
        ], entry);

        program.Blocks.SelectMany(block => block.Operations)
            .Should().Contain(operation => operation.Kind == RecompilerIrOperationKind.AddSigned);
    }

    [Fact]
    public void GeneratedHost_AddiOverflow_StopsAsExceptionBeforeWrite()
    {
        var fixture = new RecompilerDifferentialFixture(
            "addi-host-overflow",
            [MipsEncoding.I(0x08, rt: 8, rs: 9, immediate: 1)],
            EntryPc,
            stepBudget: 1,
            initialGpr: Gpr(9, 0x7FFFFFFFu));

        var result = new RecompilerHostExecutor().Execute(fixture);

        result.Status.Should().Be(RecompilerExecutionStatus.Completed, result.DiagnosticMessage);
        result.Snapshot!.Termination.Should().Be(RecompilerIrTerminationReason.Exception);
        result.Snapshot.Gpr[8].Should().Be(0u);
    }

    private static RecompilerIrEvaluationResult Run(uint[] words, IReadOnlyList<uint>? initialGpr)
    {
        var instructions = words
            .Select((word, index) => (R3000aDecoder.Decode(word), EntryPc + (uint)(index * 4)))
            .ToArray();
        var program = MipsToIrLowerer.LowerProgram(instructions);
        RecompilerIrValidator.Validate(program).IsValid.Should().BeTrue();

        return RecompilerIrEvaluator.Run(
            program,
            EntryPc,
            initialGpr ?? new uint[32],
            new RecompilerGuestMemory(),
            blockBudget: 8);
    }

    private static uint[] Gpr(byte register, uint value)
    {
        var gpr = new uint[32];
        gpr[register] = value;
        return gpr;
    }
}
