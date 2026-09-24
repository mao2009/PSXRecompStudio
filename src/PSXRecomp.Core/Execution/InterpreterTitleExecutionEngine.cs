using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;

namespace PSXRecomp.Core.Execution;

/// <summary>
/// An <see cref="IRecompiledExecutionEngine"/> that drives the existing native
/// R3000A interpreter (<see cref="PSXCoreWrapper"/>) over one persistent core, so
/// guest RAM written in a previous segment is still there for the next one. It
/// applies the shared <c>BiosVectorDispatch</c> in-band exactly like
/// <c>RecompilerInterpreterExecutor</c> (Issue #362/ADR-014): no BIOS semantics
/// are reimplemented here.
/// </summary>
/// <remarks>
/// This is the production execution backend (ADR-015). It lives in the Domain
/// layer because it needs none of the things <see cref="IRecompiledExecutionEngine"/>
/// names as reasons to live outside it: no compiler, no temporary files, no
/// process control. Its only dependency is <see cref="PSXCoreWrapper"/>, the
/// Domain-resident P/Invoke boundary the interop rule already pins here. The
/// generated-host backend — which does need a compiler and process control —
/// stays outside the Domain layer and is deferred (ADR-015).
/// </remarks>
[Domain]
public sealed class InterpreterTitleExecutionEngine : IRecompiledExecutionEngine
{
    /// <summary>The diagnostic-facing backend name this engine reports as <see cref="Name"/>.</summary>
    public const string EngineName = "interpreter-native-full-title";

    private readonly IReadOnlyList<uint> _instructions;
    private readonly uint _loadAddress;
    private readonly uint _programEnd;
    private readonly Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime>? _biosRuntimeFactory;
    private readonly PSXCoreWrapper _core = new();
    private readonly MemoryBus _bus;
    private bool _loaded;

    /// <summary>
    /// Creates an engine over the guest program <paramref name="instructions"/>,
    /// loaded at guest address <paramref name="loadAddress"/>. Execution starts
    /// wherever the orchestrator seeds each segment (the initial <c>PC</c> of the
    /// <see cref="TitleExecutionRequest"/>), so the load base and the entry point
    /// are deliberately independent: a real PS-X EXE loads its whole text region
    /// at <see cref="PsxExe.Header"/>'s text start and enters at its header entry
    /// point, which need not coincide with the text start.
    /// </summary>
    /// <param name="instructions">The guest instruction words making up the program image,
    /// written contiguously at <paramref name="loadAddress"/>.</param>
    /// <param name="loadAddress">The guest address the program image is written to; also
    /// the lower bound of the program image the engine will execute guests inside of.</param>
    /// <param name="biosRuntimeFactory">Builds the BIOS runtime this engine dispatches
    /// A0/B0/C0 vector hits to, over the engine's own guest memory. Null runs without
    /// BIOS dispatch, so a vector hit is simply an unresolved transfer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="instructions"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="instructions"/> is empty, or the
    /// program image overflows the 32-bit address space or does not map to a contiguous
    /// translatable span of physical memory.</exception>
    public InterpreterTitleExecutionEngine(
        IReadOnlyList<uint> instructions,
        uint loadAddress,
        Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime>? biosRuntimeFactory = null)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        if (instructions.Count == 0)
        {
            throw new ArgumentException("The interpreter needs at least one instruction to execute.", nameof(instructions));
        }

        // Mirrors PsxExeTitleInput.Build: Load writes at TranslateAddress(loadAddress) + i*4
        // while FetchInstruction translates each virtual PC, so an image that wraps 32 bits
        // or crosses a KUSEG/KSEG0/KSEG1 boundary would be written to different physical
        // addresses than the CPU fetches. Fail closed here too so no caller can bypass the
        // bridge's validation by constructing the engine directly.
        // instructions.Count * 4 must happen in ulong: computed in uint first (as a
        // previous revision did), a Count above 0x3FFFFFFF wraps before the ulong
        // widening ever sees it, so the overflow check below sees a small, wrong
        // length and lets a bogus loadAddress/programEnd pair through.
        var programLength = (ulong)instructions.Count * sizeof(uint);
        var programEndUlong = (ulong)loadAddress + programLength;
        if (programEndUlong > uint.MaxValue || programEndUlong <= loadAddress)
        {
            throw new ArgumentException(
                $"The program image overflows the 32-bit address space: loadAddress=0x{loadAddress:X8} count={instructions.Count}.",
                nameof(loadAddress));
        }

        var programEnd = (uint)programEndUlong;
        if (!Ps1AddressTranslation.TryTranslate(loadAddress, out var startPhysical)
            || !Ps1AddressTranslation.TryTranslate(programEnd - 1, out var endPhysical)
            || (ulong)endPhysical != (ulong)startPhysical + programLength - 1)
        {
            throw new ArgumentException(
                $"The program image 0x{loadAddress:X8}..0x{programEnd:X8} does not map to a contiguous " +
                "translatable span of physical memory.", nameof(loadAddress));
        }

        _instructions = instructions;
        _loadAddress = loadAddress;
        _programEnd = programEnd;
        _biosRuntimeFactory = biosRuntimeFactory;

        // Wire the managed MMIO layer (DMA/timers/interrupt controller) into
        // the production engine so it is reachable from the real execution path
        // instead of only from unit tests (Issue #386). The BIOS runtime seam
        // travels through this bus, so guest RAM/mirror/device semantics all
        // come from one routing point while the interpreter drives the same
        // native core.
        _bus = new MemoryBus(_core);
        _bus.AttachDmaAdapter(new DmaMmioAdapter(_core));
        _bus.AttachTimerAdapter(new TimerMmioAdapter(_core));
        _bus.AttachInterruptControllerAdapter(new InterruptControllerMmioAdapter(_core));
    }

    /// <inheritdoc />
    public string Name => EngineName;

    /// <inheritdoc />
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

        var ramOffset = TranslateAddress(_loadAddress);
        for (var i = 0; i < _instructions.Count; i++)
        {
            _core.WriteMemory32(ramOffset + unchecked((uint)i * 4u), _instructions[i]);
        }

        _loaded = true;
    }

    /// <inheritdoc />
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
            new GuestMemoryReader(_bus.Read8),
            new GuestMemoryWriter(_bus.Write8));

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

            // Step() reports only a native-call failure; a guest exception leaves
            // it 0 and moves the PC to the exception vector. Without the flag
            // (Issue #377) a faulting segment left the program bounds on the next
            // iteration and reported Success, which the orchestrator hands to the
            // handoff — a GTE/CpU fault could be classified Completed.
            if (_core.Step() != 0 || _core.ExceptionRaised)
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

    /// <summary>Releases the native core and memory bus this engine owns.</summary>
    public void Dispose()
    {
        _bus.Dispose();
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

    private bool PcWithinProgram(uint pc) => pc >= _loadAddress && pc < _programEnd;

    private static uint TranslateAddress(uint virtualAddress)
    {
        if (!Ps1AddressTranslation.TryTranslate(virtualAddress, out var physical))
        {
            throw new ArgumentOutOfRangeException(nameof(virtualAddress), "Execution must start in KUSEG/KSEG0/KSEG1.");
        }
        return physical;
    }
}