using PSXRecomp.Core.Recompiler;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

#pragma warning disable AARC003

/// <summary>
/// Issue #377: a GTE/COP2, LWC2 or SWC2 word must never leave the differential
/// reference oracle reporting a clean run.
///
/// Issue #376 made the native interpreter raise Coprocessor Unusable for these
/// opcodes instead of silently executing them as a NOP, which is the first half
/// of the fix. The second half lives here: <c>PSXCore_Step</c> returns 0 for a
/// step that faulted (an architectural exception is a normal, continuable
/// hardware event, and <c>PSXCore_Run</c> must keep executing into the handler),
/// so <see cref="RecompilerInterpreterExecutor"/>'s status check alone never
/// saw the fault. The CpU exception moved the PC to the exception vector, the
/// next iteration found the PC outside the program, and the run ended as
/// <see cref="RecompilerIrTerminationReason.Success"/> — a faulted reference
/// indistinguishable from a clean one, which is precisely the false-match risk
/// the oracle exists to rule out.
/// </summary>
[Test]
public sealed class GteCoprocessorFaultTests
{
    private const uint Entry = 0x80000000u;

    // RTPS (a GTE command), LWC2 $1, 0($0) and SWC2 $1, 0($0). All three fall in
    // the COP2 family and raise CpU with CAUSE.CE=2 on the native interpreter.
    public static TheoryData<string, uint> Cop2Words => new()
    {
        { "COP2/GTE RTPS", 0x4A180001u },
        { "LWC2", 0xC8010000u },
        { "SWC2", 0xE8010000u },
    };

    [Theory]
    [MemberData(nameof(Cop2Words))]
    public void ACop2FamilyWord_MakesTheReferenceOracleReportAnException_NotSuccess(string name, uint word)
    {
        // A nop first, so the run has retired real work before it faults: the
        // termination must reflect the fault, not merely "nothing happened".
        var fixture = new RecompilerDifferentialFixture(
            $"issue377-{name}", [MipsEncoding.Nop, word], Entry, stepBudget: 8);

        var result = new RecompilerInterpreterExecutor().Execute(fixture);

        Assert.Equal(RecompilerExecutionStatus.Completed, result.Status);
        Assert.Equal(RecompilerIrTerminationReason.Exception, result.Snapshot!.Termination);

        // The run stopped at the faulting instruction, not at the end of the
        // program: the nop and the COP2 word are the only PCs traced.
        Assert.Equal([Entry, Entry + 4], result.Snapshot.PcTrace);
    }

    [Fact]
    public void AFaultedReference_AgainstASilentlyCompletingActual_IsAHardMismatch()
    {
        // The exact #377 false-match shape: the recompiled side does not implement
        // the GTE either, so it leaves every observable field identical to the
        // interpreter's. The ONLY thing that distinguishes the two runs is that one
        // faulted. `termination` is a behavioral field in RecompilerStateDiff, so
        // that alone must force a hard Mismatch — never a Match, and never the
        // tolerated BudgetInconclusive middle case.
        var fixture = new RecompilerDifferentialFixture(
            "issue377-false-match", [MipsEncoding.Nop, 0x4A180001u], Entry, stepBudget: 8);

        var reference = new RecompilerInterpreterExecutor().Execute(fixture).Snapshot!;
        var silentlyCompleting = new RecompilerStateSnapshot(
            reference.Gpr,
            hi: reference.HI,
            lo: reference.LO,
            pc: reference.PC,
            termination: RecompilerIrTerminationReason.Success,
            pcTrace: reference.PcTrace);

        var diff = RecompilerStateDiff.Compare(
            reference, silentlyCompleting, budgetsAreShared: true, staticBlockEntryPcs: null);

        Assert.Equal(RecompilerComparisonClassification.Mismatch, diff.Classification);
        Assert.Equal("termination", Assert.Single(diff.Differences).FieldPath);
    }
}
