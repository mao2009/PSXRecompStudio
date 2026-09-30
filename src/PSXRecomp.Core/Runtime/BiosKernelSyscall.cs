using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// The SYS(n) function numbers of the kernel's SYSCALL exception handler
/// (psx-spx kernelbios function-summary, "SYS-Functions"; the number is <c>$a0</c>).
/// Only numbers with a Runtime implementation are named.
/// </summary>
[Domain]
public enum BiosKernelSyscall : uint
{
    /// <summary>SYS(02h) ExitCriticalSection().</summary>
    ExitCriticalSection = 2,
}

/// <summary>Outcome of <see cref="BiosKernelSyscallDispatch.Dispatch"/>.</summary>
/// <param name="Handled">True when the syscall was serviced; false when the run must stop with the diagnostic.</param>
/// <param name="SrAtReturn">
/// The exception frame's SR when the kernel returns from the syscall exception,
/// i.e. the value the kernel leaves for RFE to pop. Meaningful only when
/// <paramref name="Handled"/> is true.
/// </param>
/// <param name="DiagnosticCode">Stable diagnostic code when not handled.</param>
/// <param name="DiagnosticMessage">Human-readable diagnostic when not handled.</param>
[Domain]
public sealed record BiosKernelSyscallOutcome(
    bool Handled, uint SrAtReturn, string? DiagnosticCode, string? DiagnosticMessage);

/// <summary>
/// The kernel SYSCALL-exception boundary, stated once for every execution path
/// (Issue #663): given the SYS number and the SR the exception entry left, what
/// the kernel does to the exception frame before returning. The execution path
/// owns everything CPU-side — the exception entry, the RFE pop of
/// <see cref="BiosKernelSyscallOutcome.SrAtReturn"/>, and resuming at EPC+4 —
/// because SR is CPU state; this type holds no state and no second SR.
/// </summary>
/// <remarks>
/// CONFIRMED (psx-spx interrupt-exception-handling): SYS(02h) "Enables interrupts
/// by set SR (cop0r12) Bit 2 and 10 (of which, Bit2 gets copied to Bit0 once when
/// returning from the syscall exception). There's no return value (all registers
/// except SR and K0 are unchanged)." Bit 2 is IEp of the SR the exception entry
/// pushed; the RFE on return moves it to IEc (bit 0).
/// </remarks>
[Domain]
public static class BiosKernelSyscallDispatch
{
    /// <summary>CAUSE Excode of the SYSCALL instruction (docs/cpu/exceptions.md).</summary>
    public const uint SyscallExcode = 0x08;

    /// <summary>Reported when a SYSCALL names a SYS number the Runtime does not implement.</summary>
    public const string UnsupportedDiagnosticCode = "BIOS_SYSCALL_UNSUPPORTED";

    /// <summary>SR bit 2 (IEp) and bit 10 (IM2) that SYS(02h) sets.</summary>
    private const uint CriticalSectionBits = (1u << 2) | (1u << 10);

    /// <summary>Services SYS(<paramref name="number"/>); anything not implemented fails closed.</summary>
    /// <param name="number">The SYS function number (<c>$a0</c>).</param>
    /// <param name="srAtEntry">The SR after the exception entry pushed the KU/IE stack.</param>
    public static BiosKernelSyscallOutcome Dispatch(uint number, uint srAtEntry) =>
        number == (uint)BiosKernelSyscall.ExitCriticalSection
            ? new BiosKernelSyscallOutcome(true, srAtEntry | CriticalSectionBits, null, null)
            : new BiosKernelSyscallOutcome(
                false,
                0,
                UnsupportedDiagnosticCode,
                $"{UnsupportedDiagnosticCode}|SYS({number:X2}h)|no Runtime implementation for this kernel syscall.");
}
