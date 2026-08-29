using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Artifacts;

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
        return Description.Length > 0
            && Enum.IsDefined(Kind)
            && Enum.IsDefined(Status);
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