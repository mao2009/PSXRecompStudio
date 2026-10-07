using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// The complete guest-visible CPU state a BIOS service leaves behind instead of
/// a normal call's "$ra continuation plus $v0": the register file, HI/LO, COP0,
/// and the PC execution continues at (Issue #664).
/// </summary>
/// <remarks>
/// <para>
/// B0:17 ReturnFromException is the motivating case. It is not a BIOS call the
/// guest can return from — it <em>is</em> the exception-return operation, so the
/// restored register file <b>is</b> the whole post-dispatch state, $v0 included,
/// and continuation is the saved EPC rather than <c>$ra</c>. Carrying that as one
/// value instead of a per-service side channel is what keeps the interpreter, the
/// generated host and the mixed fallback on the same semantics.
/// </para>
/// <para>
/// <see cref="RestoredSr"/> is null for a replacement that does not restore COP0
/// SR. A non-null value means the restored SR must also be followed by the
/// RFE interrupt-stack pop, which is why the flag and the value are one field
/// rather than two that could disagree. EPC/CAUSE are deliberately not carried:
/// RFE does not rewrite them, so the CPU's own COP0 keeps the values the
/// exception entry recorded.
/// </para>
/// <para>
/// <see cref="Gpr"/> is taken by reference and must not be mutated after
/// construction — the service hands over the array it already built.
/// </para>
/// </remarks>
[Domain]
public sealed record BiosCpuStateMutation(
    uint[] Gpr,
    uint Hi,
    uint Lo,
    uint? RestoredSr,
    uint NextPc)
{
    /// <summary>
    /// R0 is architecturally zero on the R3000A and every restore leaves it zero,
    /// so <see cref="ApplyTo"/> never writes index 0 — it keeps whatever the CPU
    /// already holds.
    /// </summary>
    public const int FirstRestoredGpr = 1;

    /// <summary>
    /// Writes this state onto <paramref name="core"/>: registers, HI/LO, the
    /// restored SR followed by the native RFE pop when one is present, then the
    /// continuation PC. Shared by every execution form so no form can restore a
    /// different subset.
    /// </summary>
    public void ApplyTo(PSXCoreWrapper core)
    {
        ArgumentNullException.ThrowIfNull(core);
        if (Gpr.Length != PSXCoreWrapper.GprCount)
        {
            throw new ArgumentException(
                $"A CPU-state mutation needs exactly {PSXCoreWrapper.GprCount} registers, not {Gpr.Length}.",
                nameof(Gpr));
        }

        for (var i = FirstRestoredGpr; i < PSXCoreWrapper.GprCount; i++)
        {
            core.SetGpr(i, Gpr[i]);
        }

        core.Hi = Hi;
        core.Lo = Lo;
        if (RestoredSr is { } restoredSr)
        {
            core.SetCop0(SrCop0Index, restoredSr);
            core.PopExceptionSrStack();
        }

        core.Pc = NextPc;
    }

    /// <summary>COP0 register 12, SR — the only register a restore rewrites.</summary>
    public const int SrCop0Index = 12;
}