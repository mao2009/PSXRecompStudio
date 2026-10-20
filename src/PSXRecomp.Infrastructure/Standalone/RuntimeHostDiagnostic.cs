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
/// because the artifact's wire protocol carries only the transfer's target. A run that produced no
/// snapshot (a timeout, an engine failure) leaves <see cref="FinalPc"/> and the counts it never
/// obtained <c>null</c> rather than 0. <see cref="ClassifiedExitCode"/> is the host's classified result
/// code (<c>RecompiledArtifactResult.ExitCode</c>), not the artifact process's own exit code, which
/// the host does not expose.
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
    int ClassifiedExitCode,
    string? NativeTerminationReason,
    string? OrchestratorState,
    string? OrchestratorDiagnosticCode,
    bool TimedOut,
    IReadOnlyList<MixedFallbackTarget>? FallbackTargets = null,
    string? FailureExceptionType = null,
    RuntimeHostRunMetrics? Metrics = null)
{
    /// <summary>
    /// The diagnostic for a run that ended in an exception before any orchestration result existed: only the manifest's
    /// static facts and the classified exit code are known; every measured field stays <c>null</c>.
    /// </summary>
    public static RuntimeHostDiagnostic FromFailure(RuntimeHostManifest manifest, int exitCode, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(exception);

        return new RuntimeHostDiagnostic(
            FinalPc: null,
            LastAotBlockPc: null,
            TransferSourcePc: null,
            TransferTargetPc: null,
            TransferOpcode: null,
            TargetHasCompiledBlock: null,
            TargetInsideImage: null,
            FallbackEnabled: manifest.MixedFallback,
            FallbackAttempted: null,
            FallbackOutcome: null,
            FallbackRejectReason: null,
            AotRetiredInstructions: null,
            FallbackRetiredInstructions: null,
            FallbackTransitionCount: null,
            ClassifiedExitCode: exitCode,
            NativeTerminationReason: null,
            OrchestratorState: null,
            OrchestratorDiagnosticCode: null,
            TimedOut: false,
            FailureExceptionType: exception.GetType().FullName);
    }

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
        IReadOnlyList<MixedFallbackTarget>? fallbackTargets = null,
        RuntimeHostRunMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(execution);

        var finalPc = execution.FinalSnapshot?.PC;
        // A declined end marker (for example pc 0) may be followed by Completed.
        // Attribute a decline only to the terminal unsupported transfer it actually describes.
        var relevantDecline = execution.State == TitleExecutionState.UnsupportedTransfer
            && declinedTransfer is { } candidate
            && candidate.TargetPc == finalPc
                ? declinedTransfer
                : null;
        var transferTargetPc = relevantDecline?.TargetPc
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
            FallbackAttempted: relevantDecline?.FallbackAttempted,
            FallbackOutcome: relevantDecline is not null ? IneligibleOutcome : null,
            FallbackRejectReason: relevantDecline?.RejectReason,
            AotRetiredInstructions: aotRetiredInstructions,
            FallbackRetiredInstructions: fallbackRetiredInstructions,
            FallbackTransitionCount: fallbackTransitionCount,
            ClassifiedExitCode: exitCode,
            NativeTerminationReason: execution.FinalSnapshot?.Termination.ToString(),
            OrchestratorState: execution.State.ToString(),
            OrchestratorDiagnosticCode: execution.DiagnosticCode,
            TimedOut: execution.DiagnosticCode == RecompiledHostExecutionEngine.TimeoutDiagnosticCode,
            FallbackTargets: fallbackTargets,
            Metrics: metrics);
    }
}

/// <summary>
/// Where a run's wall-clock time went (measurement only): host round trips the native code made (guest MMIO relayed to the
/// device graph, guest-time reports) and the mixed-fallback wall-clock split. Fields the engine could not measure are null.
/// </summary>
[Infrastructure]
public sealed record RuntimeHostRunMetrics(
    double WallMilliseconds,
    ulong? MmioAccesses,
    ulong? TimeReports,
    double? FallbackTransferMilliseconds,
    double? FallbackInterpreterMilliseconds,
    uint? LastFallbackEntryPc = null,
    uint? LastFallbackReturnPc = null,
    uint? LastMmioPhysicalAddress = null,
    bool? LastMmioWasWrite = null,
    FallbackSegmentProgress? FallbackInFlight = null,
    FallbackReturnStats? FallbackReturns = null)
{
    /// <summary>
    /// Assembles the metrics from the engine's own surfaces; any of them may be absent (no protocol, no mixed fallback). The
    /// last-observation fields name what the host last saw a stopped-from-outside run do: the guest pc itself is unknowable then.
    /// </summary>
    public static RuntimeHostRunMetrics From(
        double wallMilliseconds,
        (ulong MmioAccesses, ulong TimeReports)? roundTrips,
        MixedFallbackTimings? timings,
        MixedFallbackEvidence? evidence = null,
        HostMmioAccess? lastMmio = null,
        FallbackSegmentProgress? fallbackInFlight = null,
        FallbackReturnStats? fallbackReturns = null) =>
        new(
            wallMilliseconds,
            roundTrips?.MmioAccesses,
            roundTrips?.TimeReports,
            timings?.TransferMilliseconds,
            timings?.FallbackMilliseconds,
            evidence?.LastEntryPc,
            evidence?.LastReturnPc,
            lastMmio?.PhysicalAddress,
            lastMmio?.IsWrite,
            fallbackInFlight,
            fallbackReturns);
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
