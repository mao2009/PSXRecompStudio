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

    /// <summary>
    /// <see cref="Serialize{T}(IReadOnlyList{uint}, T)"/> plus, only when mixed execution was enabled (Issue #693), a
    /// <c>mixedFallback</c> object with the run's deterministic evidence (counts and per-target totals; no timings), so
    /// the output alone says what the interpreter did. With no evidence the output is byte-identical to the overload above.
    /// </summary>
    public static string Serialize<T>(IReadOnlyList<uint> entryRoots, MixedFallbackEvidence? mixedFallback, T document)
    {
        var json = Serialize(entryRoots, document);
        if (mixedFallback is null)
        {
            return json;
        }

        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        var targets = new System.Text.Json.Nodes.JsonArray();
        foreach (var target in mixedFallback.Targets)
        {
            targets.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["target"] = $"0x{target.Target:X8}",
                ["entries"] = target.Entries,
                ["instructions"] = target.Instructions,
                ["lastReturnPc"] = $"0x{target.LastReturnPc:X8}",
            });
        }

        node["mixedFallback"] = new System.Text.Json.Nodes.JsonObject
        {
            ["transitions"] = mixedFallback.Transitions,
            ["returns"] = mixedFallback.Returns,
            ["fallbackInstructions"] = mixedFallback.FallbackInstructions,
            ["pagesToInterpreter"] = mixedFallback.PagesToInterpreter,
            ["pagesToArtifact"] = mixedFallback.PagesToArtifact,
            ["targets"] = targets,
        };
        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        return node.ToJsonString(options).ReplaceLineEndings("\n") + "\n";
    }
}