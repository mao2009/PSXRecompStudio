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

        // The runner is the one caller that actually knows whether the two
        // executors ran under the same budget, and can rebuild the lowered
        // program's authoritative static block-entry PCs — a pair of snapshots
        // alone cannot prove either fact (CodeRabbit findings on #305).
        RecompilerStateDiffResult? diff = referenceResult.Snapshot is not null && actualResult.Snapshot is not null
            ? RecompilerStateDiff.Compare(
                referenceResult.Snapshot,
                actualResult.Snapshot,
                budgetsAreShared: fixture.StepBudget == fixture.ReferenceStepBudget,
                staticBlockEntryPcs: StaticBlockEntryPcs(fixture))
            : null;

        return new RecompilerDifferentialResult(fixture, referenceResult, actualResult, diff);
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
