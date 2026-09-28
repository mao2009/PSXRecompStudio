using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Core.Recompiler;

/// <summary>
/// The result of running one fixture through both executors and comparing their
/// state snapshots.
/// </summary>
[Domain]
public sealed record RecompilerDifferentialResult(
    RecompilerDifferentialFixture Fixture,
    RecompilerExecutionResult Reference,
    RecompilerExecutionResult Actual,
    RecompilerStateDiffResult? Diff)
{
    /// <summary>True when both executors completed and a state comparison exists.</summary>
    public bool BothCompleted =>
        Reference.Status == RecompilerExecutionStatus.Completed &&
        Actual.Status == RecompilerExecutionStatus.Completed &&
        Diff is not null;

    /// <summary>True when both completed and the state snapshots match.</summary>
    public bool IsMatch => BothCompleted && Diff!.IsMatch;

    /// <summary>
    /// True when both completed and the comparison was inconclusive: the executors
    /// agree up to the bounded budget cut and differ only on the fields the cut
    /// leaves mid-iteration, so neither a match nor a real divergence was proven
    /// (Issue #304).
    /// </summary>
    public bool IsBudgetInconclusive => BothCompleted && Diff!.IsBudgetInconclusive;
}

/// <summary>
/// Orchestrates a single differential run: executes the fixture on the reference
/// (interpreter) executor and the actual (recompiled) executor, then compares
/// their state snapshots. Pure orchestration — it does not itself perform
/// host compiles, file I/O or process control.
/// </summary>
[Domain]
public static class RecompilerDifferentialRunner
{
    public static RecompilerDifferentialResult Run(
        RecompilerDifferentialFixture fixture,
        IRecompilerExecutor reference,
        IRecompilerExecutor actual)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(actual);

        var referenceResult = reference.Execute(fixture);
        var actualResult = actual.Execute(fixture);

        // The runner is the one caller that can supply both facts a pair of
        // snapshots alone cannot prove (CodeRabbit findings on #305): it reads the
        // fixture's own author-asserted BudgetsAreShared fact — StepBudget (host
        // blocks) and ReferenceStepBudget (guest instructions) count different
        // units, so equal numbers alone never prove the same work counter — and it
        // rebuilds the lowered program's authoritative static block-entry PCs.
        RecompilerStateDiffResult? diff = referenceResult.Snapshot is not null && actualResult.Snapshot is not null
            ? RecompilerStateDiff.Compare(
                referenceResult.Snapshot,
                actualResult.Snapshot,
                budgetsAreShared: fixture.BudgetsAreShared,
                staticBlockEntryPcs: StaticBlockEntryPcs(fixture))
            : null;

        return new RecompilerDifferentialResult(fixture, referenceResult, actualResult, diff);
    }

    /// <summary>
    /// Runs a fixture with reference-first execution-window alignment (Issue #578).
    /// <see cref="RecompilerDifferentialFixture.StepBudget"/> counts retired host
    /// blocks and <see cref="RecompilerDifferentialFixture.ReferenceStepBudget"/>
    /// counts retired guest instructions; a control-transfer instruction fused with
    /// its delay slot retires one host block per two guest instructions, so an
    /// independently-chosen numeric <c>StepBudget</c> only happens to cover the same
    /// real execution window for straight-line code. A real-ROM function with a loop
    /// can accumulate a real gap between the two counts (Persona: 204 guest
    /// instructions projected to 186 comparable host blocks), which makes the host run
    /// further than the interpreter under an equal numeric budget — a false MISMATCH,
    /// not a lowering divergence.
    /// <para>
    /// This method runs the reference (interpreter) first under its own
    /// <c>ReferenceStepBudget</c>, projects its retired PC trace onto the lowered
    /// program's authoritative static block-entry PCs, and uses the count of that
    /// projection as the host's <c>StepBudget</c> for the actual (recompiled) run —
    /// the same real guest-instruction window, expressed in the host's own retirement
    /// unit. Because the aligned budget is <em>measured</em> from the reference's own
    /// trace rather than chosen, this is the one caller allowed to derive
    /// <see cref="RecompilerDifferentialFixture.BudgetsAreShared"/> as <c>true</c> for
    /// a fixture it did not build by hand — it has actually proven the alignment
    /// <see cref="RecompilerDifferentialFixture.BudgetsAreShared"/>'s own contract
    /// demands, rather than inferring it from <c>StepBudget == ReferenceStepBudget</c>.
    /// </para>
    /// <para>
    /// This changes nothing about how a genuine divergence is classified:
    /// <see cref="RecompilerStateDiff.Compare"/> runs exactly as it does for
    /// <see cref="Run"/>, so checkpoint ordering, skipped static blocks, and any
    /// behavioral-field difference (memory/HI/LO/exception) still surface as a hard
    /// <see cref="RecompilerComparisonClassification.Mismatch"/>.
    /// </para>
    /// </summary>
    public static RecompilerDifferentialResult RunReferenceFirstAligned(
        RecompilerDifferentialFixture fixture,
        IRecompilerExecutor reference,
        IRecompilerExecutor actual)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(actual);

        var referenceResult = reference.Execute(fixture);
        var staticBlockEntryPcs = StaticBlockEntryPcs(fixture);

        var alignedFixture = fixture;
        if (referenceResult.Snapshot is not null)
        {
            var projectedBlockCount = ProjectedRetiredBlockCount(referenceResult.Snapshot.PcTrace, staticBlockEntryPcs);
            if (projectedBlockCount > 0)
            {
                alignedFixture = fixture.WithStepBudget((uint)projectedBlockCount, budgetsAreShared: true);
            }
        }

        var actualResult = actual.Execute(alignedFixture);

        RecompilerStateDiffResult? diff = referenceResult.Snapshot is not null && actualResult.Snapshot is not null
            ? RecompilerStateDiff.Compare(
                referenceResult.Snapshot,
                actualResult.Snapshot,
                budgetsAreShared: alignedFixture.BudgetsAreShared,
                staticBlockEntryPcs: staticBlockEntryPcs)
            : null;

        return new RecompilerDifferentialResult(alignedFixture, referenceResult, actualResult, diff);
    }

    /// <summary>
    /// The number of <paramref name="pcTrace"/> entries that land on a static
    /// block-entry PC — the projection <see cref="RunReferenceFirstAligned"/> uses to
    /// convert a guest-instruction execution window into the host's own retired-block
    /// unit (Issue #578).
    /// </summary>
    private static int ProjectedRetiredBlockCount(IReadOnlyList<uint> pcTrace, IReadOnlySet<uint> staticBlockEntryPcs)
    {
        var count = 0;
        foreach (var pc in pcTrace)
        {
            if (staticBlockEntryPcs.Contains(pc)) count++;
        }
        return count;
    }

    /// <summary>
    /// The lowered program's static block-entry PCs for the fixture's instructions —
    /// the authoritative projection target for <see cref="RecompilerStateDiff"/>'s
    /// budget-tail check, independent of whichever PCs a given host run happened to
    /// observe.
    /// </summary>
    private static IReadOnlySet<uint> StaticBlockEntryPcs(RecompilerDifferentialFixture fixture)
    {
        var instructions = new List<(R3000aInstruction Instruction, uint EntryPc)>(fixture.Instructions.Count);
        for (var i = 0; i < fixture.Instructions.Count; i++)
        {
            instructions.Add((R3000aDecoder.Decode(fixture.Instructions[i]), fixture.PcOfInstruction(i)));
        }

        var program = MipsToIrLowerer.LowerProgram(instructions);
        return program.Blocks.Select(block => block.EntryPc).ToHashSet();
    }
}
