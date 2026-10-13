using System.Text.Json;
using System.Text.Json.Serialization;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// A machine-readable account of one packaged runtime-host run's stop, emitted only when
/// <c>PSXRecomp.RuntimeHost</c> is asked for it (<c>--diagnostics &lt;path&gt;</c>; Issue #763).
/// </summary>
/// <remarks>
/// Observation only: it never feeds back into the run and is off by default, so it changes no
/// exit code, handoff or snapshot. Fields the host genuinely measured carry values; fields it
/// cannot measure from outside the artifact are <c>null</c> rather than guessed
/// (<see cref="TransferSourcePc"/>, <see cref="TransferOpcode"/>, <see cref="LastAotBlockPc"/>),
/// because the artifact's wire protocol carries only the transfer's target.
/// </remarks>
[Infrastructure]
public sealed record RuntimeHostDiagnostic(
    uint? FinalPc,
    uint? LastAotBlockPc,
    uint? TransferSourcePc,
    uint? TransferTargetPc,
    uint? TransferOpcode,
    bool? TargetHasCompiledBlock,
    bool? TargetInsideImage,
    bool FallbackEnabled,
    bool? FallbackAttempted,
    string? FallbackOutcome,
    string? FallbackRejectReason,
    ulong? AotRetiredInstructions,
    ulong? FallbackRetiredInstructions,
    uint? FallbackTransitionCount,
    int NativeExitCode,
    string? NativeTerminationReason,
    string OrchestratorState,
    string? OrchestratorDiagnosticCode,
    bool TimedOut,
    IReadOnlyList<MixedFallbackTarget>? FallbackTargets = null)
{
    /// <summary>The <see cref="FallbackOutcome"/> recorded when a transfer reached a fallback that declined it.</summary>
    public const string IneligibleOutcome = "ineligible";

    /// <summary>
    /// Assembles the diagnostic from the host's own surfaces (Issue #763): the manifest for the static
    /// image/block facts, the orchestration result for the stop, and the engine's last-run evidence.
    /// </summary>
    public static RuntimeHostDiagnostic From(
        RuntimeHostManifest manifest,
        TitleExecutionResult execution,
        int exitCode,
        ulong? aotRetiredInstructions,
        ulong? fallbackRetiredInstructions,
        uint? fallbackTransitionCount,
        FallbackDeclinedTransfer? declinedTransfer,
        IReadOnlyList<MixedFallbackTarget>? fallbackTargets = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(execution);

        var finalPc = execution.FinalSnapshot?.PC;
        var transferTargetPc = declinedTransfer?.TargetPc
            ?? (execution.State == TitleExecutionState.UnsupportedTransfer ? finalPc : null);

        return new RuntimeHostDiagnostic(
            FinalPc: finalPc,
            LastAotBlockPc: null,
            TransferSourcePc: null,
            TransferTargetPc: transferTargetPc,
            TransferOpcode: null,
            TargetHasCompiledBlock: transferTargetPc is { } target
                ? manifest.BlockEntryPcs?.Contains(target) ?? false
                : null,
            TargetInsideImage: transferTargetPc is { } t
                ? t >= manifest.ImageLoadAddress && t < manifest.ProgramEnd
                : null,
            FallbackEnabled: manifest.MixedFallback,
            FallbackAttempted: declinedTransfer?.FallbackAttempted,
            FallbackOutcome: declinedTransfer is not null ? IneligibleOutcome : null,
            FallbackRejectReason: declinedTransfer?.RejectReason,
            AotRetiredInstructions: aotRetiredInstructions,
            FallbackRetiredInstructions: fallbackRetiredInstructions,
            FallbackTransitionCount: fallbackTransitionCount,
            NativeExitCode: exitCode,
            NativeTerminationReason: execution.FinalSnapshot?.Termination.ToString(),
            OrchestratorState: execution.State.ToString(),
            OrchestratorDiagnosticCode: execution.DiagnosticCode,
            TimedOut: execution.DiagnosticCode == RecompiledHostExecutionEngine.TimeoutDiagnosticCode,
            FallbackTargets: fallbackTargets);
    }
}

/// <summary>Canonical JSON writing for <see cref="RuntimeHostDiagnostic"/>, matching the manifest's style.</summary>
[Infrastructure]
public static class RuntimeHostDiagnosticJson
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Serializes <paramref name="diagnostic"/> to canonical text (LF endings, trailing newline).</summary>
    public static string Serialize(RuntimeHostDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var json = JsonSerializer.Serialize(diagnostic, WriteOptions);
        return json.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal) + "\n";
    }
}
