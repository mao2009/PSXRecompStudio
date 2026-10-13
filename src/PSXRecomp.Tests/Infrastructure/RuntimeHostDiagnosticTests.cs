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
}
