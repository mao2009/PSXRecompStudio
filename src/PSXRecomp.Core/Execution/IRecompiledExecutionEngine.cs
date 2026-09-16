using PSXRecomp.Architecture;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Core.Execution;

/// <summary>
/// A backend that runs bounded segments of recompiled guest execution over a
/// persistent guest memory image.
/// </summary>
/// <remarks>
/// <para>
/// The engine is the recompiled-execution half of the full-title loop: it owns
/// the guest memory and the CPU semantics for one contiguous run, and yields at
/// segment boundaries (budget cut or an unresolvable guest transfer). BIOS
/// semantics are <b>not</b> the engine's problem — a recompiled engine relays
/// the shared <c>BiosVectorDispatch</c> decision in-band over the #368 host
/// hook, and an interpreter engine applies the same shared dispatch, so both
/// engines stay free of BIOS knowledge (ADR-014).
/// </para>
/// <para>
/// This interface is the only dependency the <see cref="ExecutionOrchestrator"/>
/// has on concrete execution: implementations that need a compiler, temporary
/// files, or process control (the generated-host path) live outside the Domain
/// layer — exactly as <c>RecompilerHostExecutor</c> does — while pure-CPU
/// backends may live anywhere below.
/// </para>
/// </remarks>
[Domain]
public interface IRecompiledExecutionEngine : IDisposable
{
    /// <summary>A stable, diagnostic-facing name for the backend (e.g. "recompiled-host-gcc").</summary>
    string Name { get; }

    /// <summary>
    /// Seeds the engine's persistent guest memory from <see cref="TitleExecutionRequest.InitialMemory"/>
    /// and prepares the backend for its first segment. Called exactly once per orchestration.
    /// </summary>
    /// <exception cref="InvalidOperationException">The backend cannot be prepared (e.g. the
    /// host source failed to build).</exception>
    void Load(TitleExecutionRequest request);

    /// <summary>
    /// Runs one bounded segment from the given architectural state over the
    /// engine's persistent guest memory and returns the post-segment state.
    /// Guest memory written during a previous segment is visible to this one.
    /// </summary>
    /// <returns>A completed execution result whose snapshot carries the
    /// post-segment state and CPU-level termination reason, or an executor-level
    /// mechanism failure.</returns>
    RecompilerExecutionResult RunSegment(TitleExecutionSegmentRequest segmentRequest);
}