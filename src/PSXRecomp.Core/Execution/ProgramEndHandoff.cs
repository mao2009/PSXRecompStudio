using PSXRecomp.Architecture;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Core.Execution;

/// <summary>
/// Declines every unresolved transfer except "the guest ran off the end of its
/// own text image", which is reported as a natural exit — the handoff semantics
/// <c>TitleExecutionService</c>, <c>run</c>, <c>screenshot</c> and the runtime
/// host establish for a full-title execution. This is caller-supplied
/// continuation policy, not domain logic, so it lives in <see cref="Execution"/>
/// alongside <see cref="ExecutionOrchestrator"/> and
/// <see cref="TitleExecutionHandoffResult"/>.
/// </summary>
[Domain]
public sealed class ProgramEndHandoff : ITitleExecutionHandoff
{
    private readonly uint _programEnd;

    /// <summary>
    /// Creates a handoff that exits when control reaches <paramref name="programEnd"/>.
    /// </summary>
    /// <param name="programEnd">The guest PC one past the program image, the natural exit boundary.</param>
    public ProgramEndHandoff(uint programEnd) => _programEnd = programEnd;

    public TitleExecutionHandoffResult? Decide(RecompilerStateSnapshot segmentState) =>
        segmentState.PC == _programEnd ? TitleExecutionHandoffResult.Exit() : null;
}
