using System.Collections.ObjectModel;
using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Recompiler;

/// <summary>
/// Whether two state snapshots agree. A <see cref="Match"/> means the states are
/// identical; a <see cref="Mismatch"/> means they disagree on a field that the
/// differential harness must treat as a real divergence. <see cref="BudgetInconclusive"/>
/// is the narrow middle case introduced for Issue #304: both executors cut off
/// mid-loop by the same artificial bounded budget, agree on every comparable
/// checkpoint/state prefix before the cut, and differ only on the fields that the
/// budget cut itself leaves parked mid-iteration.
/// </summary>
[Domain]
public enum RecompilerComparisonClassification : byte
{
    Match,
    Mismatch,
    BudgetInconclusive,
}

/// <summary>A single differing state field (register, HI/LO, PC, termination, ...).</summary>
[Domain]
public sealed record RecompilerStateDifference(
    string FieldPath,
    string ExpectedText,
    string ActualText);

/// <summary>The ordered result of comparing a reference (interpreter) snapshot with an actual (recompiled) snapshot.</summary>
[Domain]
public sealed record RecompilerStateDiffResult(
    RecompilerComparisonClassification Classification,
    IReadOnlyList<RecompilerStateDifference> Differences)
{
    public bool IsMatch => Classification == RecompilerComparisonClassification.Match;

    /// <summary>
    /// True when the run is neither a clean match nor a real divergence: both
    /// sides agree up to the artificial budget cut and differ only on the fields
    /// the cut leaves mid-iteration (Issue #304).
    /// </summary>
    public bool IsBudgetInconclusive => Classification == RecompilerComparisonClassification.BudgetInconclusive;

    /// <summary>Human-readable single-line-per-difference description.</summary>
    public string Describe()
    {
        if (IsMatch)
        {
            return "MATCH: interpreter and recompiled states are identical.";
        }

        if (IsBudgetInconclusive)
        {
            var sb = new StringBuilder();
            sb.Append("BUDGET INCONCLUSIVE: both executors exhausted the bounded budget with ").Append(Differences.Count)
              .Append(" non-behavioral difference(s) confined to the budget cut:");
            foreach (var d in Differences)
            {
                sb.AppendLine()
                  .Append("  - ").Append(d.FieldPath)
                  .Append(": interpreter=").Append(d.ExpectedText)
                  .Append(" recompiled=").Append(d.ActualText);
            }
            return sb.ToString();
        }

        var sb2 = new StringBuilder();
        sb2.Append("MISMATCH: ").Append(Differences.Count).Append(" difference(s):");
        foreach (var d in Differences)
        {
            sb2.AppendLine()
               .Append("  - ").Append(d.FieldPath)
               .Append(": interpreter=").Append(d.ExpectedText)
               .Append(" recompiled=").Append(d.ActualText);
        }
        return sb2.ToString();
    }

    /// <summary>Stable, deterministic machine-readable one-line-per-difference form.</summary>
    public string ToMachineReadable()
    {
        var sb = new StringBuilder();
        sb.Append("classification=").Append(
            IsMatch ? "MATCH" : IsBudgetInconclusive ? "BUDGET_INCONCLUSIVE" : "MISMATCH");
        foreach (var d in Differences)
        {
            sb.AppendLine()
              .Append("diff ").Append(d.FieldPath)
              .Append(" expected=").Append(d.ExpectedText)
              .Append(" actual=").Append(d.ActualText);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Compares two state snapshots field by field (GPR, HI, LO, PC, termination,
/// load-delay, exception) so a differential run can localize the first (and all)
/// diverging fields.
/// </summary>
[Domain]
public static class RecompilerStateDiff
{
    /// <summary>Compares the interpreter/reference snapshot against the recompiled/actual snapshot.</summary>
    public static RecompilerStateDiffResult Compare(
        RecompilerStateSnapshot reference,
        RecompilerStateSnapshot actual)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(actual);

        var diffs = new List<RecompilerStateDifference>();

        for (var i = 0; i < 32; i++)
        {
            AddGpr(diffs, reference.Gpr[i], actual.Gpr[i], i);
        }

        Add("hi", reference.HI, actual.HI, diffs);
        Add("lo", reference.LO, actual.LO, diffs);
        Add("pc", reference.PC, actual.PC, diffs);
        Add("termination", (byte)reference.Termination, (byte)actual.Termination, diffs);
        AddLoadDelay(diffs, reference.LoadDelay, actual.LoadDelay);
        AddException(diffs, reference.Exception, actual.Exception);
        AddMemory(diffs, reference.Memory, actual.Memory);
        AddCheckpointTrace(diffs, reference.PcTrace, actual.PcTrace);

        var classification = diffs.Count == 0
            ? RecompilerComparisonClassification.Match
            : IsBudgetInconclusive(reference, actual, diffs)
                ? RecompilerComparisonClassification.BudgetInconclusive
                : RecompilerComparisonClassification.Mismatch;
        return new RecompilerStateDiffResult(classification, new ReadOnlyCollection<RecompilerStateDifference>(diffs));
    }

    /// <summary>
    /// True when the two snapshots differ only in the way an artificial bounded
    /// budget cut mid-loop leaves them parked (Issue #304). Classifies the state
    /// as inconclusive — not a match, but neither a real lowering divergence —
    /// only when all of these hold:
    /// <list type="number">
    /// <item>Both executors exhausted the same bounded budget
    /// (<see cref="RecompilerIrTerminationReason.ExecutionBudgetExceeded"/>). A run
    /// that completed, or that stopped for any other reason, is never inconclusive.</item>
    /// <item>Checkpoint divergence is purely tail-only: either the checkpoint traces
    /// agree entirely, or the only checkpoint difference is the host having run past
    /// the end of the interpreter trace (that tail marker — the host trace being a
    /// proper extension of the interpreter's — is a loop continuation, not a new
    /// code path). This guarantees every comparable checkpoint/state prefix before
    /// the cut agrees, so no real divergence precedes the cut.</item>
    /// <item>The residual differences are confined to <c>pc</c> and <c>gpr[i]</c>,
    /// the natural variables of a mid-loop cut. Any difference in
    /// <c>hi</c>/<c>lo</c>/<c>memory.*</c>/<c>loadDelay.*</c>/<c>exception.*</c> is a
    /// behavioral divergence and must remain a hard mismatch.</item>
    /// <item>Both traces are non-empty (guards the degenerate empty-trace case).</item>
    /// </list>
    /// </summary>
    private static bool IsBudgetInconclusive(
        RecompilerStateSnapshot reference,
        RecompilerStateSnapshot actual,
        IReadOnlyList<RecompilerStateDifference> diffs)
    {
        // (1) both executors must have been cut off by the same bounded budget.
        if (reference.Termination != RecompilerIrTerminationReason.ExecutionBudgetExceeded ||
            actual.Termination != RecompilerIrTerminationReason.ExecutionBudgetExceeded)
        {
            return false;
        }

        // (4) both traces must be non-empty so prefix preservation is meaningful.
        if (reference.PcTrace.Count == 0 || actual.PcTrace.Count == 0)
        {
            return false;
        }

        // (3) + (4): the residual differences must be confined to the fields the
        // budget cut leaves mid-iteration. Any behavioral-field difference — or any
        // diff outside pc/gpr and the checkpoint tail bookkeeping — means the two
        // executors are not behaviorally equivalent and must stay a mismatch.
        // <see cref="AddCheckpointTrace"/> reports checkpoint divergence as a
        // checkpoint[i] tail marker plus its checkpoint summary line.
        var hasTailMarker = false;
        foreach (var d in diffs)
        {
            if (IsBehavioralField(d.FieldPath)) return false;

            if (d.FieldPath.StartsWith("checkpoint[", StringComparison.Ordinal))
            {
                hasTailMarker = true;
            }
            else if (d.FieldPath == "checkpoint")
            {
                // The summary line is acceptable only accompanying a tail marker; a
                // summary without one would hide a real positional divergence.
                if (!hasTailMarker) return false;
            }
            else if (d.FieldPath != "pc" && !d.FieldPath.StartsWith("gpr[", StringComparison.Ordinal))
            {
                return false;
            }
        }

        // (2) checkpoint divergence must be purely tail-only. When no checkpoint
        // diff exists, the host trace is a valid ordered subsequence of the
        // interpreter trace (the interpreter ran at least as far) — every
        // comparable prefix agrees, so the cut, not a divergence, explains the
        // difference. When a tail marker exists the host ran past the interpreter
        // trace; that is only benign if the host never executed a PC the
        // interpreter never visited — i.e. it only revisited loop-body locations
        // (a loop continuation), not a brand-new code path. Any host-only PC is a
        // real divergence before or at the cut and must stay a mismatch.
        if (hasTailMarker)
        {
            var interpreterPcs = new HashSet<uint>(reference.PcTrace);
            foreach (var hostPc in actual.PcTrace)
            {
                if (!interpreterPcs.Contains(hostPc)) return false;
            }
        }

        return true;
    }

    private static bool IsBehavioralField(string fieldPath) =>
        fieldPath == "hi" ||
        fieldPath == "lo" ||
        fieldPath == "termination" ||
        fieldPath.StartsWith("memory", StringComparison.Ordinal) ||
        fieldPath.StartsWith("loadDelay", StringComparison.Ordinal) ||
        fieldPath.StartsWith("exception", StringComparison.Ordinal);

    private static void AddGpr(List<RecompilerStateDifference> diffs, uint expected, uint actual, int index)
    {
        if (expected == actual) return;
        diffs.Add(new RecompilerStateDifference(
            $"gpr[{index}]",
            FormatUint(expected),
            FormatUint(actual)));
    }

    private static void Add(string field, uint expected, uint actual, List<RecompilerStateDifference> diffs)
    {
        if (expected == actual) return;
        diffs.Add(new RecompilerStateDifference(field, FormatUint(expected), FormatUint(actual)));
    }

    private static void AddLoadDelay(List<RecompilerStateDifference> diffs, RecompilerLoadDelayState expected, RecompilerLoadDelayState actual)
    {
        if (expected.IsPending != actual.IsPending)
        {
            diffs.Add(new RecompilerStateDifference("loadDelay.isPending", $"{expected.IsPending}", $"{actual.IsPending}"));
        }
        if (expected.TargetRegister != actual.TargetRegister)
        {
            diffs.Add(new RecompilerStateDifference("loadDelay.targetRegister", $"{expected.TargetRegister}", $"{actual.TargetRegister}"));
        }
        if (expected.Value != actual.Value)
        {
            diffs.Add(new RecompilerStateDifference("loadDelay.value", FormatUint(expected.Value), FormatUint(actual.Value)));
        }
    }

    private static void AddException(List<RecompilerStateDifference> diffs, RecompilerExceptionState expected, RecompilerExceptionState actual)
    {
        if (expected.IsRaised != actual.IsRaised)
        {
            diffs.Add(new RecompilerStateDifference("exception.isRaised", $"{expected.IsRaised}", $"{actual.IsRaised}"));
        }
        if (expected.Code != actual.Code)
        {
            diffs.Add(new RecompilerStateDifference("exception.code", FormatUint(expected.Code), FormatUint(actual.Code)));
        }
        if (expected.FaultPc != actual.FaultPc)
        {
            diffs.Add(new RecompilerStateDifference("exception.faultPc", FormatUint(expected.FaultPc), FormatUint(actual.FaultPc)));
        }
        if (expected.InDelaySlot != actual.InDelaySlot)
        {
            diffs.Add(new RecompilerStateDifference("exception.inDelaySlot", $"{expected.InDelaySlot}", $"{actual.InDelaySlot}"));
        }
    }

    /// <summary>
    /// Compares the ordered memory-observation lists (the fixture's memory window,
    /// sampled after execution by both executors). Positional comparison keeps the
    /// diff deterministic: both sides sample the same addresses in the same order.
    /// </summary>
    private static void AddMemory(
        List<RecompilerStateDifference> diffs,
        IReadOnlyList<RecompilerMemoryObservation> expected,
        IReadOnlyList<RecompilerMemoryObservation> actual)
    {
        if (expected.Count != actual.Count)
        {
            diffs.Add(new RecompilerStateDifference(
                "memory.count",
                $"{expected.Count}",
                $"{actual.Count}"));
            return;
        }

        for (var i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var a = actual[i];
            var field = $"memory[{FormatUint(e.Address)}]";
            if (e.Value != a.Value)
            {
                diffs.Add(new RecompilerStateDifference(
                    field, FormatUint(e.Value), FormatUint(a.Value)));
            }
            else if (e.Address != a.Address || e.Width != a.Width || e.Access != a.Access)
            {
                diffs.Add(new RecompilerStateDifference(
                    field,
                    $"{FormatUint(e.Address)} {e.Width} {e.Access}",
                    $"{FormatUint(a.Address)} {a.Width} {a.Access}"));
            }
        }
    }

    /// <summary>
    /// Compares the block boundary trace of the recompiled host (
    /// <paramref name="actual"/>) against the instruction trace of the interpreter
    /// (<paramref name="expected"/>). A host block always retires one or more whole
    /// instructions — the interpreter retires each of those at their own PCs — so a
    /// matching execution means every host block-entry PC appears in the interpreter
    /// trace in the same order: the host trace is an ordered subsequence of the
    /// reference trace. Both traces empty (default snapshots) contributes nothing.
    /// </summary>
    private static void AddCheckpointTrace(
        List<RecompilerStateDifference> diffs,
        IReadOnlyList<uint> expected,
        IReadOnlyList<uint> actual)
    {
        if (expected.Count == 0 || actual.Count == 0) return;

        var referenceIndex = 0;
        for (var i = 0; i < actual.Count; i++)
        {
            var matched = false;
            while (referenceIndex < expected.Count)
            {
                if (expected[referenceIndex] == actual[i])
                {
                    matched = true;
                    referenceIndex++;
                    break;
                }
                referenceIndex++;
            }

            if (matched) continue;

            // First (and only) diverging checkpoint: the host retired a block whose
            // entry PC the interpreter trace never reached at the matching position.
            var expectedText = referenceIndex < expected.Count
                ? FormatUint(expected[referenceIndex])
                : "(end of interpreter trace)";
            diffs.Add(new RecompilerStateDifference(
                $"checkpoint[{i}]",
                expectedText,
                FormatUint(actual[i])));
            diffs.Add(new RecompilerStateDifference(
                "checkpoint",
                $"{expected.Count} interpreter PCs, {actual.Count} host blocks",
                $"diverged after {i} matching checkpoint(s)"));
            return;
        }
    }

    private static string FormatUint(uint value) => "0x" + value.ToString("X8");
}
