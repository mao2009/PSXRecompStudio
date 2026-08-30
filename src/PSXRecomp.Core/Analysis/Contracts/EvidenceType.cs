using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Analysis.Contracts;

/// <summary>
/// The kind of evidence backing a finding.
/// </summary>
[Domain]
public enum EvidenceType
{
    ManualObservation = 0,
    Screenshot = 1,
    Disassembly = 2,
    Trace = 3,
    RuntimeObservation = 4,
    AnalyzerFinding = 5,
    AIPrediction = 6,
    Reference = 7,
}