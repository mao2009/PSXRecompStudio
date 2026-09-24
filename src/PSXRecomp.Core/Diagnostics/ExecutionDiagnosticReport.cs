using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;

namespace PSXRecomp.Core.Diagnostics;

/// <summary>
/// Privacy-safe machine-readable execution evidence for Issue #457's
/// <c>report.json</c>. The schema deliberately contains identifiers and
/// classifications only: there is no field for input payloads, RAM, save/card
/// data, credentials, usernames, local paths, or free-form diagnostic text.
/// </summary>
[Domain]
public sealed record ExecutionDiagnosticReport(
    string Schema,
    string? ProductRevision,
    string InputSha256,
    string? Title,
    string? Revision,
    string? ArtifactIdentity,
    RecompiledArtifactOutcome Outcome,
    TitleExecutionState State,
    uint? GuestPc,
    string? DiagnosticCode,
    uint SegmentBudget,
    string? FramebufferSha256)
{
    /// <summary>The stable schema identity for the first Alpha report contract.</summary>
    public const string CurrentSchema = "psxrecomp.execution-report.v1";

    /// <summary>
    /// Creates a report from the existing runnable-artifact result without
    /// redefining or reclassifying execution semantics.
    /// </summary>
    public static ExecutionDiagnosticReport From(
        RecompiledArtifactResult result,
        string inputSha256,
        uint segmentBudget,
        string? productRevision = null,
        string? title = null,
        string? revision = null,
        string? artifactIdentity = null,
        string? framebufferSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (segmentBudget == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentBudget), "Segment budget must be positive.");
        }

        return new ExecutionDiagnosticReport(
            CurrentSchema,
            productRevision,
            NormalizeSha256(inputSha256, nameof(inputSha256)),
            title,
            revision,
            artifactIdentity,
            result.Outcome,
            result.State,
            result.GuestPc,
            result.DiagnosticCode,
            segmentBudget,
            framebufferSha256 is null
                ? null
                : NormalizeSha256(framebufferSha256, nameof(framebufferSha256)));
    }

    private static string NormalizeSha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64 || value.Any(static c => !Uri.IsHexDigit(c)))
        {
            throw new ArgumentException("Expected a 64-character SHA-256 hexadecimal value.", parameterName);
        }

        return value.ToLowerInvariant();
    }
}
