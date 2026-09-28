using PSXRecomp.Core.Recompiler;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

#pragma warning disable AARC003

/// <summary>
/// Issue #578: a ROM-free synthetic regression for reference-first execution-window
/// alignment. <see cref="RecompilerDifferentialFixture.StepBudget"/> counts host
/// blocks and <see cref="RecompilerDifferentialFixture.ReferenceStepBudget"/> counts
/// guest instructions; a BNE fused with its delay slot retires one host block per two
/// guest instructions, so a loop that iterates several times accumulates a real gap
/// between the two counts — exactly the shape that produced Persona's false MISMATCH
/// (the host simply ran further than the interpreter under an equal numeric budget,
/// not a genuine lowering divergence). This uses real executors (interpreter +
/// compiled host) over synthetic MIPS words only, so it runs everywhere in CI without
/// a ROM/CHD fixture.
/// </summary>
[Test]
public sealed class RecompilerExecutionWindowAlignmentTests
{
    private const uint EntryPc = 0x80000000u;

    /// <summary>
    /// Straight-line setup (2 instructions) into a loop body (ADDIU + BNE/delay-slot,
    /// the BNE+delay-slot pair fused into one host block) that runs 5 iterations, then
    /// falls through to one more instruction. 6 static guest instructions lower to 5
    /// static host blocks (0x00, 0x04, 0x08, 0x0C-fused, 0x14), so a mid-loop budget
    /// cut retires strictly more guest instructions than host blocks for the same
    /// execution window.
    /// </summary>
    private static RecompilerDifferentialFixture LoopFixture(uint stepBudget, uint referenceStepBudget) =>
        new(
            "loop-body-fused-control-transfer",
            new[]
            {
                MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 5),   // 0x00 ADDIU $t0, $zero, 5   (loop bound)
                MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 0),   // 0x04 ADDIU $t1, $zero, 0   (counter)
                MipsEncoding.I(0x09, rt: 9, rs: 9, immediate: 1),   // 0x08 loop: ADDIU $t1, $t1, 1
                MipsEncoding.Branch(0x05, rs: 9, rt: 8, pc: EntryPc + 0x0C, target: EntryPc + 0x08), // 0x0C BNE $t1, $t0, loop
                MipsEncoding.Nop,                                    // 0x10 delay slot
                MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 42), // 0x14 post-loop (unreached at this budget)
            },
            entryPc: EntryPc,
            stepBudget: stepBudget,
            referenceStepBudget: referenceStepBudget);
    // budgetsAreShared intentionally omitted (defaults false) — neither runner call
    // below is allowed to infer it from equal numbers; RunReferenceFirstAligned
    // derives its own proven value internally.

    [Fact]
    public void EqualNumericBudget_RealExecutors_HostOvershootsTheInterpreter_IsAMismatch()
    {
        // 9 guest instructions retires 3 loop increments and stops mid-branch (PC =
        // 0x0C, counter = 3). 9 host blocks retires more real work — each fused
        // BNE+delay-slot block covers 2 guest instructions — so the naive
        // equal-numeric-budget host runs a full extra iteration ahead (counter = 4)
        // before it, too, exhausts its budget. This is the exact false-MISMATCH shape
        // Issue #578 reports for Persona.
        var fixture = LoopFixture(stepBudget: 9, referenceStepBudget: 9);
        var reference = new RecompilerInterpreterExecutor();
        var actual = new RecompilerHostExecutor();

        var result = RecompilerDifferentialRunner.Run(fixture, reference, actual);

        Assert.True(result.Actual.Status == RecompilerExecutionStatus.Completed,
            $"recompiled host failed: [{result.Actual.DiagnosticCode}] {result.Actual.DiagnosticMessage}");
        Assert.True(result.BothCompleted);
        Assert.Equal(RecompilerIrTerminationReason.ExecutionBudgetExceeded, result.Reference.Snapshot!.Termination);
        Assert.Equal(RecompilerIrTerminationReason.ExecutionBudgetExceeded, result.Actual.Snapshot!.Termination);
        Assert.Equal(3u, result.Reference.Snapshot.Gpr[9]); // interpreter: 3 real increments
        Assert.Equal(4u, result.Actual.Snapshot.Gpr[9]);    // host: overshot by a full iteration
        Assert.False(result.IsMatch, result.Diff!.Describe());
        Assert.False(result.IsBudgetInconclusive, result.Diff!.Describe());
    }

    [Fact]
    public void ReferenceFirstAligned_RealExecutors_StopsAtTheSameExecutionWindow_IsAMatch()
    {
        // Same fixture, same starting numeric budget — but the aligned runner derives
        // the host's real budget (7 blocks: 0x00,0x04,[0x08,0x0C]x2,0x08) from the
        // interpreter's own retirement trace projected onto the lowered program's
        // static block entries, instead of trusting the caller's numeric StepBudget.
        // Both sides must land on the exact same PC/registers/checkpoint trace.
        var fixture = LoopFixture(stepBudget: 9, referenceStepBudget: 9);
        var reference = new RecompilerInterpreterExecutor();
        var actual = new RecompilerHostExecutor();

        var result = RecompilerDifferentialRunner.RunReferenceFirstAligned(fixture, reference, actual);

        Assert.True(result.Actual.Status == RecompilerExecutionStatus.Completed,
            $"recompiled host failed: [{result.Actual.DiagnosticCode}] {result.Actual.DiagnosticMessage}");
        Assert.True(result.BothCompleted);
        Assert.Equal(3u, result.Reference.Snapshot!.Gpr[9]);
        Assert.Equal(3u, result.Actual.Snapshot!.Gpr[9]);
        Assert.Equal(result.Reference.Snapshot.PC, result.Actual.Snapshot.PC);
        Assert.True(result.IsMatch, result.Diff?.Describe());
        Assert.Empty(result.Diff!.Differences);
    }

    [Fact]
    public void ReferenceFirstAligned_ReferenceStopsBetweenBranchAndDelaySlot_DoesNotAssertASharedWindow()
    {
        // referenceStepBudget=7 retires the second loop iteration's BNE (PC 0x0C)
        // but stops before its delay slot (PC 0x10) retires. Issue #578 /
        // CodeRabbit: the old projection counted that BNE's entry PC as a fully
        // retired host block anyway, deriving StepBudget=6 with BudgetsAreShared:
        // true — the host then executed the delay slot and took the branch the
        // interpreter never resolved, landing on PC 0x08 while the interpreter sat
        // at PC 0x10, a false MISMATCH under a falsely-proven shared window. A host
        // block always retires atomically, so no derived budget can reproduce this
        // exact mid-fused-block stopping point; alignment must be refused instead.
        var fixture = LoopFixture(stepBudget: 9, referenceStepBudget: 7);
        var reference = new RecompilerInterpreterExecutor();
        var actual = new RecompilerHostExecutor();

        var result = RecompilerDifferentialRunner.RunReferenceFirstAligned(fixture, reference, actual);

        Assert.True(result.Actual.Status == RecompilerExecutionStatus.Completed,
            $"recompiled host failed: [{result.Actual.DiagnosticCode}] {result.Actual.DiagnosticMessage}");
        Assert.Equal(RecompilerIrTerminationReason.ExecutionBudgetExceeded, result.Reference.Snapshot!.Termination);
        Assert.Equal(2u, result.Reference.Snapshot.Gpr[9]);
        Assert.Equal(EntryPc + 0x10, result.Reference.Snapshot.PC);

        // Alignment refused: the fixture keeps its own author-supplied budget and
        // BudgetsAreShared stays false, exactly like the "nothing projects" fallback
        // — never the falsely-derived StepBudget: 6 / BudgetsAreShared: true the bug
        // produced.
        Assert.False(result.Fixture.BudgetsAreShared);
        Assert.Equal(9u, result.Fixture.StepBudget);

        // Whatever the unaligned comparison finds, it must never be laundered as a
        // proven match or as budget-inconclusive: BudgetsAreShared: false makes both
        // unreachable, which is the honest outcome for a window this method could
        // not prove.
        Assert.False(result.IsMatch, result.Diff?.Describe());
        Assert.False(result.IsBudgetInconclusive, result.Diff?.Describe());
    }

    [Fact]
    public void ReferenceFirstAligned_ReferenceCompletesTheDelaySlot_AlignsAndIsAMatch()
    {
        // referenceStepBudget=8 retires both loop iterations' BNE *and* delay slot
        // (through PC 0x10 the second time), so the final fused block is fully
        // retired — the alignment this method is meant to prove remains available
        // for the shape immediately adjacent to the incomplete-block regression
        // above.
        var fixture = LoopFixture(stepBudget: 9, referenceStepBudget: 8);
        var reference = new RecompilerInterpreterExecutor();
        var actual = new RecompilerHostExecutor();

        var result = RecompilerDifferentialRunner.RunReferenceFirstAligned(fixture, reference, actual);

        Assert.True(result.Actual.Status == RecompilerExecutionStatus.Completed,
            $"recompiled host failed: [{result.Actual.DiagnosticCode}] {result.Actual.DiagnosticMessage}");
        Assert.True(result.Fixture.BudgetsAreShared);
        Assert.Equal(6u, result.Fixture.StepBudget);
        Assert.Equal(2u, result.Reference.Snapshot!.Gpr[9]);
        Assert.Equal(2u, result.Actual.Snapshot!.Gpr[9]);
        Assert.Equal(result.Reference.Snapshot.PC, result.Actual.Snapshot.PC);
        Assert.True(result.IsMatch, result.Diff?.Describe());
    }

    [Fact]
    public void ReferenceFirstAligned_RealDivergence_IsStillAMismatch()
    {
        // The alignment machinery must not launder a genuine semantic bug: a host
        // whose recompiled result is corrupted after a correct, aligned run must
        // still be caught as a real Mismatch, not hidden by the new alignment path.
        var fixture = LoopFixture(stepBudget: 9, referenceStepBudget: 9);
        var reference = new RecompilerInterpreterExecutor();
        var actual = new CorruptingHostExecutor(new RecompilerHostExecutor(), corruptedRegister: 9, corruptedValue: 0xDEADBEEFu);

        var result = RecompilerDifferentialRunner.RunReferenceFirstAligned(fixture, reference, actual);

        Assert.True(result.BothCompleted);
        Assert.False(result.IsMatch, result.Diff?.Describe());
        Assert.Contains(result.Diff!.Differences, d => d.FieldPath == "gpr[9]");
    }

    /// <summary>
    /// Wraps a real executor and flips one GPR in its snapshot — simulating a genuine
    /// recompiled-path bug that the alignment machinery must not hide.
    /// </summary>
    private sealed class CorruptingHostExecutor : IRecompilerExecutor
    {
        private readonly IRecompilerExecutor _inner;
        private readonly int _corruptedRegister;
        private readonly uint _corruptedValue;

        public CorruptingHostExecutor(IRecompilerExecutor inner, int corruptedRegister, uint corruptedValue)
        {
            _inner = inner;
            _corruptedRegister = corruptedRegister;
            _corruptedValue = corruptedValue;
        }

        public string Name => "corrupting-host";

        public RecompilerExecutionResult Execute(RecompilerDifferentialFixture fixture)
        {
            var result = _inner.Execute(fixture);
            if (result.Snapshot is null) return result;

            var gpr = result.Snapshot.Gpr.ToArray();
            gpr[_corruptedRegister] = _corruptedValue;
            var corrupted = new RecompilerStateSnapshot(
                gpr, result.Snapshot.HI, result.Snapshot.LO, result.Snapshot.PC, result.Snapshot.LoadDelay,
                result.Snapshot.Exception, result.Snapshot.Termination, result.Snapshot.Memory, result.Snapshot.PcTrace);
            return RecompilerExecutionResult.Completed(corrupted);
        }
    }
}
#pragma warning restore AARC003
