using System.Text.Json;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.DiscImage.AnalysisArtifacts;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// One candidate selection outcome for one start address, as recorded by the
/// existing <see cref="RealRomCandidateSelector"/> (Issue #225 / ADR-013). This is a
/// diagnostic record only: it carries addresses, stop reasons and mnemonic names —
/// never the encoded instruction bytes themselves.
/// </summary>
[Test]
public sealed record CandidateSurveyCandidate
{
    /// <summary>Start address of the candidate window (metadata, never a code dump).</summary>
    public required uint StartAddress { get; init; }

    /// <summary>Number of instructions in the selected window; 0 means the start produced no window at all.</summary>
    public required int InstructionCount { get; init; }

    /// <summary>Why the window stopped growing (see <see cref="RealRomCandidateStopReason"/>).</summary>
    public required string StopReason { get; init; }

    /// <summary>Address of the instruction that stopped the window, when one applies.</summary>
    public required uint? StopAddress { get; init; }

    /// <summary>Decoded opcode name (lowercase) at the stop address, when one applies.</summary>
    public required string? StopOpcode { get; init; }

    /// <summary>Distinct opcode names exercised by the window, lowercase, sorted.</summary>
    public required IReadOnlyList<string> ExercisedOpcodes { get; init; }
}

/// <summary>Histogram bucket over one stop-reason value.</summary>
[Test]
public sealed record CandidateSurveyStopBucket
{
    public required string Reason { get; init; }
    public required int Count { get; init; }
}

/// <summary>
/// Granular stop bucket: stop reason combined with the decoded opcode at the stop
/// (JR vs JALR for indirect jumps, or the specific unsupported opcode, etc.).
/// </summary>
[Test]
public sealed record CandidateSurveyStopDetail
{
    public required string Reason { get; init; }
    public required string Opcode { get; init; }
    public required int Count { get; init; }
    public required int AcceptedCount { get; init; }
}

/// <summary>Opcode-level statistic over candidates or the decoded stream.</summary>
[Test]
public sealed record CandidateSurveyOpcode
{
    public required string Opcode { get; init; }
    public required int Occurrences { get; init; }
    public required int CandidatesAffected { get; init; }
}

/// <summary>One window-size bucket label → candidate count.</summary>
[Test]
public sealed record CandidateSurveySizeBucket
{
    public required string Label { get; init; }
    public required int Count { get; init; }
}

/// <summary>
/// Empirical per-opcode support probe: the first decoded occurrence of each distinct
/// opcode is run through the existing <see cref="MipsToIrLowerer.LowerProgram"/>
/// (the selector's own validator, ADR-013) on a minimal window. No second "supported
/// opcode" list exists anywhere in this survey.
/// </summary>
[Test]
public sealed record CandidateSurveyProbe
{
    public required string Opcode { get; init; }
    public required int Occurrences { get; init; }
    public required string Status { get; init; }
    public required string Note { get; init; }
}

/// <summary>
/// Aggregate snapshot of the candidate-pool survey for one fixture. Contains only
/// metadata (hashes, addresses, counts, mnemonic names, stop reasons) — never
/// copyrighted code bytes — so it is safe to keep in the git-ignored reports tree
/// and to cite as evidence.
/// </summary>
[Test]
public sealed record RealRomCandidateSurveySnapshot
{
    public required string DiscImageSha256 { get; init; }
    public required string ExecutableSha256 { get; init; }
    public required string ExecutableFileName { get; init; }
    public required string ExecutableSerial { get; init; }
    public required uint EntryPoint { get; init; }
    public required uint TextStart { get; init; }
    public required uint TextSize { get; init; }
    public required int DecodedInstructionCount { get; init; }
    public required int DecodeFailureCount { get; init; }
    public required int BasicBlockCount { get; init; }

    public required int TotalCandidates { get; init; }
    public required int AcceptedCandidates { get; init; }
    public required int RejectedCandidates { get; init; }
    public required int QualifyingCandidates { get; init; }
    public required double AcceptanceRate { get; init; }

    public required int AcceptedMinSize { get; init; }
    public required int AcceptedMedianSize { get; init; }
    public required int AcceptedP90Size { get; init; }
    public required int AcceptedMaxSize { get; init; }

    public required IReadOnlyList<CandidateSurveyStopBucket> StopBuckets { get; init; }
    public required IReadOnlyList<CandidateSurveyStopDetail> StopDetails { get; init; }
    public required IReadOnlyList<CandidateSurveyOpcode> ExercisedOpcodes { get; init; }
    public required IReadOnlyList<CandidateSurveyOpcode> UnsupportedObservedOpcodes { get; init; }
    public required IReadOnlyList<CandidateSurveyOpcode> PresentNotExercisedOpcodes { get; init; }
    public required IReadOnlyList<CandidateSurveyOpcode> ControlFlowOpcodes { get; init; }
    public required IReadOnlyList<CandidateSurveyProbe> SupportProbes { get; init; }
    public required IReadOnlyList<CandidateSurveySizeBucket> SizeBuckets { get; init; }

    /// <summary>
    /// Per-start diagnostic records, kept separate from the aggregates so the aggregate
    /// records stay small. Addresses and stop reasons only.
    /// </summary>
    public required IReadOnlyList<CandidateSurveyCandidate> Candidates { get; init; }
}

/// <summary>
/// Runs a candidate-pool survey over an existing <see cref="DiscImageAnalysisReport"/>
/// using only the current selector contract (<see cref="RealRomCandidateSelector"/> and
/// <see cref="MipsToIrLowerer"/>). It enumerates the same candidate starts the selector
/// itself considers (entry point + every basic-block start, address-ordered) and records
/// every outcome instead of only the best one, so the accept/reject distribution of a
/// real executable can be measured. No selector semantics are changed and no acceptance
/// threshold is relaxed.
/// </summary>
[Test]
public static class RealRomCandidateSurvey
{
    /// <summary>Window-size buckets used for the size histogram.</summary>
    private static readonly (string Label, Func<int, bool> Contains)[] SizeBuckets =
    {
        ("0", size => size == 0),
        ("1-7", size => size is >= 1 and <= 7),
        ("8-31", size => size is >= 8 and <= 31),
        ("32-127", size => size is >= 32 and <= 127),
        ("128-511", size => size is >= 128 and <= 511),
        ("512+", size => size >= 512),
    };

    /// <summary>The selector's own threshold for a candidate worth recompiling (see <c>RealRomRecompilerVerticalSliceTests</c>).</summary>
    private const int MinimumQualifyingInstructionCount = 8;

    private static readonly R3000aOpcode[] ControlFlowOpcodes =
    {
        R3000aOpcode.Beq, R3000aOpcode.Bne,
        R3000aOpcode.Blez, R3000aOpcode.Bgtz, R3000aOpcode.Bltz, R3000aOpcode.Bgez,
        R3000aOpcode.Bltzal, R3000aOpcode.Bgezal,
        R3000aOpcode.J, R3000aOpcode.Jal,
        R3000aOpcode.Jr, R3000aOpcode.Jalr,
        R3000aOpcode.Syscall, R3000aOpcode.Break,
    };

    /// <summary>Lowercase opcode name, matching the report's mnemonic convention.</summary>
    public static string OpcodeName(R3000aOpcode opcode) => opcode.ToString().ToLowerInvariant();

    /// <summary>
    /// Runs the survey over the report's decoded instruction window. Candidates are
    /// enumerated exactly as <see cref="RealRomCandidateSelector.SelectBest"/> enumerates
    /// them (entry + basic-block starts, ascending), and every outcome — including empty
    /// windows — is recorded.
    /// </summary>
    public static RealRomCandidateSurveySnapshot Run(
        DiscImageAnalysisReport report,
        int maxWindowInstructions = RealRomCandidateSelector.DefaultMaxWindowInstructions)
    {
        ArgumentNullException.ThrowIfNull(report);

        var ordered = report.DecodedInstructions.OrderBy(static d => d.Address).ToArray();
        var addressToWord = ordered.ToDictionary(static d => d.Address, static d => d.RawWord);

        var starts = new SortedSet<uint> { report.EntryPoint };
        foreach (var block in report.BasicBlocks)
        {
            starts.Add(block.StartAddress);
        }

        var candidates = new List<CandidateSurveyCandidate>();
        foreach (var start in starts)
        {
            var candidate = RealRomCandidateSelector.TryExtend(ordered, start, maxWindowInstructions);
            if (candidate is null)
            {
                continue; // start address not present in the decoded stream; not a candidate.
            }

            candidates.Add(new CandidateSurveyCandidate
            {
                StartAddress = start,
                InstructionCount = candidate.InstructionCount,
                StopReason = candidate.StopReason.ToString(),
                StopAddress = candidate.StopAddress,
                StopOpcode = ResolveStopOpcode(candidate.StopReason, candidate.StopAddress, addressToWord),
                ExercisedOpcodes = candidate.RequiredInstructionSubset
                    .Select(static name => name.ToLowerInvariant())
                    .OrderBy(static name => name, StringComparer.Ordinal)
                    .ToArray(),
            });
        }

        var accepted = candidates.Where(static c => c.InstructionCount > 0).ToList();
        var rejected = candidates.Count - accepted.Count;
        var sizes = accepted.Select(static c => c.InstructionCount).OrderBy(static n => n).ToArray();

        var exercised = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var candidate in accepted)
        {
            foreach (var opcode in candidate.ExercisedOpcodes)
            {
                exercised.TryGetValue(opcode, out var existing);
                exercised[opcode] = existing + 1;
            }
        }

        var streamOpcodes = new Dictionary<string, int>(StringComparer.Ordinal);
        var firstOccurrence = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var decoded in ordered)
        {
            var name = OpcodeName(R3000aDecoder.Decode(decoded.RawWord).Opcode);
            streamOpcodes.TryGetValue(name, out var count);
            streamOpcodes[name] = count + 1;
            firstOccurrence.TryAdd(name, decoded.Address);
        }

        var unsupported = candidates
            .Where(static c => c.StopReason == nameof(RealRomCandidateStopReason.UnsupportedInstruction))
            .GroupBy(static c => c.StopOpcode ?? "<none>", StringComparer.Ordinal)
            .Select(group => new CandidateSurveyOpcode
            {
                Opcode = group.Key,
                CandidatesAffected = group.Count(),
                Occurrences = streamOpcodes.GetValueOrDefault(group.Key),
            })
            .OrderByDescending(static o => o.CandidatesAffected)
            .ThenBy(static o => o.Opcode, StringComparer.Ordinal)
            .ToList();

        var exercisedSet = exercised.Keys.ToHashSet(StringComparer.Ordinal);
        var presentNotExercised = streamOpcodes
            .Where(kv => !exercisedSet.Contains(kv.Key))
            .Select(kv => new CandidateSurveyOpcode
            {
                Opcode = kv.Key,
                Occurrences = kv.Value,
                CandidatesAffected = 0,
            })
            .OrderByDescending(static o => o.Occurrences)
            .ThenBy(static o => o.Opcode, StringComparer.Ordinal)
            .ToList();

        var controlFlow = streamOpcodes
            .Where(kv => ControlFlowOpcodes.Any(opcode => OpcodeName(opcode) == kv.Key))
            .Select(kv => new CandidateSurveyOpcode
            {
                Opcode = kv.Key,
                Occurrences = kv.Value,
                CandidatesAffected = 0,
            })
            .OrderByDescending(static o => o.Occurrences)
            .ThenBy(static o => o.Opcode, StringComparer.Ordinal)
            .ToList();

        var probes = streamOpcodes.Keys
            .OrderBy(static name => name, StringComparer.Ordinal)
            .Select(name => Probe(firstOccurrence[name], addressToWord, name, streamOpcodes[name]))
            .ToList();

        var bucketCounts = SizeBuckets
            .Select((bucket, index) => new CandidateSurveySizeBucket
            {
                Label = bucket.Label,
                Count = candidates.Count(c => bucket.Contains(c.InstructionCount)),
            })
            .ToList();

        return new RealRomCandidateSurveySnapshot
        {
            DiscImageSha256 = report.DiscImageSha256,
            ExecutableSha256 = report.ExecutableFileHash,
            ExecutableFileName = report.ExecutableFileName,
            ExecutableSerial = AnalysisArtifactSchema.DeriveExecutableSerial(report.ExecutableFileName),
            EntryPoint = report.EntryPoint,
            TextStart = report.TextStart,
            TextSize = report.TextSize,
            DecodedInstructionCount = report.DecodedInstructionCount,
            DecodeFailureCount = report.DecodeFailures.Count,
            BasicBlockCount = report.BasicBlocks.Count,
            TotalCandidates = candidates.Count,
            AcceptedCandidates = accepted.Count,
            RejectedCandidates = rejected,
            QualifyingCandidates = accepted.Count(candidate => candidate.InstructionCount >= MinimumQualifyingInstructionCount),
            AcceptanceRate = candidates.Count == 0 ? 0.0 : (double)accepted.Count / candidates.Count,
            AcceptedMinSize = sizes.Length == 0 ? 0 : sizes[0],
            AcceptedMedianSize = sizes.Length == 0 ? 0 : sizes[sizes.Length / 2],
            AcceptedP90Size = sizes.Length == 0 ? 0 : sizes[Math.Min(sizes.Length - 1, (int)(sizes.Length * 0.9))],
            AcceptedMaxSize = sizes.Length == 0 ? 0 : sizes[^1],
            StopBuckets = candidates
                .GroupBy(static c => c.StopReason, StringComparer.Ordinal)
                .OrderByDescending(static g => g.Count())
                .ThenBy(static g => g.Key, StringComparer.Ordinal)
                .Select(group => new CandidateSurveyStopBucket { Reason = group.Key, Count = group.Count() })
                .ToList(),
            StopDetails = candidates
                .GroupBy(static c => (c.StopReason, c.StopOpcode ?? "<none>"))
                .OrderByDescending(static g => g.Count())
                .Select(group => new CandidateSurveyStopDetail
                {
                    Reason = group.Key.StopReason,
                    Opcode = group.Key.Item2,
                    Count = group.Count(),
                    AcceptedCount = group.Count(candidate => candidate.InstructionCount > 0),
                })
                .ToList(),
            ExercisedOpcodes = exercised
                .OrderByDescending(static kv => kv.Value)
                .ThenBy(static kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new CandidateSurveyOpcode
                {
                    Opcode = kv.Key,
                    CandidatesAffected = kv.Value,
                    Occurrences = streamOpcodes.GetValueOrDefault(kv.Key),
                })
                .ToList(),
            UnsupportedObservedOpcodes = unsupported,
            PresentNotExercisedOpcodes = presentNotExercised,
            ControlFlowOpcodes = controlFlow,
            SupportProbes = probes,
            SizeBuckets = bucketCounts,
            Candidates = candidates,
        };
    }

    /// <summary>
    /// Resolves the opcode name at the stop address for the stop reasons that point at a
    /// concrete instruction (indirect jump, unsupported instruction, or external static
    /// target). Structural stops (max window, end of stream) have no stop opcode.
    /// </summary>
    private static string? ResolveStopOpcode(
        RealRomCandidateStopReason reason,
        uint? stopAddress,
        IReadOnlyDictionary<uint, uint> addressToWord)
    {
        if (stopAddress is not uint address || !addressToWord.TryGetValue(address, out var raw))
        {
            return null;
        }

        switch (reason)
        {
            case RealRomCandidateStopReason.IndirectJumpExcluded:
            case RealRomCandidateStopReason.UnsupportedInstruction:
            case RealRomCandidateStopReason.ExternalCallExcluded:
            case RealRomCandidateStopReason.StaticTargetOutsideCandidate:
                return OpcodeName(R3000aDecoder.Decode(raw).Opcode);
            default:
                return null;
        }
    }

    /// <summary>
    /// Runs the first decoded occurrence of one opcode through the existing lowerer on a
    /// minimal window (the opcode plus, when it owns a branch delay slot, a neutral NOP
    /// delay slot). The lowerer is the single validator (ADR-013); this probe reports
    /// whether that validator accepts each observed opcode in isolation. JR/JALR are
    /// reported as <c>excluded-by-policy</c> because the selector never hands them to the
    /// lowerer at all.
    /// </summary>
    private static CandidateSurveyProbe Probe(
        uint firstAddress,
        IReadOnlyDictionary<uint, uint> addressToWord,
        string opcodeName,
        int occurrences)
    {
        const uint basePc = 0x80000000u;

        var instruction = R3000aDecoder.Decode(addressToWord[firstAddress]);
        if (instruction.Opcode is R3000aOpcode.Jr or R3000aOpcode.Jalr)
        {
            return new CandidateSurveyProbe
            {
                Opcode = opcodeName,
                Occurrences = occurrences,
                Status = "excluded-by-policy",
                Note = "JR/JALR are rejected by RealRomCandidateSelector before lowering (unresolved indirect flow).",
            };
        }

        var probe = new List<(R3000aInstruction Instruction, uint EntryPc)>();
        if (instruction.DelaySlot != R3000aDelaySlotKind.None)
        {
            var nop = R3000aDecoder.Decode(0x00000000);
            probe.Add((nop, basePc + 4));
        }

        probe.Add((instruction, basePc));

        try
        {
            _ = MipsToIrLowerer.LowerProgram(probe);
            return new CandidateSurveyProbe
            {
                Opcode = opcodeName,
                Occurrences = occurrences,
                Status = "lowered",
                Note = "Lowered by MipsToIrLowerer on a minimal window.",
            };
        }
        catch (InvalidOperationException ex)
        {
            return new CandidateSurveyProbe
            {
                Opcode = opcodeName,
                Occurrences = occurrences,
                Status = "unsupported",
                Note = ex.Message.Length <= 160 ? ex.Message : ex.Message[..160],
            };
        }
    }

    /// <summary>Deterministic JSON serialization of the snapshot (local diagnostic only).</summary>
    public static string ToJson(RealRomCandidateSurveySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
    }
}