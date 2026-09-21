using PSXRecomp.Core.Recompiler;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

#pragma warning disable AARC003

[Test]
// Issue #481: lowering reachable R3000A BREAK as an architectural synchronous
// exception (Bp, Excode 0x09). The end-to-end parity tests prove the interpreter
// and the generated host agree on the raised-exception resolution — Excode, EPC
// (faultPc) and the delay-slot flag — including when the two execution models
// park PC differently (interpreter at the BEV=0 vector, host at the faulting
// block's entry).
public sealed class RecompilerBreakExceptionTests
{
    [Fact]
    public void StandaloneBreak_MatchesTheInterpreter_AsARaisedBpException()
    {
        var result = RecompilerDifferentialRunner.Run(
            RecompilerFixtures.BreakStandalone(), new RecompilerInterpreterExecutor(), new RecompilerHostExecutor());

        Assert.True(result.BothCompleted, result.Reference.Status == RecompilerExecutionStatus.Completed
            ? $"host executor failed: [{result.Actual.DiagnosticCode}] {result.Actual.DiagnosticMessage}"
            : "interpreter executor failed.");
        Assert.True(result.IsMatch, RecompilerDifferentialArtifacts.FailureMessage(result));

        var reference = result.Reference.Snapshot!;
        var actual = result.Actual.Snapshot!;
        Assert.Equal(RecompilerIrTerminationReason.Exception, reference.Termination);
        Assert.Equal(RecompilerIrTerminationReason.Exception, actual.Termination);

        // Both sides see the same raised Bp exception resolved at the BREAK's own
        // address, not in a delay slot.
        Assert.True(reference.Exception.IsRaised);
        Assert.True(actual.Exception.IsRaised);
        Assert.Equal(MipsToIrLowerer.BreakExcode, reference.Exception.Code);
        Assert.Equal(MipsToIrLowerer.BreakExcode, actual.Exception.Code);
        Assert.Equal(0x80000000u, reference.Exception.FaultPc);
        Assert.Equal(0x80000000u, actual.Exception.FaultPc);
        Assert.False(reference.Exception.InDelaySlot);
        Assert.False(actual.Exception.InDelaySlot);

        // The interpreter parked at the BEV=0 vector; the host parked at the
        // faulting block's entry. The match above proves the classifier waived
        // the PC comparison exactly because the exception resolution agreed.
        Assert.Equal(0x80000080u, reference.PC);
        Assert.Equal(0x80000000u, actual.PC);
    }

    [Fact]
    public void BreakInAJalDelaySlot_MatchesTheInterpreter_WithEpcBbAndTheLinkWrite()
    {
        var result = RecompilerDifferentialRunner.Run(
            RecompilerFixtures.BreakInJalDelaySlot(), new RecompilerInterpreterExecutor(), new RecompilerHostExecutor());

        Assert.True(result.BothCompleted, result.Reference.Status == RecompilerExecutionStatus.Completed
            ? $"host executor failed: [{result.Actual.DiagnosticCode}] {result.Actual.DiagnosticMessage}"
            : "interpreter executor failed.");
        Assert.True(result.IsMatch, RecompilerDifferentialArtifacts.FailureMessage(result));

        var reference = result.Reference.Snapshot!;
        var actual = result.Actual.Snapshot!;
        Assert.Equal(RecompilerIrTerminationReason.Exception, reference.Termination);
        Assert.Equal(RecompilerIrTerminationReason.Exception, actual.Termination);

        // The delay-slot BREAK points at the owning JAL with BD=1 on both sides.
        Assert.True(reference.Exception.IsRaised);
        Assert.True(actual.Exception.IsRaised);
        Assert.Equal(0x80000004u, reference.Exception.FaultPc);
        Assert.Equal(0x80000004u, actual.Exception.FaultPc);
        Assert.True(reference.Exception.InDelaySlot);
        Assert.True(actual.Exception.InDelaySlot);

        // The JAL linked before the delay slot raised: $ra = PC + 8 = 0x8000000C
        // on both sides, matching hardware (link, then fault).
        Assert.Equal(0x8000000Cu, reference.Gpr[31]);
        Assert.Equal(0x8000000Cu, actual.Gpr[31]);
    }

    [Fact]
    public void StandaloneBreak_IsDeterministic_Across_Independent_Runs()
    {
        var executor = new RecompilerHostExecutor();
        var fixture = RecompilerFixtures.BreakStandalone();

        var first = executor.Execute(fixture);
        var second = executor.Execute(fixture);

        Assert.Equal(RecompilerExecutionStatus.Completed, first.Status);
        Assert.Equal(RecompilerExecutionStatus.Completed, second.Status);
        var diff = RecompilerStateDiff.Compare(
            first.Snapshot!, second.Snapshot!, budgetsAreShared: true, staticBlockEntryPcs: new HashSet<uint>(second.Snapshot!.PcTrace));
        Assert.True(diff.IsMatch, diff.Describe());
    }

    [Fact]
    public void Break_DriverSnapshot_RoundTripsThroughTheParser()
    {
        // Protocol round trip: the generated driver prints the exception keys and
        // the parser folds them back into the snapshot's exception resolution.
        var executor = new RecompilerHostExecutor();
        var compiled = executor.CompileRecompiledBinary(RecompilerFixtures.BreakStandalone());
        try
        {
            var parsed = SnapshotParser.Parse(executor.RunRecompiledBinary(compiled));

            Assert.NotNull(parsed);
            Assert.Equal(RecompilerIrTerminationReason.Exception, parsed!.Termination);
            Assert.True(parsed.Exception.IsRaised);
            Assert.Equal(MipsToIrLowerer.BreakExcode, parsed.Exception.Code);
            Assert.Equal(0x80000000u, parsed.Exception.FaultPc);
            Assert.False(parsed.Exception.InDelaySlot);
        }
        finally
        {
            if (System.IO.Directory.Exists(compiled.DirectoryPath))
            {
                System.IO.Directory.Delete(compiled.DirectoryPath, true);
            }
        }
    }
}
#pragma warning restore AARC003