using PSXRecomp.Architecture;

namespace PSXRecomp.Core;

// INTENTIONAL VERIFICATION ARTIFACT (Issue #105) - must never be merged to main.
// Domain type referencing an Application-layer type => forbidden edge PSXR004.
[Domain]
public static class DomainToApplicationViolation
{
    public static string Describe() => new PSXRecompStudio.ViewModels.RogueViewModel().GetType().Name;
}
