using PSXRecomp.Core.Recompiler;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

#pragma warning disable AARC003

[Test]
public sealed class RecompilerDifferentialTests
{
    private static RecompilerStateSnapshot Snapshot(
        uint gpr8 = 0, uint gpr9 = 0, uint gpr11 = 0,
        uint hi = 0, uint lo = 0, uint pc = 0x80000000u,
        RecompilerIrTerminationReason termination = RecompilerIrTerminationReason.Success,
        IEnumerable<uint>? pcTrace = null)
    {
        var gpr = new uint[32];
        gpr[0] = 0;
        gpr[8] = gpr8;
        gpr[9] = gpr9;
        gpr[11] = gpr11;
        return new RecompilerStateSnapshot(gpr, hi, lo, pc, termination: termination, pcTrace: pcTrace);
    }

    [Fact]
    public void Equal_States_Produce_Match()
    {
        var a = Snapshot(gpr8: 5, gpr9: 7, gpr11: 12);
        var b = Snapshot(gpr8: 5, gpr9: 7, gpr11: 12);

        var diff = RecompilerStateDiff.Compare(a, b, budgetsAreShared: true, staticBlockEntryPcs: new HashSet<uint>(b.PcTrace));

        Assert.Equal(RecompilerComparisonClassification.Match, diff.Classification);
        Assert.True(diff.IsMatch);
        Assert.Empty(diff.Differences);
    }

    [Fact]
    public void OneGprMismatch_Produces_Mismatch_With_Location_And_Value()
    {
        var a = Snapshot(gpr8: 2, gpr9: 3);
        var b = Snapshot(gpr8: 99, gpr9: 3);

        var diff = RecompilerStateDiff.Compare(a, b, budgetsAreShared: true, staticBlockEntryPcs: new HashSet<uint>(b.PcTrace));

        Assert.Equal(RecompilerComparisonClassification.Mismatch, diff.Classification);
        Assert.False(diff.IsMatch);
        var difference = Assert.Single(diff.Differences);
        Assert.Equal("gpr[8]", difference.FieldPath);
        Assert.Equal("0x00000002", difference.ExpectedText);
        Assert.Equal("0x00000063", difference.ActualText);
    }

    [Fact]
    public void HiLoPcAndTermination_Mismatches_Are_Reported()
    {
        var reference = Snapshot(gpr8: 5, gpr9: 7, gpr11: 12);

        var oddHi = Snapshot(gpr8: 5, gpr9: 7, gpr11: 12, hi: 0x1111);
        var diffWithHi = RecompilerStateDiff.Compare(reference, oddHi, budgetsAreShared: true, staticBlockEntryPcs: new HashSet<uint>(oddHi.PcTrace));
        Assert.Single(diffWithHi.Differences);
        Assert.Equal("hi", diffWithHi.Differences[0].FieldPath);

        var oddTerm = Snapshot(gpr8: 5, gpr9: 7, gpr11: 12, pc: 0x80000004u, termination: RecompilerIrTerminationReason.ExecutionBudgetExceeded);
        var diffWithPcTerm = RecompilerStateDiff.Compare(reference, oddTerm, budgetsAreShared: true, staticBlockEntryPcs: new HashSet<uint>(oddTerm.PcTrace));
        Assert.Contains(diffWithPcTerm.Differences, d => d.FieldPath == "pc");
        Assert.Contains(diffWithPcTerm.Differences, d => d.FieldPath == "termination");
    }

    [Fact]
    public void BudgetCutOff_BothExhausted_TailOnlyDivergence_IsBudgetInconclusive()
    {
        // Issue #304: a long loop cut off by the shared budget on both executors at
        // different iterations. Both exhausted; the host ran further through the
        // same loop body (the loop runs 0x08→0x0C→0x10→0x08), so its trace is a
        // continuation of the interpreter's — the only checkpoint difference is the
        // tail marker and the residual diffs are confined to pc/gpr (the parked
        // iteration counter and PC). The state is inconclusive, not a mismatch.
        var reference = Snapshot(
            gpr8: 18, pc: 0x8000000Cu,
            termination: RecompilerIrTerminationReason.ExecutionBudgetExceeded,
            pcTrace: new uint[] { 0x80000000u, 0x80000004u, 0x80000008u, 0x8000000Cu, 0x80000010u, 0x80000008u, 0x8000000Cu });
        var actual = Snapshot(
            gpr8: 20, pc: 0x80000010u,
            termination: RecompilerIrTerminationReason.ExecutionBudgetExceeded,
            pcTrace: new uint[] { 0x80000000u, 0x80000004u, 0x80000008u, 0x8000000Cu, 0x80000010u, 0x80000008u, 0x8000000Cu, 0x80000010u, 0x80000008u, 0x8000000Cu, 0x80000010u });

        var diff = RecompilerStateDiff.Compare(reference, actual, budgetsAreShared: true, staticBlockEntryPcs: new HashSet<uint>(actual.PcTrace));

        Assert.Equal(RecompilerComparisonClassification.BudgetInconclusive, diff.Classification);
        Assert.True(diff.IsBudgetInconclusive);
        Assert.False(diff.IsMatch);
        Assert.DoesNotContain(diff.Differences, d => d.FieldPath == "hi" || d.FieldPath == "lo" ||
            d.FieldPath.StartsWith("memory", StringComparison.Ordinal) ||
            d.FieldPath.StartsWith("loadDelay", StringComparison.Ordinal) ||
            d.FieldPath.StartsWith("exception", StringComparison.Ordinal));
    }

    [Fact]
    public void BudgetCutOff_OneSidedExhaustion_IsAMismatch()
    {
        // Only the reference exhausted the budget; the recompiled side ran to
        // completion. The difference is not attributable to a shared budget cut, so
        // it must remain a hard mismatch.
        var reference = Snapshot(
            gpr8: 18, pc: 0x80000014u,
            termination: RecompilerIrTerminationReason.ExecutionBudgetExceeded,
            pcTrace: new uint[] { 0x80000000u, 0x80000004u, 0x80000008u, 0x8000000Cu, 0x80000010u, 0x80000014u });
        var actual = Snapshot(
            gpr8: 20, pc: 0x80000018u,
            termination: RecompilerIrTerminationReason.Success,
            pcTrace: new uint[] { 0x80000000u, 0x80000004u, 0x80000008u, 0x8000000Cu, 0x80000010u, 0x80000014u, 0x80000018u });

        var diff = RecompilerStateDiff.Compare(reference, actual, budgetsAreShared: true, staticBlockEntryPcs: new HashSet<uint>(actual.PcTrace));

        Assert.Equal(RecompilerComparisonClassification.Mismatch, diff.Classification);
        Assert.False(diff.IsBudgetInconclusive);
    }

    [Fact]
    public void BudgetCutOff_CheckpointDivergenceBeforeCut_IsAMismatch()
    {
        // A real divergence before the cut: the host executes a code path — PC
        // 0x80000014 — the interpreter never visited in its whole trace (the
        // interpreter only looped between 0x04 and 0x08). That is a brand-new path,
        // not a loop continuation past the interpreter trace, and must stay a hard
        // mismatch even though both sides exhausted the budget.
        var reference = Snapshot(
            gpr8: 18, pc: 0x80000008u,
            termination: RecompilerIrTerminationReason.ExecutionBudgetExceeded,
            pcTrace: new uint[] { 0x80000000u, 0x80000004u, 0x80000008u, 0x80000004u, 0x80000008u });
        var actual = Snapshot(
            gpr8: 20, pc: 0x80000014u,
            termination: RecompilerIrTerminationReason.ExecutionBudgetExceeded,
            pcTrace: new uint[] { 0x80000000u, 0x80000004u, 0x80000014u, 0x80000004u, 0x80000014u });

        var diff = RecompilerStateDiff.Compare(reference, actual, budgetsAreShared: true, staticBlockEntryPcs: new HashSet<uint>(actual.PcTrace));

        Assert.Equal(RecompilerComparisonClassification.Mismatch, diff.Classification);
        Assert.False(diff.IsBudgetInconclusive);
    }

    [Fact]
    public void BudgetCutOff_SamePcSetButDifferentOrder_IsAMismatch()
    {
        // Regression for the ordering blind spot in the budget-inconclusive
        // check: a superset/set membership test cannot distinguish ordering. Both
        // sides exhaust the budget and visit exactly the same PCs, but the host
        // executes the same two-loop body in a different order (A→C→B rather than
        // A→B→C). That is a real control-flow divergence, not a loop continuation
        // past the cut, and must remain a hard mismatch even though the PC sets are
        // identical.
        var reference = Snapshot(
            gpr8: 18, pc: 0x80000008u,
            termination: RecompilerIrTerminationReason.ExecutionBudgetExceeded,
            pcTrace: new uint[] { 0x80000000u, 0x80000004u, 0x80000008u, 0x80000000u, 0x80000004u, 0x80000008u });
        var actual = Snapshot(
            gpr8: 20, pc: 0x80000004u,
            termination: RecompilerIrTerminationReason.ExecutionBudgetExceeded,
            pcTrace: new uint[] { 0x80000000u, 0x80000008u, 0x80000004u, 0x80000000u, 0x80000008u, 0x80000004u });

        var diff = RecompilerStateDiff.Compare(reference, actual, budgetsAreShared: true, staticBlockEntryPcs: new HashSet<uint>(actual.PcTrace));

        Assert.Equal(RecompilerComparisonClassification.Mismatch, diff.Classification);
        Assert.False(diff.IsBudgetInconclusive);
    }

    [Fact]
    public void BudgetCutOff_TailRunsPastInterpreterEnd_InDifferentOrder_IsAMismatch()
    {
        // Same-PC-set reordering where the host additionally runs past the end of
        // the interpreter trace: the comparable prefix must still agree in order
        // (A→B→C), so a host that loops in a different order before running ahead is
        // a divergence before the cut and stays a hard mismatch, not a tail.
        var reference = Snapshot(
            gpr8: 18, pc: 0x80000004u,
            termination: RecompilerIrTerminationReason.ExecutionBudgetExceeded,
            pcTrace: new uint[] { 0x80000000u, 0x80000004u, 0x80000008u, 0x80000000u, 0x80000004u, 0x80000008u });
        var actual = Snapshot(
            gpr8: 20, pc: 0x80000008u,
            termination: RecompilerIrTerminationReason.ExecutionBudgetExceeded,
            pcTrace: new uint[] { 0x80000000u, 0x80000004u, 0x80000008u, 0x80000000u, 0x80000008u, 0x80000004u, 0x80000000u });

        var diff = RecompilerStateDiff.Compare(reference, actual, budgetsAreShared: true, staticBlockEntryPcs: new HashSet<uint>(actual.PcTrace));

        Assert.Equal(RecompilerComparisonClassification.Mismatch, diff.Classification);
        Assert.False(diff.IsBudgetInconclusive);
    }

    [Fact]
    public void BudgetCutOff_BehavioralFieldDivergence_IsAMismatch()
    {
        // Even with both sides exhausting the budget, a behavioral-field
        // divergence (here a different HI value) proves the executors are not
        // behaviorally equivalent and must never be downgraded to inconclusive.
        var reference = Snapshot(
            gpr8: 18, hi: 0x1234, pc: 0x80000014u,
            termination: RecompilerIrTerminationReason.ExecutionBudgetExceeded,
            pcTrace: new uint[] { 0x80000000u, 0x80000004u, 0x80000008u, 0x8000000Cu, 0x80000010u, 0x80000014u });
        var actual = Snapshot(
            gpr8: 20, hi: 0x5678, pc: 0x80000018u,
            termination: RecompilerIrTerminationReason.ExecutionBudgetExceeded,
            pcTrace: new uint[] { 0x80000000u, 0x80000004u, 0x80000008u, 0x8000000Cu, 0x80000010u, 0x80000014u, 0x80000018u });

        var diff = RecompilerStateDiff.Compare(reference, actual, budgetsAreShared: true, staticBlockEntryPcs: new HashSet<uint>(actual.PcTrace));

        Assert.Equal(RecompilerComparisonClassification.Mismatch, diff.Classification);
        Assert.False(diff.IsBudgetInconclusive);
    }

    [Fact]
    public void BudgetCutOff_UnequalBudgets_IsAMismatch()
    {
        // Regression (CodeRabbit, #305): a pair of snapshots that both report
        // ExecutionBudgetExceeded does not by itself prove the two executors ran
        // under the same budget. This is the exact tail-shaped trace that earns
        // BudgetInconclusive when the budgets truly are shared (see
        // BudgetCutOff_BothExhausted_TailOnlyDivergence_IsBudgetInconclusive); with
        // the caller unable to prove a shared budget, it must stay a mismatch.
        var reference = Snapshot(
            gpr8: 18, pc: 0x8000000Cu,
            termination: RecompilerIrTerminationReason.ExecutionBudgetExceeded,
            pcTrace: new uint[] { 0x80000000u, 0x80000004u, 0x80000008u, 0x8000000Cu, 0x80000010u, 0x80000008u, 0x8000000Cu });
        var actual = Snapshot(
            gpr8: 20, pc: 0x80000010u,
            termination: RecompilerIrTerminationReason.ExecutionBudgetExceeded,
            pcTrace: new uint[] { 0x80000000u, 0x80000004u, 0x80000008u, 0x8000000Cu, 0x80000010u, 0x80000008u, 0x8000000Cu, 0x80000010u, 0x80000008u, 0x8000000Cu, 0x80000010u });

        var diff = RecompilerStateDiff.Compare(reference, actual, budgetsAreShared: false, staticBlockEntryPcs: new HashSet<uint>(actual.PcTrace));

        Assert.Equal(RecompilerComparisonClassification.Mismatch, diff.Classification);
        Assert.False(diff.IsBudgetInconclusive);
    }

    [Fact]
    public void BudgetCutOff_HostSkipsAStaticBlockEntry_IsAMismatch()
    {
        // Regression (CodeRabbit, #305): deriving the tail-projection PC set from
        // the observed host trace (instead of the lowered program's static
        // block-entry PCs) hides a real control-flow skip. Static blocks A, B, C;
        // the interpreter visits all three in order (A B C A B C); the host skips
        // B entirely (A C A C A). Projecting onto the host-observed set {A, C}
        // would erase B and let the host's tail look like a loop continuation;
        // projecting onto the static set {A, B, C} correctly keeps this a mismatch.
        const uint a = 0x80000000u, b = 0x80000004u, c = 0x80000008u;
        var reference = Snapshot(
            gpr8: 18, pc: c,
            termination: RecompilerIrTerminationReason.ExecutionBudgetExceeded,
            pcTrace: new[] { a, b, c, a, b, c });
        var actual = Snapshot(
            gpr8: 20, pc: a,
            termination: RecompilerIrTerminationReason.ExecutionBudgetExceeded,
            pcTrace: new[] { a, c, a, c, a });

        var diff = RecompilerStateDiff.Compare(reference, actual, budgetsAreShared: true, staticBlockEntryPcs: new HashSet<uint> { a, b, c });

        Assert.Equal(RecompilerComparisonClassification.Mismatch, diff.Classification);
        Assert.False(diff.IsBudgetInconclusive);
    }

    [Fact]
    public void Describe_And_MachineReadable_Are_Deterministic_For_Same_Diff()
    {
        var a = Snapshot(gpr8: 2);
        var b = Snapshot(gpr8: 3);

        var first = RecompilerStateDiff.Compare(a, b, budgetsAreShared: true, staticBlockEntryPcs: new HashSet<uint>(b.PcTrace));
        var second = RecompilerStateDiff.Compare(a, b, budgetsAreShared: true, staticBlockEntryPcs: new HashSet<uint>(b.PcTrace));

        Assert.Equal(first.Describe(), second.Describe());
        Assert.Equal(first.ToMachineReadable(), second.ToMachineReadable());
    }

    [Fact]
    public void Matching_Executors_Produce_Match()
    {
        var fixture = RecompilerFixtures.AddThree();
        var diff = RecompilerDifferentialRunner.Run(fixture, new StubExecutor(0), new StubExecutor(0));

        Assert.True(diff.BothCompleted);
        Assert.True(diff.IsMatch);
        Assert.Empty(diff.Diff!.Differences);
    }

    [Fact]
    public void Run_ProjectsOntoTheLoweredProgramsStaticBlockEntries_NotTheHostTrace()
    {
        // Integration regression for the runner wiring (CodeRabbit, #305): the
        // runner must derive the tail-projection PC set from the fixture's lowered
        // static blocks, not from whatever the host trace happened to observe.
        // Three straight-line instructions each lower to their own block (A, B, C);
        // a host stub that skips B's block entry must not have B silently vanish
        // from the projection set merely because the host trace never mentions it.
        var fixture = new RecompilerDifferentialFixture(
            "static-block-skip",
            encodedInstructions: new[]
            {
                MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 1),  // 0x00 A: ADDIU $t0, $zero, 1
                MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 2),  // 0x04 B: ADDIU $t1, $zero, 2
                MipsEncoding.I(0x09, rt: 10, rs: 0, immediate: 3), // 0x08 C: ADDIU $t2, $zero, 3
            },
            entryPc: 0x80000000u,
            stepBudget: 5,
            referenceStepBudget: 5,
            budgetsAreShared: true);

        const uint a = 0x80000000u, b = 0x80000004u, c = 0x80000008u;
        var reference = new ScriptedExecutor(Snapshot(
            gpr8: 1, pc: c,
            termination: RecompilerIrTerminationReason.ExecutionBudgetExceeded,
            pcTrace: new[] { a, b, c, a, b, c }));
        var actual = new ScriptedExecutor(Snapshot(
            gpr8: 1, pc: a,
            termination: RecompilerIrTerminationReason.ExecutionBudgetExceeded,
            pcTrace: new[] { a, c, a, c, a }));

        var result = RecompilerDifferentialRunner.Run(fixture, reference, actual);

        Assert.True(result.BothCompleted);
        Assert.False(result.IsMatch);
        Assert.False(result.IsBudgetInconclusive, result.Diff!.Describe());
    }

    [Fact]
    public void Run_EqualNumericBudgets_AcrossFusedControlTransfer_DoNotImplyASharedBudget()
    {
        // Regression (CodeRabbit, #305): StepBudget counts host blocks,
        // ReferenceStepBudget counts guest instructions; a control transfer fused
        // with its delay slot retires one host block per two guest instructions, so
        // equal numeric budgets do not by themselves prove the same work counter.
        // This fixture has a BNE+delay-slot control transfer, equal StepBudget and
        // ReferenceStepBudget (100/100, mirroring RecompilerFixtures.Issue304BudgetCutOffLoop),
        // but — unlike that fixture — does not opt into BudgetsAreShared. The exact
        // tail-shaped divergence that fixture earns BudgetInconclusive with must stay
        // a mismatch here, because nothing has proven the equal numbers actually mean
        // a shared cut point.
        var fixture = new RecompilerDifferentialFixture(
            "equal-numeric-budgets-fused-control-transfer",
            encodedInstructions: new[]
            {
                MipsEncoding.I(0x09, rt: 8, rs: 0, immediate: 1000),                     // 0x00 $t0 = 1000 (loop bound)
                MipsEncoding.I(0x09, rt: 9, rs: 0, immediate: 0),                        // 0x04 $t1 = 0 (counter)
                MipsEncoding.I(0x09, rt: 9, rs: 9, immediate: 1),                        // 0x08 loop: $t1 += 1
                MipsEncoding.I(0x09, rt: 10, rs: 10, immediate: 7),                      // 0x0C $t2 += 7
                MipsEncoding.Branch(0x05, rs: 9, rt: 8, pc: 0x80000010u, target: 0x80000008u), // 0x10 BNE $t1, $t0, loop
                MipsEncoding.Nop,                                                        // 0x14 delay slot
            },
            entryPc: 0x80000000u,
            stepBudget: 100,
            referenceStepBudget: 100);
        // budgetsAreShared intentionally omitted — defaults to false.

        var reference = new ScriptedExecutor(Snapshot(
            gpr8: 18, pc: 0x8000000Cu,
            termination: RecompilerIrTerminationReason.ExecutionBudgetExceeded,
            pcTrace: new uint[] { 0x80000000u, 0x80000004u, 0x80000008u, 0x8000000Cu, 0x80000010u, 0x80000008u, 0x8000000Cu }));
        var actual = new ScriptedExecutor(Snapshot(
            gpr8: 20, pc: 0x80000010u,
            termination: RecompilerIrTerminationReason.ExecutionBudgetExceeded,
            pcTrace: new uint[] { 0x80000000u, 0x80000004u, 0x80000008u, 0x8000000Cu, 0x80000010u, 0x80000008u, 0x8000000Cu, 0x80000010u, 0x80000008u, 0x8000000Cu, 0x80000010u }));

        var result = RecompilerDifferentialRunner.Run(fixture, reference, actual);

        Assert.True(result.BothCompleted);
        Assert.False(result.IsMatch);
        Assert.False(result.IsBudgetInconclusive, result.Diff!.Describe());
    }

    [Fact]
    public void Intentional_SemanticDivergence_Produces_Mismatch_And_Fails()
    {
        // Negative proof: an executor that genuinely diverges (flips GPR8) must be
        // detected by the harness as a MISMATCH with the diverging field localized.
        var fixture = RecompilerFixtures.AddThree();
        var reference = new StubExecutor(0);
        var corrupter = new StubExecutor(gpr8Override: 0xDEADBEEF);

        var result = RecompilerDifferentialRunner.Run(fixture, reference, corrupter);

        Assert.True(result.Reference.Status == RecompilerExecutionStatus.Completed);
        Assert.True(result.Actual.Status == RecompilerExecutionStatus.Completed);
        Assert.True(result.BothCompleted);
        Assert.False(result.IsMatch);
        var difference = Assert.Single(result.Diff!.Differences);
        Assert.Equal("gpr[8]", difference.FieldPath);
        Assert.Equal("0x00000005", difference.ExpectedText);
        Assert.Equal("0xDEADBEEF", difference.ActualText);
    }

    [Fact]
    public void Write_OnMatch_WritesNothing_And_ReturnsNull()
    {
        var fixture = RecompilerFixtures.AddThree();
        var result = RecompilerDifferentialRunner.Run(fixture, new StubExecutor(0), new StubExecutor(0));

        var artifacts = RecompilerDifferentialArtifacts.Write(result);

        Assert.Null(artifacts);
    }

    [Fact]
    public void Write_OnMismatch_EmitsTheFullArtifactBundle()
    {
        // B6: a mismatch must be diagnosable after the fact, not just in the
        // console. Every listed artifact file must exist and be non-empty.
        var fixture = RecompilerFixtures.AddThree();
        var result = RecompilerDifferentialRunner.Run(fixture, new StubExecutor(0), new StubExecutor(gpr8Override: 0xDEADBEEF));

        var artifacts = RecompilerDifferentialArtifacts.Write(result);
        try
        {
            Assert.NotNull(artifacts);
            Assert.True(Directory.Exists(artifacts));
            foreach (var expected in new[]
                     {
                         "fixture.txt", "diff.txt", "diff.machine-readable.txt", "checkpoint.txt",
                         "reference-snapshot.json", "actual-snapshot.json",
                     })
            {
                var path = Path.Combine(artifacts!, expected);
                Assert.True(File.Exists(path), $"expected artifact file missing: {expected}");
                Assert.NotEmpty(File.ReadAllText(path));
            }

            // FailureMessage writes its own bundle (it doesn't reuse Write's); it
            // must still report a path back for that bundle.
            var message = RecompilerDifferentialArtifacts.FailureMessage(result);
            Assert.Contains("Differential artifacts:", message);
            var messageArtifacts = message[(message.IndexOf("Differential artifacts: ", StringComparison.Ordinal) + "Differential artifacts: ".Length)..];
            Assert.True(Directory.Exists(messageArtifacts));
            Directory.Delete(messageArtifacts, recursive: true);
        }
        finally
        {
            if (artifacts is not null && Directory.Exists(artifacts)) Directory.Delete(artifacts, recursive: true);
        }
    }

    [Fact]
    public void GenerationFailure_Is_Reported_And_Produces_No_BothCompleted()
    {
        var fixture = RecompilerFixtures.AddThree();
        var failing = new AlwaysFailingExecutor();

        var result = RecompilerDifferentialRunner.Run(fixture, new StubExecutor(0), failing);

        Assert.Equal(RecompilerExecutionStatus.GenerationFailed, result.Actual.Status);
        Assert.False(result.BothCompleted);
        Assert.False(result.IsMatch);
    }

    /// <summary>Returns a fixed, pre-built snapshot regardless of the fixture — for tests that need full control over termination/trace shape.</summary>
    private sealed class ScriptedExecutor : IRecompilerExecutor
    {
        private readonly RecompilerStateSnapshot _snapshot;

        public ScriptedExecutor(RecompilerStateSnapshot snapshot) => _snapshot = snapshot;

        public string Name => "scripted";

        public RecompilerExecutionResult Execute(RecompilerDifferentialFixture fixture) =>
            RecompilerExecutionResult.Completed(_snapshot);
    }

    private sealed class StubExecutor : IRecompilerExecutor
    {
        private readonly uint _gpr8Override;

        public StubExecutor(uint gpr8Override = 0)
            => _gpr8Override = gpr8Override;

        public string Name => _gpr8Override == 0 ? "stub-faithful" : "stub-corrupting";

        public RecompilerExecutionResult Execute(RecompilerDifferentialFixture fixture)
        {
            var gpr = new uint[32];
            gpr[0] = 0;
            gpr[8] = 5;
            gpr[9] = 7;
            gpr[11] = 12;
            if (_gpr8Override != 0) gpr[8] = _gpr8Override;
            var snapshot = new RecompilerStateSnapshot(
                gpr, hi: 0, lo: 0,
                pc: fixture.PcOfInstruction(fixture.Instructions.Count),
                termination: RecompilerIrTerminationReason.Success);
            return RecompilerExecutionResult.Completed(snapshot);
        }
    }

    private sealed class AlwaysFailingExecutor : IRecompilerExecutor
    {
        public string Name => "stub-failing";

        public RecompilerExecutionResult Execute(RecompilerDifferentialFixture fixture)
            => RecompilerExecutionResult.Failed(RecompilerExecutionStatus.GenerationFailed, "LOWER_FAILED", "boom");
    }
}
#pragma warning restore AARC003
