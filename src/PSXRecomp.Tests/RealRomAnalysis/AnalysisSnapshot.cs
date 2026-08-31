using System.Text.Json;
using System.Text.Json.Serialization;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// A deterministic, machine-comparable analysis artifact for a real PS1 disc image.
/// It captures input identity, CHD / ISO / SYSTEM.CNF / PS-X EXE metadata, and a
/// summary of the MIPS decode in the entry-point vicinity.
///
/// The snapshot intentionally contains no timestamps, absolute local paths, or
/// execution-environment details, so that analyzing the same input twice (or
/// analyzing a different disc) yields stable, Git-diff-friendly output.
/// </summary>
[Test]
public sealed record AnalysisSnapshot
{
    public required int SchemaVersion { get; init; }
    public required AnalysisInputSnapshot Input { get; init; }
    public required ChdSnapshot Chd { get; init; }
    public required IsoSnapshot Iso { get; init; }
    public required SystemCnfSnapshot SystemCnf { get; init; }
    public required PsxExeSnapshot PsxExe { get; init; }
    public required AnalysisSummarySnapshot Analysis { get; init; }

    /// <summary>
    /// Decoded instructions in the entry-point vicinity (address, raw word, mnemonic).
    /// Kept bounded to the analyzed range rather than the entire code segment.
    /// </summary>
    public required IReadOnlyList<InstructionSnapshot> Instructions { get; init; }

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, SnapshotSerialization.Options);
    }
}

[Test]
public sealed record AnalysisInputSnapshot
{
    public required string Sha256 { get; init; }
    public required long Size { get; init; }
    public required string Format { get; init; }
    public required int ChdVersion { get; init; }
}

[Test]
public sealed record ChdSnapshot
{
    public required int Version { get; init; }
    public required long LogicalBytes { get; init; }
    public required long HunkBytes { get; init; }
    public required int TotalHunks { get; init; }
    public required int CdlzCount { get; init; }
    public required int CdzlCount { get; init; }
    public required long MapBytesConsumed { get; init; }
    public required long DataRegionSize { get; init; }
}

[Test]
public sealed record IsoSnapshot
{
    public required string? VolumeIdentifier { get; init; }
    public required long VolumeSpaceSize { get; init; }
    public required long RootDirectoryLocation { get; init; }
    public required bool SystemCnfExists { get; init; }
    public required int FileCount { get; init; }
    public required int DirectoryCount { get; init; }
}

[Test]
public sealed record SystemCnfSnapshot
{
    public required string BootPath { get; init; }
    public required string BootExecutable { get; init; }
}

[Test]
public sealed record PsxExeSnapshot
{
    public required string FileName { get; init; }
    public required string? Serial { get; init; }
    public required long FileSize { get; init; }
    public required string FileHash { get; init; }
    public required long EntryPoint { get; init; }
    public required long TextStart { get; init; }
    public required long TextSize { get; init; }
    public required long DataStart { get; init; }
    public required long DataSize { get; init; }
    public required long BssStart { get; init; }
    public required long BssSize { get; init; }
    public required long SpInitial { get; init; }
    public required long GpInitial { get; init; }
}

[Test]
public sealed record AnalysisSummarySnapshot
{
    public required long DecodeStartAddress { get; init; }
    public required int DecodedInstructionCount { get; init; }
    public required int DecodeFailureCount { get; init; }
    public required int BasicBlockCount { get; init; }
    public required int CfgEdgeCount { get; init; }
    public required int BranchCount { get; init; }
    public required int JumpCount { get; init; }
    public required int CallCandidateCount { get; init; }
    public required int ReturnCandidateCount { get; init; }
}

[Test]
public sealed record InstructionSnapshot
{
    public required string Address { get; init; }
    public required string RawWord { get; init; }
    public required string Mnemonic { get; init; }
}

[Test]
internal static class SnapshotSerialization
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
