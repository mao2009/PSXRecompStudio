using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure;
using PSXRecomp.Infrastructure.Standalone;
using System.Text.Json;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #763: the packaged runtime host's opt-in stop diagnostic. It must report what the host genuinely
/// measured and leave everything it cannot see outside the artifact <c>null</c>, never guessed.
/// </summary>
[Test]
public sealed class RuntimeHostDiagnosticTests
{
    private const uint Load = 0x80010000u;

    private static RuntimeHostManifest Manifest(uint[] blocks) => new(
        RuntimeHostManifest.CurrentSchemaVersion,
        "../game.exe",
        GuestFirmware: false,
        MixedFallback: true,
        FallbackSegmentInstructionBudget: 1000,
        FallbackMaxTransitions: 100,
        ImageLoadAddress: Load,
        Image: "game.bin",
        BlockEntryPcs: blocks,
        ProgramEnd: 0x80030000u,
        ResultRegister: 2,
        RunTimeoutMs: 1000,
        EntryPc: Load);

    private static TitleExecutionResult Stop(uint pc, string? code, RecompilerIrTerminationReason termination) => new(
        TitleExecutionState.UnsupportedTransfer,
        new RecompilerStateSnapshot(new uint[32], 0, 0, pc, termination: termination),
        1,
        "engine",
        code,
        "message");

    [Fact]
    public void DeclinedInImageTransfer_ReportsTheTransferAndTheHostMeasuredCounts()
    {
        var diagnostic = RuntimeHostDiagnostic.From(
            Manifest([Load]),
            Stop(0x800256E0u, "UNRESOLVED_TRANSFER_IN_IMAGE", RecompilerIrTerminationReason.Success),
            exitCode: 2,
            aotRetiredInstructions: 1234,
            fallbackRetiredInstructions: 56,
            fallbackTransitionCount: 7,
            declinedTransfer: new FallbackDeclinedTransfer(0x800256E0u, true, MixedFallbackDiagnostics.IneligibleNotIndirect));

        diagnostic.FinalPc.Should().Be(0x800256E0u);
        diagnostic.TransferTargetPc.Should().Be(0x800256E0u);
        diagnostic.TargetInsideImage.Should().BeTrue();
        diagnostic.TargetHasCompiledBlock.Should().BeFalse();
        diagnostic.FallbackEnabled.Should().BeTrue();
        diagnostic.FallbackAttempted.Should().BeTrue();
        diagnostic.FallbackOutcome.Should().Be(RuntimeHostDiagnostic.IneligibleOutcome);
        diagnostic.FallbackRejectReason.Should().Be(MixedFallbackDiagnostics.IneligibleNotIndirect);
        diagnostic.AotRetiredInstructions.Should().Be(1234);
        diagnostic.FallbackRetiredInstructions.Should().Be(56);
        diagnostic.FallbackTransitionCount.Should().Be(7);
        diagnostic.ClassifiedExitCode.Should().Be(2);
        diagnostic.NativeTerminationReason.Should().Be(nameof(RecompilerIrTerminationReason.Success));
        diagnostic.OrchestratorState.Should().Be(nameof(TitleExecutionState.UnsupportedTransfer));
        diagnostic.OrchestratorDiagnosticCode.Should().Be("UNRESOLVED_TRANSFER_IN_IMAGE");
        diagnostic.TimedOut.Should().BeFalse();

        // Not on the artifact's wire protocol: left null rather than guessed.
        diagnostic.TransferSourcePc.Should().BeNull();
        diagnostic.TransferOpcode.Should().BeNull();
        diagnostic.LastAotBlockPc.Should().BeNull();
    }

    [Fact]
    public void RunMetrics_AreReportedAsMeasured_AndSerializedCamelCase()
    {
        var metrics = RuntimeHostRunMetrics.From(
            wallMilliseconds: 1500.5,
            roundTrips: (MmioAccesses: 40, TimeReports: 90),
            timings: new MixedFallbackTimings(TransferMilliseconds: 2.5, FallbackMilliseconds: 7.5));

        var diagnostic = RuntimeHostDiagnostic.From(
            Manifest([Load]),
            Stop(Load, null, RecompilerIrTerminationReason.Success) with { State = TitleExecutionState.Completed },
            exitCode: 0,
            aotRetiredInstructions: 10,
            fallbackRetiredInstructions: null,
            fallbackTransitionCount: null,
            declinedTransfer: null,
            metrics: metrics);

        diagnostic.Metrics.Should().Be(new RuntimeHostRunMetrics(1500.5, 40, 90, 2.5, 7.5));
        using var json = JsonDocument.Parse(RuntimeHostDiagnosticJson.Serialize(diagnostic));
        var written = json.RootElement.GetProperty("metrics");
        written.GetProperty("wallMilliseconds").GetDouble().Should().Be(1500.5);
        written.GetProperty("mmioAccesses").GetUInt64().Should().Be(40UL);
        written.GetProperty("timeReports").GetUInt64().Should().Be(90UL);
    }

    [Fact]
    public void RunMetrics_WithoutTheProtocolOrFallback_LeaveThoseFieldsNull()
    {
        var metrics = RuntimeHostRunMetrics.From(10, roundTrips: null, timings: null);

        metrics.MmioAccesses.Should().BeNull();
        metrics.TimeReports.Should().BeNull();
        metrics.FallbackTransferMilliseconds.Should().BeNull();
        metrics.FallbackInterpreterMilliseconds.Should().BeNull();
        metrics.FallbackInFlight.Should().BeNull();
    }

    [Fact]
    public void RunMetrics_CarryTheFallbackSegmentInFlight_AsPublished()
    {
        // Issue #806: a stuck segment's published scalars reach the diagnostic unchanged.
        var inFlight = new FallbackSegmentProgress(0x80010000u, 0x80010010u, 42, 1000);

        var metrics = RuntimeHostRunMetrics.From(10, roundTrips: null, timings: null, fallbackInFlight: inFlight);

        metrics.FallbackInFlight.Should().Be(inFlight);
    }

    [Fact]
    public void NoDecline_ReportsNullTransferFields()
    {
        var diagnostic = RuntimeHostDiagnostic.From(
            Manifest([Load]),
            Stop(Load, null, RecompilerIrTerminationReason.Success) with { State = TitleExecutionState.Completed },
            exitCode: 0,
            aotRetiredInstructions: 10,
            fallbackRetiredInstructions: null,
            fallbackTransitionCount: null,
            declinedTransfer: null);

        diagnostic.TransferTargetPc.Should().BeNull();
        diagnostic.TargetInsideImage.Should().BeNull();
        diagnostic.TargetHasCompiledBlock.Should().BeNull();
        diagnostic.FallbackAttempted.Should().BeNull();
        diagnostic.FallbackOutcome.Should().BeNull();
        diagnostic.FallbackRejectReason.Should().BeNull();
    }

    [Fact]
    public void CompletedAfterDeclinedEndMarker_DoesNotReportAnIneligibleTransfer()
    {
        // The program-end handoff may complete after the artifact offered and the host declined PC 0.
        // That decline is not an UnsupportedTransfer stop and must not pollute the completed-run JSON.
        var completed = Stop(0u, null, RecompilerIrTerminationReason.Success)
            with { State = TitleExecutionState.Completed };
        var diagnostic = RuntimeHostDiagnostic.From(
            Manifest([Load]),
            completed,
            exitCode: 0,
            aotRetiredInstructions: 10,
            fallbackRetiredInstructions: 2,
            fallbackTransitionCount: 1,
            declinedTransfer: new FallbackDeclinedTransfer(0u, true, MixedFallbackDiagnostics.IneligibleOutOfImage));

        diagnostic.FinalPc.Should().Be(0u);
        diagnostic.OrchestratorState.Should().Be(nameof(TitleExecutionState.Completed));
        diagnostic.TransferTargetPc.Should().BeNull();
        diagnostic.TargetInsideImage.Should().BeNull();
        diagnostic.TargetHasCompiledBlock.Should().BeNull();
        diagnostic.FallbackAttempted.Should().BeNull();
        diagnostic.FallbackOutcome.Should().BeNull();
        diagnostic.FallbackRejectReason.Should().BeNull();
        diagnostic.AotRetiredInstructions.Should().Be(10);
        diagnostic.FallbackRetiredInstructions.Should().Be(2);
    }

    [Fact]
    public void UnsupportedTransfer_WithStaleDecline_UsesFinalPcWithoutInventingRejectReason()
    {
        const uint stoppedPc = 0x800256E0u;
        var diagnostic = RuntimeHostDiagnostic.From(
            Manifest([Load]),
            Stop(stoppedPc, "UNRESOLVED_TRANSFER_IN_IMAGE", RecompilerIrTerminationReason.Success),
            exitCode: 2,
            aotRetiredInstructions: 123,
            fallbackRetiredInstructions: 5,
            fallbackTransitionCount: 1,
            declinedTransfer: new FallbackDeclinedTransfer(0x80020000u, true, MixedFallbackDiagnostics.IneligibleNotIndirect));

        diagnostic.TransferTargetPc.Should().Be(stoppedPc);
        diagnostic.TargetInsideImage.Should().BeTrue();
        diagnostic.FallbackAttempted.Should().BeNull();
        diagnostic.FallbackOutcome.Should().BeNull();
        diagnostic.FallbackRejectReason.Should().BeNull();
    }

    [Fact]
    public void Timeout_IsFlagged()
    {
        var diagnostic = RuntimeHostDiagnostic.From(
            Manifest([Load]),
            Stop(Load, RecompiledHostExecutionEngine.TimeoutDiagnosticCode, RecompilerIrTerminationReason.Success),
            exitCode: 2,
            aotRetiredInstructions: null,
            fallbackRetiredInstructions: null,
            fallbackTransitionCount: null,
            declinedTransfer: null);

        diagnostic.TimedOut.Should().BeTrue();
        diagnostic.AotRetiredInstructions.Should().BeNull();
    }

    [Fact]
    public void TimeoutWithoutSnapshot_LeavesUnknownValuesNull_NeverZero()
    {
        var timedOut = new TitleExecutionResult(
            TitleExecutionState.RuntimeFailure, null, 0, "engine", RecompiledHostExecutionEngine.TimeoutDiagnosticCode, "timed out");

        var diagnostic = RuntimeHostDiagnostic.From(
            Manifest([Load]),
            timedOut,
            exitCode: 1,
            aotRetiredInstructions: null,
            fallbackRetiredInstructions: null,
            fallbackTransitionCount: null,
            declinedTransfer: null);

        diagnostic.TimedOut.Should().BeTrue();
        diagnostic.FinalPc.Should().BeNull("a run with no snapshot has no known pc");
        diagnostic.TransferTargetPc.Should().BeNull();
        diagnostic.NativeTerminationReason.Should().BeNull();
        diagnostic.AotRetiredInstructions.Should().BeNull();
        diagnostic.FallbackRetiredInstructions.Should().BeNull();
        diagnostic.ClassifiedExitCode.Should().Be(1);
        diagnostic.OrchestratorState.Should().Be(nameof(TitleExecutionState.RuntimeFailure));
    }

    [Fact]
    public void RuntimeFailureWithoutSnapshot_KeepsTheClassification()
    {
        var failed = new TitleExecutionResult(
            TitleExecutionState.RuntimeFailure, null, 0, "engine", "ARTIFACT_HOST_PROTOCOL_FAILED", "protocol");

        var diagnostic = RuntimeHostDiagnostic.From(
            Manifest([Load]), failed, exitCode: 1, null, null, null, declinedTransfer: null);

        diagnostic.FinalPc.Should().BeNull();
        diagnostic.TimedOut.Should().BeFalse();
        diagnostic.OrchestratorDiagnosticCode.Should().Be("ARTIFACT_HOST_PROTOCOL_FAILED");
    }

    [Fact]
    public void FromFailure_CarriesOnlyTheManifestFactsAndTheExceptionType()
    {
        var diagnostic = RuntimeHostDiagnostic.FromFailure(Manifest([Load]), 78, new InvalidOperationException("boom"));

        diagnostic.ClassifiedExitCode.Should().Be(78);
        diagnostic.FailureExceptionType.Should().Be(typeof(InvalidOperationException).FullName);
        diagnostic.FallbackEnabled.Should().BeTrue();
        diagnostic.OrchestratorState.Should().BeNull("no orchestration result existed");
        diagnostic.FinalPc.Should().BeNull();
        diagnostic.AotRetiredInstructions.Should().BeNull();
    }

    [Fact]
    public void Serialize_IsCanonicalCamelCaseJson_WithNullsPreserved()
    {
        var diagnostic = RuntimeHostDiagnostic.From(
            Manifest([Load]),
            Stop(0x800256E0u, "UNRESOLVED_TRANSFER_IN_IMAGE", RecompilerIrTerminationReason.Success),
            exitCode: 2,
            aotRetiredInstructions: 1234,
            fallbackRetiredInstructions: null,
            fallbackTransitionCount: null,
            declinedTransfer: new FallbackDeclinedTransfer(0x800256E0u, true, MixedFallbackDiagnostics.IneligibleNotIndirect));

        var json = RuntimeHostDiagnosticJson.Serialize(diagnostic);

        json.Should().EndWith("\n").And.NotContain("\r");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.GetProperty("transferTargetPc").GetUInt32().Should().Be(0x800256E0u);
        root.GetProperty("transferSourcePc").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("fallbackRejectReason").GetString().Should().Be(MixedFallbackDiagnostics.IneligibleNotIndirect);
        root.GetProperty("aotRetiredInstructions").GetUInt64().Should().Be(1234);
    }

    [Fact]
    public void RunMetrics_FallbackReturns_AreAdditiveAndSerializedUnderMetrics()
    {
        // Issue #806: the return classification rides in metrics as one additive object; the existing keys are unchanged.
        var stats = new FallbackReturnDiagnostics();
        stats.NoteVerdict(0x80030C44u, FallbackReturnVerdict.RefusedRamWordMismatch);
        stats.NoteSegment(0x80030C44u, 0x80030C44u, 163_124_226UL, FallbackSegmentStatus.BudgetExhausted);
        var metrics = RuntimeHostRunMetrics.From(1500.5, (MmioAccesses: 40, TimeReports: 90), null, fallbackReturns: stats.Snapshot());

        var diagnostic = RuntimeHostDiagnostic.From(
            Manifest([Load]),
            Stop(Load, null, RecompilerIrTerminationReason.Success) with { State = TitleExecutionState.Completed },
            exitCode: 0,
            aotRetiredInstructions: 10,
            fallbackRetiredInstructions: null,
            fallbackTransitionCount: null,
            declinedTransfer: null,
            metrics: metrics);

        using var json = JsonDocument.Parse(RuntimeHostDiagnosticJson.Serialize(diagnostic));
        var written = json.RootElement.GetProperty("metrics");
        written.GetProperty("wallMilliseconds").GetDouble().Should().Be(1500.5);
        var returns = written.GetProperty("fallbackReturns");
        returns.GetProperty("refusedRamWordMismatch").GetUInt64().Should().Be(1UL);
        returns.GetProperty("longestSegmentRetired").GetUInt64().Should().Be(163_124_226UL);
        returns.GetProperty("topRefusedPcs")[0].GetProperty("pc").GetUInt32().Should().Be(0x80030C44u);
        var topEntry = returns.GetProperty("topRefusedPcs")[0];
        topEntry.GetProperty("count").GetUInt64().Should().Be(1UL, "the existing count keeps its meaning (the estimate)");
        topEntry.GetProperty("error").GetUInt64().Should().Be(0UL, "no eviction happened, so the error bound is zero");
        topEntry.GetProperty("lowerBound").GetUInt64().Should().Be(1UL, "the guaranteed lower bound is count minus error");
        topEntry.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            new[] { "pc", "count", "error", "lowerBound" }, "the entry gains fields and loses none");
    }

    [Fact]
    public void RunMetrics_WithoutFallbackReturns_LeaveTheObjectNull()
    {
        RuntimeHostRunMetrics.From(1.0, null, null).FallbackReturns.Should().BeNull();
    }
}
