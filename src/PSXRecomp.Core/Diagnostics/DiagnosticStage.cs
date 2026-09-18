using System.Text.Json.Serialization;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Diagnostics;

/// <summary>
/// The pipeline phase in which a <see cref="Diagnostic"/> was produced, at the
/// granularity shared across the whole application. This is deliberately
/// coarser than a subsystem's own stage enum (for example
/// <c>RomAnalysisStage</c>): it answers "which big phase failed" for GUI / CLI
/// / automation, while the subsystem stage stays in the diagnostic's context.
/// </summary>
[Domain]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagnosticStage
{
    /// <summary>Not attributed to a pipeline phase.</summary>
    Unknown = 0,

    /// <summary>Input acquisition and validation (disc open, file load, executable validation).</summary>
    Input = 1,

    /// <summary>Static / architectural analysis.</summary>
    Analysis = 2,

    /// <summary>Recompilation (lowering / IR / host code generation).</summary>
    Recompile = 3,

    /// <summary>Host build / compile of generated code.</summary>
    Build = 4,

    /// <summary>Guest execution at the Runtime/BIOS boundary.</summary>
    Execute = 5,

    /// <summary>Report / artifact output persistence.</summary>
    Report = 6,
}