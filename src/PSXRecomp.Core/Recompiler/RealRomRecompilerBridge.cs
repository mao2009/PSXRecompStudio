using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.DiscImage.AnalysisArtifacts;

namespace PSXRecomp.Core.Recompiler;

/// <summary>
/// Why <see cref="RealRomCandidateSelector"/> stopped growing a candidate window.
/// Every reason is a deliberate, recorded boundary — never a silently swallowed gap
/// (Issue #225: no implicit workaround for an unsupported dependency).
/// </summary>
[Domain]
public enum RealRomCandidateStopReason : byte
{
    /// <summary>The next instruction is JR/JALR: an indirect jump, excluded by policy.</summary>
    IndirectJumpExcluded,

    /// <summary>The next instruction (or its delay slot) does not lower under the current Recompiler contract.</summary>
    UnsupportedInstruction,

    /// <summary>A control-transfer instruction's delay slot is missing or not contiguous.</summary>
    MissingDelaySlot,

    /// <summary>The next decoded instruction is not at <c>address + 4</c> (a decode gap).</summary>
    NonContiguousInstructions,

    /// <summary>The configured maximum window was reached before any other stop condition.</summary>
    MaxWindowReached,

    /// <summary>The decoded instruction stream ended before any other stop condition.</summary>
    EndOfDecodedInstructions,

    /// <summary>A JAL's static call target lands outside the candidate window: an external call dependency, excluded rather than silently treated as a program exit.</summary>
    ExternalCallExcluded,

    /// <summary>A BEQ/BNE/J's static target lands outside the candidate window, breaking the window's self-containment.</summary>
    StaticTargetOutsideCandidate,
}

/// <summary>
/// A bounded, contiguous run of real-ROM instructions that the existing Recompiler
/// contract (#206/#207) accepts unmodified: every instruction lowers under
/// <see cref="MipsToIrLowerer"/>, no indirect jump (JR/JALR) is included, and every
/// BEQ/BNE/J/JAL static target stays inside the window (see
/// <see cref="RealRomCandidateSelector.HasExternalStaticControlFlowTarget"/>). This is
/// the machine-readable candidate-selection record Issue #225 asks for.
/// </summary>
[Domain]
public sealed record RealRomFunctionCandidate
{
    public required uint StartAddress { get; init; }
    public required IReadOnlyList<uint> EncodedInstructions { get; init; }
    public required RealRomCandidateStopReason StopReason { get; init; }
    public required uint? StopAddress { get; init; }
    public required string StopDetail { get; init; }

    /// <summary>Distinct decoded opcode names in the window, sorted — the "required instruction subset".</summary>
    public required IReadOnlyList<string> RequiredInstructionSubset { get; init; }

    public int InstructionCount => EncodedInstructions.Count;
}

/// <summary>
/// Selects a bounded real-ROM instruction window that the existing Recompiler IR
/// contract already supports, by actually attempting to lower it
/// (<see cref="MipsToIrLowerer.LowerProgram"/>) rather than re-deriving a parallel
/// notion of "supported instruction" — the selection reuses the one true contract
/// instead of risking drift from it.
/// <para>
/// This is deliberately independent of any specific title: it operates on the
/// existing <see cref="DiscImageAnalysisReport"/> projection (#210/#212/#213) and
/// contains no title-specific logic or data.
/// </para>
/// </summary>
[Domain]
public static class RealRomCandidateSelector
{
    /// <summary>
    /// ponytail: a fixed cap on how many source instructions one candidate window may
    /// span. Large enough for a real subroutine, small enough that selection over a
    /// whole executable stays fast. Raise if a real candidate is found to need more.
    /// </summary>
    public const int DefaultMaxWindowInstructions = 512;

    /// <summary>
    /// Grows a candidate window forward from <paramref name="startAddress"/> for as
    /// long as the accumulated instructions keep lowering successfully and no
    /// indirect jump is encountered, then trims it so every BEQ/BNE/J/JAL static
    /// target it contains stays inside the window (see
    /// <see cref="HasExternalStaticControlFlowTarget"/>) — a self-contained window is
    /// the only thing #225 can call "no unresolved dependency" without overclaiming.
    /// Returns <see langword="null"/> only when <paramref name="startAddress"/> is not
    /// present in <paramref name="orderedInstructions"/>.
    /// </summary>
    public static RealRomFunctionCandidate? TryExtend(
        IReadOnlyList<DecodedInstruction> orderedInstructions,
        uint startAddress,
        int maxWindowInstructions = DefaultMaxWindowInstructions)
    {
        ArgumentNullException.ThrowIfNull(orderedInstructions);
        if (maxWindowInstructions <= 0) throw new ArgumentOutOfRangeException(nameof(maxWindowInstructions));

        var startIndex = IndexOfAddress(orderedInstructions, startAddress);
        if (startIndex < 0)
        {
            return null;
        }

        var accepted = new List<(R3000aInstruction Instruction, uint EntryPc)>();
        var acceptedWords = new List<uint>();
        var i = startIndex;

        RealRomCandidateStopReason reason;
        uint? stopAddress;
        string detail;

        while (true)
        {
            if (accepted.Count >= maxWindowInstructions)
            {
                reason = RealRomCandidateStopReason.MaxWindowReached;
                stopAddress = null;
                detail = "Maximum candidate window reached.";
                break;
            }

            if (i >= orderedInstructions.Count)
            {
                reason = RealRomCandidateStopReason.EndOfDecodedInstructions;
                stopAddress = null;
                detail = "No more decoded instructions.";
                break;
            }

            var current = orderedInstructions[i];
            if (accepted.Count > 0 && current.Address != accepted[^1].EntryPc + 4)
            {
                reason = RealRomCandidateStopReason.NonContiguousInstructions;
                stopAddress = current.Address;
                detail = $"Decoded instructions are not contiguous at 0x{current.Address:X8}.";
                break;
            }

            var decoded = R3000aDecoder.Decode(current.RawWord);
            if (decoded.Opcode is R3000aOpcode.Jr or R3000aOpcode.Jalr)
            {
                reason = RealRomCandidateStopReason.IndirectJumpExcluded;
                stopAddress = current.Address;
                detail = $"Indirect jump '{decoded.Opcode}' at 0x{current.Address:X8} is excluded by the #225 candidate policy.";
                break;
            }

            var candidate = new List<(R3000aInstruction, uint)>(accepted) { (decoded, current.Address) };
            var candidateWords = new List<uint>(acceptedWords) { current.RawWord };
            var consumed = 1;

            if (decoded.DelaySlot != R3000aDelaySlotKind.None)
            {
                if (i + 1 >= orderedInstructions.Count || orderedInstructions[i + 1].Address != current.Address + 4)
                {
                    reason = RealRomCandidateStopReason.MissingDelaySlot;
                    stopAddress = current.Address;
                    detail = $"'{decoded.Opcode}' at 0x{current.Address:X8} has no contiguous delay-slot instruction.";
                    break;
                }

                var delaySlotEntry = orderedInstructions[i + 1];
                var delaySlotDecoded = R3000aDecoder.Decode(delaySlotEntry.RawWord);
                if (delaySlotDecoded.Opcode is R3000aOpcode.Jr or R3000aOpcode.Jalr)
                {
                    reason = RealRomCandidateStopReason.IndirectJumpExcluded;
                    stopAddress = delaySlotEntry.Address;
                    detail = $"Indirect jump '{delaySlotDecoded.Opcode}' in the delay slot at 0x{delaySlotEntry.Address:X8} is excluded by the #225 candidate policy.";
                    break;
                }

                candidate.Add((delaySlotDecoded, delaySlotEntry.Address));
                candidateWords.Add(delaySlotEntry.RawWord);
                consumed = 2;
            }

            try
            {
                _ = MipsToIrLowerer.LowerProgram(candidate);
            }
            catch (InvalidOperationException ex)
            {
                reason = RealRomCandidateStopReason.UnsupportedInstruction;
                stopAddress = current.Address;
                detail = ex.Message;
                break;
            }

            accepted.Clear();
            accepted.AddRange(candidate);
            acceptedWords.Clear();
            acceptedWords.AddRange(candidateWords);
            i += consumed;
        }

        TrimToSelfContainedWindow(startAddress, accepted, acceptedWords, ref reason, ref stopAddress, ref detail);
        return Build(startAddress, acceptedWords, accepted, reason, stopAddress, detail);
    }

    /// <summary>
    /// Trims a candidate so every BEQ/BNE/J/JAL it contains targets an address inside
    /// its own window. A window is grown left to right, so the first violation found is
    /// where the window must end; trimming can then reveal an earlier instruction whose
    /// forward target pointed into the removed tail, so this re-scans until stable.
    /// JR/JALR are handled separately (excluded before this runs); this only classifies
    /// the statically-resolvable transfers #210's own call/branch semantics already
    /// compute (<see cref="R3000aBranchSemantics"/>/<see cref="R3000aJumpSemantics"/>).
    /// </summary>
    private static void TrimToSelfContainedWindow(
        uint startAddress,
        List<(R3000aInstruction Instruction, uint EntryPc)> accepted,
        List<uint> acceptedWords,
        ref RealRomCandidateStopReason reason,
        ref uint? stopAddress,
        ref string detail)
    {
        while (accepted.Count > 0)
        {
            var violationIndex = FindFirstExternalTargetIndex(startAddress, accepted);
            if (violationIndex < 0)
            {
                return;
            }

            var violating = accepted[violationIndex];
            reason = violating.Instruction.Opcode == R3000aOpcode.Jal
                ? RealRomCandidateStopReason.ExternalCallExcluded
                : RealRomCandidateStopReason.StaticTargetOutsideCandidate;
            stopAddress = violating.EntryPc;
            detail = $"'{violating.Instruction.Opcode}' at 0x{violating.EntryPc:X8} targets an address outside the candidate window; " +
                "#225 requires a self-contained window rather than a silent external exit.";

            var removeCount = accepted.Count - violationIndex;
            accepted.RemoveRange(violationIndex, removeCount);
            acceptedWords.RemoveRange(violationIndex, removeCount);
        }
    }

    /// <summary>
    /// The index of the first BEQ/BNE/J/JAL in <paramref name="accepted"/> whose static
    /// target falls outside <c>[startAddress, startAddress + 4 * accepted.Count)</c>, or
    /// -1 when every static target stays inside the window.
    /// </summary>
    private static int FindFirstExternalTargetIndex(
        uint startAddress, List<(R3000aInstruction Instruction, uint EntryPc)> accepted)
    {
        var rangeEnd = unchecked(startAddress + (uint)accepted.Count * 4);
        for (var index = 0; index < accepted.Count; index++)
        {
            var (instruction, entryPc) = accepted[index];
            if (!TryGetStaticControlFlowTarget(instruction, entryPc, out var target))
            {
                continue;
            }

            if (target < startAddress || target >= rangeEnd)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Reports whether any BEQ/BNE/J/JAL among <paramref name="encodedInstructions"/>
    /// (decoded and addressed from <paramref name="startAddress"/>) has a static target
    /// outside the window. This is the one dependency category this selector actually
    /// validates; it says nothing about BIOS/syscall, MMIO, or dynamic/self-modifying
    /// code dependencies, which are not analyzed here.
    /// </summary>
    public static bool HasExternalStaticControlFlowTarget(uint startAddress, IReadOnlyList<uint> encodedInstructions)
    {
        ArgumentNullException.ThrowIfNull(encodedInstructions);
        var rangeEnd = unchecked(startAddress + (uint)encodedInstructions.Count * 4);
        for (var index = 0; index < encodedInstructions.Count; index++)
        {
            var entryPc = unchecked(startAddress + (uint)(index * 4));
            var instruction = R3000aDecoder.Decode(encodedInstructions[index]);
            if (!TryGetStaticControlFlowTarget(instruction, entryPc, out var target))
            {
                continue;
            }

            if (target < startAddress || target >= rangeEnd)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetStaticControlFlowTarget(R3000aInstruction instruction, uint pc, out uint target)
    {
        switch (instruction.Opcode)
        {
            case R3000aOpcode.Beq:
            case R3000aOpcode.Bne:
                return R3000aBranchSemantics.TryGetBranchTarget(instruction, pc, out target);
            case R3000aOpcode.J:
            case R3000aOpcode.Jal:
                return R3000aJumpSemantics.TryGetJumpTarget(instruction, pc, out target);
            default:
                target = 0;
                return false;
        }
    }

    /// <summary>
    /// The full provenance contract this selector actually validates: an external
    /// static control-flow target (see <see cref="HasExternalStaticControlFlowTarget"/>)
    /// <em>or</em> an indirect jump (JR/JALR) anywhere in the window. <see cref="TryExtend"/>
    /// never returns a candidate containing either, so this only differs from
    /// <see langword="false"/> for a candidate built some other way — e.g. by hand, or
    /// by future code constructing <see cref="RealRomFunctionCandidate"/> directly —
    /// which is exactly why <see cref="RealRomFixtureAdapter.BuildProvenance"/> checks it
    /// rather than assuming a candidate the selector "would" have accepted.
    /// </summary>
    public static bool HasUnresolvedControlFlowDependency(uint startAddress, IReadOnlyList<uint> encodedInstructions)
    {
        ArgumentNullException.ThrowIfNull(encodedInstructions);
        if (HasExternalStaticControlFlowTarget(startAddress, encodedInstructions))
        {
            return true;
        }

        foreach (var word in encodedInstructions)
        {
            var opcode = R3000aDecoder.Decode(word).Opcode;
            if (opcode is R3000aOpcode.Jr or R3000aOpcode.Jalr)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Selects the longest candidate reachable from the executable's entry point or
    /// any discovered basic-block start (deterministic tie-break: lowest start
    /// address wins), or <see langword="null"/> when no non-empty candidate exists.
    /// </summary>
    public static RealRomFunctionCandidate? SelectBest(
        DiscImageAnalysisReport report, int maxWindowInstructions = DefaultMaxWindowInstructions)
    {
        ArgumentNullException.ThrowIfNull(report);

        var ordered = report.DecodedInstructions.OrderBy(static i => i.Address).ToArray();
        var starts = new SortedSet<uint> { report.EntryPoint };
        foreach (var block in report.BasicBlocks)
        {
            starts.Add(block.StartAddress);
        }

        RealRomFunctionCandidate? best = null;
        foreach (var start in starts)
        {
            var candidate = TryExtend(ordered, start, maxWindowInstructions);
            if (candidate is null || candidate.InstructionCount == 0)
            {
                continue;
            }

            if (best is null || candidate.InstructionCount > best.InstructionCount)
            {
                best = candidate;
            }
        }

        return best;
    }

    private static RealRomFunctionCandidate Build(
        uint startAddress,
        List<uint> words,
        List<(R3000aInstruction Instruction, uint EntryPc)> instructions,
        RealRomCandidateStopReason reason,
        uint? stopAddress,
        string detail)
    {
        var subset = instructions.Select(entry => entry.Instruction.Opcode.ToString())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        return new RealRomFunctionCandidate
        {
            StartAddress = startAddress,
            EncodedInstructions = new ReadOnlyCollection<uint>(words.ToArray()),
            StopReason = reason,
            StopAddress = stopAddress,
            StopDetail = detail,
            RequiredInstructionSubset = new ReadOnlyCollection<string>(subset),
        };
    }

    private static int IndexOfAddress(IReadOnlyList<DecodedInstruction> orderedInstructions, uint address)
    {
        for (var i = 0; i < orderedInstructions.Count; i++)
        {
            if (orderedInstructions[i].Address == address) return i;
        }
        return -1;
    }
}

/// <summary>
/// The minimal, reproducible provenance contract Issue #225 needs (deliberately not
/// the full #215 snapshot/manifest schema): identity of the executable the candidate
/// came from, the candidate's own static identity, and how it was selected. Contains
/// no local machine path or timestamp, so two runs against the same executable always
/// produce the same record.
/// </summary>
[Domain]
public sealed record RealRomFunctionProvenance
{
    /// <summary>Disc/executable identity, reusing the existing #215 identity contract rather than a parallel one.</summary>
    public required ArtifactFixtureIdentity Executable { get; init; }

    public required uint EntryAddress { get; init; }
    public required int InstructionCount { get; init; }
    public required IReadOnlyList<string> RequiredInstructionSubset { get; init; }

    /// <summary>Human-readable description of how the candidate was extracted/selected.</summary>
    public required string ExtractionMethod { get; init; }

    /// <summary>
    /// Deterministic identity of the selected instruction window: SHA-256 of the
    /// executable hash, start address, and encoded words. Two selections of the same
    /// bytes always agree; a local path or timestamp never participates.
    /// </summary>
    public required string SelectionIdentitySha256 { get; init; }

    /// <summary>
    /// Whether the candidate has an unresolved <em>static control-flow</em> dependency:
    /// a BEQ/BNE/J/JAL whose target lands outside the candidate window, or an indirect
    /// jump (JR/JALR). This is the only dependency category
    /// <see cref="RealRomCandidateSelector"/> validates — it is computed from the
    /// candidate, not assumed, and is <see langword="false"/> for any candidate the
    /// selector actually returned (that has already been trimmed to be self-contained).
    /// It says nothing about BIOS/syscall, MMIO, or dynamic/self-modifying code
    /// dependencies: those categories are not analyzed by this selector at all, and are
    /// deliberately not represented here as "validated absent".
    /// </summary>
    public required bool HasUnresolvedDependencies { get; init; }

    public string ToCanonicalJson() => ArtifactJson.Serialize(this);
}

/// <summary>
/// Connects a selected real-ROM candidate to the existing, unmodified Recompiler
/// differential contract (#206/#209/#211): no second semantics implementation, just
/// an adapter from analysis output to <see cref="RecompilerDifferentialFixture"/>.
/// </summary>
[Domain]
public static class RealRomFixtureAdapter
{
    /// <summary>
    /// Builds the differential fixture for a candidate. The instruction count is used
    /// as both budgets: it always over-approximates the host's retired-block count
    /// (control-transfer fusion only ever reduces it), and a surplus budget is proven
    /// harmless by the existing #209 contract (a program that falls off its own end
    /// completes with <c>Success</c> on both executors, per
    /// <c>Issue209ExtraBudgetStopsAtProgramEnd</c>).
    /// </summary>
    public static RecompilerDifferentialFixture ToDifferentialFixture(this RealRomFunctionCandidate candidate, string name)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var budget = (uint)candidate.InstructionCount;
        return new RecompilerDifferentialFixture(
            name,
            candidate.EncodedInstructions,
            candidate.StartAddress,
            stepBudget: budget,
            referenceStepBudget: budget);
    }

    public static RealRomFunctionProvenance BuildProvenance(
        RealRomFunctionCandidate candidate, ArtifactFixtureIdentity executable, string extractionMethod)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionMethod);

        var identityText = new StringBuilder()
            .Append(executable.ExecutableSha256).Append(':')
            .Append(candidate.StartAddress.ToString("X8"));
        foreach (var word in candidate.EncodedInstructions)
        {
            identityText.Append(':').Append(word.ToString("X8"));
        }

        var identityHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identityText.ToString()))).ToLowerInvariant();

        return new RealRomFunctionProvenance
        {
            Executable = executable,
            EntryAddress = candidate.StartAddress,
            InstructionCount = candidate.InstructionCount,
            RequiredInstructionSubset = candidate.RequiredInstructionSubset,
            ExtractionMethod = extractionMethod,
            SelectionIdentitySha256 = identityHash,
            HasUnresolvedDependencies = RealRomCandidateSelector.HasUnresolvedControlFlowDependency(
                candidate.StartAddress, candidate.EncodedInstructions),
        };
    }
}
