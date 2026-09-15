using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Execution;

/// <summary>
/// A pure-managed <see cref="IRecompiledExecutionEngine"/> over the existing IR
/// reference evaluator (<c>RecompilerIrEvaluator</c>), used to unit-test the
/// orchestration loop without gcc or the native core.
/// </summary>
/// <remarks>
/// Memory is a single <c>RecompilerGuestMemory</c> instance held for the whole
/// orchestration, so guest RAM written in one segment is visible in the next —
/// exactly the continuity the host engine provides through its RAM dump. The
/// evaluator retires IR blocks per segment, so <see cref="TitleExecutionRequest.SegmentBudget"/>
/// is denominated in blocks. HI/LO are not tracked by the evaluator; this engine
/// carries them unchanged across segments.
/// </remarks>
[Test]
internal sealed class RecompiledIrTitleExecutionEngine : IRecompiledExecutionEngine
{
    public const string EngineName = "ir-evaluator";

    private readonly RecompilerIrProgram _program;
    private RecompilerGuestMemory _memory = new();
    private uint _hi;
    private uint _lo;

    public RecompiledIrTitleExecutionEngine(RecompilerIrProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        _program = program;
    }

    public string Name => EngineName;

    public void Load(TitleExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _memory = new RecompilerGuestMemory();
        foreach (var item in request.InitialMemory)
        {
            _memory.Write8(item.Address, item.Value);
        }
    }

    public RecompilerExecutionResult RunSegment(TitleExecutionSegmentRequest segmentRequest)
    {
        _hi = segmentRequest.Hi;
        _lo = segmentRequest.Lo;

        var evaluation = RecompilerIrEvaluator.Run(
            _program, segmentRequest.Pc, segmentRequest.Gpr, _memory, segmentRequest.Budget);

        var snapshot = new RecompilerStateSnapshot(
            evaluation.Gpr,
            hi: _hi,
            lo: _lo,
            pc: evaluation.Pc,
            termination: evaluation.Termination);

        return RecompilerExecutionResult.Completed(snapshot);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}