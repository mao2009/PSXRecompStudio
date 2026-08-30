using System.Globalization;
using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Contracts;

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
        return !string.IsNullOrEmpty(Component)
            && (Confidence is null || Confidence.IsValid())
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
        StableToken.AppendField(_builder, "address", Address.ToString("x8", CultureInfo.InvariantCulture));
        StableToken.AppendField(_builder, "component", Component);
        StableToken.AppendField(_builder, "description", Description);
        StableToken.AppendField(_builder, "confidence", Confidence?.ToTokenString());
        StableToken.AppendIndexed(_builder, "evidenceReference", EvidenceReferences, static evidence => evidence.ToTokenString());
        return _builder.ToString();
    }
}