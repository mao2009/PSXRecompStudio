using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Compact, deterministic summary of one real-ROM analysis run: the PASS/FAIL
/// verdict, the stage table, and — for a successful run — aggregate analysis counts.
///
/// This is the summary half of the detailed-log / summary-report split. It carries no
/// instruction listings, no ROM bytes, no timings, and no local absolute paths, so it
/// stays small and diff-comparable. The detailed per-stage log is written separately
/// by <see cref="RomAnalysisLogWriter"/>, and the full analysis content keeps the
/// existing <see cref="DiscImageAnalysisReport"/> schema.
/// </summary>
[Test]
public sealed record RomAnalysisRunSummary
{
    public required string Fixture { get; init; }

    public required string Format { get; init; }

    /// <summary>Formal input identity. Empty when the image could not be read.</summary>
    public required string DiscImageSha256 { get; init; }

    public required long DiscImageSizeBytes { get; init; }

    /// <summary>PASS, FAIL or SKIP.</summary>
    public required string Status { get; init; }

    public required string? LastSuccessfulStage { get; init; }

    public string? FailedStage { get; init; }

    public string? FailureKind { get; init; }

    public string? FailureReason { get; init; }

    public required IReadOnlyList<RomAnalysisStageSummary> Stages { get; init; }

    /// <summary>Aggregate analysis counts; present only for a run that reached REPORT.</summary>
    public RomAnalysisCounts? Counts { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, SummaryJson.Options);

    /// <summary>
    /// Projects a pipeline outcome into a summary. Only aggregate values are copied;
    /// no decoded instruction, operand text, or ROM byte is included.
    /// </summary>
    public static RomAnalysisRunSummary From(
        string fixtureName,
        RomFixtureFormat format,
        string discImageSha256,
        long discImageSizeBytes,
        RomAnalysisOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return new RomAnalysisRunSummary
        {
            Fixture = fixtureName,
            Format = format.ToString().ToUpperInvariant(),
            DiscImageSha256 = discImageSha256,
            DiscImageSizeBytes = discImageSizeBytes,
            Status = outcome.Status switch
            {
                RomAnalysisStatus.Pass => "PASS",
                RomAnalysisStatus.Fail => "FAIL",
                _ => "SKIP",
            },
            LastSuccessfulStage = outcome.LastSuccessfulStage?.ToString(),
            FailedStage = outcome.FailedStage?.ToString(),
            FailureKind = outcome.FailureKind,
            FailureReason = outcome.FailureReason,
            Stages = outcome.Stages
                .Select(s => new RomAnalysisStageSummary
                {
                    Stage = s.Stage.ToString(),
                    Status = s.Status.ToString().ToUpperInvariant(),
                    FailureKind = s.FailureKind,
                })
                .ToList(),
            Counts = outcome.Report is null ? null : RomAnalysisCounts.From(outcome.Report, outcome.DecodeFailureCount),
        };
    }
}

/// <summary>One row of the stage table: which stage, and how it ended.</summary>
[Test]
public sealed record RomAnalysisStageSummary
{
    public required string Stage { get; init; }

    public required string Status { get; init; }

    public string? FailureKind { get; init; }
}

/// <summary>
/// Aggregate analysis counts safe to quote outside the local machine (no ROM content).
/// </summary>
[Test]
public sealed record RomAnalysisCounts
{
    public required string ExecutableFileName { get; init; }

    public required string ExecutableSha256 { get; init; }

    public required uint ExecutableFileSize { get; init; }

    public required string EntryPoint { get; init; }

    public required string TextStart { get; init; }

    public required uint TextSize { get; init; }

    public required int DecodedInstructionCount { get; init; }

    public required int DecodeFailureCount { get; init; }

    public required int BasicBlockCount { get; init; }

    public required int CfgEdgeCount { get; init; }

    public required int CallCandidateCount { get; init; }

    public required int ReturnCandidateCount { get; init; }

    public static RomAnalysisCounts From(DiscImageAnalysisReport report, int decodeFailureCount)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new RomAnalysisCounts
        {
            ExecutableFileName = report.ExecutableFileName,
            ExecutableSha256 = report.ExecutableFileHash,
            ExecutableFileSize = report.ExecutableFileSize,
            EntryPoint = "0x" + report.EntryPoint.ToString("X8", CultureInfo.InvariantCulture),
            TextStart = "0x" + report.TextStart.ToString("X8", CultureInfo.InvariantCulture),
            TextSize = report.TextSize,
            DecodedInstructionCount = report.DecodedInstructionCount,
            DecodeFailureCount = decodeFailureCount,
            BasicBlockCount = report.BasicBlocks.Count,
            CfgEdgeCount = report.CfgEdges.Count,
            CallCandidateCount = report.CallCandidateCount,
            ReturnCandidateCount = report.ReturnCandidateCount,
        };
    }
}

[Test]
internal static class SummaryJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
