using System.Globalization;
using System.Text.Json;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Infrastructure.Execution;

namespace PSXRecomp.Tests.Cli;

/// <summary>
/// Issue #751 / PR #798: the screenshot --json result must be valid JSON for any diagnostic text or path, keep its
/// property names and meanings, and stay locale-independent.
/// </summary>
[Test]
public sealed class ScreenshotResultJsonTests
{
    private static HeadlessRunResult Result(
        TitleExecutionState state = TitleExecutionState.RuntimeHandoff,
        string stopReason = "goal_reached",
        string? diagnosticCode = null,
        string? diagnosticMessage = null,
        bool goalReached = true,
        int screenshotsTaken = 1,
        int writeFailures = 0,
        string? exceptionType = null,
        IReadOnlyList<string>? files = null,
        TimeSpan? elapsed = null,
        bool withSnapshot = false,
        ulong maxVblankReached = 1000)
    {
        var snapshot = withSnapshot
            ? new RecompilerStateSnapshot(new uint[32], 0, 0, 0x80030000u)
            : null;
        var execution = new TitleExecutionResult(
            state, snapshot, 0, InterpreterTitleExecutionEngine.EngineName, diagnosticCode, diagnosticMessage);
        return new HeadlessRunResult(
            execution,
            screenshotsTaken,
            MaxVblankReached: maxVblankReached,
            SavedFiles: files ?? [],
            ElapsedTime: elapsed ?? TimeSpan.FromMilliseconds(1234),
            StopReason: stopReason,
            ExceptionType: exceptionType,
            GoalReached: goalReached,
            WriteFailures: writeFailures);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("line one\nline two")]
    [InlineData("quote \" and backslash \\ end")]
    [InlineData("control \u0001\u001f and tab\t and cr\r")]
    [InlineData("日本語 and emoji \U0001F3AE")]
    [InlineData(@"C:\Users\user\Documents\Projects\PSXRecompStudio\shots\persona_vblank_0300.png")]
    public void DiagnosticMessage_RoundTripsThroughValidJson(string message)
    {
        var json = ScreenshotCommand.SerializeResult(null, Result(
            diagnosticCode: "CODE", diagnosticMessage: message, withSnapshot: true));

        var root = Parse(json);
        root.GetProperty("executionResult").GetProperty("diagnosticMessage").GetString().Should().Be(message);
    }

    [Fact]
    public void FilePaths_WithBackslashesQuotesAndNewlines_RoundTrip()
    {
        var files = new[] { @"C:\Users\user\shots\a b.png", "quote\"name.png", "new\nline.png" };
        var json = ScreenshotCommand.SerializeResult(null, Result(files: files));

        var names = Parse(json).GetProperty("files").EnumerateArray().Select(e => e.GetString()).ToArray();
        names.Should().Equal(files);
    }

    [Fact]
    public void RuntimeFailure_WithoutFinalSnapshot_IsValidJsonWithDiagnosticAndNoGuestPc()
    {
        // The crash shape: no FinalSnapshot, but a diagnostic message. Manual concatenation left a trailing comma here.
        var result = Result(
            state: TitleExecutionState.RuntimeFailure, stopReason: "runtime_failure", goalReached: false,
            screenshotsTaken: 0, diagnosticCode: "CRASH", diagnosticMessage: "Runtime crash: boom",
            exceptionType: "InvalidOperationException");

        var root = Parse(ScreenshotCommand.SerializeResult(null, result));

        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("executionOk").GetBoolean().Should().BeFalse();
        root.GetProperty("stopReason").GetString().Should().Be("runtime_failure");
        root.GetProperty("exceptionType").GetString().Should().Be("InvalidOperationException");
        var execution = root.GetProperty("executionResult");
        execution.GetProperty("state").GetString().Should().Be("RuntimeFailure");
        execution.GetProperty("diagnosticCode").GetString().Should().Be("CRASH");
        execution.TryGetProperty("guestPc", out _).Should().BeFalse();
    }

    [Fact]
    public void NormalCompletion_KeepsEachFieldApart()
    {
        var root = Parse(ScreenshotCommand.SerializeResult(null,
            Result(withSnapshot: true, elapsed: TimeSpan.FromMilliseconds(1234.4))));

        root.GetProperty("kind").GetString().Should().Be("screenshot");
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("executionOk").GetBoolean().Should().BeTrue();
        root.GetProperty("captureStatus").GetString().Should().Be("complete");
        root.GetProperty("writeFailures").GetInt32().Should().Be(0);
        root.GetProperty("screenshotsTaken").GetInt32().Should().Be(1);
        root.GetProperty("maxVblankReached").GetUInt64().Should().Be(1000);
        root.GetProperty("elapsedMs").GetInt64().Should().Be(1234);
        root.GetProperty("stopReason").GetString().Should().Be("goal_reached");
        root.GetProperty("goalReached").GetBoolean().Should().BeTrue();
        root.GetProperty("exceptionType").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("executionResult").GetProperty("guestPc").GetString().Should().Be("0x80030000");
    }

    [Fact]
    public void InvalidState_IsExecutionFailureAndNotSuccess()
    {
        var root = Parse(ScreenshotCommand.SerializeResult(null, Result(
            state: TitleExecutionState.InvalidState, stopReason: "invalid_state", goalReached: false,
            screenshotsTaken: 0, diagnosticCode: "BAD", diagnosticMessage: "bad \"state\"\n")));

        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("executionOk").GetBoolean().Should().BeFalse();
        root.GetProperty("stopReason").GetString().Should().Be("invalid_state");
        root.GetProperty("captureStatus").GetString().Should().Be("none");
    }

    [Fact]
    public void Timeout_WithSomeFrames_IsPartialAndNotSuccessButExecutionOk()
    {
        var root = Parse(ScreenshotCommand.SerializeResult(null, Result(
            stopReason: "timeout", goalReached: false, screenshotsTaken: 2)));

        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("executionOk").GetBoolean().Should().BeTrue();
        root.GetProperty("stopReason").GetString().Should().Be("timeout");
        root.GetProperty("captureStatus").GetString().Should().Be("partial");
        root.GetProperty("goalReached").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void WriteFailure_IsWriteFailedAndNotSuccess()
    {
        var root = Parse(ScreenshotCommand.SerializeResult(null, Result(
            stopReason: "capture_write_failed", goalReached: false, screenshotsTaken: 0, writeFailures: 1)));

        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("executionOk").GetBoolean().Should().BeTrue();
        root.GetProperty("captureStatus").GetString().Should().Be("write_failed");
        root.GetProperty("writeFailures").GetInt32().Should().Be(1);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    public void Numbers_AreLocaleIndependent(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            // withSnapshot: the no-snapshot path has a trailing comma on the pre-PR serializer, which would fail for
            // a reason other than culture. The snapshot path isolates the numeric formatting.
            var json = ScreenshotCommand.SerializeResult(null, Result(
                elapsed: TimeSpan.FromMilliseconds(1234567), screenshotsTaken: 1200, writeFailures: 0,
                maxVblankReached: 987654, withSnapshot: true));

            // Raw text: numbers must be plain digits with '.' never used as a grouping/decimal mark.
            json.Should().Contain("\"elapsedMs\":1234567,");
            json.Should().Contain("\"screenshotsTaken\":1200,");
            json.Should().Contain("\"maxVblankReached\":987654,");
            var root = Parse(json);
            root.GetProperty("elapsedMs").GetInt64().Should().Be(1234567);
            root.GetProperty("screenshotsTaken").GetInt32().Should().Be(1200);
            root.GetProperty("maxVblankReached").GetUInt64().Should().Be(987654);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
