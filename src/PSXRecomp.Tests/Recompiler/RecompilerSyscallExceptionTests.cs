using FluentAssertions;
using PSXRecomp.Core;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

#pragma warning disable AARC003

[Test]
// Issue #628: SYSCALL lowers as the same architectural synchronous exception exit
// BREAK uses (#481), differing only in Excode (Sys, 0x08). These tests pin the
// lowering, the validator contract, the reachable-program treatment, the native
// interpreter oracle's COP0 semantics (EPC / Cause.ExcCode / Cause.BD / BEV
// vector) and native-vs-generated-host parity.
public sealed class RecompilerSyscallExceptionTests
{
    private const uint EntryPc = 0x80000000u;
    private const uint Bev0Vector = 0x80000080u;
    private const uint Bev1Vector = 0xBFC00180u;

    [Fact]
    public void Syscall_LowersToAnExceptionExit_CarryingTheSysResolution()
    {
        var result = MipsToIrLowerer.Lower(R3000aDecoder.Decode(MipsEncoding.Syscall()), EntryPc);

        result.IsSupported.Should().BeTrue($"[{result.DiagnosticCode}] {result.DiagnosticMessage}");
        var block = result.Block!;
        block.Operations.Should().BeEmpty();
        block.Exit.Reason.Should().Be(RecompilerIrTerminationReason.Exception);
        block.Exit.Flow.Should().BeNull();
        block.Exit.NextPc.Should().BeNull();
        block.Exit.Exception!.IsRaised.Should().BeTrue();
        block.Exit.Exception.Code.Should().Be(0x08u).And.Be(MipsToIrLowerer.SyscallExcode);
        block.Exit.Exception.FaultPc.Should().Be(EntryPc);
        block.Exit.Exception.InDelaySlot.Should().BeFalse();
        RecompilerIrValidator.Validate(new RecompilerIrProgram(new[] { block })).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Syscall_InADelaySlot_SuppressesTheTransfer_KeepsTheLink_AndPointsAtTheBranch()
    {
        var control = R3000aDecoder.Decode(MipsEncoding.JumpAndLink(0x80002000u));
        var result = MipsToIrLowerer.LowerControlTransfer(control, EntryPc, R3000aDecoder.Decode(MipsEncoding.Syscall()));

        result.IsSupported.Should().BeTrue($"[{result.DiagnosticCode}] {result.DiagnosticMessage}");
        var block = result.Block!;
        block.Exit.Reason.Should().Be(RecompilerIrTerminationReason.Exception);
        block.Exit.Flow.Should().BeNull();
        block.Exit.Exception!.Code.Should().Be(MipsToIrLowerer.SyscallExcode);
        block.Exit.Exception.FaultPc.Should().Be(EntryPc);
        block.Exit.Exception.InDelaySlot.Should().BeTrue();
        block.Operations.Should().Contain(op => op.Kind == RecompilerIrOperationKind.Constant && op.Immediate == EntryPc + 8);
        RecompilerIrValidator.Validate(new RecompilerIrProgram(new[] { block })).IsValid.Should().BeTrue();
    }

    [Fact]
    public void ReachableProgram_SyscallBlock_IsATerminalLeaf_WithNoEdgeToTheVectorOrFallThrough()
    {
        var program = ReachableProgramBuilder.Build(EntryPc, [
            MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 1),
            MipsEncoding.Syscall(),
            MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 2), // after SYSCALL: must not be reached statically
        ], EntryPc);

        program.Blocks.Should().OnlyContain(b => b.EntryPc != EntryPc + 8);
        var syscall = program.Blocks.Single(b => b.Exit.Exception != null);
        syscall.Exit.Exception!.Code.Should().Be(MipsToIrLowerer.SyscallExcode);
        syscall.Exit.Flow.Should().BeNull();
        program.Blocks.Should().NotContain(b => b.EntryPc == Bev0Vector || b.EntryPc == Bev1Vector);
    }

    [Fact]
    public void UnrelatedUnsupportedOpcodes_StillFailClosed()
    {
        var add = R3000aDecoder.Decode(MipsEncoding.R(0x20, rd: 8, rs: 9, rt: 10, shamt: 0));
        MipsToIrLowerer.Lower(add, EntryPc).IsSupported.Should().BeFalse();
        var mtc0 = R3000aDecoder.Decode((0x10u << 26) | (4u << 21) | (8u << 16) | (12u << 11));
        MipsToIrLowerer.Lower(mtc0, EntryPc).IsSupported.Should().BeFalse();
    }

    [Fact]
    public void StandaloneSyscall_MatchesTheInterpreter_AndPreservesGprs()
    {
        var gpr = new uint[RecompilerDifferentialFixture.GprCount];
        for (var i = 1; i < gpr.Length; i++) gpr[i] = 0x1000u + (uint)i;
        var fixture = new RecompilerDifferentialFixture(
            "syscall-standalone", new[] { MipsEncoding.Syscall() }, EntryPc, stepBudget: 1, initialGpr: gpr);

        var result = RecompilerDifferentialRunner.Run(fixture, new RecompilerInterpreterExecutor(), new RecompilerHostExecutor());

        Assert.True(result.BothCompleted, $"[{result.Actual.DiagnosticCode}] {result.Actual.DiagnosticMessage}");
        Assert.True(result.IsMatch, RecompilerDifferentialArtifacts.FailureMessage(result));
        foreach (var snap in new[] { result.Reference.Snapshot!, result.Actual.Snapshot! })
        {
            Assert.Equal(RecompilerIrTerminationReason.Exception, snap.Termination);
            Assert.True(snap.Exception.IsRaised);
            Assert.Equal(0x08u, snap.Exception.Code);
            Assert.Equal(EntryPc, snap.Exception.FaultPc);
            Assert.False(snap.Exception.InDelaySlot);
            Assert.Equal(0u, snap.Gpr[0]);
            for (var i = 1; i < gpr.Length; i++) Assert.Equal(gpr[i], snap.Gpr[i]);
        }
        Assert.Equal(Bev0Vector, result.Reference.Snapshot!.PC);
    }

    [Fact]
    public void SyscallAfterWork_RetiresPriorInstructions_ThenRaises()
    {
        var fixture = new RecompilerDifferentialFixture(
            "syscall-after-work",
            new[]
            {
                MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 5),
                MipsEncoding.Syscall(),
                MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 7), // never executed
            },
            EntryPc, stepBudget: 2, referenceStepBudget: 2);

        var result = RecompilerDifferentialRunner.Run(fixture, new RecompilerInterpreterExecutor(), new RecompilerHostExecutor());

        Assert.True(result.BothCompleted, $"[{result.Actual.DiagnosticCode}] {result.Actual.DiagnosticMessage}");
        Assert.True(result.IsMatch, RecompilerDifferentialArtifacts.FailureMessage(result));
        Assert.Equal(5u, result.Actual.Snapshot!.Gpr[8]);
        Assert.Equal(0u, result.Actual.Snapshot.Gpr[9]);
        Assert.Equal(EntryPc + 4, result.Actual.Snapshot.Exception.FaultPc);
    }

    [Fact]
    public void SyscallInJalDelaySlot_MatchesTheInterpreter_WithEpcBdAndTheLinkWrite()
    {
        var fixture = new RecompilerDifferentialFixture(
            "syscall-in-jal-delay-slot",
            new[]
            {
                MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 1),
                MipsEncoding.JumpAndLink(EntryPc + 0x10),
                MipsEncoding.Syscall(),
                MipsEncoding.I(0x09, rt: 11, rs: 0, immediate: 0xBAD),
                MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 2),
            },
            EntryPc, stepBudget: 2, referenceStepBudget: 4);

        var result = RecompilerDifferentialRunner.Run(fixture, new RecompilerInterpreterExecutor(), new RecompilerHostExecutor());

        Assert.True(result.BothCompleted, $"[{result.Actual.DiagnosticCode}] {result.Actual.DiagnosticMessage}");
        Assert.True(result.IsMatch, RecompilerDifferentialArtifacts.FailureMessage(result));
        foreach (var snap in new[] { result.Reference.Snapshot!, result.Actual.Snapshot! })
        {
            Assert.Equal(0x08u, snap.Exception.Code);
            Assert.Equal(EntryPc + 4, snap.Exception.FaultPc);
            Assert.True(snap.Exception.InDelaySlot);
            Assert.Equal(EntryPc + 0xCu, snap.Gpr[31]);
            Assert.Equal(0u, snap.Gpr[11]);
            Assert.Equal(0u, snap.Gpr[9]);
        }
    }

    [Fact]
    public void Syscall_DriverSnapshot_RoundTripsThroughTheParser()
    {
        var executor = new RecompilerHostExecutor();
        var fixture = new RecompilerDifferentialFixture(
            "syscall-driver", new[] { MipsEncoding.Syscall() }, EntryPc, stepBudget: 1);
        var compiled = executor.CompileRecompiledBinary(fixture);
        try
        {
            var parsed = SnapshotParser.Parse(executor.RunRecompiledBinary(compiled));
            Assert.NotNull(parsed);
            Assert.Equal(RecompilerIrTerminationReason.Exception, parsed!.Termination);
            Assert.True(parsed.Exception.IsRaised);
            Assert.Equal(0x08u, parsed.Exception.Code);
            Assert.Equal(EntryPc, parsed.Exception.FaultPc);
        }
        finally
        {
            if (System.IO.Directory.Exists(compiled.DirectoryPath)) System.IO.Directory.Delete(compiled.DirectoryPath, true);
        }
    }

    // Native oracle: the COP0 side the host artifact deliberately does not model.
    [Theory]
    [InlineData(false, Bev0Vector)]
    [InlineData(true, Bev1Vector)]
    public void NativeInterpreter_Syscall_WritesEpcCauseAndSelectsTheBevVector(bool bev, uint vector)
    {
        using var core = new PSXCoreWrapper();
        core.Reset();
        var words = new List<uint>();
        if (bev)
        {
            words.Add(MipsEncoding.I(0x0F, rt: 8, rs: 0, immediate: 0x0040)); // LUI $t0, 0x0040 (SR.BEV = bit 22)
            words.Add((0x10u << 26) | (4u << 21) | (8u << 16) | (12u << 11)); // MTC0 $t0, SR
            words.Add(0u);                                                    // NOP
        }
        var syscallPc = EntryPc + (uint)words.Count * 4;
        words.Add(MipsEncoding.Syscall());
        for (var i = 0; i < words.Count; i++) core.WriteMemory32((uint)i * 4, words[i]);
        core.Pc = EntryPc;

        while (core.Pc != syscallPc) core.Step().Should().Be(0);
        core.Step().Should().Be(0);

        core.ExceptionRaised.Should().BeTrue();
        core.ExceptionCode.Should().Be(0x08u);
        core.Pc.Should().Be(vector);
        core.GetCop0(14).Should().Be(syscallPc);               // EPC = the SYSCALL itself
        ((core.GetCop0(13) >> 2) & 0x1Fu).Should().Be(0x08u);  // Cause.ExcCode = Sys
        (core.GetCop0(13) >> 31).Should().Be(0u);              // Cause.BD = 0
    }

    [Fact]
    public void NativeInterpreter_SyscallInDelaySlot_SetsBdAndEpcToTheBranch()
    {
        using var core = new PSXCoreWrapper();
        core.Reset();
        core.WriteMemory32(0, MipsEncoding.JumpAndLink(EntryPc + 0x10));
        core.WriteMemory32(4, MipsEncoding.Syscall());
        core.Pc = EntryPc;

        core.Step().Should().Be(0);
        core.Step().Should().Be(0);

        core.ExceptionCode.Should().Be(0x08u);
        core.ExceptionInDelaySlot.Should().BeTrue();
        core.GetCop0(14).Should().Be(EntryPc);
        (core.GetCop0(13) >> 31).Should().Be(1u);
        core.Pc.Should().Be(Bev0Vector);
    }
}
#pragma warning restore AARC003
