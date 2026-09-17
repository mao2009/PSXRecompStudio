using PSXRecomp.Architecture;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Core.Diagnostics;

/// <summary>
/// Adapts existing subsystem outcomes into a common <see cref="Diagnostic"/>,
/// so the shared contract can be produced from the machine-readable failures
/// that predate it without rewriting those subsystems' result types.
///
/// Mapping policy: existing stable string codes (for example
/// <c>OUTER_BUDGET_EXHAUSTED</c>) that already match the SCREAMING_SNAKE shape
/// are elevated verbatim; ad-hoc PascalCase classifications (for example
/// <c>ChdOpenFailure</c>) map onto a registered code with the original kind
/// preserved in context. A clean/passing outcome maps to <c>null</c> — the
/// adapter only produces diagnostics for runs that have something to diagnose.
/// </summary>
[Domain]
public static class DiagnosticAdapter
{
    /// <summary>
    /// Builds the diagnostic for a real-ROM analysis run, or <c>null</c> when
    /// the run passed cleanly. Existing outcome semantics are untouched: this
    /// is a read-only derivation over <see cref="RomAnalysisOutcome"/>'s
    /// already-machine-readable failure fields.
    /// </summary>
    public static Diagnostic? From(RomAnalysisOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return outcome.Status switch
        {
            RomAnalysisStatus.Pass => null,
            RomAnalysisStatus.Skip => SkipDiagnostic(outcome),
            _ => FailureDiagnostic(outcome),
        };
    }

    /// <summary>
    /// Builds the diagnostic for a full-title execution, or <c>null</c> when
    /// the run ended cleanly (Completed / Returned / a clean Runtime handoff).
    /// </summary>
    public static Diagnostic? From(TitleExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        switch (result.State)
        {
            case TitleExecutionState.Completed:
            case TitleExecutionState.Returned:
                return null;
            case TitleExecutionState.RuntimeHandoff when result.DiagnosticCode is null:
                return null;
        }

        var context = new List<DiagnosticContextEntry>();
        if (result.FinalSnapshot is { } snapshot)
        {
            context.Add(new DiagnosticContextEntry(DiagnosticContextKeys.GuestPc, UIntValue: snapshot.PC));
        }

        var (code, category, recovery) = Classify(result);

        return new Diagnostic
        {
            Code = code,
            Category = category,
            Stage = DiagnosticStage.Execute,
            Severity = result.State == TitleExecutionState.BudgetExhausted
                ? DiagnosticSeverity.Warning
                : DiagnosticSeverity.Error,
            Message = result.DiagnosticMessage,
            Context = context,
            Evidence =
            [
                new DiagnosticEvidenceReference(
                    DiagnosticEvidenceKind.ExecutionTrace,
                    "bounded-title-execution",
                    "Bounded per-segment execution trace of the orchestrated run."),
            ],
            Recovery = recovery,
        };
    }

    private static Diagnostic SkipDiagnostic(RomAnalysisOutcome outcome)
    {
        var reason = outcome.Stages.Count > 0 ? outcome.Stages[0].Detail : "Analysis was skipped.";
        return new Diagnostic
        {
            Code = DiagnosticCodes.DiscInputInvalid,
            Category = DiagnosticCategory.Disc,
            Stage = DiagnosticStage.Input,
            Severity = DiagnosticSeverity.Info,
            Message = reason,
            Context = [new DiagnosticContextEntry(DiagnosticContextKeys.FailureKind, StringValue: "Skipped")],
            Recovery = DiagnosticRecovery.UserChangeThenRetry(),
        };
    }

    private static Diagnostic FailureDiagnostic(RomAnalysisOutcome outcome)
    {
        var failedStage = outcome.FailedStage ?? RomAnalysisStage.Start;
        var kind = outcome.FailureKind ?? "Unknown";
        var (code, recovery) = FailureMapping(failedStage, kind);

        var context = new List<DiagnosticContextEntry>
        {
            new(DiagnosticContextKeys.FailureKind, StringValue: kind),
        };
        if (outcome.FailureException is { } exception)
        {
            context.Add(new DiagnosticContextEntry(DiagnosticContextKeys.ExceptionType, StringValue: exception.GetType().Name));
        }

        if (outcome.DecodeFailureCount != 0)
        {
            context.Add(new DiagnosticContextEntry(DiagnosticContextKeys.Count, UIntValue: (uint)outcome.DecodeFailureCount));
        }

        return new Diagnostic
        {
            Code = code,
            Category = CategoryForStage(failedStage),
            Stage = ToDiagnosticStage(failedStage),
            Severity = DiagnosticSeverity.Error,
            Message = outcome.FailureReason,
            Context = context,
            Recovery = recovery,
        };
    }

    private static (DiagnosticCode Code, DiagnosticRecovery Recovery) FailureMapping(RomAnalysisStage stage, string kind)
    {
        return stage switch
        {
            RomAnalysisStage.Input => (DiagnosticCodes.DiscInputInvalid, DiagnosticRecovery.UserChangeThenRetry()),
            RomAnalysisStage.ChdOpen => (DiagnosticCodes.DiscChdOpenFailed, DiagnosticRecovery.UserChangeThenRetry()),
            RomAnalysisStage.Filesystem => (DiagnosticCodes.DiscFilesystemFailed, DiagnosticRecovery.UserChangeThenRetry(DiagnosticRecoveryAction.Reanalyze)),
            RomAnalysisStage.SystemCnf => (DiagnosticCodes.DiscSystemCnfInvalid, DiagnosticRecovery.UserChangeThenRetry()),
            RomAnalysisStage.BootExecutable => (DiagnosticCodes.DiscBootExecutableUnreadable, DiagnosticRecovery.UserChangeThenRetry()),
            RomAnalysisStage.PsxExe or RomAnalysisStage.ExeHeader or RomAnalysisStage.EntryPoint or RomAnalysisStage.TextRegion
                => (DiagnosticCodes.DiscInvalidExecutable, DiagnosticRecovery.UserChangeThenRetry()),
            RomAnalysisStage.MipsDecode => (DiagnosticCodes.AnalyzerDecodeFailed, DiagnosticRecovery.UserChangeThenRetry(DiagnosticRecoveryAction.Reanalyze)),
            RomAnalysisStage.BasicBlock => (DiagnosticCodes.AnalyzerAnalysisFailed, DiagnosticRecovery.UserChangeThenRetry(DiagnosticRecoveryAction.Reanalyze)),
            RomAnalysisStage.Report => (DiagnosticCodes.AnalyzerReportFailed, DiagnosticRecovery.UserChangeThenRetry(DiagnosticRecoveryAction.Reanalyze)),
            _ => (DiagnosticCodes.DiscAnalysisFailed, DiagnosticRecovery.ReportBug()),
        };
    }

    private static (DiagnosticCode Code, DiagnosticCategory Category, DiagnosticRecovery Recovery) Classify(TitleExecutionResult result)
    {
        var raw = result.DiagnosticCode;
        if (raw is not null && DiagnosticCode.TryCreate(raw, out var elevated))
        {
            return (elevated, DiagnosticCategory.Runtime, RecoveryForRawCode(raw, result.State));
        }

        return result.FinalSnapshot?.Termination switch
        {
            RecompilerIrTerminationReason.UnsupportedInstruction =>
                (DiagnosticCodes.RecompUnsupportedInstruction, DiagnosticCategory.Recompiler, DiagnosticRecovery.NotRetryable()),
            RecompilerIrTerminationReason.UnsupportedIr =>
                (DiagnosticCodes.RecompUnsupportedOperation, DiagnosticCategory.Recompiler, DiagnosticRecovery.NotRetryable()),
            RecompilerIrTerminationReason.UnsupportedMemory =>
                (DiagnosticCodes.RecompUnsupportedMemory, DiagnosticCategory.Recompiler, DiagnosticRecovery.NotRetryable()),
            RecompilerIrTerminationReason.UnsupportedMmio =>
                (DiagnosticCodes.RuntimeUnsupportedMmio, DiagnosticCategory.Runtime, DiagnosticRecovery.NotRetryable()),
            RecompilerIrTerminationReason.StateMismatch =>
                (DiagnosticCodes.RecompStateMismatch, DiagnosticCategory.Recompiler, DiagnosticRecovery.NotRetryable()),
            _ => (DiagnosticCodes.RuntimeExecutionFailed, DiagnosticCategory.Runtime, DiagnosticRecovery.NotRetryable()),
        };
    }

    private static DiagnosticRecovery RecoveryForRawCode(string code, TitleExecutionState state)
    {
        return code switch
        {
            // Engine/tool mechanism failures are transient: the identical request may succeed.
            "ENGINE_FAILED" or "MISSING_SNAPSHOT" => DiagnosticRecovery.RetrySameRequest(),
            "OUTER_BUDGET_EXHAUSTED" => DiagnosticRecovery.UserChangeThenRetry(DiagnosticRecoveryAction.ChangeConfiguration),
            // Caller / handoff contract violations and unexpected engine states are bugs.
            "INVALID_CONTINUATION_TARGET" or "INVALID_HANDOFF_ACTION" or "UNEXPECTED_TERMINATION" => DiagnosticRecovery.ReportBug(),
            _ when state == TitleExecutionState.InvalidState => DiagnosticRecovery.ReportBug(),
            _ when state == TitleExecutionState.BudgetExhausted => DiagnosticRecovery.UserChangeThenRetry(DiagnosticRecoveryAction.ChangeConfiguration),
            _ => DiagnosticRecovery.NotRetryable(),
        };
    }

    private static DiagnosticCategory CategoryForStage(RomAnalysisStage stage)
    {
        return stage is RomAnalysisStage.MipsDecode or RomAnalysisStage.BasicBlock or RomAnalysisStage.Report or RomAnalysisStage.Manifest
            ? DiagnosticCategory.Analysis
            : DiagnosticCategory.Disc;
    }

    private static DiagnosticStage ToDiagnosticStage(RomAnalysisStage stage)
    {
        return stage switch
        {
            RomAnalysisStage.MipsDecode or RomAnalysisStage.BasicBlock => DiagnosticStage.Analysis,
            RomAnalysisStage.Report or RomAnalysisStage.Manifest => DiagnosticStage.Report,
            _ => DiagnosticStage.Input,
        };
    }
}