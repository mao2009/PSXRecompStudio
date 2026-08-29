using System.Globalization;
using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Artifacts;

/// <summary>
/// A hardware / MMIO finding about a specific address.
/// </summary>
[Domain]
public record MmioFinding(
    uint Address,
    string Component,
    string? Description = null,
    Confidence? Confidence = null,
    IReadOnlyList<EvidenceReference>? EvidenceReferences = null)
{
    public bool IsValid()
    {
        return Component.Length > 0
            && (Confidence is null || Confidence.IsValid());
    }

    public string ToTokenString()
    {
        var _builder = new StringBuilder();
        StableToken.AppendField(_builder, "address", Address.ToString("x8", CultureInfo.InvariantCulture));
        StableToken.AppendField(_builder, "component", Component);
        StableToken.AppendField(_builder, "description", Description);
        StableToken.AppendField(_builder, "confidence", Confidence?.ToTokenString());
        StableToken.AppendIndexed(_builder, "evidenceReference", EvidenceReferences, static evidence => evidence.ToTokenString());
        return _builder.ToString();
    }
}