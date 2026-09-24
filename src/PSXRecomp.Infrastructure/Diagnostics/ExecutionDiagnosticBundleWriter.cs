using System.IO.Compression;
using System.Text;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Diagnostics;

namespace PSXRecomp.Infrastructure.Diagnostics;

/// <summary>
/// Packages Issue #457's already-sanitized diagnostic contracts into the
/// standard shareable ZIP bundle. This adapter owns only archive encoding:
/// report selection/sanitization remains in the Domain-owned report, log, and
/// issue-markdown contracts, and host probing remains in
/// <see cref="ExecutionEnvironmentCollector"/>.
/// </summary>
[Infrastructure]
public static class ExecutionDiagnosticBundleWriter
{
    public const string ReportEntryName = "report.json";
    public const string EnvironmentEntryName = "environment.json";
    public const string DiagnosticsEntryName = "diagnostics.log";
    public const string IssueEntryName = "issue.md";
    public const int DefaultDiagnosticEntries = 64;

    private static readonly DateTimeOffset CanonicalEntryTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly UTF8Encoding Utf8NoBom =
        new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes the four-file Alpha diagnostic bundle to
    /// <paramref name="destination"/> and leaves the destination stream open.
    /// </summary>
    public static void Write(
        Stream destination,
        ExecutionDiagnosticReport report,
        ExecutionEnvironmentReport environment,
        IReadOnlyList<Diagnostic> diagnostics,
        int maxDiagnosticEntries = DefaultDiagnosticEntries)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (!destination.CanWrite)
        {
            throw new ArgumentException("Destination stream must be writable.", nameof(destination));
        }

        var reportJson = DiagnosticJson.Serialize(report);
        var environmentJson = DiagnosticJson.Serialize(environment);
        var diagnosticLog = ExecutionDiagnosticLog.FormatTail(diagnostics, maxDiagnosticEntries);
        var issueMarkdown = ExecutionIssueMarkdown.Format(report);

        using var archive = new ZipArchive(
            destination,
            ZipArchiveMode.Create,
            leaveOpen: true,
            entryNameEncoding: Encoding.UTF8);

        WriteEntry(archive, ReportEntryName, reportJson);
        WriteEntry(archive, EnvironmentEntryName, environmentJson);
        WriteEntry(archive, DiagnosticsEntryName, diagnosticLog);
        WriteEntry(archive, IssueEntryName, issueMarkdown);
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = CanonicalEntryTimestamp;

        using var stream = entry.Open();
        var bytes = Utf8NoBom.GetBytes(content);
        stream.Write(bytes);
    }
}
