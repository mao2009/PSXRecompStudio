using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Execution;

/// <summary>
/// The public, serialisable view of a <see cref="RecompiledArtifactResult"/> (PR #839 ruling, Decision A). It is the only result type a
/// JSON document is built from. It carries no register value: there is no field for the result register's value, so no serialiser of this
/// type can write one. The internal result keeps the value for the host's own use; it never becomes a public field.
/// </summary>
[Domain]
public sealed record PublicRecompiledArtifactResult(
    RecompiledArtifactOutcome Outcome,
    int ExitCode,
    TitleExecutionState State,
    uint? GuestPc,
    string? EngineName,
    string? DiagnosticCode,
    string? DiagnosticMessage);

/// <summary>The public view of an internal result: every field except the result register's value.</summary>
public static class RecompiledArtifactResultPublic
{
    /// <summary>Builds the public view. The result register's value is not copied.</summary>
    public static PublicRecompiledArtifactResult ToPublic(this RecompiledArtifactResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new PublicRecompiledArtifactResult(
            result.Outcome,
            result.ExitCode,
            result.State,
            result.GuestPc,
            result.EngineName,
            result.DiagnosticCode,
            result.DiagnosticMessage);
    }
}
