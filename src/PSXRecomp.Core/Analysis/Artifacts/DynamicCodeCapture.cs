using System.Globalization;
using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Artifacts;

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
        return ByteCount is null or > 0;
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