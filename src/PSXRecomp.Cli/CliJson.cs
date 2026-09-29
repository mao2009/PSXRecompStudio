using PSXRecomp.Architecture;
using PSXRecomp.Core.DiscImage.AnalysisArtifacts;
using PSXRecomp.Core.Execution;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// The deterministic machine-readable envelopes for the two commands, serialized
/// with the repository's canonical JSON encoder (<see cref="ArtifactJson"/>):
/// camelCase, LF, two-space indentation, explicit nulls, and no timestamps or
/// environment data. The run envelope nests the production
/// <see cref="RecompiledArtifactResult"/> — <em>the</em> #459 termination
/// representation — rather than defining a CLI-specific termination model; the
/// ordinary run envelope keeps the pre-report field set unchanged; the
/// report-aware variant adds only the caller-visible diagnostic bundle path.
/// </summary>
[Infrastructure]
internal static class CliJson
{
    public const string RecompileKind = "recompile";
    public const string RunKind = "run";

    [Infrastructure]
    public sealed record RecompileResult(
        string Kind,
        bool Success,
        string Status,
        string? Artifact,
        string? ErrorCode,
        string? Message);

    [Infrastructure]
    public sealed record RunResult(
        string Kind,
        bool Success,
        string? Artifact,
        IReadOnlyList<byte> Output,
        RecompiledArtifactResult Result);

    /// <summary>
    /// Extended run envelope used only when <c>--report</c> is requested.
    /// Keeping it separate preserves the exact pre-#457 JSON field set for
    /// ordinary <c>run --json</c> invocations.
    /// </summary>
    [Infrastructure]
    public sealed record RunResultWithDiagnosticBundle(
        string Kind,
        bool Success,
        string? Artifact,
        IReadOnlyList<byte> Output,
        RecompiledArtifactResult Result,
        string DiagnosticBundle);

    [Infrastructure]
    public sealed record RunResultWithFrameEvidence(
        string Kind,
        bool Success,
        string? Artifact,
        IReadOnlyList<byte> Output,
        RecompiledArtifactResult Result,
        ProductionFrameEvidenceCollector.FrameEvidence FrameEvidence);

    [Infrastructure]
    public sealed record RunResultWithDiagnosticBundleAndFrameEvidence(
        string Kind,
        bool Success,
        string? Artifact,
        IReadOnlyList<byte> Output,
        RecompiledArtifactResult Result,
        string DiagnosticBundle,
        ProductionFrameEvidenceCollector.FrameEvidence FrameEvidence);

    public static string Serialize<T>(T document) => ArtifactJson.Serialize(document);

    /// <summary>
    /// Serializes <paramref name="document"/> and, only when the caller supplied explicit
    /// entry roots (Issue #644), appends an <c>entryRoots</c> array of canonical
    /// <c>0xXXXXXXXX</c> strings (ascending, distinct) so the output alone reproduces the run's
    /// reachability input. With no roots the output is byte-identical to <see cref="Serialize{T}(T)"/>.
    /// </summary>
    public static string Serialize<T>(IReadOnlyList<uint> entryRoots, T document)
    {
        var json = ArtifactJson.Serialize(document);
        if (entryRoots.Count == 0)
        {
            return json;
        }

        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        var roots = new System.Text.Json.Nodes.JsonArray();
        foreach (var root in entryRoots.Distinct().Order())
        {
            roots.Add($"0x{root:X8}");
        }

        node["entryRoots"] = roots;
        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        return node.ToJsonString(options).ReplaceLineEndings("\n") + "\n";
    }
}