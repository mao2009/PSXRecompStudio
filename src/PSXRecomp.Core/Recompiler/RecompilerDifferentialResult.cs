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
                staticBlockEntryPcs: BlockGuestInstructionWidths(fixture).Keys.ToHashSet())
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
        var blockWidths = BlockGuestInstructionWidths(fixture);
        var staticBlockEntryPcs = blockWidths.Keys.ToHashSet();

        var alignedFixture = fixture;
        if (referenceResult.Snapshot is not null)
        {
            var (projectedBlockCount, endsMidFusedBlock) =
                ProjectedRetiredBlockCount(referenceResult.Snapshot.PcTrace, blockWidths);

            // A trace that stops after retiring a fused block's control-transfer but
            // before its delay slot (Issue #578 / CodeRabbit) has no host-side
            // equivalent stopping point: the host always retires a fused block
            // atomically, so no derived StepBudget can reproduce that exact window.
            // Fall back to the fixture unchanged, exactly as when nothing projects at
            // all — asserting BudgetsAreShared here would compare two genuinely
            // different execution windows under a false proof of alignment.
            if (projectedBlockCount > 0 && !endsMidFusedBlock)
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
    /// Walks <paramref name="pcTrace"/> against each block's known guest-instruction
    /// width (<paramref name="blockGuestInstructionWidths"/>) and counts only the
    /// blocks the trace retires <em>completely</em> — every one of a fused block's
    /// instructions (a control transfer and its delay slot, or a fused load-delay
    /// pair/triple) present in order, not merely its entry PC. A trace entry that is
    /// not a known block entry where one is expected (for example a BIOS vector PC
    /// outside the fixture's own lowered program) is skipped rather than treated as
    /// ending the walk, matching the original entry-only projection for that case.
    /// <paramref name="endsMidFusedBlock"/> reports whether the trace stopped after
    /// entering a block but before retiring all of its instructions — the shape
    /// Issue #578 (CodeRabbit) reports: the reference stops between a fused block's
    /// control transfer and its delay slot, and that block must not be counted as a
    /// retired unit the host can be given credit for.
    /// </summary>
    private static (int Count, bool EndsMidFusedBlock) ProjectedRetiredBlockCount(
        IReadOnlyList<uint> pcTrace, IReadOnlyDictionary<uint, int> blockGuestInstructionWidths)
    {
        var count = 0;
        var remainingInBlock = 0;
        foreach (var pc in pcTrace)
        {
            if (remainingInBlock == 0)
            {
                if (!blockGuestInstructionWidths.TryGetValue(pc, out var width))
                {
                    continue;
                }

                remainingInBlock = width;
            }

            remainingInBlock--;
            if (remainingInBlock == 0) count++;
        }

        return (count, remainingInBlock > 0);
    }

    /// <summary>
    /// The lowered program's static block-entry PCs mapped to each block's width in
    /// guest instructions (1 for a straight-line instruction, 2 for a control
    /// transfer fused with its delay slot, 2 or 3 for a fused load-delay pair —
    /// see <see cref="MipsToIrLowerer.LowerProgram"/>) — the authoritative
    /// projection target for <see cref="RecompilerStateDiff"/>'s budget-tail check
    /// and for <see cref="ProjectedRetiredBlockCount"/>, independent of whichever PCs
    /// a given host run happened to observe. Blocks are contiguous and ordered by
    /// entry PC, so each block's width is the guest-address distance to the next
    /// block's entry PC (or to the end of the fixture's program for the last block).
    /// </summary>
    private static IReadOnlyDictionary<uint, int> BlockGuestInstructionWidths(RecompilerDifferentialFixture fixture)
    {
        var instructions = new List<(R3000aInstruction Instruction, uint EntryPc)>(fixture.Instructions.Count);
        for (var i = 0; i < fixture.Instructions.Count; i++)
        {
            instructions.Add((R3000aDecoder.Decode(fixture.Instructions[i]), fixture.PcOfInstruction(i)));
        }

        var program = MipsToIrLowerer.LowerProgram(instructions);
        var entryPcs = program.Blocks.Select(block => block.EntryPc).ToArray();
        var programEndPc = fixture.PcOfInstruction(fixture.Instructions.Count);

        var widths = new Dictionary<uint, int>(entryPcs.Length);
        for (var i = 0; i < entryPcs.Length; i++)
        {
            var nextEntryPc = i + 1 < entryPcs.Length ? entryPcs[i + 1] : programEndPc;
            widths[entryPcs[i]] = (int)((nextEntryPc - entryPcs[i]) / 4);
        }

        return widths;
    }
}
