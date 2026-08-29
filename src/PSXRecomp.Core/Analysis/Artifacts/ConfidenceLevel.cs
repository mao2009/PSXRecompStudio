using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Artifacts;

/// <summary>
/// Qualitative confidence level attached to a finding.
/// </summary>
[Domain]
public enum ConfidenceLevel
{
    Unspecified = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Certain = 4,
}