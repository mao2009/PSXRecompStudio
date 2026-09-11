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
        // "A(3Eh) or B(3Fh) std_out_puts(src)". getchar (A0:3B) and gets (A0:3D) carry an
        // explicit "verify before use" marker in docs/runtime/bios-hle-evidence.md and are
        // therefore absent by design.
        //
        // The identities below were verified the same way, against the same cited source,
        // for the function numbers real-ROM analysis observed most often (Issue #11):
        // "A(39h) InitHeap(addr,size)", "A(ABh) _card_info(port)", "A(ACh) _card_load(port)",
        // "B(4Eh) _card_write(port,sector,src)", "B(50h) _new_card()", "B(56h) GetC0Table"
        // and "B(57h) GetB0Table". None of the seven is documented as an A0/B0 alias of
        // another entry — unlike putchar/puts, each is listed for exactly one family, and
        // the same function number in the other family is an unrelated function (A0:56
        // _96_remove, A0:57 a return-0 stub, B0:39 isatty, A0:4E gpu_sync, A0:50
        // SystemError; B0:AB and B0:AC do not exist, the B table ending at B(5Dh)).
        //
        // Verification is identification only: naming an identity says what a ROM asked
        // for, never that the Runtime can provide it. Registration remains ADR-014's
        // separate, evidence-and-prerequisite-gated decision.
        name = (family, functionNumber) switch
        {
            (BiosCallFamily.A0, 0x39) => "InitHeap",
            (BiosCallFamily.A0, 0x3C) => "putchar",
            (BiosCallFamily.B0, 0x3D) => "putchar",
            (BiosCallFamily.A0, 0x3E) => "puts",
            (BiosCallFamily.B0, 0x3F) => "puts",
            (BiosCallFamily.A0, 0xAB) => "_card_info",
            (BiosCallFamily.A0, 0xAC) => "_card_load",
            (BiosCallFamily.B0, 0x4E) => "_card_write",
            (BiosCallFamily.B0, 0x50) => "_new_card",
            (BiosCallFamily.B0, 0x56) => "GetC0Table",
            (BiosCallFamily.B0, 0x57) => "GetB0Table",
            _ => string.Empty,
        };

        return name.Length > 0;
    }
}
