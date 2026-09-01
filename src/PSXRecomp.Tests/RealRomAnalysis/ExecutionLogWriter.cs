using System.Text.Json;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// An execution-stage log entry for debugging. Unlike the deterministic
/// <see cref="AnalysisSnapshot"/>, log entries may carry timing information.
/// </summary>
[Test]
public sealed record ExecutionLogEntry
{
    public required string Stage { get; init; }
    public required string Status { get; init; }
    public required string Message { get; init; }

    /// <summary>Milliseconds since the log session started (relative, not wall-clock).</summary>
    public double ElapsedMs { get; init; }
}

/// <summary>
/// Writes execution-log lines (JSONL) for a single real-ROM analysis run.
/// One JSON object per line, ordered so results are easy to scan.
/// </summary>
[Test]
public static class ExecutionLogWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
    };

    /// <summary>
    /// Writes the given entries as JSONL to <paramref name="path"/>.
    ///
    /// Persisted log lines are sanitized at this serialization boundary: no absolute
    /// Windows or POSIX path may appear in the file. Runtime code (the recorder / stage
    /// details) keeps raw exception messages; only what is persisted is redacted.
    /// </summary>
    public static void Write(string path, IReadOnlyList<ExecutionLogEntry> entries)
    {
#pragma warning disable PSXR005
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var writer = new StreamWriter(path, append: false);
        foreach (var entry in entries)
        {
            writer.WriteLine(JsonSerializer.Serialize(Sanitize(entry), Options));
        }
#pragma warning restore PSXR005
    }

    private static ExecutionLogEntry Sanitize(ExecutionLogEntry entry)
    {
        var message = PathRedactor.Redact(entry.Message);
        return message == entry.Message ? entry : entry with { Message = message };
    }
}
