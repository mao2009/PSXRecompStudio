using PSXRecomp.Architecture;

namespace PSXRecomp.ArchitectureGateVerification;

// Clean fixture: attributed Domain type with only allowed dependencies.
// Expected analyzer result: no diagnostics.
[Domain]
public static class CleanDomainCode
{
    public static int Add(int left, int right) => left + right;
}
