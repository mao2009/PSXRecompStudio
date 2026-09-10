using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage.AnalysisArtifacts;

/// <summary>
/// <c>report.json</c>: the per-fixture analysis summary. It restates the whole
/// pipeline — CHD container, ISO 9660 volume, SYSTEM.CNF boot record, PS-X EXE layout,
/// decode results, control-flow statistics — without any per-instruction data, so it
/// stays small enough to diff between two titles or two analyzer revisions.
///
/// The <c>*Mix</c> distributions are what make cross-title comparison practical: they
/// summarize which mnemonics, instruction formats, control-flow classes and CFG edge
/// kinds the analyzer produced, each in a fixed canonical order.
/// </summary>
[Domain]
public sealed record AnalysisReportDocument
{
    public required int SchemaVersion { get; init; }
    public required string ArtifactKind { get; init; }
    public required ArtifactFixtureIdentity Fixture { get; init; }
    public required ChdReportSection Chd { get; init; }
    public required IsoReportSection Iso { get; init; }
    public required SystemCnfReportSection SystemCnf { get; init; }
    public required ExecutableReportSection Executable { get; init; }
    public required DecodeReportSection Decode { get; init; }
    public required ControlFlowReportSection ControlFlow { get; init; }

    /// <summary>BIOS jump-table call evidence recognized in the decoded stream (schema version 2+).</summary>
    public required BiosCallReportSection BiosCalls { get; init; }
}

/// <summary>
/// Recognized PS1 BIOS jump-table call sites and their per-identity aggregation.
///
/// This section reports what the analyzed executable *asks* the BIOS for. It is
/// deliberately independent of which services the Runtime's HLE registry currently
/// implements: filtering it to supported services would make a ROM's BIOS surface appear
/// to shrink and grow with implementation progress, destroying its value as evidence
/// (Issue #279).
/// </summary>
[Domain]
public sealed record BiosCallReportSection
{
    /// <summary>Ordering label for <see cref="Sites"/>, recorded so a consumer never has to guess.</summary>
    public required string SiteOrdering { get; init; }

    /// <summary>Ordering label for <see cref="Summary"/>.</summary>
    public required string SummaryOrdering { get; init; }

    /// <summary>Total recognized call sites.</summary>
    public required int SiteCount { get; init; }

    /// <summary>Sites whose function number was statically resolved.</summary>
    public required int ResolvedSiteCount { get; init; }

    /// <summary>
    /// Sites whose vector was recognized but whose function number was not resolvable.
    /// These are recorded rather than dropped: an unresolved call is evidence.
    /// </summary>
    public required int UnresolvedSiteCount { get; init; }

    /// <summary>Every recognized call site, ordered per <see cref="SiteOrdering"/>.</summary>
    public required IReadOnlyList<BiosCallSiteRecord> Sites { get; init; }

    /// <summary>Per-identity aggregation, ordered per <see cref="SummaryOrdering"/>.</summary>
    public required IReadOnlyList<BiosCallSummaryRecord> Summary { get; init; }
}

/// <summary>
/// One recognized BIOS call site. Addresses use the canonical <c>0xXXXXXXXX</c> literal
/// form; <c>functionNumber</c> is a two-digit uppercase hex string, or <c>null</c> when
/// the number could not be resolved.
/// </summary>
[Domain]
public sealed record BiosCallSiteRecord
{
    public required string GuestPc { get; init; }
    public required string Family { get; init; }
    public required string? FunctionNumber { get; init; }
    public required string? ServiceName { get; init; }
    public required string Resolution { get; init; }
    public required string BasicBlockStartAddress { get; init; }
    public required string? ContainingFunctionAddress { get; init; }
}

/// <summary>One aggregated BIOS identity and the number of sites calling it.</summary>
[Domain]
public sealed record BiosCallSummaryRecord
{
    public required string Family { get; init; }
    public required string? FunctionNumber { get; init; }
    public required string? ServiceName { get; init; }

    /// <summary>Stable <c>Family:FunctionNumber</c> identity, or <c>Family:unresolved</c>.</summary>
    public required string StableKey { get; init; }

    public required int CallSiteCount { get; init; }
}

/// <summary>CHD container format and structural statistics.</summary>
[Domain]
public sealed record ChdReportSection
{
    public required int FormatVersion { get; init; }
    public required long LogicalBytes { get; init; }
    public required long HunkBytes { get; init; }
    public required int TotalHunks { get; init; }
    public required int CdlzHunks { get; init; }
    public required int CdzlHunks { get; init; }
    public required long MapBytesConsumed { get; init; }
    public required long DataRegionBytes { get; init; }
}

/// <summary>ISO 9660 volume identity and filesystem statistics.</summary>
[Domain]
public sealed record IsoReportSection
{
    public required string? VolumeIdentifier { get; init; }
    public required long VolumeSpaceSize { get; init; }
    public required long RootDirectoryLocation { get; init; }
    public required long RootDirectorySize { get; init; }
    public required bool SystemCnfPresent { get; init; }
    public required int FileCount { get; init; }
    public required int DirectoryCount { get; init; }
}

/// <summary>Boot record parsed from SYSTEM.CNF.</summary>
[Domain]
public sealed record SystemCnfReportSection
{
    /// <summary>Raw <c>BOOT</c> value as written on the disc, e.g. <c>cdrom:\SLPS_012.34;1</c>.</summary>
    public required string BootPath { get; init; }

    /// <summary>Boot executable file name resolved from <see cref="BootPath"/>.</summary>
    public required string BootExecutable { get; init; }
}

/// <summary>
/// PS-X EXE identity and memory layout. Addresses use the canonical
/// <c>0xXXXXXXXX</c> literal form so diffs align column-for-column.
/// </summary>
[Domain]
public sealed record ExecutableReportSection
{
    public required string FileName { get; init; }
    public required string Serial { get; init; }
    public required long FileSizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public required string EntryPoint { get; init; }
    public required string TextStart { get; init; }
    public required long TextSizeBytes { get; init; }
    public required string TextEnd { get; init; }
    public required string SpInitial { get; init; }
    public required string GpInitial { get; init; }
}

/// <summary>Linear MIPS decode results and their distributions.</summary>
[Domain]
public sealed record DecodeReportSection
{
    public required string StartAddress { get; init; }
    public required int InstructionCount { get; init; }
    public required int FailureCount { get; init; }

    /// <summary>Decode failures, ordered by address then reason (ordinal).</summary>
    public required IReadOnlyList<DecodeFailureRecord> Failures { get; init; }

    /// <summary>Ordering label for every distribution in this section.</summary>
    public required string DistributionOrdering { get; init; }

    /// <summary>Instruction count per mnemonic, ordered by mnemonic (ordinal ascending).</summary>
    public required IReadOnlyList<NamedCount> MnemonicMix { get; init; }

    /// <summary>Instruction count per instruction format, ordered by name (ordinal ascending).</summary>
    public required IReadOnlyList<NamedCount> FormatMix { get; init; }

    /// <summary>Instruction count per control-flow classification, ordered by name (ordinal ascending).</summary>
    public required IReadOnlyList<NamedCount> ControlFlowMix { get; init; }
}

/// <summary>A single failed decode attempt.</summary>
[Domain]
public sealed record DecodeFailureRecord
{
    public required string Address { get; init; }
    public required string Reason { get; init; }
}

/// <summary>Basic-block and CFG statistics.</summary>
[Domain]
public sealed record ControlFlowReportSection
{
    public required int BasicBlockCount { get; init; }
    public required int EdgeCount { get; init; }
    public required int BranchCount { get; init; }
    public required int JumpCount { get; init; }
    public required int CallCandidateCount { get; init; }
    public required int ReturnCandidateCount { get; init; }

    /// <summary>Edge count per edge kind, ordered by kind (ordinal ascending).</summary>
    public required IReadOnlyList<NamedCount> EdgeKindMix { get; init; }
}

/// <summary>
/// One bucket of a distribution. Buckets are always emitted in ordinal name order so
/// the array is a stable, diffable histogram rather than an enumeration-order artifact.
/// </summary>
[Domain]
public sealed record NamedCount
{
    public required string Name { get; init; }
    public required int Count { get; init; }
}
