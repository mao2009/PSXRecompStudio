using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Recompiler;

/// <summary>
/// Classifies the outcome of compiling and linking generated host C source into
/// a native artifact. Distinct from <see cref="RecompilerExecutionStatus"/>,
/// which additionally covers running the artifact; this only covers producing
/// it (Issue #458).
/// </summary>
[Domain]
public enum GeneratedHostBuildStatus : byte
{
    /// <summary>The source was compiled and linked into a native artifact.</summary>
    Succeeded,

    /// <summary>The requested compiler executable could not be started.</summary>
    ToolchainUnavailable,

    /// <summary>The compile step reported a failure.</summary>
    CompileFailed,

    /// <summary>The link step reported a failure.</summary>
    LinkFailed,

    /// <summary>A compile or link step exceeded its bounded time budget.</summary>
    TimedOut,

    /// <summary>
    /// The output directory could not be created or the generated source could
    /// not be written (e.g. an inaccessible path or a busy/locked file). The
    /// caller-owned output location was not usable; no artifact was produced.
    /// </summary>
    OutputFailed,
}

/// <summary>The native artifact produced by a successful build.</summary>
[Domain]
public sealed record GeneratedHostBuildArtifact(string BinaryPath, string SourcePath);

/// <summary>
/// A request to compile and link generated host C source into a native
/// artifact at a caller-selected location. The caller owns
/// <see cref="OutputDirectory"/>'s lifecycle; this request carries no
/// temporary-workspace concept of its own.
/// </summary>
[Domain]
public sealed record GeneratedHostBuildRequest(
    string Source,
    string OutputDirectory,
    string BinaryName,
    string? CompilerExecutable = null,
    IReadOnlyList<string>? ExtraCompilerArguments = null);

/// <summary>The outcome of one <see cref="IGeneratedHostBuildService"/> build.</summary>
[Domain]
public sealed record GeneratedHostBuildResult(
    GeneratedHostBuildStatus Status,
    GeneratedHostBuildArtifact? Artifact,
    string? DiagnosticCode,
    string? DiagnosticMessage)
{
    /// <summary>Convenience: a build that produced a native artifact.</summary>
    public static GeneratedHostBuildResult Succeeded(GeneratedHostBuildArtifact artifact) =>
        new(GeneratedHostBuildStatus.Succeeded, artifact, null, null);

    /// <summary>Convenience: a build failure with a machine-readable code.</summary>
    public static GeneratedHostBuildResult Failed(
        GeneratedHostBuildStatus status, string diagnosticCode, string diagnosticMessage) =>
        new(status, null, diagnosticCode, diagnosticMessage);
}

/// <summary>
/// Domain-owned port for compiling and linking generated host C source into a
/// native artifact (Issue #458). The concrete adapter invokes an external
/// compiler/linker toolchain and lives in managed Infrastructure per
/// ADR-015/ADR-017; Domain and Application depend only on this contract.
/// </summary>
[Domain]
public interface IGeneratedHostBuildService
{
    /// <summary>Compiles and links the request's source, or reports a structured failure.</summary>
    GeneratedHostBuildResult Build(GeneratedHostBuildRequest request);
}
