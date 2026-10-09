using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.MemoryCard;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Core.Runtime.CdRom;
using PSXRecomp.Core.Runtime.Gpu;

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
    // Firmware boot needs to execute the kernel's dynamically installed low-RAM code and vectors.
    // The legacy bounded game-image interpreter intentionally keeps this off by default.
    private readonly bool _allowRuntimeRamExecution;
    private readonly BiosExceptionChain? _exceptionChain;
    private readonly PsxDeviceGraph _devices;
    private readonly bool _ownsDevices;
    private readonly PSXCoreWrapper _core;
    private readonly MemoryBus _bus;
    private readonly InterruptControllerMmioAdapter _interruptControllerAdapter;
    private readonly GpuDevice _gpuDevice;
    private readonly GpuMmioAdapter _gpuAdapter;
    private readonly CdRomDevice _cdRomDevice;
    private readonly CdRomDmaTransfer _cdRomDmaTransfer;
    private DeviceScheduler? _scheduler;
    private readonly ExecutionTraceRing _trace = new(ExecutionTraceRing.DefaultCapacity);
    private bool _loaded;
    private bool _disposed;

    // Set when the CPU takes a hardware interrupt, cleared once the handler has
    // actually returned. Entering the program image alone does not clear it: a
    // handler may call a helper there and return to handler code outside it.
    // While set, a PC outside the image is the guest's own interrupt handler
    // rather than an unresolved transfer. This is only an execution-region
    // permission: EPC/CAUSE/SR stay owned by the native CPU.
    private bool _inInterruptHandler;

    // Issue #717: the outstanding blocking BIOS call's poll bound. Kept across a resumed segment, so a wait that
    // spans a segment boundary keeps one bound; reset wherever the CPU is re-seeded (Load, a fresh dispatch, a
    // fallback entry), because the guest state that was its continuation is gone then.
    private readonly BiosBlockingCallWait _blockingCallWait = new();

    // The PC of the interrupted instruction (cop0 EPC) captured when the first —
    // outermost — hardware interrupt of the current handler nesting was taken.
    // It is the PC the handler must finally return to: a nested interrupt
    // overwrites cop0 EPC inside the handler, so re-reading EPC after nesting
    // would lose the outermost return target, but this private copy never does.
    // It is inside the program image, or the A0/B0/C0 vector when the INT was
    // taken while a blocking BIOS call waited there (Issue #717), because the
    // engine only steps the CPU at those PCs (or inside a handler it already
    // knows), so a *nested* take is the only other way EPC lands elsewhere and
    // that take never overwrites this value.
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
    /// <param name="exceptionChain">The kernel exception handler's priority-chain walk (Issue #662);
    /// null is <see cref="BiosExceptionHandler.DefaultChain"/>. The seam modelled kernel handlers plug into.</param>
    /// <param name="memoryCardSlots">Which card is in each slot (Issue #715), the same immutable configuration
    /// <c>RecompiledHostExecutionEngine</c> takes so both backends report one slot state; null is
    /// <see cref="MemoryCardSlotConfiguration.Empty"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="instructions"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="instructions"/> is empty, or the
    /// program image overflows the 32-bit address space or does not map to a contiguous
    /// translatable span of physical memory.</exception>
    public InterpreterTitleExecutionEngine(
        IReadOnlyList<uint> instructions,
        uint loadAddress,
        Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime>? biosRuntimeFactory = null,
        BiosExceptionChain? exceptionChain = null,
        MemoryCardSlotConfiguration? memoryCardSlots = null,
        bool allowRuntimeRamExecution = false,
        ICdSectorSource? disc = null)
        : this(instructions, loadAddress, biosRuntimeFactory, exceptionChain, sharedDevices: null, sharedScheduler: null, memoryCardSlots, allowRuntimeRamExecution, disc)
    {
    }

    /// <summary>
    /// Creates an engine <i>attached</i> to a device graph and scheduler another backend already owns
    /// (Issue #693, ADR-025 amendment): the generated-host artifact's host-owned graph. It is for mixed-execution
    /// fallback only — it never <see cref="Load"/>s an image (guest RAM is copy-synced into the graph's core by the
    /// caller), never builds a graph or scheduler of its own, and does not dispose the graph it was handed. Its
    /// <see cref="RunFallbackSegment"/> steps the graph core's CPU, so interrupt controller, timers, DMA, GPU and
    /// CD-ROM state are the very objects the artifact path already relays to: nothing is copied.
    /// </summary>
    /// <param name="instructions">The guest program image words (bounds the in-image execution region).</param>
    /// <param name="loadAddress">The guest address <paramref name="instructions"/> is loaded at.</param>
    /// <param name="sharedDevices">The host-owned graph whose native core is stepped.</param>
    /// <param name="sharedScheduler">The host-owned scheduler advanced one cycle per retired fallback instruction.</param>
    /// <param name="biosRuntimeFactory">Builds the Runtime over the graph core's RAM (state lives in guest RAM).</param>
    /// <param name="exceptionChain">The kernel exception handler's chain; null is the default chain.</param>
    /// <exception cref="ArgumentNullException">An argument other than the optional ones is null.</exception>
    public static InterpreterTitleExecutionEngine Attach(
        IReadOnlyList<uint> instructions,
        uint loadAddress,
        PsxDeviceGraph sharedDevices,
        DeviceScheduler sharedScheduler,
        Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime>? biosRuntimeFactory = null,
        BiosExceptionChain? exceptionChain = null)
    {
        ArgumentNullException.ThrowIfNull(sharedDevices);
        ArgumentNullException.ThrowIfNull(sharedScheduler);
        return new InterpreterTitleExecutionEngine(
            instructions, loadAddress, biosRuntimeFactory, exceptionChain, sharedDevices, sharedScheduler);
    }

    private InterpreterTitleExecutionEngine(
        IReadOnlyList<uint> instructions,
        uint loadAddress,
        Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime>? biosRuntimeFactory,
        BiosExceptionChain? exceptionChain,
        PsxDeviceGraph? sharedDevices,
        DeviceScheduler? sharedScheduler,
        MemoryCardSlotConfiguration? memoryCardSlots = null,
        bool allowRuntimeRamExecution = false,
        ICdSectorSource? disc = null)
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
        _allowRuntimeRamExecution = allowRuntimeRamExecution;
        _exceptionChain = exceptionChain;

        // The managed MMIO layer (DMA/timers/interrupt controller/GPU/CD-ROM) is
        // the shared Runtime device graph, so it is reachable from the real
        // execution path (Issue #386) and is the same one the generated-host
        // artifact relays to (Issue #678). The BIOS runtime seam travels through
        // its bus, so guest RAM/mirror/device semantics all come from one routing
        // point while the interpreter drives the same native core.
        _devices = sharedDevices ?? new PsxDeviceGraph(memoryCardSlots: memoryCardSlots, disc: disc);
        _ownsDevices = sharedDevices is null;
        if (sharedScheduler is not null)
        {
            // Attached (Issue #693): the owner's scheduler, and no Load — the image arrives by RAM sync.
            _scheduler = sharedScheduler;
            _loaded = true;
        }

        _core = _devices.Core;
        _bus = _devices.Bus;
        _interruptControllerAdapter = _devices.InterruptControllerAdapter;
        _gpuDevice = _devices.GpuDevice;
        _gpuAdapter = _devices.GpuAdapter;
        _cdRomDevice = _devices.CdRomDevice;
        _cdRomDmaTransfer = _devices.CdRomDmaTransfer;
    }

    /// <inheritdoc />
    public string Name => EngineName;

    /// <summary>The most recent fetched (pc, word) pairs and non-sequential control transfers, oldest first (diagnostic only).</summary>
    public ExecutionTraceSnapshot RecentTrace => _trace.Snapshot();

    /// <summary>Called with the PC of every instruction about to be fetched (diagnostic observation only).</summary>
    public Action<uint>? FetchObserver { get; set; }

    /// <summary>The current value of a general register (diagnostic observation only).</summary>
    public uint ReadGuestGpr(int index) => _core.GetGpr(index);

    /// <summary>Reads an aligned guest RAM/ROM word without side effects; 0 outside RAM and ROM.</summary>
    public uint ReadGuestWord(uint address) => FetchWordForTrace(address);

    /// <summary>COP0 SR/CAUSE/EPC/BadVAddr as the CPU holds them now (diagnostic only).</summary>
    public (uint Sr, uint Cause, uint Epc, uint BadVAddr) Cop0Diagnostics =>
        (_core.GetCop0(Cop0Status), _core.GetCop0(Cop0Cause), _core.GetCop0(Cop0Epc), _core.GetCop0(8));

    /// <inheritdoc />
    public void Load(TitleExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_ownsDevices)
        {
            // Load resets the native core: on an attached engine that is the host-owned graph's core.
            throw new InvalidOperationException("An attached engine never loads an image; its RAM arrives by copy-sync.");
        }

        // Mirrors RecompilerInterpreterExecutor: initial memory first (translated
        // to physical), then the program words so the code image wins any overlap.
        _core.Reset();
        _gpuDevice.Reset();
        _gpuDevice.ResetFrameEvidence();
        _cdRomDevice.Reset();
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
        _scheduler = new DeviceScheduler(
            _core, _interruptControllerAdapter, _gpuAdapter, _cdRomDevice, _cdRomDmaTransfer);
        _inInterruptHandler = false;
        _rfePending = false;
        _handlerEpc = 0;
        _blockingCallWait.Reset();
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
            _blockingCallWait.Reset();
        }

        return RunLoop(segmentRequest.Budget, returnPcs: null, out _, out _);
    }

    /// <summary>
    /// Runs one mixed-execution fallback segment on this attached engine's graph core (Issue #693): seeds the CPU
    /// from <paramref name="entry"/> as a fresh dispatch, steps the interpreter until it is about to execute a PC in
    /// <paramref name="returnPcs"/> at a clean boundary (or stops/exhausts), and reports the CPU state to write back.
    /// Every outcome other than <see cref="FallbackSegmentStatus.Returned"/> is a fail-closed stop for the caller.
    /// </summary>
    /// <param name="entry">The artifact's CPU state at the transfer.</param>
    /// <param name="returnPcs">The PCs the artifact has a compiled block for.</param>
    /// <param name="instructionBudget">The most interpreter steps this segment may take; positive.</param>
    /// <exception cref="InvalidOperationException">The engine was not created by <see cref="Attach"/>.</exception>
    /// <exception cref="ArgumentException">The state is malformed or the budget is zero.</exception>
    public FallbackSegmentOutcome RunFallbackSegment(
        FallbackCpuState entry, IReadOnlySet<uint> returnPcs, uint instructionBudget)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(returnPcs);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ownsDevices)
        {
            throw new InvalidOperationException("RunFallbackSegment requires an engine created by Attach.");
        }

        if (entry.Gpr.Count != TitleExecutionRequest.GprCount || instructionBudget == 0)
        {
            throw new ArgumentException("A fallback entry carries 32 GPRs and a positive instruction budget.");
        }

        for (var i = 0; i < TitleExecutionRequest.GprCount; i++)
        {
            _core.SetGpr(i, entry.Gpr[i]);
        }

        _core.Hi = entry.Hi;
        _core.Lo = entry.Lo;
        _core.SetCop0(Cop0Status, entry.Sr);
        _core.SetCop0(Cop0Cause, entry.Cause);
        _core.SetCop0(Cop0Epc, entry.Epc);
        _core.Pc = entry.Pc; // a fresh dispatch: flushes the native pipeline exactly as a jump must
        _inInterruptHandler = false;
        _rfePending = false;
        _handlerEpc = 0;
        _blockingCallWait.Reset();
        _resumable = false;

        var result = RunLoop(instructionBudget, returnPcs, out var returned, out var retired);
        var snapshot = result.Snapshot!;
        var state = new FallbackCpuState(
            snapshot.Gpr.ToArray(), snapshot.HI, snapshot.LO, snapshot.PC,
            _core.GetCop0(Cop0Status), _core.GetCop0(Cop0Cause), _core.GetCop0(Cop0Epc));

        if (returned)
        {
            return new FallbackSegmentOutcome(FallbackSegmentStatus.Returned, state, retired, null, null);
        }

        switch (snapshot.Termination)
        {
            case RecompilerIrTerminationReason.ExecutionBudgetExceeded:
                // Still inside the program (or a guest handler) with no clean block entry reached.
                return new FallbackSegmentOutcome(
                    FallbackSegmentStatus.BudgetExhausted, state, retired,
                    MixedFallbackDiagnostics.SegmentBudgetExhausted,
                    $"A fallback segment retired its {instructionBudget}-instruction budget without reaching a compiled block entry " +
                    $"at a clean boundary; it stopped at pc 0x{snapshot.PC:X8}.");

            case RecompilerIrTerminationReason.UnresolvedIndirectFlow when result.DiagnosticCode is not null:
                // A BIOS/kernel boundary the Runtime says why (for example an unregistered vector): the same stop the
                // artifact path would report for it.
                return new FallbackSegmentOutcome(
                    FallbackSegmentStatus.Stopped, state, retired, result.DiagnosticCode, result.DiagnosticMessage);

            case RecompilerIrTerminationReason.Exception:
                return new FallbackSegmentOutcome(
                    FallbackSegmentStatus.Stopped, state, retired,
                    MixedFallbackDiagnostics.ExceptionUnsupported,
                    $"The fallback raised an exception the interpreter loop does not service at pc 0x{snapshot.PC:X8} " +
                    $"(Excode 0x{_core.ExceptionCode:X2}); mixed execution does not continue past it.");

            default:
                return new FallbackSegmentOutcome(
                    FallbackSegmentStatus.Stopped, state, retired,
                    MixedFallbackDiagnostics.UnsupportedState,
                    $"The fallback left the program image at pc 0x{snapshot.PC:X8} (termination {snapshot.Termination}); " +
                    "mixed execution only continues inside the executable text image.");
        }
    }

    /// <summary>
    /// The step loop shared by <see cref="RunSegment"/> and <see cref="RunFallbackSegment"/> (Issue #693), so a
    /// fallback executes with exactly the interpreter's semantics (BIOS vectors, SYSCALL service, hardware INT and
    /// the kernel exception handler, device time) and never a second copy of them. With
    /// <paramref name="returnPcs"/> it additionally stops, <i>before</i> executing, at the first PC (after at least
    /// one step) that is a member of the set while the CPU is at an architecturally clean boundary.
    /// </summary>
    private RecompilerExecutionResult RunLoop(
        uint budget, IReadOnlySet<uint>? returnPcs, out bool returned, out ulong retiredInstructions)
    {
        returned = false;
        retiredInstructions = 0;
        var biosRuntime = _biosRuntimeFactory?.Invoke(
            new GuestMemoryReader(_bus.Read8),
            new GuestMemoryWriter(_bus.Write8));
        (biosRuntime as IDeviceBiosRuntime)?.AttachDevices(_devices);

        var termination = RecompilerIrTerminationReason.Success;
        string? diagnosticCode = null;
        string? diagnosticMessage = null;

        for (uint step = 0; step < budget; step++)
        {
            // Clean-boundary return (Issue #693). The PC must be an entry the artifact compiled, no guest interrupt
            // handler may be running (its out-of-image permission is this engine's alone), and the native CPU must
            // report no pending branch delay slot and no uncommitted load: the artifact only ever starts a block at a
            // fused-unit boundary, where neither exists. The CPU's own state decides; nothing is inferred from the
            // previous instruction.
            if (returnPcs is not null && step > 0 && !_inInterruptHandler
                && returnPcs.Contains(_core.Pc) && _core.IsPipelineClean)
            {
                returned = true;
                break;
            }

            // A vector dispatch costs a step from the same budget that bounds
            // ordinary instructions, exactly like the interpreter executor.
            var interruptAtVector = false;
            if (biosRuntime is not null && BiosJumpTables.TryResolveVectorFamily(_core.Pc, out var family))
            {
                var outcome = BiosVectorDispatch.Dispatch(biosRuntime, family, ReadGpr(), _blockingCallWait);
                if (!outcome.ContinueExecution)
                {
                    diagnosticCode = outcome.DiagnosticCode;
                    diagnosticMessage = outcome.DiagnosticMessage;
                    termination = RecompilerIrTerminationReason.UnresolvedIndirectFlow;
                    break;
                }

                // Issue #717: a blocking call that has not completed. The guest stays at the vector, device time
                // passes, and an interrupt the CPU would take is taken right here by the Step below — it preempts
                // the fetch, so nothing at the vector executes — with EPC = the vector, so the handler's return
                // re-enters the call. Otherwise the next iteration simply polls again.
                if (outcome.IsPending)
                {
                    if (BiosBlockingCallWait.RefuseSoftwareInterrupt(_core.GetCop0(Cop0Status), _core.GetCop0(Cop0Cause)) is { } refusal)
                    {
                        diagnosticCode = refusal.Code;
                        diagnosticMessage = refusal.Message;
                        termination = RecompilerIrTerminationReason.UnresolvedIndirectFlow;
                        break;
                    }

                    _scheduler!.Advance(BiosBlockingCallWait.PollCycles);
                    if (!CpuTakesInterruptNow())
                    {
                        continue;
                    }

                    interruptAtVector = true;
                }
                else
                {
                    if (outcome.ReturnValue is uint returnValue)
                    {
                        _core.SetGpr((int)R3000aRegister.V0, returnValue);
                    }

                    // Issue #664: a service that replaced the CPU state owns the whole
                    // post-dispatch machine — its register file already fixes $v0, and
                    // its NextPc is where the hardware resumes (the saved EPC for
                    // B0:17), not this call's $ra. Apply it through the one shared
                    // implementation every execution form uses.
                    if (outcome.CpuState is { } cpuState)
                    {
                        cpuState.ApplyTo(_core);
                        continue;
                    }

                    // A translatable patched target is jumped to verbatim — the core
                    // fetches from any translatable address.
                    _core.Pc = outcome.NextPc;
                    continue;
                }
            }

            if (!interruptAtVector && !PcWithinProgram(_core.Pc) && !_inInterruptHandler)
            {
                break;
            }

            // Step() reports only a native-call failure; a guest exception leaves
            // it 0 and moves the PC to the exception vector. Without the flag
            // (Issue #377) a faulting segment left the program bounds on the next
            // iteration and reported Success, which the orchestrator hands to the
            // handoff — a GTE/CpU fault could be classified Completed.
            _trace.Record(_core.Pc, FetchWordForTrace(_core.Pc));
            FetchObserver?.Invoke(_core.Pc);
            if (_core.Step() != 0)
            {
                termination = RecompilerIrTerminationReason.Exception;
                break;
            }

            if (_core.ExceptionRaised)
            {
                // Issue #663: with a Runtime attached, a SYSCALL is a kernel
                // service, not a guest failure. The CPU has already done the
                // exception entry (EPC, CAUSE, SR push); the shared kernel contract
                // says what the handler leaves in SR, and the CPU then performs the
                // RFE pop and the return. A delay-slot SYSCALL (EPC is the branch)
                // is not modelled and keeps ending the segment.
                if (biosRuntime is not null &&
                    _core.ExceptionCode == BiosKernelSyscallDispatch.SyscallExcode &&
                    !_core.ExceptionInDelaySlot)
                {
                    var syscall = BiosKernelSyscallDispatch.Dispatch(
                        _core.GetGpr((int)R3000aRegister.A0), _core.GetCop0(Cop0Status));
                    if (!syscall.Handled)
                    {
                        diagnosticCode = syscall.DiagnosticCode;
                        diagnosticMessage = syscall.DiagnosticMessage;
                        termination = RecompilerIrTerminationReason.UnresolvedIndirectFlow;
                        break;
                    }

                    _core.SetCop0(Cop0Status, syscall.SrAtReturn);
                    if (syscall.V0 is uint v0)
                    {
                        _core.SetGpr((int)R3000aRegister.V0, v0);
                    }

                    _core.PopExceptionSrStack();
                    _core.Pc = unchecked(_core.ExceptionFaultPc + 4u);
                    _scheduler!.Advance(CyclesPerInstruction);
                    continue;
                }

                // With a complete guest BIOS, *all* architectural exceptions (including
                // SYSCALL, BREAK and address faults) must reach firmware-owned vectors.
                // The legacy HLE slice still fails on non-IRQ exceptions exactly as before.
                if (!TookHardwareInterrupt() && !_allowRuntimeRamExecution)
                {
                    termination = RecompilerIrTerminationReason.Exception;
                    break;
                }

                // Issue #662: with a Runtime attached and nothing of the guest's own at the
                // general exception vector, the INT is the kernel exception handler's (C0:06):
                // the shared contract saves the context, walks the chains and completes it,
                // and the CPU applies the result. The guest's handler never runs.
                var guestMemory = new GuestMemoryReader(_bus.Read8);
                if (biosRuntime is not null &&
                    _core.Pc == BiosExceptionHandler.GeneralExceptionVector &&
                    BiosExceptionHandler.IsKernelVector(guestMemory))
                {
                    var kernel = BiosExceptionHandler.Handle(
                        guestMemory,
                        new GuestMemoryWriter(_bus.Write8),
                        _interruptControllerAdapter,
                        ReadGpr(),
                        new BiosExceptionContext(
                            _core.GetCop0(Cop0Epc), _core.GetCop0(Cop0Cause), _core.GetCop0(Cop0Status), _core.Hi, _core.Lo),
                        _exceptionChain);
                    if (!kernel.Handled)
                    {
                        diagnosticCode = kernel.DiagnosticCode;
                        diagnosticMessage = kernel.DiagnosticMessage;
                        termination = RecompilerIrTerminationReason.UnresolvedIndirectFlow;
                        break;
                    }

                    ApplyKernelOutcome(kernel);
                    continue;
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
            retiredInstructions++;
        }

        var stillRunning = PcWithinProgram(_core.Pc) || _inInterruptHandler ||
                           (biosRuntime is not null && BiosJumpTables.TryResolveVectorFamily(_core.Pc, out _));
        if (!returned && termination == RecompilerIrTerminationReason.Success && stillRunning)
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

    /// <summary>
    /// Captures the current production GPU display state from the exact
    /// <see cref="GpuDevice"/> / VRAM instance mutated by this engine's guest
    /// MMIO execution (Issue #575).
    /// </summary>
    /// <remarks>
    /// This is a presentation-agnostic evidence boundary. It does not wait for
    /// VBlank or define when a frame is "final"; it snapshots the current GPU
    /// state at the caller-selected execution boundary.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The engine has not been loaded yet.</exception>
    /// <exception cref="ObjectDisposedException">The engine has already been disposed.</exception>
    public FrameSnapshot CaptureFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_loaded)
        {
            throw new InvalidOperationException("Load must complete before a production frame can be captured.");
        }

        return _gpuDevice.CaptureFrame();
    }

    /// <summary>
    /// Captures a frame only when the current production execution epoch has
    /// performed meaningful GPU display/VRAM activity (Issue #575).
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="CaptureFrame"/>, this evidence-oriented boundary
    /// distinguishes untouched power-on VRAM from a legitimate all-black frame
    /// explicitly produced/enabled by the guest.
    /// </remarks>
    /// <returns>The current production frame, or <c>null</c> when no meaningful
    /// frame activity has occurred since the most recent <see cref="Load"/>.</returns>
    public FrameSnapshot? CaptureFrameEvidence()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_loaded)
        {
            throw new InvalidOperationException("Load must complete before production frame evidence can be captured.");
        }

        return _gpuDevice.HasFrameEvidence ? _gpuDevice.CaptureFrame() : null;
    }

    /// <summary>Releases the native core, memory bus and MMIO adapters this engine owns.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_ownsDevices)
        {
            _devices.Dispose();
        }
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private uint FetchWordForTrace(uint pc) =>
        Ps1AddressTranslation.TryTranslate(pc, out var physical) && (physical < 0x00800000u || physical is >= 0x1FC00000u and < 0x1FC80000u)
            ? _core.ReadMemory32(physical)
            : 0u;

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

    /// <summary>
    /// Applies a handled kernel exception to the CPU through the same shared CPU-state
    /// replacement a BIOS service uses (Issue #664), so the exception-completion path and
    /// B0:17 cannot restore different subsets of the machine.
    /// </summary>
    private void ApplyKernelOutcome(BiosExceptionHandlerOutcome kernel) => kernel.CpuState.ApplyTo(_core);

    private bool PcWithinProgram(uint pc) =>
        (pc >= _loadAddress && pc < _programEnd) ||
        (_allowRuntimeRamExecution && Ps1AddressTranslation.TryTranslate(pc, out var physical) &&
         (physical < 0x00800000u || physical is >= 0x1FC00000u and < 0x1FC80000u));

    /// <summary>
    /// Whether the exception the last step raised is an INT the hardware
    /// interrupt line (CAUSE.IP2, enabled by SR.IM2) caused. SYSCALL, BREAK,
    /// faults and software interrupts (CAUSE.IP0/IP1) are not, and still end the
    /// segment. Reads the state the CPU left; decides nothing the CPU owns.
    /// </summary>
    private bool TookHardwareInterrupt() =>
        _core.ExceptionCode == InterruptExcode &&
        (_core.GetCop0(Cop0Cause) & _core.GetCop0(Cop0Status) & HardwareInterruptBit) != 0;

    /// <summary>
    /// Whether the next <see cref="PSXCoreWrapper.Step"/> would take an INT instead of fetching (Issue #717): the
    /// native CPU's own test (SR.IEc and CAUSE.IP &amp; SR.IM, with IP2 the controller line it samples), read
    /// without stepping, so a blocking call's wait never executes whatever lies at the vector. A pending enabled software
    /// interrupt (IP0/IP1) has already stopped the wait (<see cref="BiosBlockingCallWait.RefuseSoftwareInterrupt"/>), so
    /// what remains is IP2: the same condition the artifact's INT boundary accepts. The native check's
    /// <c>!branch_pending_</c> is left out because it always holds here: the vector is reached after a call's delay slot
    /// or by a full PC re-seed (a kernel handler's return), never inside a branch + delay-slot pair.
    /// </summary>
    private bool CpuTakesInterruptNow()
    {
        var sr = _core.GetCop0(Cop0Status);
        var ip = (_core.GetCop0(Cop0Cause) & ~HardwareInterruptBit) | (_core.GetInterruptPending() ? HardwareInterruptBit : 0u);
        return (sr & 0x1u) != 0 && (ip & sr & 0xFF00u) != 0;
    }

    private static uint TranslateAddress(uint virtualAddress)
    {
        if (!Ps1AddressTranslation.TryTranslate(virtualAddress, out var physical))
        {
            throw new ArgumentOutOfRangeException(nameof(virtualAddress), "Execution must start in KUSEG/KSEG0/KSEG1.");
        }
        return physical;
    }
}