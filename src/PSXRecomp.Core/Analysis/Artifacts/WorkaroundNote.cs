using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Artifacts;

/// <summary>
/// A title-specific workaround or quirk note.
/// </summary>
[Domain]
public record WorkaroundNote(
    string Title,
    string Description,
    string? Scope = null,
    IReadOnlyList<EvidenceReference>? EvidenceReferences = null)
{
    public bool IsValid()
    {
        return Title.Length > 0
            && Description.Length > 0;
    }

    public string ToTokenString()
    {
        var _builder = new StringBuilder();
        StableToken.AppendField(_builder, "title", Title);
        StableToken.AppendField(_builder, "description", Description);
        StableToken.AppendField(_builder, "scope", Scope);
        StableToken.AppendIndexed(_builder, "evidenceReference", EvidenceReferences, static evidence => evidence.ToTokenString());
        return _builder.ToString();
    }
}