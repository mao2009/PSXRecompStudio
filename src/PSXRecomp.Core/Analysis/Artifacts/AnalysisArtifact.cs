using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Artifacts;

/// <summary>
/// Versioned container for user-driven analysis results.
/// Holds function/CFG, overlay and dynamic-code information, MMIO findings, title workarounds,
/// attached evidence, confidence, validation status and provenance so the artifact can be
/// re-analyzed, resumed and shared without embedding the game binary.
/// Identity and region are modeled as plain primitives (<see cref="TitleId"/>, <see cref="RegionCode"/>)
/// and never reference the identity contracts owned by another workstream.
/// </summary>
[Domain]
public record AnalysisArtifact
{
    public const int CurrentVersion = 1;

    public required int Version { get; init; }

    /// <summary>Stable producer-assigned identifier. Same id must identify the same artifact.</summary>
    public required string Id { get; init; }

    /// <summary>Type of the artifact, e.g. "function-discovery" or "overlay-analysis".</summary>
    public required string ArtifactKind { get; init; }

    public string? TitleId { get; init; }

    public string? RegionCode { get; init; }

    public string? Description { get; init; }

    public ValidationStatus Status { get; init; } = ValidationStatus.Unverified;

    public Confidence? Confidence { get; init; }

    public Provenance? Provenance { get; init; }

    public long? CreatedUnixSeconds { get; init; }

    public long? UpdatedUnixSeconds { get; init; }

    public IReadOnlyList<EvidenceReference>? EvidenceReferences { get; init; }

    public IReadOnlyList<FunctionInfo>? Functions { get; init; }

    public IReadOnlyList<OverlayInfo>? Overlays { get; init; }

    public IReadOnlyList<DynamicCodeCapture>? DynamicCode { get; init; }

    public IReadOnlyList<MmioFinding>? MmioFindings { get; init; }

    public IReadOnlyList<WorkaroundNote>? TitleWorkarounds { get; init; }

    public IReadOnlyList<UnresolvedItem>? UnresolvedItems { get; init; }

    public static bool IsValidTimestamp(long? timestamp)
    {
        return timestamp is null or >= 0;
    }

    public bool IsValid()
    {
        return Version > 0
            && Id.Length > 0
            && ArtifactKind.Length > 0
            && Enum.IsDefined(Status)
            && (Confidence is null || Confidence.IsValid())
            && (Provenance is null || Provenance.IsValid())
            && IsValidTimestamp(CreatedUnixSeconds)
            && IsValidTimestamp(UpdatedUnixSeconds)
            && AllValid(EvidenceReferences, static evidence => evidence.IsValid())
            && AllValid(Functions, static function => function.IsValid())
            && AllValid(Overlays, static overlay => overlay.IsValid())
            && AllValid(DynamicCode, static capture => capture.IsValid())
            && AllValid(MmioFindings, static finding => finding.IsValid())
            && AllValid(TitleWorkarounds, static workaround => workaround.IsValid())
            && AllValid(UnresolvedItems, static item => item.IsValid());
    }

    /// <summary>
    /// Deterministic canonical representation of the artifact. Identical content always
    /// produces an identical token, making the serialized (JSON/YAML) shape reproducible.
    /// </summary>
    public string ToTokenString()
    {
        var _builder = new StringBuilder();
        StableToken.AppendField(_builder, "version", StableToken.FormatLong(Version));
        StableToken.AppendField(_builder, "id", Id);
        StableToken.AppendField(_builder, "kind", ArtifactKind);
        StableToken.AppendField(_builder, "titleId", TitleId);
        StableToken.AppendField(_builder, "regionCode", RegionCode);
        StableToken.AppendField(_builder, "description", Description);
        StableToken.AppendField(_builder, "status", Status.ToString());
        StableToken.AppendField(_builder, "createdUnixSeconds", StableToken.FormatLong(CreatedUnixSeconds));
        StableToken.AppendField(_builder, "updatedUnixSeconds", StableToken.FormatLong(UpdatedUnixSeconds));
        StableToken.AppendField(_builder, "confidence", Confidence?.ToTokenString());
        StableToken.AppendField(_builder, "provenance", Provenance?.ToTokenString());
        StableToken.AppendIndexed(_builder, "evidenceReference", EvidenceReferences, static evidence => evidence.ToTokenString());
        StableToken.AppendIndexed(_builder, "function", Functions, static function => function.ToTokenString());
        StableToken.AppendIndexed(_builder, "overlay", Overlays, static overlay => overlay.ToTokenString());
        StableToken.AppendIndexed(_builder, "dynamicCode", DynamicCode, static capture => capture.ToTokenString());
        StableToken.AppendIndexed(_builder, "mmioFinding", MmioFindings, static finding => finding.ToTokenString());
        StableToken.AppendIndexed(_builder, "titleWorkaround", TitleWorkarounds, static workaround => workaround.ToTokenString());
        StableToken.AppendIndexed(_builder, "unresolvedItem", UnresolvedItems, static item => item.ToTokenString());
        return _builder.ToString();
    }

    private static bool AllValid<T>(IReadOnlyList<T>? items, Func<T, bool> isValid)
    {
        if (items is null)
        {
            return true;
        }

        for (var index = 0; index < items.Count; index++)
        {
            if (!isValid(items[index]))
            {
                return false;
            }
        }

        return true;
    }
}