using System.Text.Json;
using PSXRecomp.Core.Diagnostics;
using PSXRecomp.Core.Execution;

namespace PSXRecomp.Tests.Diagnostics;

[Test]
public sealed class ExecutionDiagnosticReportTests
{
    private const string InputHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string FrameHash = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public void From_MapsExistingExecutionClassificationWithoutDiagnosticMessage()
    {
        var result = new RecompiledArtifactResult(
            RecompiledArtifactOutcome.Blocked,
            RecompiledArtifactExitCode.Blocked,
            TitleExecutionState.UnsupportedTransfer,
            GuestPc: 0x80001234u,
            ResultValue: 0x42u,
            EngineName: "generated-host",
            DiagnosticCode: "BIOS_HLE_UNSUPPORTED_CALL",
            DiagnosticMessage: "sensitive free-form text must not enter report.json");

        var report = ExecutionDiagnosticReport.From(
            result,
            InputHash,
            segmentBudget: 1234,
            productRevision: "abc123",
            title: "Synthetic title",
            revision: "NTSC-J",
            artifactIdentity: "artifact-1",
            framebufferSha256: FrameHash);

        report.Schema.Should().Be(ExecutionDiagnosticReport.CurrentSchema);
        report.InputSha256.Should().Be(InputHash.ToLowerInvariant());
        report.FramebufferSha256.Should().Be(FrameHash.ToLowerInvariant());
        report.Outcome.Should().Be(result.Outcome);
        report.State.Should().Be(result.State);
        report.GuestPc.Should().Be(result.GuestPc);
        report.DiagnosticCode.Should().Be(result.DiagnosticCode);
        report.SegmentBudget.Should().Be(1234);
    }

    [Fact]
    public void DiagnosticJson_IsDeterministic_AndWritesOptionalFieldsExplicitly()
    {
        var result = new RecompiledArtifactResult(
            RecompiledArtifactOutcome.Success,
            RecompiledArtifactExitCode.Success,
            TitleExecutionState.Completed,
            GuestPc: null,
            ResultValue: null,
            EngineName: null,
            DiagnosticCode: null,
            DiagnosticMessage: null);

        var report = ExecutionDiagnosticReport.From(result, InputHash, segmentBudget: 16);

        var first = DiagnosticJson.Serialize(report);
        var second = DiagnosticJson.Serialize(report);

        first.Should().Be(second);
        using var json = JsonDocument.Parse(first);
        var root = json.RootElement;

        root.GetProperty("schema").GetString().Should().Be(ExecutionDiagnosticReport.CurrentSchema);
        root.GetProperty("inputSha256").GetString().Should().Be(InputHash.ToLowerInvariant());
        root.GetProperty("productRevision").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("title").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("revision").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("artifactIdentity").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("guestPc").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("diagnosticCode").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("framebufferSha256").ValueKind.Should().Be(JsonValueKind.Null);

        first.Should().NotContain("diagnosticMessage");
        first.Should().NotContain("resultValue");
        first.Should().NotContain("engineName");
        first.ToLowerInvariant().Should().NotContain("path");
        first.ToLowerInvariant().Should().NotContain("ram");
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    public void From_RejectsInvalidInputSha256(string hash)
    {
        var result = new RecompiledArtifactResult(
            RecompiledArtifactOutcome.Success,
            RecompiledArtifactExitCode.Success,
            TitleExecutionState.Completed,
            null, null, null, null, null);

        var act = () => ExecutionDiagnosticReport.From(result, hash, segmentBudget: 1);

        act.Should().Throw<ArgumentException>();
    }
}
