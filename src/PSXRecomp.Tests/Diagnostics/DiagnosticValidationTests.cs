using PSXRecomp.Core.Diagnostics;

namespace PSXRecomp.Tests.Diagnostics;

/// <summary>
/// Contract tests for the validation entry points, <see cref="Diagnostic.IsValid"/>
/// and <see cref="DiagnosticRecovery.IsValid"/>: a malformed-but-parseable
/// document is reported invalid instead of throwing, and a recovery whose
/// action and retry semantics contradict each other is rejected while every
/// state the factory methods actually produce stays valid.
/// </summary>
[Test]
public sealed class DiagnosticValidationTests
{
    private const string MinimalBody =
        """
          "code": "DISC_INPUT_INVALID",
          "category": "Disc",
          "severity": "Error",
          "stage": "Input",
          "message": null,
          "messageKey": null,
        """;

    private static string DiagnosticJsonWith(string contextJson, string evidenceJson, string recoveryJson = """
        {
            "action": "ProvideInput",
            "retry": "RetryAfterUserChange",
            "automaticRetryAllowed": false,
            "requiresUserAction": true,
            "destructive": false
          }
        """) =>
        $$"""
        {
        {{MinimalBody}}
          "context": {{contextJson}},
          "evidence": {{evidenceJson}},
          "recovery": {{recoveryJson}}
        }
        """;

    private const string ValidContext = """[{ "key": "guestPc", "uIntValue": 32768, "stringValue": null }]""";
    private const string ValidEvidence = """[{ "kind": "AnalysisArtifact", "identifier": "abc", "description": null }]""";

    [Fact]
    public void Deserialize_NullContext_IsInvalidRatherThanThrowing()
    {
        var diagnostic = DiagnosticJson.Deserialize<Diagnostic>(DiagnosticJsonWith("null", ValidEvidence));

        diagnostic.Invoking(d => d.IsValid()).Should().NotThrow();
        diagnostic.IsValid().Should().BeFalse();
    }

    [Fact]
    public void Deserialize_NullEvidence_IsInvalidRatherThanThrowing()
    {
        var diagnostic = DiagnosticJson.Deserialize<Diagnostic>(DiagnosticJsonWith(ValidContext, "null"));

        diagnostic.Invoking(d => d.IsValid()).Should().NotThrow();
        diagnostic.IsValid().Should().BeFalse();
    }

    [Fact]
    public void Deserialize_NullContextEntry_IsInvalidRatherThanThrowing()
    {
        var diagnostic = DiagnosticJson.Deserialize<Diagnostic>(DiagnosticJsonWith("[null]", ValidEvidence));

        diagnostic.Invoking(d => d.IsValid()).Should().NotThrow();
        diagnostic.IsValid().Should().BeFalse();
    }

    [Fact]
    public void Deserialize_NullEvidenceEntry_IsInvalidRatherThanThrowing()
    {
        var diagnostic = DiagnosticJson.Deserialize<Diagnostic>(DiagnosticJsonWith(ValidContext, "[null]"));

        diagnostic.Invoking(d => d.IsValid()).Should().NotThrow();
        diagnostic.IsValid().Should().BeFalse();
    }

    [Fact]
    public void Deserialize_EmptyCollections_StayValid()
    {
        var diagnostic = DiagnosticJson.Deserialize<Diagnostic>(DiagnosticJsonWith("[]", "[]"));

        diagnostic.IsValid().Should().BeTrue();
    }

    [Fact]
    public void Deserialize_PopulatedCollections_StayValid()
    {
        var diagnostic = DiagnosticJson.Deserialize<Diagnostic>(DiagnosticJsonWith(ValidContext, ValidEvidence));

        diagnostic.IsValid().Should().BeTrue();
    }

    [Fact]
    public void Deserialize_ContradictoryRecovery_IsInvalid()
    {
        // ReportBug ("not recoverable by any local action") claiming the
        // identical request can simply be replayed.
        var diagnostic = DiagnosticJson.Deserialize<Diagnostic>(DiagnosticJsonWith(
            "[]",
            "[]",
            """
            {
                "action": "ReportBug",
                "retry": "RetrySameRequest",
                "automaticRetryAllowed": false,
                "requiresUserAction": false,
                "destructive": false
              }
            """));

        diagnostic.Invoking(d => d.IsValid()).Should().NotThrow();
        diagnostic.IsValid().Should().BeFalse();
    }

    public static TheoryData<DiagnosticRecovery> FactoryProducedRecoveries() =>
    [
        DiagnosticRecovery.NotRetryable(),
        DiagnosticRecovery.RetrySameRequest(),
        DiagnosticRecovery.UserChangeThenRetry(),
        DiagnosticRecovery.UserChangeThenRetry(DiagnosticRecoveryAction.Reanalyze),
        DiagnosticRecovery.UserChangeThenRetry(DiagnosticRecoveryAction.ChangeConfiguration),
        DiagnosticRecovery.UserChangeThenRetry(DiagnosticRecoveryAction.RetryAfterChange),
        DiagnosticRecovery.UserChangeThenRetry(DiagnosticRecoveryAction.Rebuild),
        DiagnosticRecovery.ExternalStateThenRetry(),
        DiagnosticRecovery.ExternalStateThenRetry(DiagnosticRecoveryAction.ProvideInput),
        DiagnosticRecovery.ExternalStateThenRetry(DiagnosticRecoveryAction.Retry),
        DiagnosticRecovery.ReportBug(),
        new DiagnosticRecovery(),
    ];

    [Theory]
    [MemberData(nameof(FactoryProducedRecoveries))]
    public void Recovery_EveryFactoryProducedState_IsValid(DiagnosticRecovery recovery)
    {
        recovery.IsValid().Should().BeTrue();
    }

    [Theory]
    [InlineData(DiagnosticRetrySemantics.NotRetryable)]
    [InlineData(DiagnosticRetrySemantics.RetrySameRequest)]
    [InlineData(DiagnosticRetrySemantics.RetryAfterUserChange)]
    [InlineData(DiagnosticRetrySemantics.RetryAfterExternalChange)]
    public void Recovery_EveryRetrySemantics_HasAtLeastOneValidAction(DiagnosticRetrySemantics retry)
    {
        var requiresUserAction = retry == DiagnosticRetrySemantics.RetryAfterUserChange;

        Enum.GetValues<DiagnosticRecoveryAction>()
            .Where(action => new DiagnosticRecovery(action, retry, false, requiresUserAction, false).IsValid())
            .Should().NotBeEmpty();
    }

    [Fact]
    public void Recovery_ReportBugClaimingRetry_IsInvalid()
    {
        new DiagnosticRecovery(DiagnosticRecoveryAction.ReportBug, DiagnosticRetrySemantics.RetrySameRequest)
            .IsValid().Should().BeFalse();
    }

    [Fact]
    public void Recovery_RetryActionThatIsNotRetryable_IsInvalid()
    {
        new DiagnosticRecovery(DiagnosticRecoveryAction.Retry, DiagnosticRetrySemantics.NotRetryable)
            .IsValid().Should().BeFalse();
    }

    [Fact]
    public void Recovery_NoActionRequiringUserAction_IsInvalid()
    {
        new DiagnosticRecovery(DiagnosticRecoveryAction.None, DiagnosticRetrySemantics.NotRetryable, RequiresUserAction: true)
            .IsValid().Should().BeFalse();
    }

    [Fact]
    public void Recovery_ReportBugRequiringUserAction_IsInvalid()
    {
        new DiagnosticRecovery(DiagnosticRecoveryAction.ReportBug, DiagnosticRetrySemantics.NotRetryable, RequiresUserAction: true)
            .IsValid().Should().BeFalse();
    }

    [Fact]
    public void Recovery_RetryActionAfterExternalChange_StaysValid()
    {
        // The request itself is unchanged; only external state has to change.
        new DiagnosticRecovery(DiagnosticRecoveryAction.Retry, DiagnosticRetrySemantics.RetryAfterExternalChange)
            .IsValid().Should().BeTrue();
    }

    [Fact]
    public void Recovery_UndefinedEnumValue_IsInvalid()
    {
        new DiagnosticRecovery((DiagnosticRecoveryAction)999, DiagnosticRetrySemantics.NotRetryable)
            .IsValid().Should().BeFalse();
    }
}
