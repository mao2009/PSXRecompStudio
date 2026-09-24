using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Diagnostics;

/// <summary>
/// Deterministic, bounded formatter for Issue #457's standard
/// <c>diagnostics.log</c>. It deliberately projects the richer
/// <see cref="Diagnostic"/> contract onto a small privacy-safe field set rather
/// than serializing human-readable messages, evidence, files, or recovery text.
/// </summary>
[Domain]
public static class ExecutionDiagnosticLog
{
    /// <summary>Absolute maximum number of diagnostics emitted into one standard report.</summary>
    public const int MaximumEntries = 256;

    private static readonly HashSet<string> AllowedStringContextKeys =
    [
        DiagnosticContextKeys.BiosCallKey,
        DiagnosticContextKeys.DiscHash,
        DiagnosticContextKeys.FailureKind,
    ];

    private static readonly HashSet<string> AllowedUIntContextKeys =
    [
        DiagnosticContextKeys.GuestPc,
        DiagnosticContextKeys.GuestAddress,
        DiagnosticContextKeys.InstructionOpcode,
        DiagnosticContextKeys.Count,
    ];

    /// <summary>
    /// Formats the newest <paramref name="maxEntries"/> diagnostics as a
    /// deterministic LF-delimited tail.
    /// </summary>
    public static string FormatTail(IReadOnlyList<Diagnostic> diagnostics, int maxEntries = 64)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (maxEntries is < 1 or > MaximumEntries)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxEntries),
                maxEntries,
                $"Tail length must be between 1 and {MaximumEntries}.");
        }

        var first = Math.Max(0, diagnostics.Count - maxEntries);
        var lines = new List<string>(diagnostics.Count - first);

        for (var index = first; index < diagnostics.Count; index++)
        {
            var diagnostic = diagnostics[index]
                ?? throw new ArgumentException("Diagnostic collections cannot contain null entries.", nameof(diagnostics));

            if (!diagnostic.IsValid())
            {
                throw new ArgumentException("Diagnostic collections cannot contain malformed entries.", nameof(diagnostics));
            }

            lines.Add(FormatLine(diagnostic));
        }

        return lines.Count == 0
            ? string.Empty
            : string.Join("\n", lines) + "\n";
    }

    private static string FormatLine(Diagnostic diagnostic)
    {
        var fields = new List<string>
        {
            $"severity={diagnostic.Severity}",
            $"category={diagnostic.Category}",
            $"stage={diagnostic.Stage}",
            $"code={diagnostic.Code}",
        };

        foreach (var context in diagnostic.Context
                     .Where(IsAllowedContext)
                     .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                     .ThenBy(static entry => entry.UIntValue)
                     .ThenBy(static entry => entry.StringValue, StringComparer.Ordinal))
        {
            if (context.UIntValue is uint value)
            {
                fields.Add(context.Key == DiagnosticContextKeys.Count
                    ? $"{context.Key}={value}"
                    : $"{context.Key}=0x{value:X8}");
            }
            else if (context.StringValue is string text)
            {
                fields.Add($"{context.Key}={Quote(SanitizeSingleLine(text))}");
            }
        }

        return string.Join(" ", fields);
    }

    private static bool IsAllowedContext(DiagnosticContextEntry entry) =>
        (entry.UIntValue is not null && AllowedUIntContextKeys.Contains(entry.Key))
        || (entry.StringValue is not null && AllowedStringContextKeys.Contains(entry.Key));

    private static string SanitizeSingleLine(string value) =>
        value.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\t", " ", StringComparison.Ordinal)
            .Trim();

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
