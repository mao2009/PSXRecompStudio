using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Contracts;

/// <summary>
/// Classification of an unresolved item held by an analysis artifact.
/// </summary>
[Domain]
public enum UnresolvedItemKind
{
    None = 0,
    UnknownFunction = 1,
    LowConfidenceBoundary = 2,
    UnresolvedMmio = 3,
    OverlayCandidate = 4,
    DynamicCodeCapture = 5,
    PsyQLibraryUncertainty = 6,
}