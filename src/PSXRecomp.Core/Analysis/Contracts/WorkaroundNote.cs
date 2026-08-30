using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Contracts;

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
        return !string.IsNullOrEmpty(Title)
            && !string.IsNullOrEmpty(Description)
            && AllValid(EvidenceReferences, static evidence => evidence.IsValid());
    }

    private static bool AllValid<T>(IReadOnlyList<T>? items, Func<T, bool> isValid)
    {
        if (items is null)
        {
            return true;
        }

        for (var index = 0; index < items.Count; index++)
        {
            if (items[index] is null || !isValid(items[index]))
            {
                return false;
            }
        }

        return true;
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