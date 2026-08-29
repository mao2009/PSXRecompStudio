using System.Globalization;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Artifacts;

/// <summary>
/// Confidence attached to a finding: a qualitative level with an optional rationale
/// and an optional numeric score in the closed range [0, 1].
/// </summary>
[Domain]
public record Confidence(ConfidenceLevel Level, string? Rationale, double? Score)
{
    public bool IsValid()
    {
        return Enum.IsDefined(Level)
            && (Score is null or >= 0.0 and <= 1.0);
    }

    public string ToTokenString()
    {
        var _builder = new System.Text.StringBuilder();
        StableToken.AppendField(_builder, "level", Level.ToString());
        StableToken.AppendField(_builder, "rationale", Rationale);
        StableToken.AppendField(_builder, "score", StableToken.FormatDouble(Score));
        return _builder.ToString();
    }
}