using System.Text.Json.Serialization;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Diagnostics;

/// <summary>
/// Severity of a <see cref="Diagnostic"/>. The meaning is deliberately
/// coarser-grained than any per-subsystem status vocabulary: it answers
/// "how bad is this and can the current operation continue?" without
/// knowing which subsystem reported it.
/// </summary>
[Domain]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagnosticSeverity
{
    /// <summary>An observation or advisory. The operation completed as intended.</summary>
    Info = 0,

    /// <summary>The operation completed but with a concern the user should see
    /// (a partial decode, a skipped stage, a fallback path).</summary>
    Warning = 1,

    /// <summary>The operation failed. The specific operation cannot produce its
    /// intended result; recovery (if any) is described by the diagnostic's
    /// <see cref="Diagnostic.Recovery"/>.</summary>
    Error = 2,

    /// <summary>The current process or session cannot continue. The application,
    /// not just the individual operation, must stop or restart.</summary>
    Fatal = 3,
}