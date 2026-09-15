using PSXRecomp.Core;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Tests.Execution;

/// <summary>
/// An <see cref="IRecompiledExecutionEngine"/> that drives the existing native
/// R3000A interpreter (<c>PSXCoreWrapper</c>) over one persistent core, so guest
/// RAM written in a previous segment is still there for the next one. It applies
/// the shared <c>BiosVectorDispatch</c> in-band exactly like
/// <c>RecompilerInterpreterExecutor</c> (Issue #362/ADR-014): no BIOS semantics
/// are reimplemented here.
/// </summary>
[Test]
internal sealed class InterpreterTitleExecutionEngine : IRecompiledExecutionEngine
{
    public const string EngineName = "interpreter-native-full-title";

    private readonly IReadOnlyList<uint> _instructions;
    private readonly uint _entryPc;
    private readonly uint _programEnd;
    private readonly Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime>? _biosRuntimeFactory;
    private readonly PSXCoreWrapper _core = new();
    private bool _loaded;

    public InterpreterTitleExecutionEngine(
        IReadOnlyList<uint> instructions,
        uint entryPc,
        Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime>? biosRuntimeFactory = null)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        if (instructions.Count == 0)
        {
            throw new ArgumentException("The interpreter needs at least one instruction to execute.", nameof(instructions));
        }

        _instructions = instructions;
        _entryPc = entryPc;
        _programEnd = unchecked(entryPc + (uint)instructions.Count * 4u);
        _biosRuntimeFactory = biosRuntimeFactory;
    }

    public string Name => EngineName;

    public void Load(TitleExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Mirrors RecompilerInterpreterExecutor: initial memory first (translated
        // to physical), then the program words so the code image wins any overlap.
        _core.Reset();
        foreach (var item in request.InitialMemory)
        {
            _core.WriteMemory8(TranslateAddress(item.Address), item.Value);
        }

        var ramOffset = TranslateAddress(_entryPc);
        for (var i = 0; i < _instructions.Count; i++)
        {
            _core.WriteMemory32(ramOffset + unchecked((uint)i * 4u), _instructions[i]);
        }

        _loaded = true;
    }

    public RecompilerExecutionResult RunSegment(TitleExecutionSegmentRequest segmentRequest)
    {
        ArgumentNullException.ThrowIfNull(segmentRequest);
        if (!_loaded)
        {
            throw new InvalidOperationException("Load must complete before the first segment runs.");
        }

        for (var i = 0; i < TitleExecutionRequest.GprCount; i++)
        {
            _core.SetGpr(i, segmentRequest.Gpr[i]);
        }
        _core.Hi = segmentRequest.Hi;
        _core.Lo = segmentRequest.Lo;
        _core.Pc = segmentRequest.Pc;

        var biosRuntime = _biosRuntimeFactory?.Invoke(
            new GuestMemoryReader(_core.ReadMemory8),
            new GuestMemoryWriter(_core.WriteMemory8));

        var termination = RecompilerIrTerminationReason.Success;
        string? diagnosticCode = null;
        string? diagnosticMessage = null;

        for (uint step = 0; step < segmentRequest.Budget; step++)
        {
            // A vector dispatch costs a step from the same budget that bounds
            // ordinary instructions, exactly like the interpreter executor.
            if (biosRuntime is not null && BiosJumpTables.TryResolveVectorFamily(_core.Pc, out var family))
            {
                var outcome = BiosVectorDispatch.Dispatch(biosRuntime, family, ReadGpr());
                if (!outcome.ContinueExecution)
                {
                    diagnosticCode = outcome.DiagnosticCode;
                    diagnosticMessage = outcome.DiagnosticMessage;
                    termination = RecompilerIrTerminationReason.UnresolvedIndirectFlow;
                    break;
                }

                if (outcome.ReturnValue is uint returnValue)
                {
                    _core.SetGpr((int)R3000aRegister.V0, returnValue);
                }

                // A translatable patched target is jumped to verbatim — the core
                // fetches from any translatable address.
                _core.Pc = outcome.NextPc;
                continue;
            }

            if (!PcWithinProgram(_core.Pc))
            {
                break;
            }

            if (_core.Step() != 0)
            {
                termination = RecompilerIrTerminationReason.Exception;
                break;
            }
        }

        var stillRunning = PcWithinProgram(_core.Pc) ||
                           (biosRuntime is not null && BiosJumpTables.TryResolveVectorFamily(_core.Pc, out _));
        if (termination == RecompilerIrTerminationReason.Success && stillRunning)
        {
            termination = RecompilerIrTerminationReason.ExecutionBudgetExceeded;
        }

        var snapshot = new RecompilerStateSnapshot(
            ReadGpr(),
            hi: _core.Hi,
            lo: _core.Lo,
            pc: _core.Pc,
            termination: termination);

        return new RecompilerExecutionResult(
            RecompilerExecutionStatus.Completed, snapshot, diagnosticCode, diagnosticMessage);
    }

    public void Dispose()
    {
        _core.Dispose();
        GC.SuppressFinalize(this);
    }

    private uint[] ReadGpr()
    {
        var gpr = new uint[TitleExecutionRequest.GprCount];
        for (var i = 0; i < gpr.Length; i++)
        {
            gpr[i] = _core.GetGpr(i);
        }
        return gpr;
    }

    private bool PcWithinProgram(uint pc) => pc >= _entryPc && pc < _programEnd;

    private static uint TranslateAddress(uint virtualAddress)
    {
        if (!Ps1AddressTranslation.TryTranslate(virtualAddress, out var physical))
        {
            throw new ArgumentOutOfRangeException(nameof(virtualAddress), "Execution must start in KUSEG/KSEG0/KSEG1.");
        }
        return physical;
    }
}