using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Artifacts;

/// <summary>
/// Lifecycle validation state of an analysis artifact or unresolved item.
/// </summary>
[Domain]
public enum ValidationStatus
{
    Unverified = 0,
    PendingHumanReview = 1,
    Accepted = 2,
    Rejected = 3,
    Superseded = 4,
}