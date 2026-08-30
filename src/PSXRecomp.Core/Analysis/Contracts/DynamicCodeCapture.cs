using System.Globalization;
using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Contracts;

/// <summary>
/// A dynamic-code capture point or self-modifying region detected during analysis.
/// </summary>
[Domain]
public record DynamicCodeCapture(
    uint? StartAddress,
    uint? ByteCount,
    string? Description = null,
    IReadOnlyList<EvidenceReference>? EvidenceReferences = null)
{
    public bool IsValid()
    {
        return ByteCount is null or > 0
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
        StableToken.AppendField(_builder, "startAddress", StartAddress is { } start
            ? start.ToString("x8", CultureInfo.InvariantCulture)
            : string.Empty);
        StableToken.AppendField(_builder, "byteCount", StableToken.FormatLong(ByteCount));
        StableToken.AppendField(_builder, "description", Description);
        StableToken.AppendIndexed(_builder, "evidenceReference", EvidenceReferences, static evidence => evidence.ToTokenString());
        return _builder.ToString();
    }
}