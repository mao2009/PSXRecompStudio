using System.Text.Json.Serialization;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Diagnostics;

/// <summary>
/// The kind of artifact a <see cref="DiagnosticEvidenceReference"/> points at.
/// Distinct from the Analysis contract's <c>EvidenceType</c>, which classifies
/// evidence backing analysis findings; this classifies the kinds of evidence a
/// diagnostic operation can point at. The set is the minimum actually used;
/// it grows when a production subsystem needs a new artifact kind.
/// </summary>
[Domain]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagnosticEvidenceKind
{
    /// <summary>A real-ROM recompilation coverage document.</summary>
    RealRomCoverage = 0,

    /// <summary>An automated execution trace (guest PCs retired per segment).</summary>
    ExecutionTrace = 1,

    /// <summary>A generated-source / host build log.</summary>
    BuildLog = 2,

    /// <summary>A deterministic analysis artifact (report, manifest, snapshot).</summary>
    AnalysisArtifact = 3,

    /// <summary>A guest address / instruction within the analyzed binary.</summary>
    GuestAddress = 4,

    /// <summary>A source file (generated host source, fixture, or configuration).</summary>
    SourceFile = 5,
}

/// <summary>
/// A lightweight, stable reference from a <see cref="Diagnostic"/> to the
/// evidence it is tied to: which kind of artifact plus the identifier that
/// locates it within that kind. The identifier is expected to be a stable
/// content-addressed id where the referenced artifact provides one (for
/// example an analysis-artifact hash); otherwise it is the artifact's own
/// stable id. This references evidence — it does not embed it.
/// </summary>
[Domain]
public sealed record DiagnosticEvidenceReference(DiagnosticEvidenceKind Kind, string Identifier, string? Description = null)
{
    /// <summary>Whether the reference is well-formed: a defined kind and a non-empty identifier.</summary>
    public bool IsValid()
    {
        return Enum.IsDefined(Kind) && !string.IsNullOrWhiteSpace(Identifier);
    }
}