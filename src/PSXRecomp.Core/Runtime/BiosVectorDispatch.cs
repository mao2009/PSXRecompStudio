using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// What an execution path must do after one guest transfer to a BIOS trampoline
/// vector has been dispatched through <see cref="BiosVectorDispatch.Dispatch"/>.
/// </summary>
/// <param name="ContinueExecution">
/// True when control was resolved and execution continues at
/// <paramref name="NextPc"/>; false when the run must stop with the carried
/// diagnostic.
/// </param>
/// <param name="NextPc">
/// The guest address control continues at. Meaningful only when
/// <paramref name="ContinueExecution"/> is true.
/// </param>
/// <param name="ReturnValue">
/// The value to write into <c>$v0</c> before continuing, when the dispatched
/// service produced one. Null means <c>$v0</c> is left untouched.
/// </param>
/// <param name="IsPatchedTarget">
/// True when <paramref name="NextPc"/> is the raw guest address a patched
/// jump-table entry named, rather than the call site's own <c>$ra</c>. An
/// execution path that can only enter addresses it has already translated (the
/// generated host) needs this distinction; the interpreter does not.
/// </param>
/// <param name="DiagnosticCode">Stable diagnostic code when the run must stop.</param>
/// <param name="DiagnosticMessage">Human-readable diagnostic when the run must stop.</param>
[Domain]
public sealed record BiosVectorDispatchOutcome(
    bool ContinueExecution,
    uint NextPc,
    uint? ReturnValue,
    bool IsPatchedTarget,
    string? DiagnosticCode,
    string? DiagnosticMessage);

/// <summary>
/// The BIOS trampoline-vector dispatch semantics, stated once for every
/// execution path. Given the guest register file at a transfer to an A0/B0/C0
/// vector, it builds the call identity from the PS1 ABI, asks
/// <see cref="IBiosRuntime"/> what the call resolves to, and reports what the
/// caller must do with its own PC and registers.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the interpreter and the recompiled/generated host cannot
/// disagree about what a BIOS call means (Issue #362). Both read the same
/// registers, ask the same Runtime, and get the same three outcomes; only
/// <em>applying</em> the outcome to a concrete machine state is path-specific.
/// No BIOS behavior is implemented here and no CPU semantics are reimplemented:
/// this is the ABI glue between a register file and
/// <see cref="IBiosRuntime.Invoke"/>.
/// </para>
/// <para>
/// The three <see cref="BiosServiceStatus"/> outcomes map onto the three things a
/// real trampoline can do with a jump-table entry:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="BiosServiceStatus.PatchedTarget"/> — guest code owns this entry
/// now, so control moves to the raw guest address it holds. The target returns
/// through the <c>$ra</c> the original call site already linked, exactly as on
/// hardware, because the trampoline never consumes the link register.
/// </description></item>
/// <item><description>
/// <see cref="BiosServiceStatus.Supported"/> — the entry still dispatches to
/// HLE, so the service's return value lands in <c>$v0</c> and control returns to
/// <c>$ra</c>.
/// </description></item>
/// <item><description>
/// <see cref="BiosServiceStatus.Unsupported"/> — the Runtime can neither service
/// the call nor name a guest target for it. Execution stops with the Runtime's
/// own diagnostic rather than continuing past a call whose effects never
/// happened (Issue #279: never a silent success).
/// </description></item>
/// </list>
/// </remarks>
[Domain]
public static class BiosVectorDispatch
{
    /// <summary>
    /// Reported when a guest transfer reached a BIOS trampoline vector that the
    /// Runtime could neither service nor turn into a jumpable guest target, and
    /// the Runtime supplied no diagnostic of its own.
    /// </summary>
    public const string UnresolvedDiagnosticCode = "BIOS_DISPATCH_UNRESOLVED";

    /// <summary>
    /// Reported when a patched jump-table entry names an address outside every
    /// translatable region (KSEG2 and above), so control cannot be transferred to it.
    /// </summary>
    public const string UntranslatableTargetDiagnosticCode = "BIOS_PATCHED_TARGET_UNTRANSLATABLE";

    /// <summary>
    /// Reported when a registered service's arity exceeds the number of ABI
    /// argument registers ($a0-$a3) a register-only live trap can read. No PS1
    /// service is registered above that arity today; this exists so a future one
    /// fails loudly rather than the trap guessing stack arguments.
    /// </summary>
    public const string ArityExceedsRegisterBoundaryDiagnosticCode = "BIOS_ARITY_EXCEEDS_REGISTER_BOUNDARY";

    /// <summary>Number of ABI argument registers ($a0-$a3) a live BIOS trap can read.</summary>
    public const int AbiArgumentRegisterCount = 4;

    /// <summary>
    /// Dispatches one guest transfer to a BIOS trampoline vector and reports where
    /// control must continue.
    /// </summary>
    /// <param name="biosRuntime">The Runtime that owns every BIOS service and the guest jump-table state.</param>
    /// <param name="family">The trampoline family the guest transferred to, from <see cref="BiosJumpTables.TryResolveVectorFamily"/>.</param>
    /// <param name="gpr">
    /// The guest general-purpose register file at the transfer: <c>$t1</c> selects
    /// the function number, <c>$a0</c>-<c>$a3</c> carry the argument words and
    /// <c>$ra</c> holds the call site's own link — the trampoline is transparent
    /// to it. Must hold <see cref="RecompilerGprCount"/> entries.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="biosRuntime"/> or <paramref name="gpr"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="gpr"/> is not a full register file.</exception>
    public static BiosVectorDispatchOutcome Dispatch(
        IBiosRuntime biosRuntime,
        BiosCallFamily family,
        IReadOnlyList<uint> gpr)
    {
        ArgumentNullException.ThrowIfNull(biosRuntime);
        ArgumentNullException.ThrowIfNull(gpr);
        if (gpr.Count != RecompilerGprCount)
        {
            throw new ArgumentException(
                $"A BIOS vector dispatch reads the whole register file; expected {RecompilerGprCount} values.",
                nameof(gpr));
        }

        // PS1 ABI: $t1 selects the function number, $a0-$a3 carry the argument
        // words, $v0 takes the return value, and $ra holds the call site's own link.
        var functionNumber = (byte)(gpr[(int)R3000aRegister.T1] & 0xFFu);

        // The Runtime's registry is the single source of truth for how many of
        // $a0-$a3 a registered service actually consumes (Issue #365 owns any
        // richer descriptor; this trap only needs the count). An unregistered call
        // has no known arity, so every ABI argument register is preserved rather
        // than fabricated as zero — the Runtime's own Unsupported outcome does not
        // consume Arguments either way.
        uint[] arguments;
        if (biosRuntime.TryGetServiceArgumentCount(family, functionNumber, out var argumentCount))
        {
            if (argumentCount > AbiArgumentRegisterCount)
            {
                return Stop(
                    ArityExceedsRegisterBoundaryDiagnosticCode,
                    $"{family}:{functionNumber:X2} requires {argumentCount} arguments, which exceeds the " +
                    $"{AbiArgumentRegisterCount} ABI argument registers ($a0-$a3) this live trap can read; " +
                    "stack arguments are not supported.");
            }

            arguments = AbiArgumentRegisters(gpr)[..argumentCount];
        }
        else
        {
            arguments = AbiArgumentRegisters(gpr);
        }

        // GuestPc is the guest call-site PC, not the trampoline vector. Neither
        // execution path tracks the transfer instruction's own PC separately from
        // the live PC (which, at this point, IS the trampoline vector address), so
        // it is left null rather than misreported.
        var identity = new BiosCallIdentity(family, functionNumber, guestPc: null, arguments);

        var result = biosRuntime.Invoke(identity);
        switch (result.Status)
        {
            case BiosServiceStatus.PatchedTarget:
                var target = result.ReturnValue ?? 0u;

                // No address policy of its own: this is the same
                // Ps1AddressTranslation boundary every guest memory access already
                // passes through, applied to an address about to be fetched from.
                if (!Ps1AddressTranslation.TryTranslate(target, out _))
                {
                    return Stop(
                        UntranslatableTargetDiagnosticCode,
                        $"{identity.StableKey}: patched jump-table entry names guest address 0x{target:X8}, " +
                        "which falls outside every translatable region, so control cannot be transferred to it.");
                }

                return new BiosVectorDispatchOutcome(
                    ContinueExecution: true,
                    NextPc: target,
                    ReturnValue: null,
                    IsPatchedTarget: true,
                    DiagnosticCode: null,
                    DiagnosticMessage: null);

            case BiosServiceStatus.Supported:
                return new BiosVectorDispatchOutcome(
                    ContinueExecution: true,
                    NextPc: gpr[(int)R3000aRegister.Ra],
                    ReturnValue: result.ReturnValue,
                    IsPatchedTarget: false,
                    DiagnosticCode: null,
                    DiagnosticMessage: null);

            default:
                return Stop(
                    result.Diagnostic?.Code ?? UnresolvedDiagnosticCode,
                    result.Diagnostic?.ToStableString() ??
                    $"{identity.StableKey}: the Runtime returned {result.Status} with no diagnostic.");
        }
    }

    /// <summary>The number of general-purpose registers a dispatch reads.</summary>
    private const int RecompilerGprCount = 32;

    private static BiosVectorDispatchOutcome Stop(string code, string message) =>
        new(ContinueExecution: false, NextPc: 0, ReturnValue: null, IsPatchedTarget: false, code, message);

    /// <summary>Reads the four PS1 ABI argument registers, $a0-$a3, in order.</summary>
    private static uint[] AbiArgumentRegisters(IReadOnlyList<uint> gpr) =>
    [
        gpr[(int)R3000aRegister.A0],
        gpr[(int)R3000aRegister.A1],
        gpr[(int)R3000aRegister.A2],
        gpr[(int)R3000aRegister.A3],
    ];
}
