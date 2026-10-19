using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #786 review (M-A, M-C): the <c>--loaded-code-diagnostics</c> wiring of the runtime host entry point. Those runs
/// need a RAM-placed code table only a firmware distribution provides, so the flag fails closed elsewhere; and a report
/// never outlives its run or passes for a complete one when the run did not finish.
/// </summary>
[Test]
public sealed class RuntimeHostLoadedCodeReportTests
{
    private const string Flag = "--loaded-code-diagnostics";

    [Fact]
    public void LoadedCodeDiagnostics_OnANonFirmwareManifest_FailsClosed_AndWritesNoReport()
    {
        using var dir = new TempDirectory();
        var manifest = dir.WriteFile("runtime-host.json", Encoding.UTF8.GetBytes("{}"));
        var report = dir.Combine("loaded.json");

        var code = PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, report, manifest]);

        code.Should().Be(78, "a non-firmware run has no RAM-placed code table to report on");
        #pragma warning disable AARC003
        File.Exists(report).Should().BeFalse("an empty, complete-looking report must not be written");
        #pragma warning restore AARC003
    }

    [Fact]
    public void LoadedCodeDiagnostics_AStaleReport_IsRemovedBeforeTheRun()
    {
        using var dir = new TempDirectory();
        var manifest = dir.WriteFile("runtime-host.json", Encoding.UTF8.GetBytes("{}"));
        var report = dir.WriteFile("loaded.json", Encoding.UTF8.GetBytes("{\"stale\":true}"));

        PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, report, manifest]);

        #pragma warning disable AARC003
        File.Exists(report).Should().BeFalse("a previous run's report must not survive into this one");
        #pragma warning restore AARC003
    }

    [Fact]
    public void LoadedCodeDiagnostics_AFailedManifestParse_WritesAReportThatSaysItDidNotExecute()
    {
        using var dir = new TempDirectory();
        dir.WriteFile(FirmwareBootManifest.FileName, Encoding.UTF8.GetBytes("{}"));
        var manifest = dir.WriteFile("runtime-host.json", Encoding.UTF8.GetBytes("not json"));
        var report = dir.Combine("loaded.json");

        var code = PSXRecomp.Infrastructure.RuntimeHost.Program.Main([Flag, report, manifest]);

        code.Should().Be(78);
        #pragma warning disable AARC003
        var json = JsonNode.Parse(File.ReadAllText(report))!.AsObject();
        #pragma warning restore AARC003
        json["executed"]!.GetValue<bool>().Should().BeFalse("the run never started");
        json["incomplete"]!.GetValue<bool>().Should().BeTrue("a run that did not execute is not a complete report");
    }

    // Issue #786 review MEDIUM-1: a host timeout cut the run short, so its report is not complete even with no gaps.
    [Fact]
    public void MarkLoadedCodeReportFinished_OnAHostTimeout_LeavesTheReportIncomplete()
    {
        var report = new LoadedCodeMismatchDiagnostics();
        var timedOut = new TitleExecutionResult(
            TitleExecutionState.RuntimeFailure, null, 0, "engine", RecompiledHostExecutionEngine.TimeoutDiagnosticCode, "timed out");

        RecompiledHostExecutionEngine.MarkLoadedCodeReportFinished(report, timedOut);

        var json = JsonNode.Parse(report.ToJson())!.AsObject();
        json["executed"]!.GetValue<bool>().Should().BeFalse("the run was stopped by the host, not classified");
        json["incomplete"]!.GetValue<bool>().Should().BeTrue();
    }

    // Issue #786 review (round 3, MAJOR): only a run that reached a completed end is executed. Every other end is an allowlist
    // miss, so a failed or budget-stopped run never reads as complete.
    [Theory]
    [InlineData(TitleExecutionState.Completed)]
    [InlineData(TitleExecutionState.Returned)]
    [InlineData(TitleExecutionState.RuntimeHandoff)]
    public void MarkLoadedCodeReportFinished_OnACompletedEnd_MarksTheReportExecuted(TitleExecutionState state)
    {
        var report = new LoadedCodeMismatchDiagnostics();
        var result = new TitleExecutionResult(state, null, 1, "engine", null, null);

        RecompiledHostExecutionEngine.MarkLoadedCodeReportFinished(report, result);

        var json = JsonNode.Parse(report.ToJson())!.AsObject();
        json["executed"]!.GetValue<bool>().Should().BeTrue();
        json["incomplete"]!.GetValue<bool>().Should().BeFalse();
    }

    [Theory]
    [InlineData(TitleExecutionState.BudgetExhausted, null)]
    [InlineData(TitleExecutionState.UnsupportedTransfer, null)]
    [InlineData(TitleExecutionState.InvalidState, null)]
    [InlineData(TitleExecutionState.RuntimeFailure, "CPU_EXCEPTION")]
    [InlineData(TitleExecutionState.RuntimeFailure, "UNRESOLVED_TRANSFER")]
    [InlineData(TitleExecutionState.RuntimeFailure, "ARTIFACT_HOST_PROTOCOL_FAILED")]
    [InlineData(TitleExecutionState.RuntimeFailure, "ENGINE_FAILED")]
    public void MarkLoadedCodeReportFinished_OnAnEndThatIsNotCompleted_LeavesTheReportIncomplete(TitleExecutionState state, string? code)
    {
        var report = new LoadedCodeMismatchDiagnostics();
        var result = new TitleExecutionResult(state, null, 1, "engine", code, "stopped");

        RecompiledHostExecutionEngine.MarkLoadedCodeReportFinished(report, result);

        var json = JsonNode.Parse(report.ToJson())!.AsObject();
        json["executed"]!.GetValue<bool>().Should().BeFalse($"{state}/{code} is not a completed end");
        json["incomplete"]!.GetValue<bool>().Should().BeTrue();
    }
}
