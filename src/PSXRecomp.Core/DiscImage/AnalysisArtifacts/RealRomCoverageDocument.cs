using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage.AnalysisArtifacts;

/// <summary>
/// Why one analyzed unit is, or is not, currently recompilable. Every value is decided
/// from evidence the existing analysis already produces — never from a guess (Issue #410:
/// "Do not invent classifications that cannot be evidenced from the current analyzer").
///
/// <para>
/// Three classifications Issue #410 lists as desirable are deliberately <em>absent</em>,
/// because nothing in the current repository can establish them: MMIO / hardware
/// dependency (no analyzer computes a static effective address, so a load's target region
/// is unknown), dynamic / overlay suspicion (<c>OverlayInfo</c> exists as a contract but
/// has no producer), and self-modifying code. Emitting an always-zero bucket for them
/// would read as "measured and absent" rather than "not measured", so they are reported
/// nowhere rather than reported falsely.
/// </para>
/// </summary>
[Domain]
public enum RealRomCoverageClass : byte
{
    /// <summary>
    /// <c>MipsToIrLowerer</c> accepts this instruction's shape. This is an
    /// <em>upper bound</em> on recompilability, not a proof: see
    /// <see cref="RealRomCoverageDocument.Unit"/>.
    /// </summary>
    Lowerable = 0,

    /// <summary>
    /// A register-indirect transfer (<c>JR</c>/<c>JALR</c>) whose target the analyzer
    /// cannot resolve statically — the same evidence <c>BasicBlockBuilder</c> records as an
    /// <c>"indirect"</c> CFG edge. Counted separately from
    /// <see cref="UnsupportedInstruction"/> because the two need different fixes.
    /// </summary>
    IndirectControlFlow = 1,

    /// <summary>
    /// A recognized PS1 BIOS jump-table call site (<c>BiosCallRecognizer</c> evidence,
    /// carried on the report as <c>biosCalls</c>). The code is not self-contained: it
    /// depends on a runtime service, whether or not the function number resolved.
    /// </summary>
    BiosDependency = 2,

    /// <summary>The lowering stage rejects this instruction's shape outright.</summary>
    UnsupportedInstruction = 3,

    /// <summary>
    /// The decoder could not produce an instruction at this address — a
    /// <see cref="DecodeFailure"/> recorded by the existing pipeline.
    /// </summary>
    MalformedOrUndecodable = 4,

    /// <summary>
    /// The instruction decoded, but the analyzer's own basic-block partition does not
    /// cover it, so no structural claim about it is defensible. Recorded rather than
    /// silently folded into a supported or rejected bucket.
    /// </summary>
    AnalysisUncertainty = 5,

    /// <summary>
    /// A word of the executable's text region that this analysis never reached, because
    /// the decode window is bounded. It is neither supported nor rejected: it is the
    /// honest remainder of the denominator, and the reason a coverage ratio computed over
    /// decoded instructions alone would overstate whole-title coverage.
    /// </summary>
    NotAnalyzed = 6,
}

/// <summary>One coverage class and how many units fall into it.</summary>
[Domain]
public sealed record CoverageClassCount
{
    /// <summary>The <see cref="RealRomCoverageClass"/> name.</summary>
    public required string Class { get; init; }

    public required long InstructionCount { get; init; }
}

/// <summary>
/// One <c>(class, detail)</c> bucket: the named, machine-readable reason a unit was
/// rejected. <c>detail</c> is the decoded opcode name for an instruction-level rejection,
/// the BIOS identity (<c>A0:3C</c>, <c>A0:unresolved</c>) for a BIOS dependency, and the
/// recorded decoder reason for an undecodable word — never a generic silent bucket.
/// </summary>
[Domain]
public sealed record CoverageReasonCount
{
    public required string Class { get; init; }
    public required string Detail { get; init; }
    public required long InstructionCount { get; init; }
}

/// <summary>
/// Headline coverage numbers. Instruction counts are the primary unit; the basic-block
/// counts describe how the rejections are distributed structurally.
/// </summary>
[Domain]
public sealed record CoverageTotals
{
    /// <summary>Instruction-sized words in the executable's text region (<c>textSize / 4</c>): the whole-title denominator.</summary>
    public required long TextInstructionSlots { get; init; }

    /// <summary>Words this analysis actually decoded.</summary>
    public required int DecodedInstructions { get; init; }

    /// <summary>Words the decoder rejected (each is one <see cref="RealRomCoverageClass.MalformedOrUndecodable"/> unit).</summary>
    public required int DecodeFailures { get; init; }

    /// <summary>Text words this analysis never reached, because the decode window is bounded.</summary>
    public required long NotAnalyzedInstructions { get; init; }

    /// <summary>Decoded words the lowering stage accepts.</summary>
    public required int LowerableInstructions { get; init; }

    /// <summary>
    /// Analyzed words (decoded + decode failures) that are not lowerable.
    /// <see cref="NotAnalyzedInstructions"/> is deliberately excluded: an unanalyzed word
    /// has not been rejected, only never examined.
    /// </summary>
    public required int RejectedInstructions { get; init; }

    public required int BasicBlocks { get; init; }

    /// <summary>Blocks in which every decoded instruction is lowerable.</summary>
    public required int FullyLowerableBasicBlocks { get; init; }

    /// <summary>Blocks holding both lowerable and rejected instructions.</summary>
    public required int PartiallyLowerableBasicBlocks { get; init; }

    /// <summary>Blocks in which no decoded instruction is lowerable.</summary>
    public required int RejectedBasicBlocks { get; init; }
}

/// <summary>One differentially validated window and its outcome.</summary>
[Domain]
public sealed record CoverageDifferentialWindow
{
    public required string StartAddress { get; init; }
    public required int InstructionCount { get; init; }

    /// <summary><c>"matched"</c> or <c>"mismatched"</c>.</summary>
    public required string Outcome { get; init; }
}

/// <summary>
/// Coverage that was actually <em>proven</em> by running the unmodified differential
/// contract, as opposed to coverage that is merely lowerable.
///
/// <para>
/// These two must never be added together or compared as if they measured the same thing.
/// "Lowerable" says the lowering stage accepts an instruction shape; "differentially
/// validated" says a recompiled window executed identically to the reference interpreter.
/// A window is only counted here when a differential run actually produced a result —
/// an empty section means "nothing was proven", never "nothing failed".
/// </para>
/// </summary>
[Domain]
public sealed record CoverageDifferentialSection
{
    public required int AttemptedWindows { get; init; }
    public required int MatchedWindows { get; init; }
    public required int MismatchedWindows { get; init; }

    /// <summary>Instructions inside windows whose recompiled execution matched the reference.</summary>
    public required int MatchedInstructions { get; init; }

    /// <summary>Instructions inside windows whose recompiled execution diverged.</summary>
    public required int MismatchedInstructions { get; init; }

    /// <summary>
    /// Windows ordered by start address ascending, then instruction count ascending, then
    /// outcome ascending (<c>"matched"</c> before <c>"mismatched"</c>) so two validations of
    /// the same window with different outcomes still sort deterministically.
    /// </summary>
    public required IReadOnlyList<CoverageDifferentialWindow> Windows { get; init; }

    /// <summary>Nothing was differentially validated for this analysis.</summary>
    public static CoverageDifferentialSection None { get; } = new()
    {
        AttemptedWindows = 0,
        MatchedWindows = 0,
        MismatchedWindows = 0,
        MatchedInstructions = 0,
        MismatchedInstructions = 0,
        Windows = Array.Empty<CoverageDifferentialWindow>(),
    };
}

/// <summary>
/// <c>coverage.json</c>: how much of one analyzed real-ROM code corpus is currently
/// recompilable, and — for the remainder — why not.
///
/// <para>
/// This is descriptive measurement, structurally separate from
/// <c>RealRomCandidateSelector</c>, which stays as conservative as it is: the selector
/// answers "which code can be proven right now", this document answers "how much of the
/// title is currently recompilable". Neither number may be used as the other.
/// </para>
///
/// <para>
/// Like every other artifact in this namespace the document is pure: no timestamp, no
/// local path, no host value, stable ordering throughout, so two analyses of the same
/// disc image at the same repository revision produce byte-identical text.
/// </para>
/// </summary>
[Domain]
public sealed record RealRomCoverageDocument
{
    public required int SchemaVersion { get; init; }
    public required string ArtifactKind { get; init; }
    public required ArtifactFixtureIdentity Fixture { get; init; }

    /// <summary>
    /// The coverage unit, recorded in the artifact so a consumer never has to infer it.
    /// See <see cref="UnitRationale"/> for why it is not the function.
    /// </summary>
    public required string Unit { get; init; }

    /// <summary>Why this unit, and precisely what a <c>Lowerable</c> count does and does not claim.</summary>
    public required string UnitRationale { get; init; }

    public required string ClassOrdering { get; init; }
    public required string ReasonOrdering { get; init; }
    public required string DifferentialWindowOrdering { get; init; }

    public required CoverageTotals Totals { get; init; }

    /// <summary>
    /// Every <see cref="RealRomCoverageClass"/>, always present even at zero, so a
    /// document's key set depends only on its schema version and two fixtures diff
    /// row-for-row.
    /// </summary>
    public required IReadOnlyList<CoverageClassCount> Classes { get; init; }

    /// <summary>
    /// Named rejection reasons. <see cref="RealRomCoverageClass.Lowerable"/> has no
    /// entries here — its per-opcode breakdown is already <c>report.json</c>'s
    /// <c>mnemonicMix</c>, and duplicating it would create a second place to drift.
    /// </summary>
    public required IReadOnlyList<CoverageReasonCount> Reasons { get; init; }

    public required CoverageDifferentialSection Differential { get; init; }

    public string ToCanonicalJson() => ArtifactJson.Serialize(this);
}
