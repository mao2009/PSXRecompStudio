using System.Text.Json.Serialization;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Diagnostics;

/// <summary>
/// The subsystem that owns the problem surface a <see cref="Diagnostic"/>
/// reports. This is the coarse grouping that makes a machine contract of
/// "which subsystem is responsible", independent of a specific stage or code.
///
/// Values map to existing (or explicitly planned) subsystems:
/// <c>PSXRecomp.Core.DiscImage</c>, <c>PSXRecomp.Core.Recompiler</c>,
/// <c>PSXRecomp.Core.Runtime</c>, <c>PSXRecomp.Core.Analysis</c>, the host
/// build step, and the GUI/CLI configuration, input and infrastructure areas.
/// The set is deliberately not grown speculatively; a new category needs an
/// existing or accepted role in the architecture.
/// </summary>
[Domain]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagnosticCategory
{
    /// <summary>Static / architectural analysis of a PS-X executable.</summary>
    Analysis = 0,

    /// <summary>Disc image acquisition, validation and real-ROM analysis pipeline.</summary>
    Disc = 1,

    /// <summary>MIPS-to-IR lowering, IR validation and recompiled host code generation.</summary>
    Recompiler = 2,

    /// <summary>The host build/compile step that turns generated host code into an executable.</summary>
    Build = 3,

    /// <summary>Guest execution at the Runtime/BIOS boundary.</summary>
    Runtime = 4,

    /// <summary>Application / workflow configuration.</summary>
    Configuration = 5,

    /// <summary>Host input devices and the mapping/abstraction layer.</summary>
    Input = 6,

    /// <summary>PS1 memory-card images and storage.</summary>
    MemoryCard = 7,

    /// <summary>Managed infrastructure / host I/O adapter boundary.</summary>
    Infrastructure = 8,
}