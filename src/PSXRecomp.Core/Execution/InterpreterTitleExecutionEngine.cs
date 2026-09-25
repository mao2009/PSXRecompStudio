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

    /// <summary>
    /// CPU cycles each retired <see cref="PSXCoreWrapper.Step"/> reports to the
    /// <see cref="DeviceScheduler"/>. The native interpreter has no cycle model,
    /// so its retired-instruction count is the elapsed-time source and one
    /// instruction is costed as one cycle (Issue #442; not cycle-exact).
    /// </summary>
    public const uint CyclesPerInstruction = 1;

    private const uint InterruptExcode = 0x00; // INT, docs/cpu/exceptions.md
    private const int Cop0Status = 12;
    private const int Cop0Cause = 13;
    private const int Cop0Epc = 14;
    private const uint HardwareInterruptBit = 1u << 10; // CAUSE.IP2 / SR.IM2

    private readonly IReadOnlyList<uint> _instructions;
    private readonly uint _loadAddress;
    private readonly uint _programEnd;
    private readonly Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime>? _biosRuntimeFactory;
    private readonly PSXCoreWrapper _core = new();
    private readonly MemoryBus _bus;
    private readonly DmaMmioAdapter _dmaAdapter;
    private readonly TimerMmioAdapter _timerAdapter;
    private readonly InterruptControllerMmioAdapter _interruptControllerAdapter;
    private DeviceScheduler? _scheduler;
    private bool _loaded;

    // Set when the CPU takes a hardware interrupt, cleared once the handler has
    // actually returned. Entering the program image alone does not clear it: a
    // handler may call a helper there and return to handler code outside it.
    // While set, a PC outside the image is the guest's own interrupt handler
    // rather than an unresolved transfer. This is only an execution-region
    // permission: EPC/CAUSE/SR stay owned by the native CPU.
    private bool _inInterruptHandler;

    // The PC of the interrupted instruction (cop0 EPC) captured when the first —
    // outermost — hardware interrupt of the current handler nesting was taken.
    // It is the PC the handler must finally return to: a nested interrupt
    // overwrites cop0 EPC inside the handler, so re-reading EPC after nesting
    // would lose the outermost return target, but this private copy never does.
    // It is always inside the program image, because the engine only steps the
    // CPU while PC is inside the image (or inside a handler it already knows),
    // so a *nested* take is the only way EPC lands outside the image and that
    // take never overwrites this value.
    private uint _handlerEpc;

    // Set when the CPU reports the handler executed RFE (ExecRfe, psx_cpu.cpp):
    // RFE only restores SR, it never moves PC (PC restore is a JR responsibility,
    // ADR-005), so RFE alone does not mean the handler has returned. This arms
    // the check below instead of clearing _inInterruptHandler outright, so a
    // handler that keeps running after a standalone RFE (not sharing its return
    // JR's delay slot) is still recognized as handler code (CodeRabbit, PR #502).
    private bool _rfePending;

    // Set when the last segment ended ExecutionBudgetExceeded: the core still
    // holds that segment's live state, including in-flight load-delay and
    // branch-delay state that re-seeding it (SetGpr/SetPC) would flush.
    private bool _resumable;

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
        _dmaAdapter = new DmaMmioAdapter(_core);
        _timerAdapter = new TimerMmioAdapter(_core);
        _interruptControllerAdapter = new InterruptControllerMmioAdapter(_core);
        _bus.AttachDmaAdapter(_dmaAdapter);
        _bus.AttachTimerAdapter(_timerAdapter);
        _bus.AttachInterruptControllerAdapter(_interruptControllerAdapter);

        // SIO0 register model (Issue #542): native/Rust-owned inside
        // PSXMemory (see MemoryBus.ReadMmio/WriteMmio's Sio0 case and
        // crate::sio0's module documentation), so no adapter is attached
        // here — Load()'s _core.Reset() already resets it.
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

        // Fresh device timing for the freshly reset core (Issue #442).
        _scheduler = new DeviceScheduler(_core, _interruptControllerAdapter);
        _inInterruptHandler = false;
        _rfePending = false;
        _handlerEpc = 0;
        _resumable = false;
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

        // A continuation of a budget-cut segment with the state it returned runs
        // on as is. Anything else (first segment, a handoff's ContinueAt, a
        // caller-modified state) is a fresh dispatch and is seeded, which
        // flushes the native pipeline exactly as a jump to a new PC must.
        if (!(_resumable && CoreHolds(segmentRequest)))
        {
            for (var i = 0; i < TitleExecutionRequest.GprCount; i++)
            {
                _core.SetGpr(i, segmentRequest.Gpr[i]);
            }
            _core.Hi = segmentRequest.Hi;
            _core.Lo = segmentRequest.Lo;
            _core.Pc = segmentRequest.Pc;
        }

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

            if (!PcWithinProgram(_core.Pc) && !_inInterruptHandler)
            {
                break;
            }

            // Step() reports only a native-call failure; a guest exception leaves
            // it 0 and moves the PC to the exception vector. Without the flag
            // (Issue #377) a faulting segment left the program bounds on the next
            // iteration and reported Success, which the orchestrator hands to the
            // handoff — a GTE/CpU fault could be classified Completed.
            if (_core.Step() != 0)
            {
                termination = RecompilerIrTerminationReason.Exception;
                break;
            }

            if (_core.ExceptionRaised)
            {
                if (!TookHardwareInterrupt())
                {
                    termination = RecompilerIrTerminationReason.Exception;
                    break;
                }

                // Issue #499: a device IRQ taken as INT is ordinary guest control
                // flow. The CPU has already set EPC/CAUSE/SR and vectored; the
                // guest's handler runs from here and returns with its own
                // MFC0 EPC / JR / RFE. No instruction retired, so no device time.
                var wasInHandler = _inInterruptHandler;
                _inInterruptHandler = true;
                if (!wasInHandler)
                {
                    // First — outermost — take of this handler nesting: record the
                    // interrupted PC the handler must return to. A nested take
                    // overwrites cop0 EPC inside the handler, so it must not
                    // re-capture (and must not erase a still-pending RFE arm).
                    _handlerEpc = _core.GetCop0(Cop0Epc);
                    _rfePending = false;
                }
                continue;
            }

            // RFE armed the return check; it lands only once PC is actually back
            // on the EPC the interrupt captured — whether that happens in this
            // same step (RFE sharing the return JR's delay slot) or several steps
            // later (a standalone RFE ahead of a separate return JR). Comparing
            // against that EPC instead of "inside the program image" is what
            // keeps handler permission while the handler runs a helper in the
            // image after a standalone RFE: the helper entry lands on the helper,
            // not on the interrupted PC, so it cannot look like the return.
            if (_core.RfeExecuted)
            {
                _rfePending = true;
            }
            if (_rfePending && _core.Pc == _handlerEpc)
            {
                _inInterruptHandler = false;
                _rfePending = false;
            }

            // Devices advance by the time the retired instruction took, so
            // Timer/DMA/VBlank interrupts reach the CPU during a real run.
            _scheduler!.Advance(CyclesPerInstruction);
        }

        var stillRunning = PcWithinProgram(_core.Pc) || _inInterruptHandler ||
                           (biosRuntime is not null && BiosJumpTables.TryResolveVectorFamily(_core.Pc, out _));
        if (termination == RecompilerIrTerminationReason.Success && stillRunning)
        {
            termination = RecompilerIrTerminationReason.ExecutionBudgetExceeded;
        }
        _resumable = termination == RecompilerIrTerminationReason.ExecutionBudgetExceeded;

        var snapshot = new RecompilerStateSnapshot(
            ReadGpr(),
            hi: _core.Hi,
            lo: _core.Lo,
            pc: _core.Pc,
            termination: termination);

        return new RecompilerExecutionResult(
            RecompilerExecutionStatus.Completed, snapshot, diagnosticCode, diagnosticMessage);
    }

    /// <summary>Releases the native core, memory bus and MMIO adapters this engine owns.</summary>
    public void Dispose()
    {
        _bus.Dispose();
        // The adapters unregister their callbacks in Dispose(); MemoryBus.Dispose()
        // only clears its own references to them, so they must be disposed here,
        // and before _core.Dispose(), so no adapter can touch a freed native core.
        _dmaAdapter.Dispose();
        _timerAdapter.Dispose();
        _interruptControllerAdapter.Dispose();
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

    private bool CoreHolds(TitleExecutionSegmentRequest request)
    {
        if (_core.Pc != request.Pc || _core.Hi != request.Hi || _core.Lo != request.Lo)
        {
            return false;
        }

        for (var i = 0; i < TitleExecutionRequest.GprCount; i++)
        {
            if (_core.GetGpr(i) != request.Gpr[i])
            {
                return false;
            }
        }
        return true;
    }

    private bool PcWithinProgram(uint pc) => pc >= _loadAddress && pc < _programEnd;

    /// <summary>
    /// Whether the exception the last step raised is an INT the hardware
    /// interrupt line (CAUSE.IP2, enabled by SR.IM2) caused. SYSCALL, BREAK,
    /// faults and software interrupts (CAUSE.IP0/IP1) are not, and still end the
    /// segment. Reads the state the CPU left; decides nothing the CPU owns.
    /// </summary>
    private bool TookHardwareInterrupt() =>
        _core.ExceptionCode == InterruptExcode &&
        (_core.GetCop0(Cop0Cause) & _core.GetCop0(Cop0Status) & HardwareInterruptBit) != 0;

    private static uint TranslateAddress(uint virtualAddress)
    {
        if (!Ps1AddressTranslation.TryTranslate(virtualAddress, out var physical))
        {
            throw new ArgumentOutOfRangeException(nameof(virtualAddress), "Execution must start in KUSEG/KSEG0/KSEG1.");
        }
        return physical;
    }
}