using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Contracts;

/// <summary>
/// An item that remains unresolved in an analysis artifact so it can be revisited or resumed later.
/// </summary>
[Domain]
public record UnresolvedItem(
    string Description,
    UnresolvedItemKind Kind,
    IReadOnlyList<EvidenceReference>? EvidenceReferences = null,
    ValidationStatus Status = ValidationStatus.Unverified)
{
    public bool IsValid()
    {
        return !string.IsNullOrEmpty(Description)
            && Enum.IsDefined(Kind)
            && Enum.IsDefined(Status)
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
        StableToken.AppendField(_builder, "description", Description);
        StableToken.AppendField(_builder, "kind", Kind.ToString());
        StableToken.AppendField(_builder, "status", Status.ToString());
        StableToken.AppendIndexed(_builder, "evidenceReference", EvidenceReferences, static evidence => evidence.ToTokenString());
        return _builder.ToString();
    }
}