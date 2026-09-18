using PSXRecomp.Core.Diagnostics;

namespace PSXRecomp.Tests.Diagnostics;

/// <summary>
/// Contract tests for <see cref="DiagnosticJson"/>: a <see cref="Diagnostic"/>
/// round-trips through the canonical encoding without losing its machine
/// identity (code, severity, category, stage, context, evidence, recovery),
/// and the encoding itself is deterministic (stable key order, LF endings, no
/// timestamp/host-derived content).
/// </summary>
[Test]
public sealed class DiagnosticJsonTests
{
    private static Diagnostic FullDiagnostic() => new()
    {
        Code = DiagnosticCodes.DiscInvalidExecutable,
        Category = DiagnosticCategory.Disc,
        Severity = DiagnosticSeverity.Warning,
        Stage = DiagnosticStage.Input,
        Message = "the executable header is malformed",
        MessageKey = "disc.invalid_executable",
        Context =
        [
            new DiagnosticContextEntry(DiagnosticContextKeys.GuestPc, UIntValue: 0x8001_0000),
            new DiagnosticContextEntry(DiagnosticContextKeys.FileIdentity, StringValue: "SLUS_012.34"),
        ],
        Evidence =
        [
            new DiagnosticEvidenceReference(DiagnosticEvidenceKind.AnalysisArtifact, "artifact-hash-abc", "analysis report"),
        ],
        Recovery = DiagnosticRecovery.UserChangeThenRetry(DiagnosticRecoveryAction.Reanalyze),
    };

    [Fact]
    public void Serialize_Deserialize_RoundTripsEveryField()
    {
        var original = FullDiagnostic();

        var json = DiagnosticJson.Serialize(original);
        var restored = DiagnosticJson.Deserialize<Diagnostic>(json);

        // Diagnostic is a record whose Context/Evidence are IReadOnlyList<T>,
        // so its synthesized Equals compares those lists by reference; a
        // deserialized copy is structurally, not referentially, equal.
        restored.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Serialize_IsDeterministicAcrossRepeatedCalls()
    {
        var diagnostic = FullDiagnostic();

        var first = DiagnosticJson.Serialize(diagnostic);
        var second = DiagnosticJson.Serialize(diagnostic);

        first.Should().Be(second);
        first.Should().NotContain("\r");
        first.Should().EndWith("\n");
        first.Should().NotContain("\n\n");
    }

    [Fact]
    public void Serialize_UsesCamelCaseKeysAndWritesNulls()
    {
        var diagnostic = new Diagnostic
        {
            Code = DiagnosticCodes.DiscInputInvalid,
            Category = DiagnosticCategory.Disc,
        };

        var json = DiagnosticJson.Serialize(diagnostic);

        json.Should().Contain("\"code\"");
        json.Should().Contain("\"messageKey\": null");
        json.Should().Contain("\"message\": null");
    }

    [Fact]
    public void Deserialize_UnknownButWellFormedCode_RoundTrips()
    {
        var diagnostic = new Diagnostic
        {
            Code = DiagnosticCode.Create("FUTURE_SUBSYSTEM_CONDITION"),
            Category = DiagnosticCategory.Infrastructure,
        };

        var restored = DiagnosticJson.Deserialize<Diagnostic>(DiagnosticJson.Serialize(diagnostic));

        restored.Code.Value.Should().Be("FUTURE_SUBSYSTEM_CONDITION");
    }
}
