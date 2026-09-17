using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Diagnostics;

/// <summary>
/// The common diagnostics contract: one structured, machine-readable problem
/// produced by any subsystem (Analysis, Disc, Recompiler, Build, Runtime,
/// Configuration, Input, MemoryCard, Infrastructure). It answers, in order:
/// what happened (<see cref="Code"/> / <see cref="Severity"/> / <see cref="Category"/>),
/// in which phase (<see cref="Stage"/>), tied to what (<see cref="Context"/> /
/// <see cref="Evidence"/>), and what the user should do next
/// (<see cref="Recovery"/>).
///
/// <para>
/// The <see cref="Code"/> is the SSOT that identifies the failure for GUI, CLI,
/// JSON and automation. <see cref="Message"/> is a human-readable secondary
/// text and is never required to identify the failure; <see cref="MessageKey"/>
/// is the locale-neutral key that Issue #21 localization will resolve against
/// this code's structured context. The model carries no timestamps, host names
/// or absolute paths, so it is deterministic as an artifact.
/// </para>
///
/// <para>
/// A Diagnostic describes an expected / domain failure (see
/// <see cref="DiagnosticExceptionPolicy"/> for the boundary); it is not a
/// logging record and it does not replace the trace/log systems (Issues #16/#45).
/// </para>
/// </summary>
[Domain]
public sealed record Diagnostic
{
    /// <summary>Stable machine-readable identity of the failure.</summary>
    public required DiagnosticCode Code { get; init; }

    /// <summary>The subsystem that owns the problem surface.</summary>
    public required DiagnosticCategory Category { get; init; }

    /// <summary>How severe the problem is. Defaults to <see cref="DiagnosticSeverity.Error"/>.</summary>
    public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Error;

    /// <summary>The pipeline phase in which the problem occurred.</summary>
    public DiagnosticStage Stage { get; init; } = DiagnosticStage.Unknown;

    /// <summary>Human-readable secondary message; never required to identify the failure.</summary>
    public string? Message { get; init; }

    /// <summary>Locale-neutral message key for Issue #21 localization; <c>null</c> until one exists.</summary>
    public string? MessageKey { get; init; }

    /// <summary>Structured key/value metadata (guest PC, opcode, file identity, hashes, ...).</summary>
    public IReadOnlyList<DiagnosticContextEntry> Context { get; init; } = [];

    /// <summary>References to the evidence the diagnostic is tied to (traces, logs, artifacts).</summary>
    public IReadOnlyList<DiagnosticEvidenceReference> Evidence { get; init; } = [];

    /// <summary>What the user or automation can do next, under what retry semantics.</summary>
    public DiagnosticRecovery Recovery { get; init; }

    /// <summary>Whether the diagnostic is well-formed and all referenced parts are valid.</summary>
    public bool IsValid()
    {
        if (Code.IsEmpty
            || !Enum.IsDefined(Severity)
            || !Enum.IsDefined(Category)
            || !Enum.IsDefined(Stage))
        {
            return false;
        }

        return Context.All(entry => entry.IsValid())
            && Evidence.All(reference => reference.IsValid())
            && Recovery.IsValid();
    }
}