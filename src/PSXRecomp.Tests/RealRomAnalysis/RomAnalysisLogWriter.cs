using System.Text.Json;
using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// One line of the detailed per-stage execution log.
/// </summary>
[Test]
public sealed record RomAnalysisLogEntry
{
    public required int Order { get; init; }

    public required string Stage { get; init; }

    public required string Status { get; init; }

    public string? FailureKind { get; init; }

    /// <summary>Full stage detail, or the failure reason for a failed stage.</summary>
    public required string Detail { get; init; }
}

/// <summary>
/// Writes the detailed half of the detailed-log / summary-report split: one JSON
/// object per stage, in execution order, carrying each stage's full detail text.
///
/// Detail strings are pipeline-authored metadata (counts, addresses, hashes,
/// filenames) — never ROM payload — so the log stays small and shareable, and
/// lives under the git-ignored <c>logs/</c> tree.
/// </summary>
[Test]
public static class RomAnalysisLogWriter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>
    /// Writes the stage results of <paramref name="outcome"/> as JSONL to <paramref name="path"/>,
    /// creating the containing directory if needed.
    /// </summary>
    public static void Write(string path, RomAnalysisOutcome outcome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(outcome);

#pragma warning disable PSXR005
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var writer = new StreamWriter(path, append: false);
#pragma warning restore PSXR005
        for (int i = 0; i < outcome.Stages.Count; i++)
        {
            var stage = outcome.Stages[i];
            var entry = new RomAnalysisLogEntry
            {
                Order = i,
                Stage = stage.Stage.ToString(),
                Status = stage.Status.ToString().ToUpperInvariant(),
                FailureKind = stage.FailureKind,
                Detail = stage.Detail,
            };
            writer.WriteLine(JsonSerializer.Serialize(entry, Options));
        }
    }
}
