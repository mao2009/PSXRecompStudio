using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Diagnostics;

/// <summary>
/// Deterministic formatter for Issue #457's ready-to-paste <c>issue.md</c>.
/// It accepts only the sanitized <see cref="ExecutionDiagnosticReport"/> model
/// and never includes free-form diagnostic messages, host paths, or raw data.
/// </summary>
[Domain]
public static class ExecutionIssueMarkdown
{
    /// <summary>Formats one sanitized execution report as a GitHub Issue body.</summary>
    public static string Format(ExecutionDiagnosticReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var lines = new List<string>
        {
            "# PSXRecompStudio execution report",
            string.Empty,
        };

        AddOptional(lines, "Game/title", report.Title);
        AddOptional(lines, "Region/revision", report.Revision);
        AddOptional(lines, "PSXRecompStudio revision", report.ProductRevision);

        lines.Add($"- Result: `{report.Outcome}` (`{report.State}`)");
        if (report.GuestPc is uint guestPc)
        {
            lines.Add($"- Guest PC: `0x{guestPc:X8}`");
        }

        AddOptional(lines, "Diagnostic code", report.DiagnosticCode, code: true);
        lines.Add($"- Input SHA-256: `{report.InputSha256}`");
        AddOptional(lines, "Artifact identity", report.ArtifactIdentity, code: true);
        AddOptional(lines, "Framebuffer SHA-256", report.FramebufferSha256, code: true);
        lines.Add($"- Segment budget: `{report.SegmentBudget}`");

        lines.Add(string.Empty);
        lines.Add("## Additional notes");
        lines.Add(string.Empty);
        lines.Add("<!-- Add any user-observed context here before submitting. -->");

        return string.Join("\n", lines) + "\n";
    }

    private static void AddOptional(List<string> lines, string label, string? value, bool code = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var sanitized = SanitizeInline(value);
        lines.Add(code
            ? $"- {label}: `{sanitized}`"
            : $"- {label}: {sanitized}");
    }

    private static string SanitizeInline(string value) =>
        value.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("`", "'", StringComparison.Ordinal)
            .Trim();
}
