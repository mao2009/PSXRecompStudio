using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// The verified BIOS jump-table identities: <c>(family, function number)</c> to service
/// name. This is the shared identity contract both Analysis and Runtime may depend on,
/// and it is deliberately *not* the HLE registry: Analysis records which service a ROM
/// asks for, the registry records which service the Runtime can currently provide, and
/// conflating the two would make analysis evidence shrink and grow with implementation
/// progress (Issue #279).
///
/// Only identities verified against the documentation cited in
/// <c>docs/REFERENCES.md</c> appear here. An unlisted function number resolves to no
/// name rather than to a guessed one — an unnamed but recorded call site is evidence,
/// a mis-named one is a defect (ADR-014's no-guessing rule).
/// </summary>
[Domain]
public static class BiosCallNames
{
    /// <summary>
    /// Resolves the documented service name for a BIOS jump-table entry.
    /// Returns <see langword="false"/> for every identity this repository has not
    /// verified, leaving the call site recorded without a name.
    /// </summary>
    public static bool TryResolve(BiosCallFamily family, byte functionNumber, out string name)
    {
        // Verified in docs/REFERENCES.md: "A(3Ch) or B(3Dh) std_out_putchar(char)" and
        // "A(3Eh) or B(3Fh) std_out_puts(src)". Nothing else is verified yet; getchar
        // (A0:3B) and gets (A0:3D) carry an explicit "verify before use" marker in
        // docs/runtime/bios-hle-evidence.md and are therefore absent by design.
        name = (family, functionNumber) switch
        {
            (BiosCallFamily.A0, 0x3C) => "putchar",
            (BiosCallFamily.B0, 0x3D) => "putchar",
            (BiosCallFamily.A0, 0x3E) => "puts",
            (BiosCallFamily.B0, 0x3F) => "puts",
            _ => string.Empty,
        };

        return name.Length > 0;
    }
}
