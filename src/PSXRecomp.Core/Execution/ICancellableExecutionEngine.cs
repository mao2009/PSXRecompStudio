using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Execution;

/// <summary>
/// An optional capability of an <see cref="IRecompiledExecutionEngine"/> that can be asked to stop the segment it is
/// running (Issue #820). It is separate from the engine contract so existing engines do not change; the orchestrator
/// uses it when a cancellation token is supplied.
/// </summary>
[Domain]
public interface ICancellableExecutionEngine
{
    /// <summary>
    /// Asks the running segment to stop. Idempotent, safe to call from any thread, and never waits for the run to end: the
    /// segment returns on its own, classified as <see cref="RecompilerExecutionStatus.Cancelled"/>.
    /// </summary>
    void RequestCancel();
}
