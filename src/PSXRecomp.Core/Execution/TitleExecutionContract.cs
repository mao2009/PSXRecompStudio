using PSXRecomp.Architecture;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Core.Execution;

/// <summary>
/// Classifies how a full-title execution ended. This is the orchestrator-level
/// outcome — distinct from the CPU-level termination reason carried by a
/// <see cref="RecompilerStateSnapshot"/>, which describes a single bounded
/// segment and is why those reasons map onto these states rather than being
/// exposed directly.
/// </summary>
[Domain]
public enum TitleExecutionState : byte
{
    /// <summary>The guest reached a classified, legitimate end.</summary>
    Completed,

    /// <summary>The guest transferred its control back to a PC the handoff
    /// reports as a natural return, and the slice stops there.</summary>
    Returned,

    /// <summary>The guest stopped at the Runtime/BIOS boundary and the outer
    /// world is expected to take over (a clean segment end, or a handoff that
    /// asked to pause).</summary>
    RuntimeHandoff,

    /// <summary>The outer execution budget expired while the guest was still
    /// running.</summary>
    BudgetExhausted,

    /// <summary>Control transferred to a guest address with no compiled code
    /// and no continuation rule (the dynamic-overlay recompilation boundary,
    /// Issue #249).</summary>
    UnsupportedTransfer,

    /// <summary>Guest execution stopped in a failed state: a Runtime/BIOS
    /// diagnostic, a CPU exception, or an engine failure.</summary>
    RuntimeFailure,

    /// <summary>The orchestrator received a segment result or a handoff
    /// decision that contradicts the execution contract (e.g. an invalid
    /// continuation target). Not a guest failure — a caller/handoff failure.</summary>
    InvalidState,
}

/// <summary>
/// The full-title execution request: the initial guest state, the guest memory
/// seed, and the two execution budgets. Title-agnostic — it has no notion of a
/// single function, of a particular game, or of BIOS personalities (those live
/// in the handoff and in the executed program).
/// </summary>
[Domain]
public sealed class TitleExecutionRequest
{
    /// <summary>Number of general-purpose registers in <see cref="InitialGpr"/>.</summary>
    public const int GprCount = 32;

    /// <summary>Constructs a request and validates its shape up front.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="initialGpr"/> or
    /// <paramref name="initialMemory"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="initialGpr"/> is not exactly
    /// <see cref="GprCount"/> values.</exception>
    public TitleExecutionRequest(
        uint entryPc,
        IReadOnlyList<uint> initialGpr,
        uint initialHi,
        uint initialLo,
        IReadOnlyList<RecompilerInitialMemoryItem> initialMemory,
        uint outerBudget,
        uint segmentBudget)
    {
        ArgumentNullException.ThrowIfNull(initialGpr);
        if (initialGpr.Count != GprCount)
        {
            throw new ArgumentException(
                $"Initial GPR must contain exactly {GprCount} values, found {initialGpr.Count}.", nameof(initialGpr));
        }

        ArgumentNullException.ThrowIfNull(initialMemory);

        var gpr = new uint[GprCount];
        for (var i = 0; i < GprCount; i++) gpr[i] = initialGpr[i];
        gpr[0] = 0;

        var memory = new RecompilerInitialMemoryItem[initialMemory.Count];
        for (var i = 0; i < memory.Length; i++) memory[i] = initialMemory[i];

        EntryPc = entryPc;
        InitialGpr = Array.AsReadOnly(gpr);
        InitialHi = initialHi;
        InitialLo = initialLo;
        InitialMemory = Array.AsReadOnly(memory);
        OuterBudget = outerBudget;
        SegmentBudget = segmentBudget;
    }

    /// <summary>The guest address execution starts at.</summary>
    public uint EntryPc { get; }

    /// <summary>The initial register file (index 0 is pinned to 0).</summary>
    public IReadOnlyList<uint> InitialGpr { get; }

    /// <summary>The initial HI register of the multiply/divide unit.</summary>
    public uint InitialHi { get; }

    /// <summary>The initial LO register of the multiply/divide unit.</summary>
    public uint InitialLo { get; }

    /// <summary>Byte writes applied to guest memory before the first segment.</summary>
    public IReadOnlyList<RecompilerInitialMemoryItem> InitialMemory { get; }

    /// <summary>The number of segments the orchestrator may run before giving up.</summary>
    public uint OuterBudget { get; }

    /// <summary>The per-segment budget handed to the engine each run.</summary>
    public uint SegmentBudget { get; }
}

/// <summary>
/// The state a segment is seeded with: the register file, HI/LO, the PC to
/// resume at, and how much of that engine's per-segment budget the segment may
/// spend before it must yield.
/// </summary>
[Domain]
public sealed class TitleExecutionSegmentRequest
{
    /// <summary>Constructs a segment request and validates its shape up front.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="gpr"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="gpr"/> is not exactly
    /// <see cref="TitleExecutionRequest.GprCount"/> values.</exception>
    public TitleExecutionSegmentRequest(
        IReadOnlyList<uint> gpr,
        uint hi,
        uint lo,
        uint pc,
        uint budget)
    {
        ArgumentNullException.ThrowIfNull(gpr);
        if (gpr.Count != TitleExecutionRequest.GprCount)
        {
            throw new ArgumentException(
                $"Segments run on the whole register file; expected {TitleExecutionRequest.GprCount} values.",
                nameof(gpr));
        }

        var copy = new uint[TitleExecutionRequest.GprCount];
        for (var i = 0; i < copy.Length; i++) copy[i] = gpr[i];
        copy[0] = 0;

        Gpr = Array.AsReadOnly(copy);
        Hi = hi;
        Lo = lo;
        Pc = pc;
        Budget = budget;
    }

    /// <summary>The register file to seed the segment with.</summary>
    public IReadOnlyList<uint> Gpr { get; }

    /// <summary>The HI register to seed the segment with.</summary>
    public uint Hi { get; }

    /// <summary>The LO register to seed the segment with.</summary>
    public uint Lo { get; }

    /// <summary>The PC to resume guest execution at.</summary>
    public uint Pc { get; }

    /// <summary>How much of the engine's own budget the segment may consume.</summary>
    public uint Budget { get; }
}

/// <summary>
/// What the orchestrator should do when a segment ends on an unresolved PC
/// (the guest transferred somewhere the engine has no compiled code for).
/// </summary>
[Domain]
public enum TitleExecutionHandoffAction : byte
{
    /// <summary>Resume guest execution at <see cref="TitleExecutionHandoffResult.NextPc"/>.</summary>
    ContinueAt,

    /// <summary>Treat this as the guest's natural end: the orchestrator reports
    /// <see cref="TitleExecutionState.Completed"/>.</summary>
    Exit,

    /// <summary>Treat this as a guest return: the orchestrator reports
    /// <see cref="TitleExecutionState.Returned"/>.</summary>
    Return,

    /// <summary>Stop and hand control to the outer world:
    /// <see cref="TitleExecutionState.RuntimeHandoff"/>.</summary>
    Pause,
}

/// <summary>
/// A handoff's answer to one unresolved segment end. A null decision (the
/// interface returns null) means "no rule for this PC", which maps to
/// <see cref="TitleExecutionState.UnsupportedTransfer"/>.
/// </summary>
[Domain]
public readonly record struct TitleExecutionHandoffResult(
    TitleExecutionHandoffAction Action,
    uint NextPc = 0,
    uint? ReturnValue = null)
{
    /// <summary>A decision that resumes guest execution at <paramref name="nextPc"/>,
    /// optionally writing a new V0 return value first.</summary>
    public static TitleExecutionHandoffResult ContinueAt(uint nextPc, uint? returnValue = null) =>
        new(TitleExecutionHandoffAction.ContinueAt, nextPc, returnValue);

    /// <summary>A decision that reports a natural guest exit.</summary>
    public static TitleExecutionHandoffResult Exit() =>
        new(TitleExecutionHandoffAction.Exit);

    /// <summary>A decision that reports a guest return.</summary>
    public static TitleExecutionHandoffResult Return() =>
        new(TitleExecutionHandoffAction.Return);

    /// <summary>A decision that stops so the outer world can take over.</summary>
    public static TitleExecutionHandoffResult Pause() =>
        new(TitleExecutionHandoffAction.Pause);
}

/// <summary>
/// Decides what an unresolved segment end means. Implementations describe where
/// guest control is allowed to continue — a patched jump-table entry resolved at
/// runtime, a known exit point, a function return — without ever carrying BIOS
/// semantics themselves; those remain in the shared <c>BiosVectorDispatch</c>
/// contract that the engines already invoke in-band.
/// </summary>
[Domain]
public interface ITitleExecutionHandoff
{
    /// <summary>
    /// Called once per segment that ends with the guest at a PC the engine
    /// could not continue from itself.
    /// </summary>
    /// <param name="segmentState">The full post-segment architectural state,
    /// including the PC the guest transfer landed on.</param>
    /// <returns>An action, or null when this PC has no continuation rule
    /// (which the orchestrator reports as <see cref="TitleExecutionState.UnsupportedTransfer"/>).</returns>
    TitleExecutionHandoffResult? Decide(RecompilerStateSnapshot segmentState);
}

/// <summary>
/// The outcome of one full-title orchestration run.
/// </summary>
[Domain]
public sealed record TitleExecutionResult(
    TitleExecutionState State,
    RecompilerStateSnapshot? FinalSnapshot,
    uint SegmentsRetired,
    string? EngineName,
    string? DiagnosticCode,
    string? DiagnosticMessage);