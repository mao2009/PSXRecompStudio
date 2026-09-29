using PSXRecomp.Core.Recompiler;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

#pragma warning disable AARC003

[Test]
// Issue #635: a register-indirect JR/JALR carries its runtime target on
// RecompilerIrExit.TargetValueId, and the generated block hands it to the
// dispatch loop as next_pc. These tests pin the validator contract, the host
// emission, and native-interpreter parity on the generated host for the
// JALR link ordering, a return through $ra, and a trapping delay slot.
public sealed class RecompilerRegisterIndirectTransferTests
{
    private const uint EntryPc = 0x80000000u;

    // --- validator ----------------------------------------------------------

    [Fact]
    public void Validator_Accepts_ASuccessExitCarryingADefinedTargetValue()
    {
        Validate(new RecompilerIrExit(RecompilerIrTerminationReason.Success, targetValueId: 0))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validator_Rejects_AnExitCarryingBothANextPcAndATargetValue()
    {
        Validate(new RecompilerIrExit(RecompilerIrTerminationReason.Success, nextPc: EntryPc + 4, targetValueId: 0))
            .Diagnostics.Should().ContainSingle(d => d.Code == RecompilerIrDiagnosticCode.IllegalTermination);
    }

    [Fact]
    public void Validator_Rejects_AnUndefinedTargetValue()
    {
        Validate(new RecompilerIrExit(RecompilerIrTerminationReason.Success, targetValueId: 7))
            .Diagnostics.Should().ContainSingle(d => d.Code == RecompilerIrDiagnosticCode.MissingOperand);
    }

    [Fact]
    public void Validator_Rejects_ATargetValueOnANonSuccessOrFlowExit()
    {
        Validate(new RecompilerIrExit(RecompilerIrTerminationReason.UnresolvedIndirectFlow, targetValueId: 0))
            .Diagnostics.Should().ContainSingle(d => d.Code == RecompilerIrDiagnosticCode.IllegalTermination);
        Validate(new RecompilerIrExit(
                RecompilerIrTerminationReason.Success,
                flow: new RecompilerIrFlow(RecompilerIrFlowKind.Jump, EntryPc),
                targetValueId: 0))
            .Diagnostics.Should().ContainSingle(d => d.Code == RecompilerIrDiagnosticCode.IllegalTermination);
    }

    // --- host emission ------------------------------------------------------

    [Fact]
    public void CodeGen_WritesTheTargetValueAsNextPc_AndReturnsSuccess()
    {
        var result = RecompilerHostCodeGen.Generate(new RecompilerIrProgram(new[] { BlockReadingT0(
            new RecompilerIrExit(RecompilerIrTerminationReason.Success, targetValueId: 0)) }));

        result.Success.Should().BeTrue(result.DiagnosticMessage);
        result.Source.Should().Contain("uint32_t v0 = state->gpr[8];");
        result.Source.Should().Contain("state->next_pc = v0; state->termination_reason = 0; return 0;");
    }

    [Fact]
    public void CodeGen_RefusesAMalformedTargetExit_ThroughTheValidator()
    {
        var result = RecompilerHostCodeGen.Generate(new RecompilerIrProgram(new[] { BlockReadingT0(
            new RecompilerIrExit(RecompilerIrTerminationReason.Success, nextPc: EntryPc + 4, targetValueId: 0)) }));

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be("IR_VALIDATION_FAILED");
    }

    // --- generated host vs native interpreter ------------------------------

    [Fact]
    public void JalrRaRa_TransfersToThePreLinkValue_OnTheGeneratedHost()
    {
        // JALR $ra, $ra: the target is read before the link write.
        var fixture = new RecompilerDifferentialFixture(
            "jalr-ra-ra",
            new[]
            {
                MipsEncoding.I(0x0F, rt: 31, rs: 0, immediate: 0x8000),              // 0x00 LUI $ra, 0x8000
                MipsEncoding.I(0x09, rt: 31, rs: 31, immediate: 0x18),               // 0x04 $ra = 0x80000018
                MipsEncoding.JumpAndLinkRegister(rd: 31, rs: 31),                    // 0x08
                MipsEncoding.Nop,                                                    // 0x0C delay slot
                MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 0xBAD),               // 0x10 link address (skipped)
                MipsEncoding.Nop,                                                    // 0x14
                MipsEncoding.R(0x21, rd: 11, rs: 31, rt: 0, shamt: 0),               // 0x18 $t3 = $ra
            },
            EntryPc, stepBudget: 4, referenceStepBudget: 5);

        var result = RunMatched(fixture);

        var actual = result.Actual.Snapshot!;
        Assert.Equal(RecompilerIrTerminationReason.Success, actual.Termination);
        Assert.Equal(0x80000010u, actual.Gpr[31]);
        Assert.Equal(0u, actual.Gpr[10]);
        Assert.Equal(0x80000010u, actual.Gpr[11]);
        Assert.Equal(0x8000001Cu, actual.PC);
    }

    [Fact]
    public void JrRa_ReturnsToTheCompiledCaller_OnTheGeneratedHost()
    {
        var fixture = new RecompilerDifferentialFixture(
            "jal-then-jr-ra",
            new[]
            {
                MipsEncoding.JumpAndLink(EntryPc + 0x14),                            // 0x00
                MipsEncoding.Nop,                                                    // 0x04
                MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 5),                    // 0x08 return address
                MipsEncoding.Jump(EntryPc + 0x20),                                   // 0x0C leave the program
                MipsEncoding.Nop,                                                    // 0x10
                MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 7),                   // 0x14 callee
                MipsEncoding.JumpRegister(rs: 31),                                   // 0x18
                MipsEncoding.Nop,                                                    // 0x1C
            },
            EntryPc, stepBudget: 16, referenceStepBudget: 8);

        var result = RunMatched(fixture);

        var actual = result.Actual.Snapshot!;
        Assert.Equal(RecompilerIrTerminationReason.Success, actual.Termination);
        Assert.Equal(7u, actual.Gpr[10]);
        Assert.Equal(5u, actual.Gpr[9]);
        Assert.Equal(0x80000020u, actual.PC);
    }

    [Fact]
    public void JrToAnUncompiledTarget_EndsTheGeneratedRunAtThatPc()
    {
        // No block and no host claim at the target: the generated dispatch ends
        // the segment at that PC; the orchestrator then fails it closed as
        // UNRESOLVED_TRANSFER (see RecompiledArtifactE2ETests).
        var fixture = new RecompilerDifferentialFixture(
            "jr-uncompiled-target",
            new[]
            {
                MipsEncoding.I(0x0F, rt: 8, rs: 0, immediate: 0x8002),               // 0x00 $t0 = 0x80020000
                MipsEncoding.JumpRegister(rs: 8),                                    // 0x04
                MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 3),                    // 0x08 delay slot
            },
            EntryPc, stepBudget: 8, referenceStepBudget: 3);

        var actual = new RecompilerHostExecutor().Execute(fixture);

        Assert.Equal(RecompilerExecutionStatus.Completed, actual.Status);
        Assert.Equal(RecompilerIrTerminationReason.Success, actual.Snapshot!.Termination);
        Assert.Equal(0x80020000u, actual.Snapshot.PC);
        Assert.Equal(3u, actual.Snapshot.Gpr[9]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BreakInARegisterIndirectDelaySlot_SuppressesTheTransfer(bool link)
    {
        var fixture = TrappingDelaySlot("break", link, MipsEncoding.Break());

        var result = RunMatched(fixture);

        AssertDelaySlotTrap(result, MipsToIrLowerer.BreakExcode, link);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SyscallInARegisterIndirectDelaySlot_SuppressesTheTransfer(bool link)
    {
        var fixture = TrappingDelaySlot("syscall", link, MipsEncoding.Syscall());

        var result = RunMatched(fixture);

        AssertDelaySlotTrap(result, MipsToIrLowerer.SyscallExcode, link);
    }

    private static RecompilerDifferentialFixture TrappingDelaySlot(string name, bool link, uint trap) =>
        new(
            $"{name}-in-{(link ? "jalr" : "jr")}-delay-slot",
            new[]
            {
                MipsEncoding.I(0x0F, rt: 8, rs: 0, immediate: 0x8000),               // 0x00
                MipsEncoding.I(0x09, rt: 8, rs: 8, immediate: 0x14),                 // 0x04 $t0 = 0x80000014
                link ? MipsEncoding.JumpAndLinkRegister(rd: 31, rs: 8)               // 0x08
                     : MipsEncoding.JumpRegister(rs: 8),
                trap,                                                                // 0x0C delay slot traps
                MipsEncoding.I(0x09, rt: 11, rs: 0, immediate: 0xBAD),               // 0x10
                MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 2),                    // 0x14 target (never reached)
            },
            EntryPc, stepBudget: 3, referenceStepBudget: 4);

    private static void AssertDelaySlotTrap(RecompilerDifferentialResult result, uint excode, bool link)
    {
        foreach (var snap in new[] { result.Reference.Snapshot!, result.Actual.Snapshot! })
        {
            Assert.Equal(RecompilerIrTerminationReason.Exception, snap.Termination);
            Assert.True(snap.Exception.IsRaised);
            Assert.Equal(excode, snap.Exception.Code);
            Assert.Equal(EntryPc + 8, snap.Exception.FaultPc);
            Assert.True(snap.Exception.InDelaySlot);
            Assert.Equal(0u, snap.Gpr[9]);
            Assert.Equal(0u, snap.Gpr[11]);
            Assert.Equal(link ? EntryPc + 0x10 : 0u, snap.Gpr[31]);
        }
    }

    private static RecompilerDifferentialResult RunMatched(RecompilerDifferentialFixture fixture)
    {
        var result = RecompilerDifferentialRunner.Run(
            fixture, new RecompilerInterpreterExecutor(), new RecompilerHostExecutor());

        Assert.True(result.BothCompleted, $"[{result.Actual.DiagnosticCode}] {result.Actual.DiagnosticMessage}");
        Assert.True(result.IsMatch, RecompilerDifferentialArtifacts.FailureMessage(result));
        return result;
    }

    private static RecompilerIrBlock BlockReadingT0(RecompilerIrExit exit) =>
        new(EntryPc, new[] { new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 0, register: 8) }, exit);

    private static RecompilerIrValidationResult Validate(RecompilerIrExit exit) =>
        RecompilerIrValidator.Validate(new RecompilerIrProgram(new[] { BlockReadingT0(exit) }));
}
