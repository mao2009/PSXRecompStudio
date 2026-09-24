using PSXRecomp.Core.Diagnostics;
using PSXRecomp.Core.Execution;

namespace PSXRecomp.Tests.Diagnostics;

[Test]
public sealed class ExecutionIssueMarkdownTests
{
    private const string InputHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string FrameHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Format_CompleteReport_IsDeterministicAndContainsSanitizedEvidence()
    {
        var report = new ExecutionDiagnosticReport(
            ExecutionDiagnosticReport.CurrentSchema,
            ProductRevision: "abc123",
            InputSha256: InputHash,
            Title: "Synthetic\nTitle",
            Revision: "NTSC-J\r\nRev 1",
            ArtifactIdentity: "artifact-1",
            Outcome: RecompiledArtifactOutcome.Blocked,
            State: TitleExecutionState.UnsupportedTransfer,
            GuestPc: 0x80001234u,
            DiagnosticCode: "BIOS_HLE_UNSUPPORTED_CALL",
            SegmentBudget: 4096,
            FramebufferSha256: FrameHash);

        var first = ExecutionIssueMarkdown.Format(report);
        var second = ExecutionIssueMarkdown.Format(report);

        first.Should().Be(second);
        first.Should().Contain("- Game/title: Synthetic Title\n");
        first.Should().Contain("- Region/revision: NTSC-J Rev 1\n");
        first.Should().Contain("- Result: `Blocked` (`UnsupportedTransfer`)\n");
        first.Should().Contain("- Guest PC: `0x80001234`\n");
        first.Should().Contain("- Diagnostic code: `BIOS_HLE_UNSUPPORTED_CALL`\n");
        first.Should().Contain($"- Input SHA-256: `{InputHash}`\n");
        first.Should().Contain($"- Framebuffer SHA-256: `{FrameHash}`\n");
        first.Should().EndWith("\n");
        first.Should().NotContain("Synthetic\nTitle");
        first.Should().NotContain("DiagnosticMessage");
    }

    [Fact]
    public void Format_SparseReport_OmitsUnavailableOptionalFieldsCleanly()
    {
        var report = new ExecutionDiagnosticReport(
            ExecutionDiagnosticReport.CurrentSchema,
            ProductRevision: null,
            InputSha256: InputHash,
            Title: null,
            Revision: null,
            ArtifactIdentity: null,
            Outcome: RecompiledArtifactOutcome.Success,
            State: TitleExecutionState.Completed,
            GuestPc: null,
            DiagnosticCode: null,
            SegmentBudget: 16,
            FramebufferSha256: null);

        var markdown = ExecutionIssueMarkdown.Format(report);

        markdown.Should().Contain("- Result: `Success` (`Completed`)\n");
        markdown.Should().Contain($"- Input SHA-256: `{InputHash}`\n");
        markdown.Should().Contain("- Segment budget: `16`\n");
        markdown.Should().NotContain("Game/title:");
        markdown.Should().NotContain("Region/revision:");
        markdown.Should().NotContain("Guest PC:");
        markdown.Should().NotContain("Diagnostic code:");
        markdown.Should().NotContain("Artifact identity:");
        markdown.Should().NotContain("Framebuffer SHA-256:");
    }

    [Fact]
    public void Format_BackticksInMetadata_CannotEscapeInlineFormatting()
    {
        var report = new ExecutionDiagnosticReport(
            ExecutionDiagnosticReport.CurrentSchema,
            ProductRevision: null,
            InputSha256: InputHash,
            Title: null,
            Revision: null,
            ArtifactIdentity: "artifact`injected",
            Outcome: RecompiledArtifactOutcome.Failure,
            State: TitleExecutionState.RuntimeFailure,
            GuestPc: null,
            DiagnosticCode: "CODE`BREAK",
            SegmentBudget: 1,
            FramebufferSha256: null);

        var markdown = ExecutionIssueMarkdown.Format(report);

        markdown.Should().Contain("- Diagnostic code: `CODE'BREAK`");
        markdown.Should().Contain("- Artifact identity: `artifact'injected`");
    }
}
