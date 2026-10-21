using PSXRecomp.Architecture;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Core.Execution;

/// <summary>
/// What the fallback's return check decided for one PC (Issue #806). The verdict is the predicate's own answer; the step
/// loop's pipeline and interrupt-handler checks are counted separately (see <see cref="FallbackReturnDiagnostics"/>).
/// </summary>
[Domain]
public enum FallbackReturnVerdict
{
    /// <summary>The PC has a static compiled block entry: returned to (<c>_blockEntryPcs</c>).</summary>
    AcceptedAotEntry,

    /// <summary>The PC has no static block, but a registered RAM-placed version whose words RAM holds now: returned to.</summary>
    AcceptedRamVersion,

    /// <summary>No static block entry and no RAM-placed version registered at the PC (<c>!loadedCode.Contains</c>).</summary>
    RefusedNoAotEntry,

    /// <summary>Versions are registered at the PC, but it is outside the RAM mapping (addresses fold into the 8 MiB mirror window), so no RAM word can be compared.</summary>
    RefusedPcNotRamMapped,

    /// <summary>Versions are registered and comparable, but RAM differs from every one of them at the PC.</summary>
    RefusedRamWordMismatch,

    /// <summary>Versions are registered at the PC, but none is comparable (empty, or longer than the RAM left at that PC).</summary>
    RefusedNoComparableVersion,
}

/// <summary>The return-point decision as the fallback session makes it (Issue #806), factored out so it can be tested without a session.</summary>
[Domain]
public static class FallbackReturnCheck
{
    /// <summary>
    /// The artifact's translation of a guest PC to a RAM offset (artifact_translate / artifact_ram_offset): KUSEG as is,
    /// KSEG0/1 masked, the low 8 MiB mirroring the 2 MiB RAM; false when the PC is not RAM (so there is nothing to compare).
    /// </summary>
    public static bool TryRamOffset(uint address, out uint physical)
    {
        physical = address <= 0x7FFFFFFFu ? address : address & 0x1FFFFFFFu;
        if (address >= 0xC0000000u || physical >= 0x00800000u) return false;
        physical &= PSXCoreWrapper.RamSize - 1;
        return true;
    }

    /// <summary>Whether a verdict lets the step loop return to the artifact (the predicate's answer).</summary>
    public static bool IsAccepted(FallbackReturnVerdict verdict) =>
        verdict is FallbackReturnVerdict.AcceptedAotEntry or FallbackReturnVerdict.AcceptedRamVersion;

    /// <summary>
    /// Decides one return point. The order and the comparisons are those of the session's return check (Issue #732): a
    /// static block entry first, then a RAM-placed version whose every word equals RAM at its physical offset.
    /// </summary>
    /// <param name="readRamWord">Reads the aligned word at a physical RAM offset (not a guest virtual address).</param>
    public static FallbackReturnVerdict Classify(
        uint pc, IReadOnlySet<uint> blockEntryPcs, LoadedCodeTable loadedCode, Func<uint, uint> readRamWord)
    {
        if (blockEntryPcs.Contains(pc)) return FallbackReturnVerdict.AcceptedAotEntry;
        if (!loadedCode.Contains(pc)) return FallbackReturnVerdict.RefusedNoAotEntry;
        if (!TryRamOffset(pc, out var physical)) return FallbackReturnVerdict.RefusedPcNotRamMapped;

        var compared = false;
        foreach (var version in loadedCode.VersionsAt(pc))
        {
            // A longer version must not hide a current shorter version at the same entry.
            if (version.Words.Count == 0 || (ulong)version.Words.Count * 4ul > PSXCoreWrapper.RamSize - physical) continue;
            compared = true;
            var current = true;
            for (var i = 0; i < version.Words.Count && current; i++)
            {
                current = readRamWord(physical + (uint)i * 4u) == version.Words[i];
            }

            if (current) return FallbackReturnVerdict.AcceptedRamVersion;
        }

        return compared ? FallbackReturnVerdict.RefusedRamWordMismatch : FallbackReturnVerdict.RefusedNoComparableVersion;
    }
}

/// <summary>
/// One PC of the most-frequent refused-check table (Issue #806). <see cref="Count"/> is the space-saving estimate: an upper
/// bound on the true refusal count. <see cref="Error"/> is the count inherited when the PC took over a slot (zero if it
/// never evicted anything), so the true count lies in [<see cref="LowerBound"/>, <see cref="Count"/>]. The bounds hold for a
/// quiescent snapshot; a snapshot read while the pump runs can pair a PC with a neighbouring slot's error (diagnostic only).
/// </summary>
[Domain]
public sealed record FallbackPcCount(uint Pc, ulong Count, ulong Error)
{
    /// <summary>The minimum refusal count for this PC (estimate minus error), exact only for a quiescent snapshot.</summary>
    public ulong LowerBound => Count > Error ? Count - Error : 0;
}

/// <summary>
/// The aggregate return classification of one run (Issue #806). Counts only, plus the longest segment's PCs and the most
/// frequently refused PCs. Each refusal bucket counts exactly one condition; see the members for the code each maps to.
/// </summary>
/// <param name="ReturnChecks">Predicate evaluations (a return point was asked about, at a step after the first, outside a handler).</param>
/// <param name="AotEntryHits">Evaluations accepted by a static compiled block entry.</param>
/// <param name="RamVersionHits">Evaluations accepted by a RAM-placed version whose words equal RAM.</param>
/// <param name="RefusedNoAotEntry">No static block entry and no RAM-placed version registered at the PC.</param>
/// <param name="RefusedPcNotRamMapped">Versions registered, but the PC has no RAM mapping to compare against.</param>
/// <param name="RefusedRamWordMismatch">Versions registered and comparable, none equal to RAM.</param>
/// <param name="RefusedNoComparableVersion">Versions registered, none comparable (empty or too long for the RAM left).</param>
/// <param name="RefusedPipelineNotClean">The predicate accepted the PC, but the CPU had a pending branch or load (not clean).</param>
/// <param name="StepsInInterruptHandler">Steps the guest's interrupt handler ran. Counted, not refused: no return check is made there, so the return condition is not evaluated.</param>
/// <param name="Segments">Fallback segments the run executed.</param>
/// <param name="SuccessfulReturns">Segments that returned to a compiled block.</param>
/// <param name="BudgetExhaustedSegments">Segments that retired their whole instruction budget without a return.</param>
/// <param name="LongestSegmentRetired">Instructions retired by the longest segment.</param>
/// <param name="LongestSegmentEntryPc">Where the longest segment started.</param>
/// <param name="LongestSegmentExitPc">Where the longest segment ended.</param>
/// <param name="LongestSegmentStatus">How the longest segment ended (a <see cref="FallbackSegmentStatus"/> name).</param>
/// <param name="TopRefusedPcs">Up to <see cref="FallbackReturnDiagnostics.TopN"/> PCs with the most refusals, most frequent first. Each carries its estimate, error bound and lower bound.</param>
[Domain]
public sealed record FallbackReturnStats(
    ulong ReturnChecks,
    ulong AotEntryHits,
    ulong RamVersionHits,
    ulong RefusedNoAotEntry,
    ulong RefusedPcNotRamMapped,
    ulong RefusedRamWordMismatch,
    ulong RefusedNoComparableVersion,
    ulong RefusedPipelineNotClean,
    ulong StepsInInterruptHandler,
    ulong Segments,
    ulong SuccessfulReturns,
    ulong BudgetExhaustedSegments,
    ulong LongestSegmentRetired,
    uint? LongestSegmentEntryPc,
    uint? LongestSegmentExitPc,
    string? LongestSegmentStatus,
    IReadOnlyList<FallbackPcCount> TopRefusedPcs);

/// <summary>
/// The mutable counters behind <see cref="FallbackReturnStats"/> (Issue #806), owned by one fallback session and written only
/// by the stepping thread. A host may read <see cref="Snapshot"/> while the pump owns the graph: the reads are unsynchronized,
/// so a snapshot taken mid-run may mix adjacent updates. That is acceptable for a diagnostic and it is why no lock is taken
/// on the step path.
/// </summary>
/// <remarks>
/// Cost per return check: one classification (already paid by the predicate), one counter increment, and for a refusal a
/// linear scan over at most <see cref="TopN"/> entries. Nothing is allocated per step; the top table is fixed-size.
/// </remarks>
[Domain]
public sealed class FallbackReturnDiagnostics
{
    /// <summary>Capacity of the most-frequent-refused-PC table.</summary>
    public const int TopN = 8;

    private readonly uint[] _topPcs = new uint[TopN];
    private readonly ulong[] _topCounts = new ulong[TopN];
    private readonly ulong[] _topErrors = new ulong[TopN];
    private int _topUsed;

    private ulong _returnChecks;
    private ulong _aotEntryHits;
    private ulong _ramVersionHits;
    private ulong _refusedNoAotEntry;
    private ulong _refusedPcNotRamMapped;
    private ulong _refusedRamWordMismatch;
    private ulong _refusedNoComparableVersion;
    private ulong _refusedPipelineNotClean;
    private ulong _stepsInInterruptHandler;
    private ulong _segments;
    private ulong _successfulReturns;
    private ulong _budgetExhaustedSegments;
    private ulong _longestRetired;
    private uint? _longestEntryPc;
    private uint? _longestExitPc;
    private string? _longestStatus;

    /// <summary>Records the verdict of one return check the predicate made.</summary>
    public void NoteVerdict(uint pc, FallbackReturnVerdict verdict)
    {
        _returnChecks++;
        switch (verdict)
        {
            case FallbackReturnVerdict.AcceptedAotEntry:
                _aotEntryHits++;
                return;
            case FallbackReturnVerdict.AcceptedRamVersion:
                _ramVersionHits++;
                return;
            case FallbackReturnVerdict.RefusedNoAotEntry:
                _refusedNoAotEntry++;
                break;
            case FallbackReturnVerdict.RefusedPcNotRamMapped:
                _refusedPcNotRamMapped++;
                break;
            case FallbackReturnVerdict.RefusedRamWordMismatch:
                _refusedRamWordMismatch++;
                break;
            case FallbackReturnVerdict.RefusedNoComparableVersion:
                _refusedNoComparableVersion++;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(verdict), verdict, null);
        }

        NoteRefusedPc(pc);
    }

    /// <summary>The predicate accepted <paramref name="pc"/>, but the CPU's pipeline was not clean, so the step loop went on.</summary>
    public void NotePipelineRefusal(uint pc)
    {
        _refusedPipelineNotClean++;
        NoteRefusedPc(pc);
    }

    /// <summary>A step ran while the guest's interrupt handler was active: no return check was made there.</summary>
    public void NoteStepInInterruptHandler() => _stepsInInterruptHandler++;

    /// <summary>Records how one fallback segment ended, and keeps the longest one's PCs.</summary>
    public void NoteSegment(uint entryPc, uint exitPc, ulong retired, FallbackSegmentStatus status)
    {
        _segments++;
        if (status == FallbackSegmentStatus.Returned) _successfulReturns++;
        if (status == FallbackSegmentStatus.BudgetExhausted) _budgetExhaustedSegments++;
        if (_longestEntryPc is null || retired > _longestRetired)
        {
            _longestRetired = retired;
            _longestEntryPc = entryPc;
            _longestExitPc = exitPc;
            _longestStatus = status.ToString();
        }
    }

    /// <summary>The counters as they stand now (see the class remarks on unsynchronized reads).</summary>
    public FallbackReturnStats Snapshot()
    {
        var top = new List<FallbackPcCount>(_topUsed);
        for (var i = 0; i < _topUsed; i++)
        {
            // The error is read first: eviction writes the error before the count, so a snapshot taken mid-eviction never
            // sees an error above its count (Count - Error would wrap). LowerBound also saturates as a second guard.
            var error = _topErrors[i];
            var count = _topCounts[i];
            top.Add(new FallbackPcCount(_topPcs[i], count, error));
        }

        top.Sort(static (a, b) => a.Count != b.Count ? b.Count.CompareTo(a.Count) : a.Pc.CompareTo(b.Pc));
        return new FallbackReturnStats(
            _returnChecks, _aotEntryHits, _ramVersionHits, _refusedNoAotEntry, _refusedPcNotRamMapped,
            _refusedRamWordMismatch, _refusedNoComparableVersion, _refusedPipelineNotClean, _stepsInInterruptHandler,
            _segments, _successfulReturns, _budgetExhaustedSegments, _longestRetired,
            _longestEntryPc, _longestExitPc, _longestStatus, top);
    }

    /// <summary>
    /// Space-saving frequency table (Metwally et al.): a known PC is incremented; an unknown PC takes a free slot or replaces
    /// the least frequent entry and inherits its count plus one, recording the inherited count as its error. Counts are
    /// therefore upper bounds, and count minus error is a guaranteed lower bound. O(TopN) per call, no allocation.
    /// </summary>
    private void NoteRefusedPc(uint pc)
    {
        for (var i = 0; i < _topUsed; i++)
        {
            if (_topPcs[i] != pc) continue;
            _topCounts[i]++;
            return;
        }

        if (_topUsed < TopN)
        {
            _topPcs[_topUsed] = pc;
            _topCounts[_topUsed] = 1;
            _topErrors[_topUsed] = 0;
            _topUsed++;
            return;
        }

        var least = 0;
        for (var i = 1; i < TopN; i++)
        {
            if (_topCounts[i] < _topCounts[least]) least = i;
        }

        // The new PC inherits the evicted minimum: that inherited count is its error bound.
        var inherited = _topCounts[least];
        _topPcs[least] = pc;
        _topErrors[least] = inherited;
        _topCounts[least] = inherited + 1;
    }
}
