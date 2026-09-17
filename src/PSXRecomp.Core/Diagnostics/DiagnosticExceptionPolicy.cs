using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Diagnostics;

/// <summary>
/// The boundary between <see cref="Diagnostic"/> (expected / domain failures)
/// and exceptions (programming bugs, invariant violations, process-level
/// fatals).
///
/// <para>
/// Expected failures — malformed input, unsupported coverage, missing files,
/// tool failures — are classified as <see cref="Diagnostic"/> values and flow
/// through results rather than being caught. Exceptions are reserved for
/// programming bugs and invariant violations and must propagate. In
/// particular, process-fatal exceptions must never be swallowed and converted
/// into a diagnostic: recovering from them from inside the same process is
/// not defined.
/// </para>
/// </summary>
[Domain]
public static class DiagnosticExceptionPolicy
{
    /// <summary>
    /// Whether an exception is process-fatal and must never be caught and
    /// converted into a <see cref="Diagnostic"/>. Returns <c>true</c> for
    /// out-of-memory, stack overflow, and access-violation faults (and their
    /// subtypes). Catches in production paths should use
    /// <c>catch (Exception e) when (!DiagnosticExceptionPolicy.IsProcessFatal(e))</c>.
    /// </summary>
    public static bool IsProcessFatal(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
    }
}