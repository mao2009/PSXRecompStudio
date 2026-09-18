using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Execution;

/// <summary>
/// The stable, machine-readable result of launching one runnable recompiled
/// artifact (Issue #459): the full-fidelity <see cref="TitleExecutionResult"/>
/// plus the coarse three-way classification and process exit code #460's CLI
/// consumes. This is the JSON serialization boundary — see
/// <c>PSXRecomp.Infrastructure</c>'s launcher, which produces one from a real
/// run via <c>PSXRecomp.Core.DiscImage.AnalysisArtifacts.ArtifactJson</c>'s
/// existing canonical encoding.
/// </summary>
[Domain]
public sealed record RecompiledArtifactResult(
    RecompiledArtifactOutcome Outcome,
    int ExitCode,
    TitleExecutionState State,
    uint? GuestPc,
    uint? ResultValue,
    string? EngineName,
    string? DiagnosticCode,
    string? DiagnosticMessage)
{
    /// <summary>Builds the stable result from a full-title execution outcome.</summary>
    /// <param name="execution">The classified outcome from <see cref="ExecutionOrchestrator"/>.</param>
    /// <param name="resultRegister">
    /// Index into <see cref="RecompilerStateSnapshot.Gpr"/> the caller treats as the
    /// generated program's observable result marker (e.g. V0); null when the caller
    /// has none to report.
    /// </param>
    public static RecompiledArtifactResult From(TitleExecutionResult execution, int? resultRegister = null)
    {
        ArgumentNullException.ThrowIfNull(execution);

        uint? resultValue = resultRegister is int index
            && execution.FinalSnapshot is { } snapshot
            && index >= 0
            && index < snapshot.Gpr.Count
                ? snapshot.Gpr[index]
                : null;

        return new RecompiledArtifactResult(
            RecompiledArtifactExitCode.Classify(execution.State),
            RecompiledArtifactExitCode.ExitCodeFor(execution.State),
            execution.State,
            execution.FinalSnapshot?.PC,
            resultValue,
            execution.EngineName,
            execution.DiagnosticCode,
            execution.DiagnosticMessage);
    }
}
