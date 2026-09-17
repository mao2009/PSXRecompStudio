using System.Globalization;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.DiscImage.AnalysisArtifacts;

namespace PSXRecomp.Core.Recompiler;

/// <summary>
/// One differential run that actually happened, folded into the coverage document as
/// <em>proven</em> coverage. Only a real executed outcome may be passed here: the coverage
/// analyzer never runs a differential itself and never infers one.
/// </summary>
[Domain]
public sealed record RealRomCoverageValidation
{
    public required uint StartAddress { get; init; }
    public required int InstructionCount { get; init; }

    /// <summary>True when the recompiled window executed identically to the reference interpreter.</summary>
    public required bool Matched { get; init; }
}

/// <summary>
/// Measures how much of an analyzed real-ROM code corpus is currently recompilable, and
/// why the rest is not.
///
/// <para>
/// This is the descriptive counterpart to <see cref="RealRomCandidateSelector"/>, and is
/// deliberately a <em>separate</em> responsibility (Issue #410). The selector answers
/// "which bounded window can be proven correct right now" and stays exactly as
/// conservative as it is; this analyzer answers "how much of the whole title would lower,
/// and what blocks the remainder". Nothing here feeds back into selection, and no
/// threshold anywhere is relaxed to make a number look better.
/// </para>
///
/// <para>
/// Like the selector (ADR-013) it has no second notion of "supported": support is decided
/// by actually calling <see cref="MipsToIrLowerer.LowerProgram"/>. There is one contract,
/// and extending it automatically extends what this analyzer reports.
/// </para>
/// </summary>
[Domain]
public static class RealRomCoverageAnalyzer
{
    /// <summary>The coverage unit, recorded in the artifact itself.</summary>
    public const string Unit = "instruction";

    public const string ClassOrdering = "class-ordinal-ascending";
    public const string ReasonOrdering = "class-ordinal-ascending,detail-ordinal-ascending";
    public const string DifferentialWindowOrdering = "start-address-ascending,instruction-count-ascending";

    /// <summary>
    /// Why the unit is the instruction and not the function, and what a
    /// <c>Lowerable</c> count does and does not claim. Written into every document so the
    /// caveat travels with the numbers instead of living only in a document nobody opens.
    /// </summary>
    public const string UnitRationale =
        "The unit is one instruction-sized word of the executable's text region. Function-level " +
        "coverage is not reported: FunctionDiscovery grows each function by reachability from a " +
        "seed, so its functions overlap, stop at unresolved indirect transfers, and do not " +
        "partition the corpus — whole-program percentages over them would be fabricated. Basic " +
        "blocks do partition the decoded stream and are reported as a secondary structural " +
        "breakdown. A 'Lowerable' instruction means only that MipsToIrLowerer accepts that " +
        "instruction's shape in a minimal window; it is an upper bound on what " +
        "RealRomCandidateSelector could take as a contiguous, self-contained window, and it is " +
        "strictly weaker than the 'differential' section, which counts code actually proven to " +
        "execute identically to the reference interpreter.";

    /// <summary>
    /// A fixed, address-independent PC used when probing one instruction's shape. Lowering
    /// acceptance is a property of the instruction word (the lowerer dispatches on opcode
    /// shape, and branch/jump targets always resolve from PC plus operands), so the probe
    /// result depends only on the raw word — which is what makes <see cref="ProbeLowerable"/>
    /// cacheable by word and the whole document reproducible.
    /// </summary>
    private const uint ProbeBasePc = 0x80000000u;

    /// <summary>
    /// Classifies every unit of <paramref name="report"/>'s analyzed corpus and aggregates
    /// the result into a deterministic document.
    /// </summary>
    /// <param name="validations">
    /// Differential runs that actually executed, or <see langword="null"/> when none did.
    /// An absent section means "nothing was proven", never "nothing failed".
    /// </param>
    public static RealRomCoverageDocument Analyze(
        DiscImageAnalysisReport report,
        ArtifactFixtureIdentity fixture,
        IReadOnlyList<RealRomCoverageValidation>? validations = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(fixture);

        var classCounts = new long[Enum.GetValues<RealRomCoverageClass>().Length];
        var reasonCounts = new Dictionary<(RealRomCoverageClass Class, string Detail), long>();
        var probeCache = new Dictionary<uint, bool>();

        var blockCoverage = BuildBlockCoverage(report.BasicBlocks);
        var biosSites = BuildBiosSiteDetails(report.BiosCalls);

        var lowerable = 0;
        foreach (var decoded in report.DecodedInstructions)
        {
            var owningBlock = FindOwningBlock(blockCoverage, decoded.Address);
            var (coverageClass, detail) = Classify(decoded, owningBlock is not null, biosSites, probeCache);

            classCounts[(int)coverageClass]++;
            if (coverageClass == RealRomCoverageClass.Lowerable)
            {
                lowerable++;
            }
            else
            {
                Increment(reasonCounts, coverageClass, detail);
            }

            if (owningBlock is not null)
            {
                owningBlock.Decoded++;
                if (coverageClass == RealRomCoverageClass.Lowerable)
                {
                    owningBlock.Lowerable++;
                }
            }
        }

        foreach (var failure in report.DecodeFailures)
        {
            classCounts[(int)RealRomCoverageClass.MalformedOrUndecodable]++;
            Increment(reasonCounts, RealRomCoverageClass.MalformedOrUndecodable, failure.Reason);
        }

        // The decode window is bounded, so the text region's remaining words were never
        // examined. Counting them as rejected would be as wrong as ignoring them.
        var textSlots = (long)(report.TextSize / 4);
        var analyzed = (long)report.DecodedInstructions.Count + report.DecodeFailures.Count;
        var notAnalyzed = Math.Max(0L, textSlots - analyzed);
        classCounts[(int)RealRomCoverageClass.NotAnalyzed] += notAnalyzed;

        var (fullBlocks, partialBlocks, rejectedBlocks) = SummarizeBlocks(blockCoverage);

        return new RealRomCoverageDocument
        {
            SchemaVersion = AnalysisArtifactSchema.CoverageSchemaVersion,
            ArtifactKind = AnalysisArtifactSchema.CoverageArtifactKind,
            Fixture = fixture,
            Unit = Unit,
            UnitRationale = UnitRationale,
            ClassOrdering = ClassOrdering,
            ReasonOrdering = ReasonOrdering,
            DifferentialWindowOrdering = DifferentialWindowOrdering,
            Totals = new CoverageTotals
            {
                TextInstructionSlots = textSlots,
                DecodedInstructions = report.DecodedInstructions.Count,
                DecodeFailures = report.DecodeFailures.Count,
                NotAnalyzedInstructions = notAnalyzed,
                LowerableInstructions = lowerable,
                RejectedInstructions = (int)(analyzed - lowerable),
                BasicBlocks = report.BasicBlocks.Count,
                FullyLowerableBasicBlocks = fullBlocks,
                PartiallyLowerableBasicBlocks = partialBlocks,
                RejectedBasicBlocks = rejectedBlocks,
            },
            Classes = Enum.GetValues<RealRomCoverageClass>()
                .OrderBy(static value => (byte)value)
                .Select(value => new CoverageClassCount
                {
                    Class = value.ToString(),
                    InstructionCount = classCounts[(int)value],
                })
                .ToList(),
            Reasons = reasonCounts
                .OrderBy(static pair => (byte)pair.Key.Class)
                .ThenBy(static pair => pair.Key.Detail, StringComparer.Ordinal)
                .Select(static pair => new CoverageReasonCount
                {
                    Class = pair.Key.Class.ToString(),
                    Detail = pair.Key.Detail,
                    InstructionCount = pair.Value,
                })
                .ToList(),
            Differential = BuildDifferential(validations),
        };
    }

    /// <summary>
    /// Decides one decoded instruction's class. The order of the checks is the precedence
    /// contract: a structural gap outranks every instruction-level judgement (nothing can be
    /// claimed about a word the block partition does not cover), a recognized BIOS call site
    /// outranks the generic indirect-transfer bucket because it names a concrete runtime
    /// dependency, and only then is the lowering stage asked.
    /// </summary>
    private static (RealRomCoverageClass Class, string Detail) Classify(
        DecodedInstruction decoded,
        bool insideBasicBlock,
        IReadOnlyDictionary<uint, string> biosSites,
        Dictionary<uint, bool> probeCache)
    {
        if (!insideBasicBlock)
        {
            return (RealRomCoverageClass.AnalysisUncertainty, "outside-basic-block-partition");
        }

        if (biosSites.TryGetValue(decoded.Address, out var biosIdentity))
        {
            return (RealRomCoverageClass.BiosDependency, biosIdentity);
        }

        var instruction = R3000aDecoder.Decode(decoded.RawWord);

        // The report's own recorded mnemonic, not a second rendering of the opcode name, so a
        // coverage reason joins exactly against report.json's mnemonicMix.
        var mnemonic = decoded.Mnemonic;

        if (instruction.ControlFlow == R3000aControlFlowKind.JumpRegister)
        {
            return (RealRomCoverageClass.IndirectControlFlow, mnemonic);
        }

        return ProbeLowerable(decoded.RawWord, instruction, probeCache)
            ? (RealRomCoverageClass.Lowerable, mnemonic)
            : (RealRomCoverageClass.UnsupportedInstruction, mnemonic);
    }

    /// <summary>
    /// Asks the one true validator whether an instruction's shape lowers, on the smallest
    /// window that is legal for it: the instruction alone, or — when it owns a branch delay
    /// slot — the instruction followed by a NOP. The result is cached by raw word, which is
    /// sound because acceptance depends on the word and not on its address.
    /// </summary>
    private static bool ProbeLowerable(
        uint rawWord, R3000aInstruction instruction, Dictionary<uint, bool> probeCache)
    {
        if (probeCache.TryGetValue(rawWord, out var cached))
        {
            return cached;
        }

        var window = new List<(R3000aInstruction Instruction, uint EntryPc)> { (instruction, ProbeBasePc) };
        if (instruction.DelaySlot != R3000aDelaySlotKind.None)
        {
            window.Add((R3000aDecoder.Decode(0x00000000), ProbeBasePc + 4));
        }

        bool supported;
        try
        {
            _ = MipsToIrLowerer.LowerProgram(window);
            supported = true;
        }
        catch (InvalidOperationException)
        {
            supported = false;
        }

        probeCache[rawWord] = supported;
        return supported;
    }

    /// <summary>
    /// Maps each recognized BIOS call site's guest PC to its stable identity, using the same
    /// <c>family:functionNumber</c> key <c>report.json</c>'s BIOS summary already uses so the
    /// two documents name the same dependency the same way.
    /// </summary>
    private static Dictionary<uint, string> BuildBiosSiteDetails(BiosCallEvidence? evidence)
    {
        var sites = new Dictionary<uint, string>();
        foreach (var site in evidence?.Sites ?? Array.Empty<BiosCallSite>())
        {
            sites[site.GuestPc] = site.Family.ToString() + ":" + (site.FunctionNumber is byte number
                ? number.ToString("X2", CultureInfo.InvariantCulture)
                : "unresolved");
        }

        return sites;
    }

    private static void Increment(
        Dictionary<(RealRomCoverageClass, string), long> counts, RealRomCoverageClass coverageClass, string detail)
    {
        var key = (coverageClass, detail);
        counts[key] = counts.TryGetValue(key, out var existing) ? existing + 1 : 1;
    }

    /// <summary>Mutable per-block tally, ordered by start address so lookup can binary-search it.</summary>
    private sealed class BlockCoverage
    {
        public required uint StartAddress { get; init; }
        public required uint EndAddress { get; init; }
        public int Decoded { get; set; }
        public int Lowerable { get; set; }
    }

    private static BlockCoverage[] BuildBlockCoverage(IReadOnlyList<BasicBlock> blocks)
    {
        return blocks
            .OrderBy(static block => block.StartAddress)
            .ThenBy(static block => block.EndAddress)
            .Select(static block => new BlockCoverage
            {
                StartAddress = block.StartAddress,
                EndAddress = block.EndAddress,
            })
            .ToArray();
    }

    /// <summary>
    /// The block containing <paramref name="address"/>, or <see langword="null"/> when the
    /// partition does not cover it.
    /// <para>
    /// <c>BasicBlockBuilder</c> produces a strict partition — leaders come from a sorted set,
    /// so no two blocks share a start and none overlaps — which makes "the last block
    /// starting at or before the address, if its range contains it" both unambiguous and
    /// exact. For a caller-supplied report that does overlap, the blocks are ordered by start
    /// then end, so the widest block at a given start is the one consulted; an address no
    /// block covers is reported as uncertainty rather than credited to a neighbour.
    /// </para>
    /// </summary>
    private static BlockCoverage? FindOwningBlock(BlockCoverage[] blocks, uint address)
    {
        var low = 0;
        var high = blocks.Length - 1;
        var candidate = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (blocks[middle].StartAddress <= address)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (candidate < 0)
        {
            return null;
        }

        return address <= blocks[candidate].EndAddress ? blocks[candidate] : null;
    }

    private static (int Full, int Partial, int Rejected) SummarizeBlocks(BlockCoverage[] blocks)
    {
        var full = 0;
        var partial = 0;
        var rejected = 0;
        foreach (var block in blocks)
        {
            // Decoded == 0 cannot arise from BasicBlockBuilder output (every block it emits
            // owns at least one decoded instruction); for a caller-supplied report it falls
            // into the same bucket as a block with nothing lowerable, so the three counts
            // always partition BasicBlocks.
            if (block.Decoded == 0 || block.Lowerable == 0)
            {
                rejected++;
            }
            else if (block.Lowerable == block.Decoded)
            {
                full++;
            }
            else
            {
                partial++;
            }
        }

        return (full, partial, rejected);
    }

    private static CoverageDifferentialSection BuildDifferential(
        IReadOnlyList<RealRomCoverageValidation>? validations)
    {
        if (validations is null || validations.Count == 0)
        {
            return CoverageDifferentialSection.None;
        }

        var windows = validations
            .OrderBy(static validation => validation.StartAddress)
            .ThenBy(static validation => validation.InstructionCount)
            .Select(static validation => new CoverageDifferentialWindow
            {
                StartAddress = AnalysisArtifactSchema.FormatWord32(validation.StartAddress),
                InstructionCount = validation.InstructionCount,
                Outcome = validation.Matched ? "matched" : "mismatched",
            })
            .ToList();

        return new CoverageDifferentialSection
        {
            AttemptedWindows = validations.Count,
            MatchedWindows = validations.Count(static validation => validation.Matched),
            MismatchedWindows = validations.Count(static validation => !validation.Matched),
            MatchedInstructions = validations.Where(static validation => validation.Matched)
                .Sum(static validation => validation.InstructionCount),
            MismatchedInstructions = validations.Where(static validation => !validation.Matched)
                .Sum(static validation => validation.InstructionCount),
            Windows = windows,
        };
    }
}
