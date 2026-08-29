using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Artifacts;

/// <summary>
/// Information about a game overlay segment.
/// </summary>
[Domain]
public record OverlayInfo(
    string Name,
    string? Description = null,
    IReadOnlyList<EvidenceReference>? EvidenceReferences = null)
{
    public bool IsValid()
    {
        return Name.Length > 0;
    }

    public string ToTokenString()
    {
        var _builder = new StringBuilder();
        StableToken.AppendField(_builder, "name", Name);
        StableToken.AppendField(_builder, "description", Description);
        StableToken.AppendIndexed(_builder, "evidenceReference", EvidenceReferences, static evidence => evidence.ToTokenString());
        return _builder.ToString();
    }
}