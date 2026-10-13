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
        diagnostic.NativeExitCode.Should().Be(2);
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
