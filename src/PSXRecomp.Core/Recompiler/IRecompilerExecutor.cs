using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Recompiler;

/// <summary>
/// Common contract for an execution path that runs a
/// <see cref="RecompilerDifferentialFixture"/> and yields a
/// <see cref="RecompilerStateSnapshot"/>. The interpreter executor and the
/// generated-host executor both implement this so the differential harness can
/// drive them identically (Issue #211).
/// </summary>
[Domain]
public interface IRecompilerExecutor
{
    /// <summary>Short, stable identifier used in diagnostics.</summary>
    string Name { get; }

    /// <summary>Runs the fixture and returns the resulting state, or an executor-level failure.</summary>
    RecompilerExecutionResult Execute(RecompilerDifferentialFixture fixture);
}
