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
/// only caller-selected value it carries is the artifact path the command was
/// asked to produce.
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

    public static string Serialize<T>(T document) => ArtifactJson.Serialize(document);
}