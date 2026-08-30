using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Contracts;

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
        return !string.IsNullOrEmpty(Name)
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
            if (!isValid(items[index]))
            {
                return false;
            }
        }

        return true;
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