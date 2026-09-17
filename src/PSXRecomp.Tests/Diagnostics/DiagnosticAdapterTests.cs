using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Diagnostics;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Diagnostics;

/// <summary>
/// Contract tests for <see cref="DiagnosticAdapter"/>: a passing outcome adapts
/// to <c>null</c>, a failing/skipped outcome preserves its code, severity,
/// category, stage, context and recovery/retry identity, and existing outcome
/// semantics (Status, FailedStage, FailureReason, ...) are left untouched.
/// </summary>
[Test]
public sealed class DiagnosticAdapterTests
{
    private static RomAnalysisOutcome PassingOutcome()
    {
        var recorder = new RomAnalysisStageRecorder();
        recorder.Pass(RomAnalysisStage.Input, "ok");
        return RomAnalysisOutcome.From(recorder);
    }

    private static RomAnalysisOutcome FailingOutcome(RomAnalysisStage stage, string kind, string reason)
    {
        var recorder = new RomAnalysisStageRecorder();
        recorder.Fail(stage, kind, reason);
        return RomAnalysisOutcome.From(recorder);
    }

    [Fact]
    public void RomAnalysisOutcome_Pass_AdaptsToNull()
    {
        var diagnostic = DiagnosticAdapter.From(PassingOutcome());

        diagnostic.Should().BeNull();
    }

    [Fact]
    public void RomAnalysisOutcome_Skip_AdaptsToInfoWithUserChangeRetry()
    {
        var outcome = RomAnalysisOutcome.Skipped("no fixture available");

        var diagnostic = DiagnosticAdapter.From(outcome);

        diagnostic.Should().NotBeNull();
        diagnostic!.Severity.Should().Be(DiagnosticSeverity.Info);
        diagnostic.Category.Should().Be(DiagnosticCategory.Disc);
        diagnostic.Recovery.Retry.Should().Be(DiagnosticRetrySemantics.RetryAfterUserChange);
        diagnostic.Recovery.RequiresUserAction.Should().BeTrue();
        diagnostic.IsValid().Should().BeTrue();

        // Existing outcome semantics untouched by adapting a diagnostic.
        outcome.Status.Should().Be(RomAnalysisStatus.Skip);
        outcome.Diagnostic.Should().BeNull();
    }

    [Theory]
    [InlineData(RomAnalysisStage.ChdOpen, DiagnosticCategory.Disc, DiagnosticStage.Input)]
    [InlineData(RomAnalysisStage.Filesystem, DiagnosticCategory.Disc, DiagnosticStage.Input)]
    [InlineData(RomAnalysisStage.SystemCnf, DiagnosticCategory.Disc, DiagnosticStage.Input)]
    [InlineData(RomAnalysisStage.MipsDecode, DiagnosticCategory.Analysis, DiagnosticStage.Analysis)]
    [InlineData(RomAnalysisStage.BasicBlock, DiagnosticCategory.Analysis, DiagnosticStage.Analysis)]
    [InlineData(RomAnalysisStage.Report, DiagnosticCategory.Analysis, DiagnosticStage.Report)]
    public void RomAnalysisOutcome_Failure_PreservesCategoryAndStage(
        RomAnalysisStage stage, DiagnosticCategory expectedCategory, DiagnosticStage expectedStage)
    {
        var outcome = FailingOutcome(stage, "SomeFailureKind", "it broke");

        var diagnostic = DiagnosticAdapter.From(outcome);

        diagnostic.Should().NotBeNull();
        diagnostic!.Category.Should().Be(expectedCategory);
        diagnostic.Stage.Should().Be(expectedStage);
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
        diagnostic.Message.Should().Be("it broke");
    }

    [Fact]
    public void RomAnalysisOutcome_Failure_PreservesFailureKindAndDecodeCountAsContext()
    {
        var recorder = new RomAnalysisStageRecorder();
        recorder.Fail(RomAnalysisStage.MipsDecode, "DecodeFailure", "could not decode");
        var outcome = RomAnalysisOutcome.From(recorder, decodeFailureCount: 3);

        var diagnostic = DiagnosticAdapter.From(outcome);

        diagnostic.Should().NotBeNull();
        diagnostic!.Context.Should().Contain(
            e => e.Key == DiagnosticContextKeys.FailureKind && e.StringValue == "DecodeFailure");
        diagnostic.Context.Should().Contain(
            e => e.Key == DiagnosticContextKeys.Count && e.UIntValue == 3);
    }

    [Fact]
    public void RomAnalysisOutcome_UnknownStage_MapsToReportBugRecovery()
    {
        var outcome = FailingOutcome(RomAnalysisStage.Start, "Unclassified", "unexpected");

        var diagnostic = DiagnosticAdapter.From(outcome);

        diagnostic.Should().NotBeNull();
        diagnostic!.Code.Should().Be(DiagnosticCodes.DiscAnalysisFailed);
        diagnostic.Recovery.Action.Should().Be(DiagnosticRecoveryAction.ReportBug);
        diagnostic.Recovery.Retry.Should().Be(DiagnosticRetrySemantics.NotRetryable);
    }

    private static TitleExecutionResult CompletedResult() =>
        new(TitleExecutionState.Completed, FinalSnapshot: null, SegmentsRetired: 1, EngineName: "engine", DiagnosticCode: null, DiagnosticMessage: null);

    [Theory]
    [InlineData(TitleExecutionState.Completed)]
    [InlineData(TitleExecutionState.Returned)]
    public void TitleExecutionResult_CleanEnd_AdaptsToNull(TitleExecutionState state)
    {
        var result = new TitleExecutionResult(state, FinalSnapshot: null, SegmentsRetired: 1, EngineName: "engine", DiagnosticCode: null, DiagnosticMessage: null);

        DiagnosticAdapter.From(result).Should().BeNull();
    }

    [Fact]
    public void TitleExecutionResult_RuntimeHandoffWithoutCode_AdaptsToNull()
    {
        var result = new TitleExecutionResult(TitleExecutionState.RuntimeHandoff, FinalSnapshot: null, SegmentsRetired: 1, EngineName: "engine", DiagnosticCode: null, DiagnosticMessage: null);

        DiagnosticAdapter.From(result).Should().BeNull();
    }

    [Theory]
    [InlineData("ENGINE_FAILED", DiagnosticRetrySemantics.RetrySameRequest, true)]
    [InlineData("MISSING_SNAPSHOT", DiagnosticRetrySemantics.RetrySameRequest, true)]
    [InlineData("OUTER_BUDGET_EXHAUSTED", DiagnosticRetrySemantics.RetryAfterUserChange, false)]
    [InlineData("INVALID_CONTINUATION_TARGET", DiagnosticRetrySemantics.NotRetryable, false)]
    [InlineData("INVALID_HANDOFF_ACTION", DiagnosticRetrySemantics.NotRetryable, false)]
    [InlineData("UNEXPECTED_TERMINATION", DiagnosticRetrySemantics.NotRetryable, false)]
    public void TitleExecutionResult_RawDiagnosticCode_ElevatesWithExpectedRetrySemantics(
        string rawCode, DiagnosticRetrySemantics expectedRetry, bool expectAutomaticRetry)
    {
        var result = new TitleExecutionResult(
            TitleExecutionState.RuntimeFailure, FinalSnapshot: null, SegmentsRetired: 2, EngineName: "engine",
            DiagnosticCode: rawCode, DiagnosticMessage: "boom");

        var diagnostic = DiagnosticAdapter.From(result);

        diagnostic.Should().NotBeNull();
        diagnostic!.Code.Value.Should().Be(rawCode);
        diagnostic.Category.Should().Be(DiagnosticCategory.Runtime);
        diagnostic.Recovery.Retry.Should().Be(expectedRetry);
        diagnostic.Recovery.AutomaticRetryAllowed.Should().Be(expectAutomaticRetry);
        diagnostic.Message.Should().Be("boom");
    }

    [Fact]
    public void TitleExecutionResult_BudgetExhausted_IsWarningWithConfigurationRecovery()
    {
        var result = new TitleExecutionResult(
            TitleExecutionState.BudgetExhausted, FinalSnapshot: null, SegmentsRetired: 5, EngineName: "engine",
            DiagnosticCode: "OUTER_BUDGET_EXHAUSTED", DiagnosticMessage: null);

        var diagnostic = DiagnosticAdapter.From(result);

        diagnostic.Should().NotBeNull();
        diagnostic!.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.Recovery.Action.Should().Be(DiagnosticRecoveryAction.ChangeConfiguration);
    }

    [Theory]
    [InlineData(RecompilerIrTerminationReason.UnsupportedInstruction, "RECOMP_UNSUPPORTED_INSTRUCTION", DiagnosticCategory.Recompiler)]
    [InlineData(RecompilerIrTerminationReason.UnsupportedIr, "RECOMP_UNSUPPORTED_OPERATION", DiagnosticCategory.Recompiler)]
    [InlineData(RecompilerIrTerminationReason.UnsupportedMemory, "RECOMP_UNSUPPORTED_MEMORY", DiagnosticCategory.Recompiler)]
    [InlineData(RecompilerIrTerminationReason.UnsupportedMmio, "RUNTIME_UNSUPPORTED_MMIO", DiagnosticCategory.Runtime)]
    [InlineData(RecompilerIrTerminationReason.StateMismatch, "RECOMP_STATE_MISMATCH", DiagnosticCategory.Recompiler)]
    public void TitleExecutionResult_TerminationReason_MapsToUnsupportedCoverageCode(
        RecompilerIrTerminationReason termination, string expectedCode, DiagnosticCategory expectedCategory)
    {
        var snapshot = new RecompilerStateSnapshot(new uint[32], hi: 0, lo: 0, pc: 0x8001_0000, termination: termination);
        var result = new TitleExecutionResult(
            TitleExecutionState.RuntimeFailure, snapshot, SegmentsRetired: 1, EngineName: "engine",
            DiagnosticCode: null, DiagnosticMessage: null);

        var diagnostic = DiagnosticAdapter.From(result);

        diagnostic.Should().NotBeNull();
        diagnostic!.Code.Value.Should().Be(expectedCode);
        diagnostic.Category.Should().Be(expectedCategory);
        diagnostic.Recovery.Retry.Should().Be(DiagnosticRetrySemantics.NotRetryable);
        diagnostic.Context.Should().Contain(e => e.Key == DiagnosticContextKeys.GuestPc && e.UIntValue == 0x8001_0000);
    }
}
