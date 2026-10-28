using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.MemoryCard;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Core.Runtime.CdRom;
using PSXRecomp.Infrastructure.Standalone;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// Production <see cref="IRecompiledExecutionEngine"/> that builds a runnable
/// native artifact through <see cref="IGeneratedHostBuildService"/> (Issue #458)
/// and launches it as a real process (Issue #459) — the non-test counterpart of
/// <c>PSXRecomp.Tests.Execution.HostTitleExecutionEngine</c>. When constructed
/// with a <c>biosRuntimeFactory</c>, an unresolved control transfer is relayed
/// to a real <see cref="IBiosRuntime"/> over the artifact's host-transfer
/// protocol (<see cref="RecompiledArtifactCodeGen"/>) instead of stopping the
/// run — the shared Runtime/BIOS HLE contract, reused rather than reimplemented
/// (ADR-014).
/// </summary>
/// <remarks>
/// This milestone's engine runs the whole guest program in one artifact launch:
/// it does not carry guest RAM across repeated <see cref="RunSegment"/> calls
/// (unlike the differential harness's per-segment full-RAM-dump technique),
/// so it supports exactly one <see cref="RunSegment"/> call per instance. A
/// caller drives it through <see cref="ExecutionOrchestrator"/> with
/// <c>TitleExecutionRequest.OuterBudget</c> of 1; multi-segment continuity
/// across repeated launches is deliberately out of scope for Issue #459 (see
/// its non-goals) and is follow-up work for the CLI/E2E issues that consume
/// this engine.
/// <para>
/// Within that single launch, the artifact always runs from the state
/// <see cref="RunSegment"/> is handed, never from the state <see cref="Load"/>
/// was called with: the input file is written from
/// <see cref="TitleExecutionSegmentRequest"/> immediately before each process
/// launch, while <see cref="TitleExecutionRequest.InitialMemory"/> — the one
/// input a segment request carries no field for — is retained from
/// <see cref="Load"/> and re-applied to every rewrite.
/// </para>
/// </remarks>
[Infrastructure]
public sealed class RecompiledHostExecutionEngine : IRecompiledExecutionEngine, ICancellableExecutionEngine
{
    public const string EngineName = "recompiled-host-artifact";

    /// <summary>
    /// How a prebuilt artifact (see <see cref="ForPrebuiltArtifact"/>) is launched
    /// by the runtime host. <see cref="HostTransfer"/> relaunches the artifact with
    /// <c>--host-transfer</c> and pumps the shared host-transfer bridge;
    /// <see cref="RuntimeHost"/> relaunches it with <c>--runtime-host</c> (no launcher
    /// re-delegation, no BIOS HLE Runtime: native firmware / openBIOS). Guest firmware still
    /// gets the device-MMIO bridge, so its launch also carries <c>--host-transfer</c>.
    /// </summary>
    public enum PrebuiltArtifactLaunchMode
    {
        HostTransfer,
        RuntimeHost,
    }

    /// <summary>Mixed-execution evidence of the last run, or null when mixed execution was not enabled (Issue #693).</summary>
    public MixedFallbackEvidence? FallbackEvidence { get; private set; }

    /// <summary>Wall-clock costs of the last run's mixed execution; measurement only (Issue #693).</summary>
    public MixedFallbackTimings? FallbackTimings { get; private set; }

    /// <summary>
    /// The fallback segment that was in flight when the last run ended, or null (Issue #806). Unlike <see cref="FallbackEvidence"/>
    /// it is read even when the host-transfer pump did not end (a stuck segment), because it carries scalars only.
    /// </summary>
    public FallbackSegmentProgress? FallbackInFlight { get; private set; }

    /// <summary>
    /// The return classification of the last run (Issue #806): counts only, read even when the host-transfer pump did not
    /// end, like <see cref="FallbackInFlight"/>. Null without mixed execution.
    /// </summary>
    public FallbackReturnStats? FallbackReturns { get; private set; }

    /// <summary>
    /// Guest instructions the last run's compiled blocks retired, as the artifact reported them over the host protocol
    /// (Issue #732); null without the protocol. Never includes fallback instructions (<see cref="FallbackEvidence"/>).
    /// Live while the run is in progress (for example from <see cref="FallbackFetchObserver"/>).
    /// </summary>
    public ulong? NativeRetiredInstructions => _bridge?.NativeRetiredInstructions;

    /// <summary>
    /// Synchronous host round trips the last run's native code made (Issue #732, measurement only): guest MMIO accesses
    /// relayed to the device graph and guest-time reports. Null without the protocol.
    /// </summary>
    /// <summary>The opt-in pipe-protocol profile of the last run (Issue #768), or null when not enabled.</summary>
    public Standalone.IpcProfile? IpcProfile => _bridge?.Profile;

    public (ulong MmioAccesses, ulong TimeReports)? HostRoundTrips =>
        _bridge is null ? null : (_bridge.MmioAccesses, _bridge.TimeReports);

    /// <summary>
    /// The last control transfer the last run's host bridge declined to claim, or null without the bridge (Issue #763).
    /// </summary>
    public FallbackDeclinedTransfer? LastDeclinedTransfer => _bridge?.LastDeclinedTransfer;

    /// <summary>
    /// The display the last run's guest left in the shared GPU/VRAM state as BGRA32, or null when the guest drew nothing or
    /// there is no bridge (Issue #762). It is read when the run's pump has ended and before the run's device graph is
    /// disposed: the GPU's VRAM is pinned native memory that does not outlive that graph.
    /// </summary>
    public (byte[] Bgra, int Width, int Height)? LastFrameBgra32 { get; private set; }

    /// <summary>The guest MMIO access the last run's host bridge relayed most recently, or null (Issue #763, measurement only).</summary>
    public HostMmioAccess? LastMmioAccess => _bridge?.LastMmioAccess;

    private HostTransferBridge? _bridge;

    /// <summary>Reported when control reaches the general exception vector with no generated code there (Issue #680).</summary>
    public const string ExceptionVectorUnhandledDiagnosticCode = "ARTIFACT_EXCEPTION_VECTOR_UNHANDLED";

    /// <summary>Reported when the runnable artifact exceeds the run timeout (Issue #763 diagnostics).</summary>
    public const string TimeoutDiagnosticCode = "ARTIFACT_TIMEOUT";

    /// <summary>
    /// Marks a loaded-code report as executed only when the run reached a completed end: <c>Completed</c>,
    /// <c>Returned</c> or <c>RuntimeHandoff</c>. Every other end (a failure, a timeout, an exhausted budget, an
    /// unsupported transfer) leaves the report incomplete (Issue #786 review, allowlist of completed ends).
    /// </summary>
    public static void MarkLoadedCodeReportFinished(LoadedCodeMismatchDiagnostics? report, TitleExecutionResult result)
    {
        report?.MarkExecuted(result.State is TitleExecutionState.Completed or TitleExecutionState.Returned or TitleExecutionState.RuntimeHandoff);
    }

    private const int RunTimeoutMs = 30000;
    // Also the drain cap of RuntimeHostBuildService (RuntimeHostBuildService.PipeDrainBudgetMs refers to this value).
    internal const int PumpDrainTimeoutMs = 5000;

    private readonly Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime>? _biosRuntimeFactory;
    private readonly Action<PsxDeviceGraph>? _configureDevices;
    private readonly MemoryCardSlotConfiguration? _memoryCardSlots;
    private readonly BiosExceptionChain? _exceptionChain;
    private readonly IReadOnlySet<uint> _blockEntryPcs;
    private readonly MixedFallbackOptions? _mixedFallback;
    private string _binaryPath = string.Empty;
    private readonly string _inputPath;
    private readonly string _imagePath;
    private readonly uint _imageLoadAddress;
    private readonly IReadOnlyList<uint> _imageWords;
    private readonly bool _guestFirmware;
    private readonly ICdSectorSource? _disc;
    private readonly int _runTimeoutMs;
    private readonly LoadedCodeTable _loadedCode;
    private readonly RecompilerIrProgram _program;
    private readonly string _outputDirectory;
    private readonly IGeneratedHostBuildService _buildService;
    private readonly bool _embedImage;
    private readonly bool _prebuilt;
    private string _prebuiltArtifactPath = string.Empty;
    private PrebuiltArtifactLaunchMode _launchMode;
    private LiveFrameSlot? _liveFrames;
    private string? _consoleOutput;

    /// <summary>
    /// Console output captured from a prebuilt runtime-host run (the guest's C
    /// stdout up to the engine's RSNAPSHOT diagnostic block). The runtime host
    /// forwards this to its own stdout so boot/TTY messages are visible.
    /// </summary>
    public string? ConsoleOutput => _consoleOutput;

    /// <summary>
    /// Called with the mixed-execution interpreter and the PC of every instruction it is about to fetch (diagnostic
    /// observation only, Issue #732). Native blocks are not observed: this sees exactly the fallback instructions.
    /// </summary>
    public Action<InterpreterTitleExecutionEngine, uint>? FallbackFetchObserver { get; set; }

    /// <summary>
    /// Builds the artifact with per-entry loaded-code guard counters, written to the file named by PSX_GUARD_STATS_FILE
    /// (measurement only, Issue #773). Opt-in through <see cref="GuardStatsScope"/> or PSXRECOMP_NATIVE_GUARD_STATS (the
    /// artifact is generated at construction, or at the first RunSegment of an embedded image, so the scope must be in effect then); off, the
    /// generated text is unchanged.
    /// </summary>
    private static bool GuardStats => GuardStatsScope.Enabled;

    /// <summary>Called once per artifact-to-interpreter handoff after its segment ran (measurement only, Issue #732).</summary>
    public Action<MixedFallbackTransition>? FallbackTransitionObserver { get; set; }

    /// <summary>
    /// Opt-in record of RAM-placed code the artifact refused at a handoff (Issue #773). Set before the run; null (the default)
    /// keeps the run exactly as it was. Only read on the handoff path, so it does not change what executes.
    /// </summary>
    public LoadedCodeMismatchDiagnostics? LoadedCodeMismatchDiagnostics { get; set; }

    private IReadOnlyList<RecompilerInitialMemoryItem> _initialMemory =
        Array.Empty<RecompilerInitialMemoryItem>();

    private bool _loaded;
    private bool _ran;

    /// <summary>
    /// The stop request for this engine's run (Issue #820). Cancelling it kills the child process tree; the run then
    /// returns on its own, classified as <see cref="RecompilerExecutionStatus.Cancelled"/>.
    /// </summary>
    private readonly CancellationTokenSource _stopRequested = new();

    /// <summary>Diagnostic code of a segment stopped by <see cref="RequestCancel"/> (Issue #820).</summary>
    public const string CancelledDiagnosticCode = "ARTIFACT_CANCELLED";

    /// <summary>Diagnostic code of a stop the OS refused, or whose child did not confirm its exit (Issue #820).</summary>
    public const string StopRefusedDiagnosticCode = "ARTIFACT_STOP_REFUSED";

    /// <summary>
    /// Diagnostic code of a run whose stdout drain ran out of time: the child's output was not read, so its result cannot be read either
    /// (Issue #827 review). It is reported as a failure, with this reason, and never as a silent failure.
    /// </summary>
    public const string OutputDrainTimeoutDiagnosticCode = "ARTIFACT_OUTPUT_DRAIN_TIMEOUT";

    /// <summary>
    /// Descendants that a stop found still running after its child had already ended on its own (Issue #827 review). A diagnostic only: the
    /// child's result is not changed by it. Set by the last run of this engine.
    /// </summary>
    internal StopDiagnostics? LastStopDiagnostics { get; private set; }

    /// <summary>
    /// The shutdown of the last run, separate from its result (Issue #827, round 10). <see cref="ShutdownOutcome.NotStopped"/> means no stop was
    /// processed for the run: it says nothing about residual children, which the natural end does not check (that is Issue #829).
    /// <see cref="ShutdownOutcome.Clean"/> means a stop was processed and left no refusal and no residual process.
    /// <see cref="ShutdownOutcome.Residual"/> means a stop left a refusal or a residual process (see <see cref="LastStopDiagnostics"/>). A residual
    /// shutdown is not a success, whatever the run's result is.
    /// </summary>
    internal ShutdownOutcome ShutdownStatus => LastStopDiagnostics is not { } diagnostics
        ? ShutdownOutcome.NotStopped
        : diagnostics.Clean ? ShutdownOutcome.Clean : ShutdownOutcome.Residual;

    /// <summary>How many times the last run read a frame from the bridge (Issue #827 review): zero after a stop is the check.</summary>
    internal int FrameCaptureCalls { get; private set; }

    /// <summary>
    /// Test seam (Issue #827 review): runs just before the frame decision of a segment, after the stop record is sealed. A test requests a
    /// stop here to place it in that window. Null in production.
    /// </summary>
    internal Action? BeforeFrameCaptureForTest { get; set; }

    /// <summary>
    /// Test seam (Issue #827): the stdout drain budget of a child that ended on its own, in milliseconds. Null uses the run's timeout.
    /// </summary>
    internal int? OutputBudgetMsForTest { get; set; }

    /// <summary>
    /// Test seam (Issue #827): replaces the descendant listing of a stop. A real child can then be reported with a survivor, which
    /// drives the refusal path. Null in production.
    /// </summary>
    internal Func<Process, IReadOnlyList<Process>?>? StopDescendantsProbe { get; set; }

    /// <summary>
    /// Test seam (Issue #827): observes each wait of a segment (see <see cref="StopRecord.ObserveForTest"/>). It can force a
    /// reported state, which lets a test place a stop at an exact point of the wait. Null in production.
    /// </summary>
    internal Func<StopRecord, Process, string, bool, bool>? WaitObservedForTest { get; set; }

    /// <summary>
    /// Test seam (Issue #827): replaces the kill of the child, so a test injects the OS refusal on the stop and timeout paths. Null
    /// in production.
    /// </summary>
    internal Action<Process>? KillOverrideForTest { get; set; }

    /// <summary>
    /// The result of a run that was asked to stop (Issue #827, decided in review round 10). The run's own result always stands unless the stop
    /// came first and was confirmed: a child that ended before the stop reached it keeps its result (completed, or the failure it had), and a
    /// stop that was refused or left a residual keeps the classification too. Each refusal and residual goes to the stop's diagnostics
    /// (<see cref="StopDiagnostics"/>), not into the result. Only a confirmed stop of a live child is <c>Cancelled</c>.
    /// </summary>
    internal static RecompilerExecutionResult ApplyStopOutcome(RecompilerExecutionResult classified, StopRecord stop)
    {
        if (stop.StoppedAt is null || stop.ChildEndedBeforeStop)
        {
            return classified;
        }

        var (confirmed, _) = stop.Outcome;
        return confirmed
            ? RecompilerExecutionResult.Failed(RecompilerExecutionStatus.Cancelled, CancelledDiagnosticCode, "The run was cancelled before the guest finished.")
            : classified;
    }

    /// <summary>
    /// Asks the run to stop (Issue #820). It is idempotent. It waits only for a frame read already in progress, which is bounded to one
    /// read (see FrameReadGate), and then returns; the child process tree is killed on a thread-pool thread, so a caller on the UI thread
    /// never waits for the process to exit.
    /// </summary>
    public void RequestCancel()
    {
        // The request flag is set through the frame gate (owner requirement). A read in progress finishes first, and no read starts after
        // the flag is set. The wait is bounded by one frame read (see FrameReadGate). The stop is then applied on the pool.
        _frameReads.RequestStop();
        ThreadPool.QueueUserWorkItem(_ => CancelStopSource());
    }

    /// <summary>
    /// Every read of this engine's GPU/VRAM goes through this gate (the run-end frame and the live VBlank frames). It holds the stop request flag
    /// and the external cancellation of the run.
    /// </summary>
    private readonly FrameReadGate _frameReads = new();

    /// <summary>Attaches the run's external cancellation to the gate (see <see cref="ICancellableExecutionEngine.AttachCancellation"/>).</summary>
    public IDisposable AttachCancellation(CancellationToken cancellationToken)
    {
        _frameReads.SetExternalStop(cancellationToken);
        return new ExternalStopScope(_frameReads);
    }

    /// <summary>
    /// The refusal for an unimplemented GTE command. The low 25 bits of the COP2 instruction are part of a guest word, so the
    /// message names only the fact; the bits are withheld from every public artifact (PR #839, decision A).
    /// </summary>
    internal static string GteRefusalMessage(uint commandBits) => "GTE command is not implemented.";

    /// <summary>Ends an attachment: the gate stops watching the run's external token.</summary>
    private sealed class ExternalStopScope(FrameReadGate gate) : IDisposable
    {
        public void Dispose() => gate.SetExternalStop(CancellationToken.None);
    }

    /// <summary>
    /// The live VBlank read guard of this engine, as the bridge builds it (no stop record in the predicate, so the flag and the external token alone
    /// decide). Test seam (review round 12): a guard call after a stop must read nothing.
    /// </summary>
    internal Func<Action, bool> LiveReadGuardForTest => read => _frameReads.TryRead(() => false, read);

    /// <summary>Whether the engine's stop token has been cancelled (after any dispose, false). Test seam.</summary>
    internal bool StopIsRequestedForTest
    {
        get
        {
            try
            {
                return _stopRequested.IsCancellationRequested;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Test seam (Issue #827, review round 10): runs inside the frame gate, after the flag check and before the GPU read starts. A test starts
    /// a stop from here to show that the stop waits for the read. Null in production.
    /// </summary>
    internal Action? DuringFrameReadForTest { get; set; }

    /// <summary>Whether the stop request flag is set. For tests.</summary>
    internal bool StopRequestFlagForTest => _frameReads.StopRequested;

    /// <summary>
    /// The reads of this engine's GPU/VRAM so far, and how many had been done when the stop was requested (Issue #827, owner requirement). After a
    /// stop, the second number must not change: no read runs between the stop and the end of the pump. For tests.
    /// </summary>
    internal (int Reads, int ReadsAtStop) FrameReadStats => _frameReads.Stats;

    private void CancelStopSource()
    {
        try
        {
            _stopRequested.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The engine was disposed after its run: there is nothing left to stop.
        }
        catch (AggregateException exception)
        {
            // A stop callback failed. The child kill already logs its own failures; this keeps a pool thread from dying.
            Trace.WriteLine($"Stop request failed: {exception}");
        }
    }

    /// <summary>
    /// Generates, builds, and stages the artifact for <paramref name="program"/>.
    /// Building happens here (not in <see cref="Load"/>) so a build failure — the
    /// only way this engine cannot be prepared — surfaces as soon as the engine
    /// exists, matching <see cref="IRecompiledExecutionEngine.Load"/>'s documented
    /// failure mode without deferring it past construction.
    /// </summary>
    /// <param name="program">The lowered Recompiler IR to build a runnable artifact from.</param>
    /// <param name="imageWords">The guest program image (the PS-X EXE text segment) the
    /// interpreter writes into guest RAM before execution; the artifact loads the same
    /// words at <paramref name="imageLoadAddress"/> before its first guest instruction
    /// (Issue #637), so data loads from the text region see the same bytes.</param>
    /// <param name="imageLoadAddress">The guest address <paramref name="imageWords"/> is loaded at.</param>
    /// <param name="buildService">The production build substrate (Issue #458).</param>
    /// <param name="outputDirectory">
    /// Where the artifact's source, object, and binary are written. The caller owns
    /// this directory's lifecycle.
    /// </param>
    /// <param name="biosRuntimeFactory">
    /// Builds the Runtime the artifact's unresolved control transfers are relayed
    /// to, over the reader/writer this engine exposes for the artifact's own guest
    /// memory. Null runs the artifact independently, with no Runtime attached: any
    /// unresolved transfer then stops the run immediately, a legitimate classified
    /// boundary rather than a failure.
    /// </param>
    /// <param name="configureDevices">
    /// Seeds the Runtime device graph once the artifact's handshake has built it, before the guest
    /// runs: the seam a disc/sector source attaches to (a device's own input, never guest RAM,
    /// which stays the artifact's). Null leaves the devices as constructed.
    /// </param>
    /// <param name="exceptionChain">
    /// The kernel exception handler's priority-chain walk (Issue #662); null is
    /// <see cref="BiosExceptionHandler.DefaultChain"/>. The seam the modelled kernel handlers plug into.
    /// </param>
    /// <param name="mixedFallback">
    /// Opt-in mixed execution (Issue #693): when set, an unresolved in-image indirect transfer is handed to the interpreter
    /// and control returns to the artifact at a clean compiled block entry. Null (the default) keeps the pre-existing stop.
    /// </param>
    /// <param name="memoryCardSlots">Which card is in each slot (Issue #715); null is <see cref="MemoryCardSlotConfiguration.Empty"/>.</param>
    /// <param name="guestFirmware">
    /// Firmware mode (Issue #732): the image is a guest firmware such as OpenBIOS (loaded in the BIOS ROM window when its
    /// load address is there) that owns its kernel, vectors and exceptions. The artifact runs with
    /// <see cref="RecompiledArtifactCodeGen.GuestExceptionsFlag"/>, no BIOS HLE is attached (pass a null
    /// <paramref name="biosRuntimeFactory"/>), and every PC without a block — kernel code the firmware copied to RAM, its
    /// vectors, the shell, a loaded executable — goes to the <paramref name="mixedFallback"/> interpreter, which is counted
    /// as fallback, never as native.
    /// </param>
    /// <param name="runTimeout">The bound on one artifact run; null is 30 seconds.</param>
    /// <param name="disc">The disc in the Runtime CD-ROM drive (Issue #732); null is the legacy drive model.</param>
    /// <param name="loadedCode">
    /// The pre-generated (ahead-of-time) versions of code the guest places in RAM (Issue #732), built with
    /// <see cref="ReachableProgramBuilder.BuildLoadedImage"/> and linked into one table. A version runs only while guest
    /// RAM holds exactly the words it was compiled from — checked by the artifact before entering it and by the fallback
    /// interpreter before returning to it — and otherwise its PC runs in the <paramref name="mixedFallback"/>
    /// interpreter. Null: no RAM-placed code.
    /// </param>
    /// <param name="embedImage">
    /// When true, the artifact embeds the program image and this run's initial
    /// state (Phase 2, Issue #757): no input files are written and the artifact
    /// attaches the shared Runtime host bridge over the existing --host-transfer
    /// path. The embedded state is generated at the first RunSegment — the state
    /// THAT call names — so Load's "never frozen here" contract is preserved and
    /// build failures surface at that call. When false (the default) the artifact
    /// reads its input files exactly as before.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="program"/>,
    /// <paramref name="imageWords"/>, or <paramref name="buildService"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="imageWords"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">Code generation or the build failed.</exception>
    public RecompiledHostExecutionEngine(
        RecompilerIrProgram program,
        IReadOnlyList<uint> imageWords,
        uint imageLoadAddress,
        IGeneratedHostBuildService buildService,
        string outputDirectory,
        Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime>? biosRuntimeFactory = null,
        Action<PsxDeviceGraph>? configureDevices = null,
        BiosExceptionChain? exceptionChain = null,
        MixedFallbackOptions? mixedFallback = null,
        MemoryCardSlotConfiguration? memoryCardSlots = null,
        bool guestFirmware = false,
        TimeSpan? runTimeout = null,
        ICdSectorSource? disc = null,
        LoadedCodeTable? loadedCode = null,
        IReadOnlySet<uint>? blockEntryPcs = null,
        string? prebuiltArtifactPath = null,
        PrebuiltArtifactLaunchMode launchMode = PrebuiltArtifactLaunchMode.HostTransfer,
        bool embedImage = false,
        LiveFrameSlot? liveFrames = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(imageWords);

        // Prebuilt artifact: no build service or output directory needed; they are ignored.
        // Validate prebuilt-specific parameters first to allow null buildService/empty outputDirectory.
        var prebuilt = prebuiltArtifactPath is not null;
        if (prebuilt)
        {
            if (buildService is not null || !string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new InvalidOperationException(
                    "A prebuilt artifact is supplied directly; buildService and outputDirectory are ignored.");
            }
            // For prebuilt artifacts, we still need a non-empty program image.
            if (imageWords.Count == 0)
            {
                throw new ArgumentException("The artifact needs a non-empty program image.", nameof(imageWords));
            }
        }
        else
        {
            ArgumentNullException.ThrowIfNull(buildService);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
            if (imageWords.Count == 0)
            {
                // Mirrors InterpreterTitleExecutionEngine: no program image, nothing to run.
                throw new ArgumentException("The artifact needs a non-empty program image.", nameof(imageWords));
            }
        }

        if (guestFirmware && biosRuntimeFactory is not null)
        {
            throw new ArgumentException("A guest firmware owns its kernel: no BIOS HLE Runtime may be attached.", nameof(biosRuntimeFactory));
        }

        _guestFirmware = guestFirmware;
        _disc = disc;
        _runTimeoutMs = runTimeout is { } timeout ? (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue) : RunTimeoutMs;
        _biosRuntimeFactory = biosRuntimeFactory;
        _configureDevices = configureDevices;
        _memoryCardSlots = memoryCardSlots;
        if (mixedFallback is { IsValid: false })
        {
            throw new ArgumentException("Mixed fallback needs positive instruction and transition budgets.", nameof(mixedFallback));
        }

        _mixedFallback = mixedFallback;
        _exceptionChain = exceptionChain;
        _blockEntryPcs = blockEntryPcs ?? program.Blocks.Select(static block => block.EntryPc).ToHashSet();
        _inputPath = Path.Combine(outputDirectory, "artifact-input.txt");
        _imagePath = Path.Combine(outputDirectory, "artifact-image.bin");
        _imageLoadAddress = imageLoadAddress;
        _imageWords = imageWords;

        _loadedCode = loadedCode ?? LoadedCodeTable.Empty;
        _program = program;
        _outputDirectory = outputDirectory;
        _buildService = buildService;
        _embedImage = embedImage;
        _prebuilt = prebuiltArtifactPath is not null;
        _prebuiltArtifactPath = prebuiltArtifactPath ?? string.Empty;
        _launchMode = launchMode;
        _liveFrames = liveFrames;

        if (_prebuilt)
        {
            // A prebuilt artifact is supplied by the caller: no code generation,
            // no build, no input files. The artifact embeds its own initial state
            // and program image, so RunSegment merely launches it with the chosen
            // launch mode and pumps its host-transfer bridge when requested.
            if (buildService is not null || !string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new InvalidOperationException(
                    "A prebuilt artifact is supplied directly; buildService and outputDirectory are ignored.");
            }
            _binaryPath = _prebuiltArtifactPath;
            return;
        }

        if (embedImage)
        {
            // The embedded-image artifact is generated at the first RunSegment:
            // the state it embeds is the state THAT call names (PC, GPRs, HI/LO,
            // budget), matching Load's "the architectural state a run starts from
            // is named by each RunSegment call, never frozen here" contract.
            // Build failures then surface at that call, not at construction.
            return;
        }

        var dispatch = RecompilerHostCodeGen.Generate(program, _loadedCode, traceUnits: UnitTraceScope.CurrentPath is not null, guardStats: GuardStats);
        if (!dispatch.Success)
        {
            throw new InvalidOperationException(dispatch.DiagnosticMessage ?? "Host dispatch code generation failed.");
        }

        var artifact = RecompiledArtifactCodeGen.Generate(dispatch);
        if (!artifact.Success)
        {
            throw new InvalidOperationException(artifact.DiagnosticMessage ?? "Artifact entrypoint code generation failed.");
        }

        var build = buildService.Build(new GeneratedHostBuildRequest(artifact.Source!, outputDirectory, "recompiled-artifact"));
        if (build.Status != GeneratedHostBuildStatus.Succeeded)
        {
            throw new InvalidOperationException(
                $"Artifact build failed ({build.Status})." +
                (string.IsNullOrEmpty(build.DiagnosticMessage) ? "" : "\n" + build.DiagnosticMessage));
        }

        _binaryPath = build.Artifact!.BinaryPath;
    }

    /// <summary>
    /// Wraps an existing native artifact built by the CLI's packaging flow
    /// (Issue #739 Phase 2 Step C, ADR-027 Option B). No code generation or
    /// build happens: the artifact's own embedded initial state and program
    /// image are used, and the engine becomes the orchestration/bridge layer
    /// — shared <see cref="ExecutionOrchestrator"/>, classification via
    /// <see cref="Core.Execution.RecompiledArtifactResult"/>, and mixed-execution
    /// fallback accounting. The caller owns the artifact's lifecycle (it is
    /// launched as a separate process from <see cref="RunSegment"/>).
    /// </summary>
    /// <param name="prebuiltArtifactPath">
    /// The prebuilt artifact to launch. It must exist and be runnable; the
    /// engine never rebuilds or regenerates it.
    /// </param>
    /// <param name="blockEntryPcs">
    /// Every compiled block entry PC the artifact has (from the manifest),
    /// so the host-transfer bridge can distinguish a patched transfer from an
    /// actual compiled block. Null block PCs mean no bridge is useful: the host
    /// must run in <see cref="PrebuiltArtifactLaunchMode.RuntimeHost"/> mode
    /// (no bridge at all).
    /// </param>
    /// <param name="guestFirmware">True when the image is a firmware image
    /// (for example OpenBIOS) that owns its kernel and vectors. A guest
    /// firmware never has a BIOS HLE Runtime attached.</param>
    /// <param name="biosRuntimeFactory">
    /// Builds the HLE Runtime the artifact is relayed to when a bridge is
    /// requested. Required for <see cref="PrebuiltArtifactLaunchMode.HostTransfer"/>;
    /// forbidden for guest firmware.
    /// </param>
    /// <param name="mixedFallback">
    /// Opt-in mixed execution: when set, an unresolved in-image indirect
    /// transfer runs on the interpreter fallback and returns to the artifact
    /// at a clean block entry, with fallback instructions counted. Null keeps
    /// the pre-existing stop. For guest firmware this must be null.
    /// </param>
    /// <param name="runTimeout">The bound on one artifact run; null is 30 seconds.</param>
    /// <param name="loadedCode">
    /// The pre-generated (ahead-of-time) versions of code the guest places in
    /// RAM; null: no RAM-placed code. For guest firmware this must be null.
    /// </param>
    /// <param name="launchMode">
    /// How the prebuilt artifact is launched: <c>HostTransfer</c> relaunches it
    /// with <c>--host-transfer</c> and pumps the shared host-transfer bridge
    /// (HLE games); <c>RuntimeHost</c> relaunches it with <c>--runtime-host</c>,
    /// without the BIOS HLE Runtime (native firmware / openBIOS); guest firmware still gets the
    /// device-MMIO bridge, so that launch also passes <c>--host-transfer</c>.
    /// </param>
    /// <param name="imageWords">
    /// The program image words for the host-transfer bridge / interpreter fallback
    /// (for a prebuilt artifact this is the same image the artifact embeds). Null
    /// keeps the default (empty); used by the bridge when it is attached.
    /// </param>
    /// <param name="imageLoadAddress">Guest address where <paramref name="imageWords"/> load; used by the bridge when attached.</param>
    /// <param name="disc">The disc in the CD-ROM drive (a guest firmware reads it through the shared device graph); null is an empty drive.</param>
    public static RecompiledHostExecutionEngine ForPrebuiltArtifact(
        string prebuiltArtifactPath,
        IReadOnlySet<uint> blockEntryPcs,
        bool guestFirmware = false,
        Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime>? biosRuntimeFactory = null,
        MixedFallbackOptions? mixedFallback = null,
        TimeSpan? runTimeout = null,
        LoadedCodeTable? loadedCode = null,
        IReadOnlyList<uint>? imageWords = null,
        uint? imageLoadAddress = null,
        PrebuiltArtifactLaunchMode launchMode = PrebuiltArtifactLaunchMode.HostTransfer,
        ICdSectorSource? disc = null,
        LiveFrameSlot? liveFrames = null)
    {
        if (string.IsNullOrWhiteSpace(prebuiltArtifactPath))
        {
            throw new ArgumentException("The prebuilt artifact path must be non-empty.", nameof(prebuiltArtifactPath));
        }

        if (!File.Exists(prebuiltArtifactPath))
        {
            throw new FileNotFoundException("The prebuilt artifact was not found.", prebuiltArtifactPath);
        }

        if (blockEntryPcs is null)
        {
            throw new ArgumentNullException(nameof(blockEntryPcs));
        }

        if (biosRuntimeFactory is not null && guestFirmware)
        {
            throw new ArgumentException(
                "A guest firmware owns its kernel: no BIOS HLE Runtime may be attached.", nameof(biosRuntimeFactory));
        }

        if (guestFirmware && biosRuntimeFactory is not null)
        {
            throw new ArgumentException(
                "A guest firmware owns its kernel: no BIOS HLE Runtime may be attached.", nameof(biosRuntimeFactory));
        }

        if (guestFirmware && mixedFallback is not null && launchMode == PrebuiltArtifactLaunchMode.HostTransfer)
        {
            // Guest firmware with HostTransfer launch mode and mixed fallback is not a supported combination:
            // the firmware owns its kernel and vectors, so HLE bridge + fallback doesn't apply.
            // For RuntimeHost launch mode (native firmware), mixed fallback is allowed for the
            // interpreter to handle RAM-resident code the firmware places.
            throw new ArgumentException(
                "Guest firmware with HostTransfer launch mode does not support mixed fallback.",
                nameof(mixedFallback));
        }

        return new RecompiledHostExecutionEngine(
            program: new RecompilerIrProgram([]),
            imageWords: imageWords ?? Array.Empty<uint>(),
            imageLoadAddress: imageLoadAddress ?? 0u,
            buildService: null!,
            outputDirectory: "",
            biosRuntimeFactory: biosRuntimeFactory,
            guestFirmware: guestFirmware,
            runTimeout: runTimeout,
            disc: disc,
            loadedCode: loadedCode,
            blockEntryPcs: blockEntryPcs,
            prebuiltArtifactPath: prebuiltArtifactPath,
            launchMode: launchMode,
            mixedFallback: mixedFallback,
            liveFrames: liveFrames);
    }

    public string Name => EngineName;

    public void Load(TitleExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.InitialMemory.Count > RecompiledArtifactCodeGen.MaxInitEntries)
        {
            // The artifact driver's own PSX_MAX_INIT bound (derived from
            // MaxInitEntries — see RecompiledArtifactCodeGen.Generate) would
            // otherwise reject the input file with an opaque process exit code;
            // fail with a clear message here, where the request is prepared.
            throw new InvalidOperationException(
                $"Initial memory has {request.InitialMemory.Count} entries, exceeding the artifact driver's " +
                $"{RecompiledArtifactCodeGen.MaxInitEntries}-entry limit.");
        }

        // Load retains only the initial-memory seed: it is the one input a
        // TitleExecutionSegmentRequest carries no field for, so it is stored here
        // and re-applied by RunSegment's file rewrite. The architectural state a
        // run actually starts from (PC, GPRs, HI/LO, budget) is named by each
        // RunSegment call, never frozen here.
        _initialMemory = request.InitialMemory;
        _loaded = true;
    }

    public RecompilerExecutionResult RunSegment(TitleExecutionSegmentRequest segmentRequest)
    {
        ArgumentNullException.ThrowIfNull(segmentRequest);

        // Each run starts with no frame and no stop diagnostics of the previous run (Issue #827, review round 11).
        LastFrameBgra32 = null;
        LastStopDiagnostics = null;
        if (!_loaded)
        {
            throw new InvalidOperationException("RunSegment called before Load.");
        }

        if (_ran)
        {
            // Honest, loud failure rather than a silent wrong answer: this engine
            // does not carry guest RAM across repeated launches (see the class
            // remarks), so a second call would restart from the first call's
            // initial memory, discarding whatever the guest wrote.
            throw new InvalidOperationException(
                "RecompiledHostExecutionEngine supports exactly one RunSegment call per instance " +
                "(Issue #459's minimal slice carries no guest RAM continuity across artifact launches).");
        }

        _ran = true;

        // Issue #820: a stop requested before the child starts ends the segment without launching anything.
        if (_stopRequested.IsCancellationRequested)
        {
            // A stop that began before the child started: there is no child to stop, so nothing is left behind. The diagnostics say so, which keeps
            // the shutdown status in line with the Cancelled result (review round 12).
            LastStopDiagnostics = new StopDiagnostics(0, ChildEndedBeforeStop: false, Confirmed: true, Refused: null, Residuals: []);
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.Cancelled, CancelledDiagnosticCode, "The run was cancelled before the artifact started.");
        }

        // The artifact must run from the state THIS call names — segmentRequest's
        // PC, GPRs, HI/LO and budget — not the state Load was handed, so the input
        // file is written here, immediately before launch. InitialMemory is the
        // sole input a TitleExecutionSegmentRequest carries no field for: it is
        // retained from Load and re-applied to this rewrite. The embedded-image
        // artifact (Phase 2, Issue #757) instead embeds exactly this state and
        // needs no input files. A prebuilt artifact (Issue #739 Step C) embeds its
        // own state and image and is never rebuilt: its initial state is whatever
        // the packaging flow compiled into it, so RunSegment never touches the
        // request's state fields for launch.
        if (_prebuilt)
        {
            // No input files are written for a prebuilt artifact: the artifact
            // embeds its own initial state and program image.
        }
        else if (_embedImage)
        {
            BuildEmbeddedArtifact(segmentRequest.Pc, segmentRequest.Gpr, segmentRequest.Hi, segmentRequest.Lo, _initialMemory, segmentRequest.Budget);
        }
        else
        {
            WriteInputFile(segmentRequest.Pc, segmentRequest.Gpr, segmentRequest.Hi, segmentRequest.Lo, _initialMemory, segmentRequest.Budget);
        }

        using var bridge = _biosRuntimeFactory is null && !_guestFirmware
            ? null
            : new HostTransferBridge(_biosRuntimeFactory, _blockEntryPcs, _configureDevices, _exceptionChain, _mixedFallback, _imageWords, _imageLoadAddress, _memoryCardSlots, _guestFirmware, FallbackFetchObserver, _disc, FallbackTransitionObserver, _loadedCode, LoadedCodeMismatchDiagnostics)
            {
                LiveFrames = _liveFrames,
            };
        _bridge = bridge;

        // Build the launch arguments. A prebuilt artifact in RuntimeHost mode gets
        // --runtime-host (suppresses the launcher's re-delegation to the runtime
        // host); a HostTransfer one needs no mode flag. Otherwise the ordinary
        // build path supplies the input files. Whenever a bridge exists the artifact
        // must be told to attach it (--host-transfer): attaching is the only thing
        // that enables its MMIO bridge, so guest firmware launched in RuntimeHost
        // mode would otherwise exit with the MMIO-unavailable code. With no bridge
        // none of the bridge flags are passed.
        var arguments = new List<string>(5);
        if (_prebuilt)
        {
            if (_launchMode == PrebuiltArtifactLaunchMode.RuntimeHost)
            {
                arguments.Add(RecompiledArtifactCodeGen.RuntimeHostModeFlag);
            }
        }
        else if (!_embedImage)
        {
            arguments.Add(_inputPath);
            arguments.Add(_imagePath);
        }

        if (bridge is not null)
        {
            arguments.Add(RecompiledArtifactCodeGen.HostTransferFlag);
            arguments.Add(RecompiledArtifactCodeGen.ExactDeviceTimeFlag);
        }

        if (_guestFirmware)
        {
            arguments.Add(RecompiledArtifactCodeGen.GuestExceptionsFlag);
        }

        // What a stop did to this child. It alone decides a Cancelled or refused result (see ApplyStopOutcome).
        var stopRecord = new StopRecord
        {
            DescendantsProbe = StopDescendantsProbe,
            ObserveForTest = WaitObservedForTest,
            KillForTest = KillOverrideForTest,
        };
        CancellationToken stopToken;
        try
        {
            stopToken = _stopRequested.Token;
        }
        catch (ObjectDisposedException)
        {
            // Disposed while this segment was being prepared: the run is stopped before any child starts (no child, nothing left behind).
            LastStopDiagnostics = new StopDiagnostics(0, ChildEndedBeforeStop: false, Confirmed: true, Refused: null, Residuals: []);
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.Cancelled, CancelledDiagnosticCode, "The engine was disposed before the artifact started.");
        }

        (int exit, string stdout, string stderr, bool hostProtocolFaulted, bool pumpEnded) run;
        // The bridge reads the GPU only through the frame gate, and only while no stop was requested: its live VBlank publisher uses both (Issue
        // #827, owner requirement). Set before the pump starts, so no VBlank can read without them.
        if (bridge is not null)
        {
            bridge.FrameReads = _frameReads;
            bridge.StopNow = () => stopToken.IsCancellationRequested || stopRecord.StoppedAt is not null;
        }

        bool timedOut;
        bool drainTimedOut;
        bool drainCutWhileAlive;
        try
        {
            run = RunProcess(_binaryPath, arguments, _runTimeoutMs, OutputBudgetMsForTest ?? _runTimeoutMs, out timedOut, out drainTimedOut, out drainCutWhileAlive, bridge, stopToken, stopRecord);
        }
        catch (Exception exception) when (stopToken.IsCancellationRequested && !stopRecord.ChildEndedBeforeStop)
        {
            // A failure while a live child was being stopped is the run's result, and what the stop could not do is in the diagnostics. A child that
            // ended on its own before the stop reached it is not a stop failure: the exception is not converted into ARTIFACT_RUN_EXCEPTION, which
            // would replace its own result (owner rule 3). The exception then propagates (round 18, LOW 4); no result is fabricated for it.
            stopRecord.RefuseIfUnset($"the stop could not be completed for process {stopRecord.ProcessId}, which may still be running: {exception.Message}");
            LastStopDiagnostics = new StopDiagnostics(stopRecord.ProcessId, false, false, stopRecord.Outcome.Refused, stopRecord.Residuals);
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.ExecutionFailed,
                "ARTIFACT_RUN_EXCEPTION",
                $"The run failed while it was being stopped: {exception.Message}");
        }
        finally
        {
            stopRecord.Seal();
        }

        var (exit, stdout, _, hostProtocolFaulted, pumpEnded) = run;

        // Capture the no-bridge run's stdout so a runtime host can forward the
        // guest's console output (the boot/TTY text before the engine's
        // RSNAPSHOT diagnostic block). Bridge runs surface TTY through the bridge's
        // runtime sink instead and must not be double-buffered here.
        if (_prebuilt && _launchMode == PrebuiltArtifactLaunchMode.RuntimeHost)
        {
            _consoleOutput = stdout;
        }

        // Mixed-execution evidence is captured before any early return below: a handoff that happened is reported
        // even when the run then ends on a protocol fault, a timeout, or an MMIO or guest-time failure (Issue #693).
        // None of the bridge's state is read while the host-transfer pump still owns the device graph (Issue #762):
        // the pump may still be mutating that state (its VRAM is live native memory it may still draw through).
        FallbackEvidence = pumpEnded ? bridge?.FallbackEvidence : null;
        FallbackTimings = pumpEnded ? bridge?.FallbackTimings : null;
        FallbackInFlight = bridge?.FallbackInFlight;
        FallbackReturns = bridge?.FallbackReturns;
        // Issue #820: a stopped run does not read the GPU/VRAM after the stop. The frame is captured only for a run that was not stopped.
        // The record, not the token, decides: a stop that has begun is recorded before its kill, and the record is sealed at this point.
        BeforeFrameCaptureForTest?.Invoke();
        (byte[] Bgra, int Width, int Height)? frame = null;
        if (pumpEnded && bridge is not null)
        {
            // The run-end frame is read through the frame gate like every other read: only while no stop was requested, with the check and
            // the read start in one section (Issue #827, owner requirement). The token and the stop record are checked too.
            _frameReads.TryRead(
                () => stopToken.IsCancellationRequested || stopRecord.StoppedAt is not null,
                () =>
                {
                    DuringFrameReadForTest?.Invoke();
                    FrameCaptureCalls++;
                    frame = bridge.CaptureFrameBgra32();
                });
        }

        LastFrameBgra32 = frame;

        // What the stop left behind is reported apart from the result (round 10): a refusal or a residual process is in the diagnostics, and the
        // result stays as the run had it.
        // A stop that was requested but never processed by the record (the registration was sealed before the request arrived) is not reported as
        // NotStopped: its descendants were not checked, so it is a refusal of the shutdown, never a clean one (Issue #827, round 14).
        // The outcome first: ResolveOutcome adds the residuals of the output drains (a pipe holder, a cut drain). The diagnostics are taken after
        // it, so they include every residual (owner spec, round 17; a snapshot taken earlier missed them).
        var result = ResolveOutcome(
            ClassifyArtifactRun(exit, stdout, hostProtocolFaulted, timedOut, bridge),
            stopRecord,
            drainTimedOut,
            drainCutWhileAlive,
            OutputBudgetMsForTest ?? _runTimeoutMs);

        var stopRequested = stopToken.IsCancellationRequested || _frameReads.StopRequested;
        LastStopDiagnostics = ShutdownDiagnostics(stopRecord, stopRequested);
        if (LastStopDiagnostics is { Clean: false } residual)
        {
            Trace.WriteLine($"Stop of artifact process {residual.ProcessId} left a residual: refused={residual.Refused}; residuals={residual.ResidualText}");
        }

        return result;
    }

    /// <summary>
    /// The instant from which a stop caps the output drains and the pump wait (Issue #827, review round 12). Only a stop that found its child alive,
    /// with its decision settled, caps them. A child that ended on its own before the stop keeps the run's own output budget: a stop must not turn
    /// its completed output into a drain timeout. Null when no cap applies.
    /// </summary>
    private static long? StopCapInstant(StopRecord stop) =>
        stop.Settled && !stop.ChildEndedBeforeStop ? stop.StoppedAt : null;

    /// <summary>
    /// The one place where a run's result is decided after its child ended or was stopped (Issue #827). Four branches, in this order:
    /// <list type="number">
    /// <item>A confirmed stop (the live child was killed and its exit seen): Cancelled. A stdout drain cut short is a residual reason only.</item>
    /// <item>A refused stop (the kill was refused, so the child is still running): the run's own result, which is the timeout of the live child
    /// (<see cref="TimeoutDiagnosticCode"/>). The refusal and any cut drain are residuals; no stop or drain code replaces the result.</item>
    /// <item>A child killed at its own budget whose stdout could not be drained: ExecutionFailed with
    /// <see cref="OutputDrainTimeoutDiagnosticCode"/> (the guest did not finish, and its output is unread).</item>
    /// <item>A child that ended on its own: its own classification stands, with the output read up to the cut. A grandchild that still holds
    /// the stdout pipe is a residual of the cleanup (holder unidentified), not a guest result.</item>
    /// </list>
    /// </summary>
    internal static RecompilerExecutionResult ResolveOutcome(
        RecompilerExecutionResult classified, StopRecord stop, bool drainTimedOut, bool drainCutWhileAlive, int budgetMs)
    {
        var afterStop = ApplyStopOutcome(classified, stop);

        // 1. A confirmed stop decides.
        if (afterStop.Status == RecompilerExecutionStatus.Cancelled)
        {
            if (drainTimedOut || drainCutWhileAlive)
            {
                stop.AddResidual(new ResidualProcess(
                    null,
                    ResidualState.Unverified,
                    $"the stdout drain timed out after the stop (budget {budgetMs} ms), so the artifact's output was not read; the holder is unidentified"));
            }

            return afterStop;
        }

        // 2. A refused stop: the child is still running, so the run's own result is its timeout. Neither the refusal nor the cut drain changes it.
        if (stop.Outcome.Refused is { } refusal)
        {
            if (drainCutWhileAlive)
            {
                stop.AddResidual(new ResidualProcess(
                    null,
                    ResidualState.Unverified,
                    $"the stdout was not drained after the refused stop ({refusal}); the holder is unidentified (pid not known)"));
            }

            return afterStop;
        }

        // 3. A child killed at its own budget, whose output could not be drained.
        if (drainCutWhileAlive)
        {
            stop.AddResidual(new ResidualProcess(
                null,
                ResidualState.Unverified,
                $"an output pipe is still held after the artifact was killed at its budget ({budgetMs} ms); the holder is unidentified (pid not known)"));
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.ExecutionFailed,
                OutputDrainTimeoutDiagnosticCode,
                $"The stdout drain timed out after {budgetMs} ms while the artifact was still running, so the artifact's output was not read.");
        }

        // 4. The child ended on its own: its own result stands; a held pipe is a residual of the cleanup.
        if (drainTimedOut)
        {
            stop.AddResidual(new ResidualProcess(
                null,
                ResidualState.Unverified,
                $"an output pipe is still held after the child ended on its own (drain budget {budgetMs} ms); the holder is unidentified (pid not known)"));
        }

        return afterStop;
    }

    /// <summary>
    /// The facts a run's stop left, as plain values (Issue #827, round 19). The shutdown and its diagnostics are derived from these alone: the engine's
    /// run and the test harness call the same function, and unit tests state the facts directly, on every platform.
    /// </summary>
    /// <param name="ProcessId">The child's process id (0 before it started).</param>
    /// <param name="StopRecorded">The stop record processed a stop (its instant was set).</param>
    /// <param name="StopRequested">A stop was requested during the run or before its end.</param>
    /// <param name="ChildEndedBeforeStop">The child ended on its own before the stop reached it.</param>
    /// <param name="KillConfirmed">The live child was killed and its exit was seen.</param>
    /// <param name="Refused">The reason the stop itself failed, or null.</param>
    /// <param name="Residuals">What the stop or the drains left behind.</param>
    [Infrastructure]
    internal sealed record ShutdownEvidence(
        int ProcessId,
        bool StopRecorded,
        bool StopRequested,
        bool ChildEndedBeforeStop,
        bool KillConfirmed,
        string? Refused,
        IReadOnlyList<ResidualProcess> Residuals);

    /// <summary>
    /// The shutdown diagnostics of a run (Issue #827, rules 2, 3 and 7): a processed stop reports its own facts; a stop that was requested but not
    /// processed is a refusal of the shutdown (its descendants were not checked), never NotStopped; a natural end that left a residual reports it with
    /// the child ended before any stop; otherwise there are no diagnostics (NotStopped). The result of the run is not an input: the diagnostics
    /// cannot rewrite it.
    /// </summary>
    internal static StopDiagnostics? ShutdownDiagnostics(ShutdownEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.StopRecorded)
        {
            return new StopDiagnostics(evidence.ProcessId, evidence.ChildEndedBeforeStop, evidence.KillConfirmed, evidence.Refused, evidence.Residuals);
        }

        if (evidence.StopRequested)
        {
            return new StopDiagnostics(
                evidence.ProcessId,
                false,
                false,
                "the stop was requested after the run's stop record was sealed, so the child's descendants were not checked",
                evidence.Residuals);
        }

        if (evidence.Residuals.Count > 0)
        {
            return new StopDiagnostics(evidence.ProcessId, true, false, null, evidence.Residuals);
        }

        return null;
    }

    private static StopDiagnostics? ShutdownDiagnostics(StopRecord stop, bool stopRequested) =>
        ShutdownDiagnostics(new ShutdownEvidence(
            stop.ProcessId,
            stop.StoppedAt is not null,
            stopRequested,
            stop.ChildEndedBeforeStop,
            stop.Outcome.Confirmed,
            stop.Outcome.Refused,
            stop.Residuals));

    /// <summary>Classifies the finished child's output into a segment result (the Issue #459 result contract).</summary>
    private static RecompilerExecutionResult ClassifyArtifactRun(
        int exit, string stdout, bool hostProtocolFaulted, bool timedOut, HostTransferBridge? bridge)
    {
        if (hostProtocolFaulted)
        {
            // A host-transfer pump fault (a BIOS factory, protocol, or stream
            // failure) is an engine mechanism failure, classified here so it never
            // surfaces as a raw exception past the launcher — and its details
            // never leak into the result contract (Issue #459's stable JSON).
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.ExecutionFailed, "ARTIFACT_HOST_PROTOCOL_FAILED",
                "The artifact host-transfer protocol failed.");
        }

        if (timedOut)
        {
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.TimedOut, TimeoutDiagnosticCode, "The runnable artifact exceeded its bounded execution timeout.");
        }

        if (bridge?.MmioFailureCode is { } mmioFailureCode)
        {
            // The artifact stopped itself on a refused guest MMIO access (Issue #678).
            // Classified here instead of the generic ARTIFACT_FAILED so an unsupported
            // device address is never mistaken for a child crash, and never read as 0.
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.ExecutionFailed, mmioFailureCode, bridge.MmioFailureMessage!);
        }

        if (bridge?.RetiredFailureCode is { } retiredFailureCode)
        {
            // The scheduler or a device failed while consuming the artifact's guest time
            // (Issue #679): the run stopped rather than continue with devices that
            // diverged from the guest.
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.ExecutionFailed, retiredFailureCode, bridge.RetiredFailureMessage!);
        }

        if (exit == RecompiledArtifactCodeGen.FallbackProtocolExitCode)
        {
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.ExecutionFailed, "ARTIFACT_FALLBACK_PROTOCOL_FAILED",
                "The artifact rejected a malformed mixed-execution fallback command.");
        }

        if (exit < 0 || exit > byte.MaxValue || !stdout.Contains(RecompiledArtifactCodeGen.SnapshotBeginMarker, StringComparison.Ordinal))
        {
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.ExecutionFailed, "ARTIFACT_FAILED", $"The runnable artifact failed (exit {exit}).");
        }

        var snapshot = ArtifactSnapshotParser.Parse(stdout);
        if (snapshot is null)
        {
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.MalformedResult, "MALFORMED_ARTIFACT_SNAPSHOT", MalformedSnapshotMessage(stdout));
        }

        if (bridge is { ExceptionVectorReached: true })
        {
            // Issue #680: the INT entry state, so the diagnostic says what the CPU delivered.
            var entry = ArtifactSnapshotParser.ReadCop0(stdout) is { } c
                ? $"EPC=0x{c.Epc:X8}, CAUSE=0x{c.Cause:X8}, SR=0x{c.Sr:X8}"
                : "COP0 state unavailable";
            return new RecompilerExecutionResult(
                RecompilerExecutionStatus.Completed, snapshot, bridge.DiagnosticCode, $"{bridge.DiagnosticMessage}|{entry}");
        }

        return new RecompilerExecutionResult(RecompilerExecutionStatus.Completed, snapshot, bridge?.DiagnosticCode, bridge?.DiagnosticMessage);
    }

    /// <summary>
    /// The diagnostic for a snapshot the parser rejects. It names only the shape (complete or not, and the count of body lines),
    /// never the raw protocol text: the snapshot carries GPR values, and no line of it may reach a public diagnostic.
    /// </summary>
    internal static string MalformedSnapshotMessage(string stdout)
    {
        var begin = stdout.IndexOf(RecompiledArtifactCodeGen.SnapshotBeginMarker, StringComparison.Ordinal);
        var end = stdout.IndexOf(RecompiledArtifactCodeGen.SnapshotEndMarker, StringComparison.Ordinal);
        if (begin < 0 || end < begin)
        {
            return "The runnable artifact's state snapshot is incomplete.";
        }

        var lines = stdout[(begin + RecompiledArtifactCodeGen.SnapshotBeginMarker.Length)..end]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        return $"The runnable artifact's state snapshot is malformed ({lines} body lines).";
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        // Same route as RequestCancel (owner requirement): the request flag is set through the frame gate first; the token is cancelled after
        // it, on the pool. A run still in progress is stopped rather than left to its timeout. The stop waits for the child's exit (up to
        // StopRecord.ConfirmTimeout), so it runs on the thread pool: a caller on the UI thread is never blocked (Issue #827). After a finished
        // run this does nothing.
        _frameReads.RequestStop();
        var source = _stopRequested;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed by an earlier Dispose call.
            }
            catch (AggregateException exception)
            {
                Trace.WriteLine($"Stop on dispose failed: {exception}");
            }
            finally
            {
                source.Dispose();
            }
        });
    }

    private void WriteInputFile(
        uint pc, IReadOnlyList<uint> gpr, uint hi, uint lo, IReadOnlyList<RecompilerInitialMemoryItem> initialMemory, uint budget)
    {
        if (initialMemory.Count > RecompiledArtifactCodeGen.MaxInitEntries)
        {
            // The driver's own PSX_MAX_INIT bound (emitted from MaxInitEntries by
            // RecompiledArtifactCodeGen.Generate) would otherwise reject this input
            // file with an opaque process exit code; fail with a clear message
            // instead. Load validates the same bound up front, so this is defense
            // in depth for a direct-preparation path.
            throw new InvalidOperationException(
                $"Initial memory has {initialMemory.Count} entries, exceeding the artifact driver's " +
                $"{RecompiledArtifactCodeGen.MaxInitEntries}-entry limit.");
        }

        var sb = new StringBuilder();
        for (var i = 0; i < gpr.Count; i++) sb.Append(gpr[i]).Append(' ');
        sb.Append('\n').Append(hi).Append('\n').Append(lo).Append('\n').Append(pc).Append('\n').Append(budget).Append('\n');
        sb.Append(initialMemory.Count).Append('\n');
        foreach (var item in initialMemory)
        {
            sb.Append(item.Address).Append(' ').Append(item.Value).Append('\n');
        }

        // Issue #637: the program image travels as a raw little-endian sidecar
        // (argv[2]) rather than as init-memory entries — a PS-X EXE text segment
        // is far beyond MaxInitEntries — and the input file declares its load
        // address and exact byte length so a missing or truncated sidecar fails
        // closed in the driver.
        var image = new byte[_imageWords.Count * sizeof(uint)];
        for (var i = 0; i < _imageWords.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(i * sizeof(uint)), _imageWords[i]);
        }

        sb.Append(_imageLoadAddress).Append(' ').Append(image.Length).Append('\n');

        File.WriteAllText(_inputPath, sb.ToString());
        File.WriteAllBytes(_imagePath, image);
    }

    /// <summary>
    /// Generates, builds, and stages the embedded-image artifact for this run
    /// (Phase 2, Issue #757): initial GPR/HI/LO/PC/budget, the init-memory seed
    /// and the program image are embedded in the C source, so the artifact needs
    /// no input files and attaches the shared Runtime host bridge over the
    /// existing <c>--host-transfer</c> path. The embedded state is the state the
    /// calling <see cref="RunSegment"/> names, so generation is deferred to the
    /// first (and only) segment call and Load's "never frozen here" contract is
    /// preserved.
    /// </summary>
    private void BuildEmbeddedArtifact(
        uint pc,
        IReadOnlyList<uint> gpr,
        uint hi,
        uint lo,
        IReadOnlyList<RecompilerInitialMemoryItem> initialMemory,
        uint budget)
    {
        if (initialMemory.Count > RecompiledArtifactCodeGen.MaxInitEntries)
        {
            // Same defense as WriteInputFile/Load: the embedded driver's own
            // PSX_MAX_INIT bound would otherwise reject the source with an opaque
            // artifact exit code.
            throw new InvalidOperationException(
                $"Initial memory has {initialMemory.Count} entries, exceeding the artifact driver's " +
                $"{RecompiledArtifactCodeGen.MaxInitEntries}-entry limit.");
        }

        var dispatch = RecompilerHostCodeGen.Generate(_program, _loadedCode, traceUnits: UnitTraceScope.CurrentPath is not null, guardStats: GuardStats);
        if (!dispatch.Success)
        {
            throw new InvalidOperationException(dispatch.DiagnosticMessage ?? "Host dispatch code generation failed.");
        }

        var image = new byte[_imageWords.Count * sizeof(uint)];
        for (var i = 0; i < _imageWords.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(i * sizeof(uint)), _imageWords[i]);
        }

        var artifact = RecompiledArtifactCodeGen.GenerateStandalone(
            dispatch,
            gpr,
            hi,
            lo,
            pc,
            budget,
            initialMemory.Select(static item => (item.Address, (uint)item.Value)).ToArray(),
            _imageLoadAddress,
            image,
            hostBridge: true);
        if (!artifact.Success)
        {
            throw new InvalidOperationException(artifact.DiagnosticMessage ?? "Embedded artifact code generation failed.");
        }

        var build = _buildService.Build(new GeneratedHostBuildRequest(artifact.Source!, _outputDirectory, "recompiled-artifact"));
        if (build.Status != GeneratedHostBuildStatus.Succeeded)
        {
            throw new InvalidOperationException(
                $"Artifact build failed ({build.Status})." +
                (string.IsNullOrEmpty(build.DiagnosticMessage) ? "" : "\n" + build.DiagnosticMessage));
        }

        _binaryPath = build.Artifact!.BinaryPath;
    }

    /// <summary>
    /// Runs one process, optionally pumping <paramref name="bridge"/>'s
    /// host-transfer protocol. Arguments travel as a collection through
    /// <see cref="ProcessStartInfo.ArgumentList"/> — never as a hand-quoted raw
    /// <see cref="ProcessStartInfo.Arguments"/> string — so an input path
    /// containing spaces or quotes is received by the artifact unchanged as one
    /// argument.
    /// </summary>
    /// <returns>The process exit code, captured stdout/stderr, whether the
    /// host-transfer pump faulted, and whether the pump has stopped. A pump fault
    /// (a BIOS factory exception, a protocol parse failure, a stdin/stdout
    /// read-or-write failure, or a transfer send failure) is a classified
    /// protocol failure, not a caller-visible exception: the child is killed and
    /// drained here and the fault indicator lets <see cref="RunSegment"/> map it to
    /// <c>ARTIFACT_HOST_PROTOCOL_FAILED</c> without leaking the underlying
    /// exception into the result contract. <c>PumpEnded</c> is false only when the
    /// pump outlived its drain budget and still owns the bridge's devices: the
    /// caller then neither reads nor disposes those devices (Issue #762).</returns>
    private static (int ExitCode, string Stdout, string Stderr, bool HostProtocolFaulted, bool PumpEnded) RunProcess(
        string fileName, IReadOnlyList<string> arguments, int timeoutMs, int outputBudgetMs, out bool timedOut, out bool drainTimedOut,
        out bool drainCutWhileAlive, HostTransferBridge? bridge, CancellationToken stopToken, StopRecord stopRecord)
    {
        // The stdout drain of a child that ended on its own is bounded by outputBudgetMs; drainTimedOut reports that it ran out (Issue #827).
        // drainCutWhileAlive reports that the child was still alive (it was killed at its budget) and its output could not be drained.
        drainTimedOut = false;
        drainCutWhileAlive = false;
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = bridge is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        // Issue #774: only a traced run changes the child's environment; an untraced run passes it through unchanged.
        if (bridge?.UnitTraceChildEnvironment is { } traceEnvironment)
        {
            foreach (var (name, value) in traceEnvironment)
            {
                psi.Environment[name] = value;
            }
        }

        // Issue #773: a guard-stats scope names the report for this child only; without one the environment passes through.
        if (GuardStatsScope.CurrentPath is { } guardStatsFile)
        {
            psi.Environment[GuardStatsScope.FileEnvironmentVariable] = guardStatsFile;
            // Each child draws its own run id: the generated code cannot tell two processes apart by time and clock() alone (Issue #804).
            psi.Environment[GuardStatsScope.RunIdEnvironmentVariable] = Random.Shared.NextInt64().ToString("x16", CultureInfo.InvariantCulture);
            var (periodUnits, abortAfterUnits) = GuardStatsScope.CurrentTiming;
            // Always set (0 when unset), so a stray value in the parent cannot leak into a guard-stats child.
            psi.Environment[GuardStatsScope.PeriodEnvironmentVariable] = periodUnits.ToString(CultureInfo.InvariantCulture);
            psi.Environment[GuardStatsScope.AbortAfterEnvironmentVariable] = abortAfterUnits.ToString(CultureInfo.InvariantCulture);
        }

        using var process = Process.Start(psi)!;
        // Issue #820: a stop request kills the child tree. That closes its pipes, so a read blocked on the RHOST
        // protocol (or on stderr) returns, and the existing timeout path below does the cleanup and classification.
        // If the request arrived before this point, the registration runs at once and the child is killed here.
        // Declared after the process, so it is sealed before the process is disposed on any return path (Issue #827).
        using var stopSeal = stopRecord.Attach(process, stopToken);
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (bridge is null)
        {
            // The stdout is collected as it arrives, so a drain that is cut short still has the text read so far.
            var stdoutSink = new OutputSink();
            var stdoutTask = stdoutSink.Pump(process.StandardOutput);
            if (!WaitBounded(milliseconds => process.WaitForExit(milliseconds), timeoutMs, stopRecord, process, "exit"))
            {
                // The budget ran out, or a stop was refused or confirmed: the child is killed, and the stop (if any) decides the result.
                TryKillTree(process, stopRecord, stopToken);
                // The child had not exited when the wait ended: it is a timeout, whatever the stop decided (a confirmed stop is Cancelled in
                // ResolveOutcome; a refused one keeps this timeout). Round 18, MEDIUM 1.
                timedOut = true;
                // Both pipes share one cleanup budget (and a stop caps it), so the two drains do not add up. A stdout drain that is cut here
                // is reported as cut while alive: the child did not end, so its output was not read.
                var cleanupStart = Stopwatch.GetTimestamp();
                WaitForDrain(stdoutTask, PumpDrainTimeoutMs, stopRecord, cleanupStart, out drainCutWhileAlive);
                return (
                    int.MinValue,
                    stdoutSink.Text,
                    DrainUntil(stderrTask, PumpDrainTimeoutMs, stopRecord, cleanupStart, out _),
                    false,
                    true);
            }

            timedOut = false;

            // The child ended on its own: its pipes drain within the run's output budget. A stop that arrives during the drain caps the rest
            // of it at the cleanup budget (see DrainUntil). A stdout drain cut here is reported through drainTimedOut: the child's own result
            // stands, and the holder of the open pipe is a residual (see ResolveOutcome).
            var drainStart = Stopwatch.GetTimestamp();
            WaitForDrain(stdoutTask, outputBudgetMs, stopRecord, drainStart, out drainTimedOut);
            var stdout = stdoutSink.Text;
            var stderr = DrainUntil(stderrTask, outputBudgetMs, stopRecord, drainStart, out _);
            RecordPipeHeldAfterStop(stopToken, stopRecord, stdoutTask, stderrTask);
            return (process.ExitCode, stdout, stderr, false, true);
        }

        process.StandardInput.AutoFlush = true;
        bridge.Attach(process.StandardOutput, process.StandardInput);

        var output = new StringBuilder();
        var outputGate = new object();
        // Issue #815: the pump runs on its own thread, not a thread-pool worker (see ProtocolPump).
        var pump = ProtocolPump.Start(() =>
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                if (!bridge.TryHandle(line))
                {
                    lock (outputGate) output.AppendLine(line);
                }
            }
        });
        bridge.TrackPump(pump);

        bool pumpCompleted;
        try
        {
            pumpCompleted = WaitPumpWithinOutputBudget(pump, process, timeoutMs, outputBudgetMs, stopRecord, out drainTimedOut);
        }
        catch (Exception exception)
        {
            // The host-transfer pump faulted. Kill and drain the child, keep the
            // exception out of the result contract (log it locally for
            // diagnostics), and report the fault so RunSegment classifies it as
            // ARTIFACT_HOST_PROTOCOL_FAILED instead of letting a raw exception
            // escape the launcher. This is distinct from a timeout or a normal
            // process-failure classification. A faulted pump has already stopped,
            // so its devices are free for the caller to read and dispose.
            TryKillTree(process, stopRecord, stopToken);
            Trace.WriteLine($"Host-transfer pump failed: {exception}");
            timedOut = false;
            return (int.MinValue, TakeOutput(output, outputGate), DrainUntil(stderrTask, PumpDrainTimeoutMs, stopRecord, Stopwatch.GetTimestamp(), out _), true, true);
        }

        if (!pumpCompleted && drainTimedOut)
        {
            // The child ended on its own, but its output did not reach its end within the output budget (a grandchild holds the pipe). The output
            // was not read, so the result is a drain timeout, never a silent failure or a plain timeout (Issue #827 review, MEDIUM 1).
            TryReleaseStdout(process);
            var drainedPumpEnded = HostTransferPumpLifetime.AwaitEnded(pump, TimeSpan.FromMilliseconds(PumpDrainTimeoutMs));
            timedOut = false;
            return (process.ExitCode, TakeOutput(output, outputGate), DrainUntil(stderrTask, PumpDrainTimeoutMs, stopRecord, Stopwatch.GetTimestamp(), out _), false, drainedPumpEnded);
        }

        if (!pumpCompleted)
        {
            // The pump outlived the run budget. Stop the child and release its stdout
            // pipe so a pump blocked in a read observes EOF, then give the pump a
            // bounded drain. If it still has not ended, the bridge's devices remain
            // under the pump's ownership: the caller neither reads nor disposes them
            // (Issue #762).
            TryKillTree(process, stopRecord, stopToken);

            // The pump ends on its own only when the pipe closes. The stream is released after this wait: releasing it ends the pump whatever
            // the pipe holder does, so checking afterwards would never see a stalled drain (round 17, MEDIUM 2).
            var drainedOnItsOwn = HostTransferPumpLifetime.AwaitEnded(pump, TimeSpan.FromMilliseconds(PumpDrainTimeoutMs));

            // The child was alive at its budget and killed, and its output did not end on its own: the same cut as the null-bridge path, so the
            // guest did not finish and its output is unread (owner spec B).
            drainCutWhileAlive = !drainedOnItsOwn;
            TryReleaseStdout(process);
            var pumpEnded = drainedOnItsOwn || HostTransferPumpLifetime.AwaitEnded(pump, TimeSpan.FromSeconds(1));
            timedOut = true;
            return (int.MinValue, TakeOutput(output, outputGate), DrainUntil(stderrTask, PumpDrainTimeoutMs, stopRecord, Stopwatch.GetTimestamp(), out _), false, pumpEnded);
        }

        if (!WaitBounded(milliseconds => process.WaitForExit(milliseconds), timeoutMs, stopRecord, process, "exit"))
        {
            // The pump ended but the child has not exited: stop it. Nothing is using
            // the bridge's devices any more.
            TryKillTree(process, stopRecord, stopToken);
            timedOut = true;
            return (int.MinValue, TakeOutput(output, outputGate), DrainUntil(stderrTask, PumpDrainTimeoutMs, stopRecord, Stopwatch.GetTimestamp(), out _), false, true);
        }

        timedOut = false;
        var bridgeStderr = DrainUntil(stderrTask, outputBudgetMs, stopRecord, Stopwatch.GetTimestamp(), out _);
        RecordPipeHeldAfterStop(stopToken, stopRecord, stderrTask);
        return (process.ExitCode, TakeOutput(output, outputGate), bridgeStderr, false, true);
    }

    /// <summary>
    /// Waits for <paramref name="waitSlice"/> to report the end within <paramref name="timeoutMs"/>, but returns early once a stop has
    /// settled (Issue #827): a refused or confirmed stop must not wait out the run's whole budget. Returns true only when the end was seen.
    /// </summary>
    private static bool WaitBounded(Func<int, bool> waitSlice, int timeoutMs, StopRecord stopRecord, Process process, string phase)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var remaining = timeoutMs - (int)clock.ElapsedMilliseconds;
            if (remaining <= 0)
            {
                return false;
            }

            // The child's descendants are tracked while it is alive (Issue #827 review): a claim cannot be made after the child is gone.
            stopRecord.SampleTree();
            if (stopRecord.Observe(process, phase, waitSlice(Math.Min(StopRecord.PollMs, remaining))))
            {
                return true;
            }

            if (!stopRecord.Settled)
            {
                continue;
            }

            // A stop has settled. A child that already ended normally is not a timeout: its end is taken, and its output is drained
            // within the drain bound (Issue #827 review). A live child ends the wait with the stop's own decision.
            if (waitSlice(0))
            {
                return true;
            }

            return StopRecord.HasExitedOrGone(process) && waitSlice(PumpDrainTimeoutMs);
        }
    }

    /// <summary>
    /// Waits for the host-transfer pump to reach the end of the child's output (Issue #827 review, MEDIUM 1). While the child is alive, the
    /// run's budget applies. Once the child has ended, the pump has the output budget (a grandchild may hold the pipe open after the child's
    /// exit); a stop caps that from the stop, as for the other drains. A pump that is still open when its budget ends is reported through
    /// <paramref name="drainTimedOut"/>: the child's output was not read.
    /// </summary>
    internal static bool WaitPumpWithinOutputBudget(Task pump, Process process, int runTimeoutMs, int outputBudgetMs, StopRecord stop, out bool drainTimedOut)
    {
        drainTimedOut = false;
        var frequency = Stopwatch.Frequency;
        var start = Stopwatch.GetTimestamp();
        long? childEnded = null;
        while (true)
        {
            var now = Stopwatch.GetTimestamp();
            if (childEnded is null && StopRecord.HasExitedOrGone(process))
            {
                childEnded = now;
            }

            long end = start + runTimeoutMs * frequency / 1000;
            if (childEnded is { } ended)
            {
                end = ended + outputBudgetMs * frequency / 1000;

                // A stop caps the wait only when it found the child alive (round 12): a child that ended first keeps the output budget.
                if (StopCapInstant(stop) is { } stoppedAt)
                {
                    var cap = Math.Max(ended, stoppedAt) + PumpDrainTimeoutMs * frequency / 1000;
                    if (cap < end)
                    {
                        end = cap;
                    }
                }

                if (now >= end)
                {
                    drainTimedOut = true;
                    return false;
                }
            }
            else if (stop.Settled || now >= end)
            {
                return false;
            }

            // The descendants are tracked while the child is alive, and each wait is observed, as in WaitBounded.
            stop.SampleTree();
            var remainingMs = (end - now) * 1000.0 / frequency;
            var pumpEnded = pump.Wait((int)Math.Min(StopRecord.PollMs, Math.Max(1.0, remainingMs)));
            if (stop.Observe(process, "pump", pumpEnded))
            {
                return true;
            }
        }
    }

    /// <summary>
    /// A stop that reached its child can still leave an output pipe open: a grandchild that outlived the child holds it. The child was killed
    /// (the result is Cancelled), so the holder is a residual, reported with the tracked processes still alive (Issue #827, round 14). The
    /// holder's id is known only when it is a tracked process; the OS gives no cheap owner lookup for a pipe.
    /// </summary>
    private static void RecordPipeHeldAfterStop(CancellationToken stopToken, StopRecord stopRecord, params Task[] pipes)
    {
        // A child that ended before the stop reached it is not a stop's failure: a pipe it leaves to a console host is the drain's business.
        if (stopToken.IsCancellationRequested && !stopRecord.ChildEndedBeforeStop && pipes.Any(pipe => !pipe.IsCompletedSuccessfully))
        {
            // The holder is not identified: the stop has no owner lookup for a pipe, so no pid is reported (a guess would be wrong).
            stopRecord.AddResidual(new ResidualProcess(
                null,
                ResidualState.Unverified,
                "an output pipe is still held by a process that outlived the child; the holder is unidentified (pid not known)"));
        }
    }

    /// <summary>
    /// Runs one child with a stop record and no artifact (Issue #827): the same bounded wait, drain and stop decision as a segment.
    /// Used by the stop tests with a real child process.
    /// </summary>
    internal static (int ExitCode, string Stdout, bool TimedOut, bool Confirmed, string? Refused, string? Leftover, RecompilerExecutionResult Result) RunChildUntilStopped(
        string fileName,
        IReadOnlyList<string> arguments,
        int timeoutMs,
        CancellationToken stopToken,
        Func<Process, IReadOnlyList<Process>?>? descendantsProbe = null,
        Func<StopRecord, Process, string, bool, bool>? observe = null,
        Action<Process>? kill = null,
        Func<Process, DateTime?>? startTime = null,
        int? outputBudgetMs = null,
        Action<Process>? descendantKill = null)
    {
        var stopRecord = new StopRecord
        {
            DescendantsProbe = descendantsProbe,
            ObserveForTest = observe,
            KillForTest = kill,
            StartTimeForTest = startTime,
            DescendantKillForTest = descendantKill,
        };
        (int exit, string stdout, string stderr, bool hostProtocolFaulted, bool pumpEnded) run;
        bool timedOut;
        bool drainTimedOut;
        bool drainCutWhileAlive;
        try
        {
            run = RunProcess(fileName, arguments, timeoutMs, outputBudgetMs ?? timeoutMs, out timedOut, out drainTimedOut, out drainCutWhileAlive, bridge: null, stopToken, stopRecord);
        }
        finally
        {
            stopRecord.Seal();
        }

        var (confirmed, refused) = stopRecord.Outcome;

        // The harness classifies its child by the engine's own rule (round 19, MEDIUM 2): an exit 0 without a snapshot is a failure, exactly as in
        // a segment. Then the same resolution and the same shutdown diagnostics as RunSegment, so the Windows tests check the same contract.
        var classified = ClassifyArtifactRun(run.exit, run.stdout, run.hostProtocolFaulted, timedOut, bridge: null);
        var result = ResolveOutcome(classified, stopRecord, drainTimedOut, drainCutWhileAlive, outputBudgetMs ?? timeoutMs);
        var diagnostics = ShutdownDiagnostics(stopRecord, stopToken.IsCancellationRequested);
        return (run.exit, run.stdout, timedOut, confirmed, refused, diagnostics?.ResidualText, result);
    }

    private static string TakeOutput(StringBuilder output, object gate)
    {
        lock (gate)
        {
            return output.ToString();
        }
    }

    /// <summary>
    /// Best-effort release of the child's stdout read pipe after it has been killed, so a pump
    /// blocked in <c>ReadLine</c> cannot outlive the child. The child's death (its closed write
    /// end) is the primary stop; a failure here is diagnostic-only.
    /// </summary>
    private static void TryReleaseStdout(Process process)
    {
        try
        {
            process.StandardOutput.Close();
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Releasing the host-transfer stdout pipe failed: {exception}");
        }
    }

    /// <summary>
    /// Consumes a redirected stream within <paramref name="budgetMs"/>. A stop that arrives during the drain caps the rest of it at the
    /// cleanup budget, measured from the stop, so a stop never waits for the full budget (Issue #827). <paramref name="timedOut"/> reports
    /// that the budget ran out before the stream ended; the result is then empty, and the caller reports it.
    /// </summary>
    private static string DrainUntil(Task<string> readTask, int budgetMs, StopRecord stop, long drainStart, out bool timedOut)
    {
        var completed = WaitForDrain(readTask, budgetMs, stop, drainStart, out timedOut);
        return completed && readTask.IsCompletedSuccessfully ? readTask.Result : string.Empty;
    }

    /// <summary>
    /// Collects a stream as it is read, so a drain that is cut short still has the text read so far (Issue #827, round 16). The whole text
    /// is available once the stream has ended.
    /// </summary>
    private sealed class OutputSink
    {
        private readonly StringBuilder _text = new();
        private readonly object _gate = new();

        /// <summary>The text read so far.</summary>
        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return _text.ToString();
                }
            }
        }

        /// <summary>Reads the stream to its end, appending each chunk as it arrives.</summary>
        public async Task Pump(StreamReader reader)
        {
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory())) > 0)
            {
                lock (_gate)
                {
                    _text.Append(buffer, 0, read);
                }
            }
        }
    }

    /// <summary>
    /// Waits for a drain within its budget. Returns true when the read task has finished (in any state), false when the budget ran out first;
    /// <paramref name="timedOut"/> is the negation.
    /// </summary>
    private static bool WaitForDrain(Task readTask, int budgetMs, StopRecord stop, long drainStart, out bool timedOut)
    {
        timedOut = false;
        var frequency = Stopwatch.Frequency;
        var budgetEnd = drainStart + budgetMs * frequency / 1000;
        try
        {
            while (true)
            {
                if (readTask.IsCompleted)
                {
                    return true;
                }

                // The deadline is absolute and shared by the drains of one run. A stop caps it at the cleanup budget counted from the later of the
                // drain's start and the stop: the stop's own work (ending the tree) is not charged to the drain.
                var end = budgetEnd;
                var capStop = StopCapInstant(stop);
                var capFrom = capStop is { } stoppedAt ? Math.Max(drainStart, stoppedAt) : drainStart;
                if (capStop is not null)
                {
                    var cap = capFrom + PumpDrainTimeoutMs * frequency / 1000;
                    if (cap < end)
                    {
                        end = cap;
                    }
                }

                var now = Stopwatch.GetTimestamp();
                if (now >= end)
                {
                    timedOut = true;
                    return false;
                }

                var remainingMs = (end - now) * 1000.0 / frequency;
                readTask.Wait((int)Math.Min(StopRecord.PollMs, Math.Max(1.0, remainingMs)));
            }
        }
        catch (AggregateException)
        {
            // A stream read failure on the drain path is diagnostic-only: the read has finished (faulted).
            return true;
        }
    }


    /// <summary>
    /// Kills the child's tree on a timeout or a stop path. An exited child is left alone. A kill the OS refuses is never a raw exception
    /// out of the run: during a stop it is recorded as a refusal with its reason (Issue #827 review, MEDIUM 1).
    /// </summary>
    private static void TryKillTree(Process process, StopRecord stopRecord, CancellationToken stopToken)
    {
        try
        {
            stopRecord.KillTree(process);
        }
        catch (InvalidOperationException)
        {
            // Already exited: nothing to kill.
        }
        catch (Exception exception)
        {
            if (stopToken.IsCancellationRequested && !StopRecord.HasExitedOrGone(process))
            {
                stopRecord.RefuseIfUnset($"the kill was refused: {exception.Message}");
            }

            Trace.WriteLine($"Killing the artifact process failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Relays one artifact run's host-transfer offers to a real
    /// <see cref="IBiosRuntime"/> — the production analogue of
    /// <c>PSXRecomp.Tests.Recompiler.RecompilerHostExecutor.HostTransferSession</c>,
    /// against <see cref="RecompiledArtifactCodeGen"/>'s protocol instead of the
    /// differential driver's.
    /// </summary>
    private sealed class HostTransferBridge : IDisposable
    {
        private const int TransferFieldCount = 33; // pc + 32 GPRs.

        private readonly Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime>? _biosRuntimeFactory;
        private readonly IReadOnlySet<uint> _blockEntryPcs;
        private readonly bool _guestFirmware;
        private readonly Action<InterpreterTitleExecutionEngine, uint>? _fallbackFetchObserver;
        private readonly Action<MixedFallbackTransition>? _fallbackTransitionObserver;
        private readonly ICdSectorSource? _disc;
        private readonly LoadedCodeTable _loadedCode;
        private readonly LoadedCodeMismatchDiagnostics? _loadedCodeMismatch;

        private TextReader? _fromArtifact;
        private TextWriter? _toArtifact;
        private IBiosRuntime? _biosRuntime;
        private PsxDeviceGraph? _devices;
        private DeviceScheduler? _scheduler;
        private ulong _issuedCredit;

        /// <summary>The outstanding blocking BIOS call's poll bound (Issue #717); lives as long as this launch's devices.</summary>
        private readonly BiosBlockingCallWait _blockingCallWait = new();
        private ArtifactDeviceRam? _deviceRam;
        private readonly Action<PsxDeviceGraph>? _configureDevices;
        private readonly MemoryCardSlotConfiguration? _memoryCardSlots;
        private readonly BiosExceptionChain? _exceptionChain;
        private readonly MixedFallbackOptions? _mixedFallback;
        private readonly IReadOnlyList<uint> _imageWords;
        private readonly uint _imageLoadAddress;
        private ArtifactFallbackSession? _fallback;

        /// <summary>Opt-in protocol profile (Issue #768); null unless PSXRECOMP_IPC_PROFILE is set, so the hot path pays a null check.</summary>
        public Standalone.IpcProfile? Profile { get; } = Standalone.IpcProfile.FromEnvironment();

        /// <summary>
        /// Releases the device graph and fallback session only once the host-transfer pump that owns them has ended
        /// (Issue #762): the pump runs on its own thread and calls into both, so neither may be disposed underneath it.
        /// </summary>
        private readonly PumpOwnedResource _resourceLifetime;

        /// <summary>Evidence of this run's mixed execution, or null when it was not enabled (Issue #693).</summary>
        public MixedFallbackEvidence? FallbackEvidence => _fallback?.Evidence;

        /// <summary>Wall-clock costs of this run's mixed execution (Issue #693).</summary>
        public MixedFallbackTimings? FallbackTimings => _fallback?.Timings;

        /// <summary>The fallback segment in flight, scalars only and safe to read while the pump owns the graph (Issue #806).</summary>
        public FallbackSegmentProgress? FallbackInFlight => _fallback?.InFlightSegment;

        /// <summary>The return classification of this run so far (Issue #806).</summary>
        public FallbackReturnStats? FallbackReturns => _fallback?.ReturnStats;

        /// <summary>The sum of every accepted guest-time report: instructions retired by compiled blocks (Issue #732).</summary>
        public ulong NativeRetiredInstructions { get; private set; }

        public ulong MmioAccesses { get; private set; }

        /// <summary>The most recent MMIO access relayed to the device graph (Issue #763).</summary>
        public HostMmioAccess? LastMmioAccess { get; private set; }

        public ulong TimeReports { get; private set; }

        /// <summary>Why the host refused the artifact's guest-time report, or null when it did not (Issue #679).</summary>
        public string? RetiredFailureCode { get; private set; }
        public string? RetiredFailureMessage { get; private set; }

        public string? DiagnosticCode { get; private set; }
        public string? DiagnosticMessage { get; private set; }

        /// <summary>
        /// The last control transfer the host declined to claim, with whether the fallback was consulted and why it
        /// declined (Issue #763). The artifact treats a decline as an unsupported transfer, so this names the transfer
        /// that ends the run when it stops there.
        /// </summary>
        public FallbackDeclinedTransfer? LastDeclinedTransfer { get; private set; }

        /// <summary>The R3000A general exception vector, SR.BEV = 0 (RAM) and BEV = 1 (BIOS ROM), docs/cpu/exceptions.md.</summary>
        public const uint GeneralExceptionVectorBev0 = 0x80000080u;
        public const uint GeneralExceptionVectorBev1 = 0xBFC00180u;

        private const uint InterruptStatusAddress = 0x1F801070u;
        private const uint InterruptMaskAddress = 0x1F801074u;

        /// <summary>The run stopped because control reached the general exception vector and nothing was generated there (Issue #680).</summary>
        public bool ExceptionVectorReached { get; private set; }

        /// <summary>Why the host refused a guest MMIO access, or null when none was refused (Issue #678).</summary>
        public string? MmioFailureCode { get; private set; }
        public string? MmioFailureMessage { get; private set; }

        /// <summary>Whether the host-transfer pump is still running and still using this bridge's devices (Issue #762).</summary>
        public bool IsPumpActive => _resourceLifetime.IsPumpActive;

        /// <summary>Makes <paramref name="pump"/> own the bridge's devices until it ends (Issue #762).</summary>
        // Issue #774: the parent's side of the unit trace. Null unless a trace scope is active (see UnitTraceScope).
        private readonly UnitTraceRun? _unitTrace = UnitTraceRun.TryStart();

        /// <summary>The environment the child process gets for its own trace of this run, or null when tracing is off.</summary>
        public IReadOnlyDictionary<string, string>? UnitTraceChildEnvironment => _unitTrace?.ChildEnvironment();

        public void TrackPump(Task pump) => _resourceLifetime.Track(pump);

        public void Dispose()
        {
            _resourceLifetime.Dispose();
            _unitTrace?.Dispose();
        }

        /// <summary>
        /// Where VBlank frames are published while the run executes (Issue #818). Null leaves the run unchanged; the
        /// headless path never sets it.
        /// </summary>
        public LiveFrameSlot? LiveFrames { get; init; }

        /// <summary>
        /// The gate every read of this run's GPU/VRAM goes through, and the stop predicate it checks (Issue #827, owner requirement). Set by the
        /// engine before the pump starts. Without them the live publisher reads nothing (it fails closed).
        /// </summary>
        public FrameReadGate? FrameReads { get; set; }

        /// <summary>The stop predicate of the run's gate (see <see cref="FrameReads"/>).</summary>
        public Func<bool>? StopNow { get; set; }

        /// <summary>The displayed frame of this run's GPU device as BGRA32, or null when nothing was drawn (Issue #762).</summary>
        public (byte[] Bgra, int Width, int Height)? CaptureFrameBgra32()
        {
            if (_resourceLifetime.IsPumpActive)
            {
                // The pump still owns the device graph: reading the GPU now would race it. The frame
                // is only ever read once the pump has stopped (Issue #762).
                return null;
            }

            if (_devices?.GpuDevice is not { HasFrameEvidence: true } gpu)
            {
                return null;
            }

            try
            {
                var frame = gpu.CaptureFrame();
                var bgra = PSXRecomp.Infrastructure.Runtime.Gpu.ScreenshotCapture.ToBgra32(frame, gpu);
                return bgra.Length == 0 ? null : (bgra, frame.Width, frame.Height);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                // Observation only: a pump that outlived a timed-out run may still own the device graph. Never let the
                // frame read change the run's classified result.
                Trace.WriteLine($"Frame capture failed: {exception}");
                return null;
            }
        }

        public HostTransferBridge(
            Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime>? biosRuntimeFactory,
            IReadOnlySet<uint> blockEntryPcs,
            Action<PsxDeviceGraph>? configureDevices,
            BiosExceptionChain? exceptionChain,
            MixedFallbackOptions? mixedFallback,
            IReadOnlyList<uint> imageWords,
            uint imageLoadAddress,
            MemoryCardSlotConfiguration? memoryCardSlots,
            bool guestFirmware,
            Action<InterpreterTitleExecutionEngine, uint>? fallbackFetchObserver,
            ICdSectorSource? disc,
            Action<MixedFallbackTransition>? fallbackTransitionObserver,
            LoadedCodeTable loadedCode,
            LoadedCodeMismatchDiagnostics? loadedCodeMismatch)
        {
            _fallbackTransitionObserver = fallbackTransitionObserver;
            _disc = disc;
            _loadedCode = loadedCode;
            _loadedCodeMismatch = loadedCodeMismatch;
            _guestFirmware = guestFirmware;
            _fallbackFetchObserver = fallbackFetchObserver;
            _memoryCardSlots = memoryCardSlots;
            _mixedFallback = mixedFallback;
            _imageWords = imageWords;
            _imageLoadAddress = imageLoadAddress;
            _biosRuntimeFactory = biosRuntimeFactory;
            _blockEntryPcs = blockEntryPcs;
            _configureDevices = configureDevices;
            _exceptionChain = exceptionChain;
            _resourceLifetime = new PumpOwnedResource(() =>
            {
                _fallback?.Dispose();
                _devices?.Dispose();
            });
        }

        public void Attach(TextReader fromArtifact, TextWriter toArtifact)
        {
            _fromArtifact = fromArtifact;
            _toArtifact = toArtifact;
        }

        public bool TryHandle(string line)
        {
            if (Profile is null) return TryHandleCore(line);
            Profile.Begin(line);
            try { return TryHandleCore(line); }
            finally { Profile.End(); }
        }

        private bool TryHandleCore(string line)
        {
            var trimmed = line.TrimEnd();
            if (trimmed == RecompiledArtifactCodeGen.ProtocolInitLine)
            {
                // The existing Runtime device graph the artifact's non-RAM accesses are
                // relayed to (Issue #678). Guest RAM is the artifact's alone: the graph's own
                // native RAM is never used for it, and a device that moves data into RAM
                // (CD-ROM DMA3) does so through _deviceRam, i.e. into artifact_ram (Issue #679).
                _deviceRam = new ArtifactDeviceRam(ReadPhysicalByte, WritePhysicalByte);
                _devices = new PsxDeviceGraph(_deviceRam, _memoryCardSlots, _disc, _guestFirmware);
                _configureDevices?.Invoke(_devices);
                // The same wiring the interpreter engine builds (InterpreterTitleExecutionEngine.Load):
                // device time, order and interrupt delivery to the controller are the existing
                // scheduler's, fed by the guest time the artifact reports.
                _scheduler = _devices.CreateScheduler();
                // Issue #774 (B-7): the MDEC event observer reports this scheduler's time. Diagnostic only.
                _devices.Mdec.CycleSource = () => _scheduler?.ElapsedCycles ?? 0;
                // Issue #818: VBlank is raised inside AdvanceExact on the pump thread, the one thread that owns the GPU.
                if (LiveFrames is { } liveFrames)
                {
                    var livePublisher = new LiveFrameVBlankPublisher(
                        liveFrames,
                        () => _devices!.GpuDevice,
                        exception => Trace.WriteLine($"Live frame publishing stopped for this run: {exception}"),
                        // Every live read goes through the run's gate, which refuses it once a stop was requested (fails closed without one).
                        action => FrameReads is { } gate && StopNow is { } stopNow && gate.TryRead(stopNow, action));
                    _scheduler.VblankCallback = _ => livePublisher.OnVBlank();
                }
                _biosRuntime = _biosRuntimeFactory?.Invoke(new GuestMemoryReader(ReadPhysicalByte), new GuestMemoryWriter(WritePhysicalByte));
                (_biosRuntime as IDeviceBiosRuntime)?.AttachDevices(_devices);
                if (_mixedFallback is not null)
                {
                    // Mixed execution (Issue #693): the interpreter steps this graph's own core, so the devices and scheduler
                    // above stay the only ones; only RAM and CPU state are copied.
                    _fallback = new ArtifactFallbackSession(
                        _mixedFallback, _imageWords, _imageLoadAddress, _blockEntryPcs, _devices, _scheduler, _deviceRam,
                        _biosRuntimeFactory, _exceptionChain, Send, ReadReply, _guestFirmware, _fallbackFetchObserver, _fallbackTransitionObserver, _loadedCode, _loadedCodeMismatch);
                }
                Send(string.Create(CultureInfo.InvariantCulture,
                    $"{RecompiledArtifactCodeGen.ProtocolInterruptLineCommand} {(_devices.Core.GetInterruptPending() ? 1 : 0)}"));
                // The Runtime's construction above already issued whatever R/W
                // seeding it needed; this initial handshake itself claims no pc.
                Decline();
                return true;
            }

            if (trimmed.StartsWith(RecompiledArtifactCodeGen.ProtocolGtePrefix, StringComparison.Ordinal))
            {
                HandleGte(trimmed[RecompiledArtifactCodeGen.ProtocolGtePrefix.Length..]);
                return true;
            }

            if (trimmed.StartsWith(RecompiledArtifactCodeGen.ProtocolCop0AccessPrefix, StringComparison.Ordinal))
            {
                var fields = trimmed[RecompiledArtifactCodeGen.ProtocolCop0AccessPrefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (!_guestFirmware || _devices is null || fields.Length != 3
                    || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var reg)
                    || reg is < 0 or > 31 or 12 or 13 or 14
                    || !uint.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var write) || write > 1
                    || !uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                    throw new InvalidOperationException("Malformed firmware COP0 access.");
                if (write != 0) _devices.Core.SetCop0(reg, value);
                Send(FormattableString.Invariant($"{RecompiledArtifactCodeGen.ProtocolMmioValueReply} {_devices.Core.GetCop0(reg)}"));
                return true;
            }

            if (trimmed.StartsWith(RecompiledArtifactCodeGen.ProtocolMmioReadPrefix, StringComparison.Ordinal))
            {
                HandleMmio(trimmed[RecompiledArtifactCodeGen.ProtocolMmioReadPrefix.Length..], isWrite: false);
                return true;
            }

            if (trimmed.StartsWith(RecompiledArtifactCodeGen.ProtocolMmioWritePrefix, StringComparison.Ordinal))
            {
                HandleMmio(trimmed[RecompiledArtifactCodeGen.ProtocolMmioWritePrefix.Length..], isWrite: true);
                return true;
            }

            if (trimmed.StartsWith(RecompiledArtifactCodeGen.ProtocolRetiredPrefix, StringComparison.Ordinal))
            {
                HandleRetiredMessage(trimmed[RecompiledArtifactCodeGen.ProtocolRetiredPrefix.Length..]);
                return true;
            }

            if (trimmed.StartsWith(RecompiledArtifactCodeGen.ProtocolSyscallPrefix, StringComparison.Ordinal))
            {
                HandleSyscall(trimmed[RecompiledArtifactCodeGen.ProtocolSyscallPrefix.Length..]);
                return true;
            }

            if (trimmed.StartsWith(RecompiledArtifactCodeGen.ProtocolTransferPrefix, StringComparison.Ordinal))
            {
                var transferFields = trimmed[RecompiledArtifactCodeGen.ProtocolTransferPrefix.Length..];
                // Issue #774: every transfer is one handoff, in the order the child sent it. The fallback segment it ran (if
                // any) is read after it is served; the protocol itself is unchanged.
                _fallback?.BeginSegmentRecord();
                HandleTransfer(transferFields);
                if (_unitTrace is not null && ulong.TryParse(transferFields.Split(' ')[0], NumberStyles.None,
                        CultureInfo.InvariantCulture, out var transferPc))
                {
                    _unitTrace.RecordTransfer(transferPc, _fallback?.LastSegment);
                }

                return true;
            }

            return false;
        }

        private void HandleTransfer(string fields)
        {
            var parts = fields.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if ((_biosRuntime is null && !_guestFirmware) || parts.Length != TransferFieldCount)
            {
                // Issue #786 review (round 3, MEDIUM-1): a malformed firmware transfer still names its PC when parts[0] parses. If
                // that PC is RAM-placed code, the refusal is counted as unobserved instead of disappearing.
                if (_guestFirmware && _loadedCodeMismatch is not null && parts.Length > 0
                    && uint.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var malformedPc)
                    && _loadedCode.Contains(malformedPc))
                {
                    _loadedCodeMismatch.NoteUnobservedHandoff();
                }

                Decline();
                return;
            }

            var pc = ParseUInt(parts[0]);
            var gpr = new uint[TitleExecutionRequest.GprCount];
            for (var i = 0; i < gpr.Length; i++) gpr[i] = ParseUInt(parts[i + 1]);

            if (_guestFirmware)
            {
                // Issue #732: the firmware owns its vectors, kernel and exception handler; none of the Runtime's BIOS
                // or kernel-handler routes apply. A PC without a block is the interpreter fallback's, or the run stops.
                HandleFirmwareTransfer(pc, gpr);
                return;
            }

            if (!BiosJumpTables.TryResolveVectorFamily(pc, out var family))
            {
                // Only a vector the artifact reached by its own hardware INT entry is the kernel's: landing
                // on the unpopulated vector by an ordinary transfer is not an exception (Issue #662).
                if (pc == BiosExceptionHandler.GeneralExceptionVector &&
                    BiosExceptionHandler.IsKernelVector(new GuestMemoryReader(ReadPhysicalByte)) &&
                    QueryCop0() is { IntEntry: true } cop0)
                {
                    HandleKernelException(gpr, cop0.Context);
                    return;
                }

                if (pc is GeneralExceptionVectorBev0 or GeneralExceptionVectorBev1)
                {
                    // Issue #680: a hardware INT leaves the artifact at the general exception vector. The
                    // kernel handler is the Runtime's only when the guest left the RAM vector unpopulated
                    // (above); a guest-installed vector or the BEV = 1 ROM vector has no generated block
                    // here, so fail closed with a diagnostic rather than hand a bare unresolved pc to the handoff.
                    ExceptionVectorReached = true;
                    DiagnosticCode = ExceptionVectorUnhandledDiagnosticCode;
                    var iStat = _devices!.Core.ReadInterruptControllerRegister(InterruptStatusAddress);
                    var iMask = _devices.Core.ReadInterruptControllerRegister(InterruptMaskAddress);
                    DiagnosticMessage = $"{ExceptionVectorUnhandledDiagnosticCode}|pc=0x{pc:X8}|I_STAT=0x{iStat:X4}, I_MASK=0x{iMask:X4}|Control reached the general exception " +
                        "vector and the artifact has no generated block there: guest-installed vector code and the BEV = 1 ROM vector are not entered " +
                        "(the Runtime's kernel exception handler serves only an unpopulated RAM vector, #662).";
                    Send($"{RecompiledArtifactCodeGen.ProtocolDecisionPrefix}{(byte)RecompilerIrTerminationReason.UnresolvedIndirectFlow} 0 0 0");
                    return;
                }

                // Issue #693: an in-image indirect target with no compiled block may be run by the interpreter and
                // handed back at a clean block entry. Not a case unless mixed execution was opted into.
                if (_fallback is not null)
                {
                    switch (_fallback.Handle(pc, gpr))
                    {
                        case ArtifactFallbackSession.Decision.Resumed:
                            return;
                        case ArtifactFallbackSession.Decision.Stopped:
                            DiagnosticCode = _fallback.StopCode;
                            DiagnosticMessage = _fallback.StopMessage;
                            Send($"{RecompiledArtifactCodeGen.ProtocolDecisionPrefix}{(byte)RecompilerIrTerminationReason.UnresolvedIndirectFlow} 0 0 0");
                            return;
                    }
                }

                LastDeclinedTransfer = new FallbackDeclinedTransfer(pc, _fallback is not null, _fallback?.RejectReason);
                Decline();
                return;
            }

            var outcome = BiosVectorDispatch.Dispatch(_biosRuntime, family, gpr, _blockingCallWait);
            if (!outcome.ContinueExecution)
            {
                DiagnosticCode = outcome.DiagnosticCode;
                DiagnosticMessage = outcome.DiagnosticMessage;
                Send($"{RecompiledArtifactCodeGen.ProtocolDecisionPrefix}{(byte)RecompilerIrTerminationReason.UnresolvedIndirectFlow} 0 0 0");
                return;
            }

            // Issue #717: a blocking call that has not completed. No instruction retires, so the artifact reports no
            // time: the host advances the devices by one poll, hands the artifact the controller line, and resumes it
            // at the same vector. The artifact's own INT boundary then takes a pending enabled IRQ there (EPC = the
            // vector), and the handler's return re-enters this call. A pending enabled software interrupt stops the wait
            // first, exactly as in the interpreter (the artifact's INT boundary would never take it).
            if (outcome.IsPending)
            {
                var cop0 = QueryCop0().Context;
                if (BiosBlockingCallWait.RefuseSoftwareInterrupt(cop0.Sr, cop0.Cause) is { } refusal)
                {
                    DiagnosticCode = refusal.Code;
                    DiagnosticMessage = refusal.Message;
                    Send($"{RecompiledArtifactCodeGen.ProtocolDecisionPrefix}{(byte)RecompilerIrTerminationReason.UnresolvedIndirectFlow} 0 0 0");
                    return;
                }

                if (AdvanceDevices(BiosBlockingCallWait.PollCycles) is { } failure)
                {
                    DiagnosticCode = failure.Code;
                    DiagnosticMessage = failure.Message;
                    Send($"{RecompiledArtifactCodeGen.ProtocolDecisionPrefix}{(byte)RecompilerIrTerminationReason.UnresolvedIndirectFlow} 0 0 0");
                    return;
                }

                Send(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{RecompiledArtifactCodeGen.ProtocolInterruptLineCommand} {(_devices!.InterruptControllerAdapter.HasPendingInterrupts ? 1 : 0)}"));
                Send(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{RecompiledArtifactCodeGen.ProtocolDecisionPrefix}{(byte)RecompilerIrTerminationReason.Success} {pc} 0 0"));
                return;
            }

            // The artifact can only enter a pc it compiled a block for. A patched
            // target outside that static block table is where this run-once launch
            // runs out of code; record why without treating it as this bridge's own
            // stop reason (Issue #379's parity: the interpreter reaches the same
            // unresolved pc for the same guest condition).
            if (outcome.IsPatchedTarget && !_blockEntryPcs.Contains(outcome.NextPc))
            {
                DiagnosticCode = "BIOS_PATCHED_TARGET_NO_GENERATED_BLOCK";
                DiagnosticMessage =
                    $"Patched jump-table entry names guest address 0x{outcome.NextPc:X8}, which this artifact " +
                    "has no generated block for; the recompiled path stops there instead of entering it.";
            }

            // Issue #664: a BIOS service that replaced the CPU state (B0:17 ReturnFromException) carries its own
            // register file, HI/LO, restored SR and continuation PC — the artifact's own protocol applies each
            // part, and the decision line then resumes at the saved EPC rather than the call's $ra.
            if (outcome.CpuState is { } cpuState)
            {
                ApplyCpuState(gpr, cpuState);
            }

            // Issue #763: only the plain HLE return (NextPc == $ra) is the host's own `jr ra`, a runtime-discovered target.
            // A patched jump-table target and a replaced CPU state (B0:17) keep their fail-closed provenance.
            SendResume(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{RecompiledArtifactCodeGen.ProtocolDecisionPrefix}{(byte)RecompilerIrTerminationReason.Success} {outcome.NextPc} " +
                    $"{(outcome.ReturnValue is null ? 0 : 1)} {outcome.ReturnValue ?? 0}"),
                indirect: !outcome.IsPatchedTarget && outcome.CpuState is null);
        }

        /// <summary>
        /// Sends a successful resume decision. An <paramref name="indirect"/> one is the host's own register-indirect jump
        /// (a plain HLE <c>jr ra</c>, or the kernel exception handler's <c>jr k0</c>): it is marked with <c>J</c>. The artifact
        /// accepts nothing between J and its decision, so the time credit (which <see cref="Send"/> would put before the
        /// decision) goes first and J and the decision are then written back to back (Issue #763).
        /// </summary>
        private void SendResume(string decision, bool indirect)
        {
            if (!indirect)
            {
                Send(decision);
                return;
            }

            if (_scheduler is not null) SendTimeCredit();
            WriteLine(RecompiledArtifactCodeGen.ProtocolIndirectResumeCommand);
            WriteLine(decision);
        }

        private void HandleFirmwareTransfer(uint pc, uint[] gpr)
        {
            switch (_fallback?.Handle(pc, gpr))
            {
                case ArtifactFallbackSession.Decision.Resumed:
                    return;
                case ArtifactFallbackSession.Decision.Stopped:
                    DiagnosticCode = _fallback!.StopCode;
                    DiagnosticMessage = _fallback.StopMessage;
                    Send($"{RecompiledArtifactCodeGen.ProtocolDecisionPrefix}{(byte)RecompilerIrTerminationReason.UnresolvedIndirectFlow} 0 0 0");
                    return;
                default:
                    // Issue #786 review MAJOR-1: without a fallback session nothing else sees the refusal, so it is counted here.
                    // With one, the session already counted its own declines.
                    if (_fallback is null && _loadedCodeMismatch is not null && _loadedCode.Contains(pc))
                    {
                        _loadedCodeMismatch.NoteUnobservedHandoff();
                    }

                    LastDeclinedTransfer = new FallbackDeclinedTransfer(pc, _fallback is not null, _fallback?.RejectReason);
                    Decline();
                    return;
            }
        }

        private readonly record struct Cop0Query(BiosExceptionContext Context, bool IntEntry);

        /// <summary>
        /// The artifact's exception state and whether the vector it is at was reached by its own hardware INT entry
        /// (the provenance; CAUSE/IRQ state alone cannot prove it, Issue #662).
        /// </summary>
        private Cop0Query QueryCop0()
        {
            Send(RecompiledArtifactCodeGen.ProtocolCop0QueryCommand);
            var reply = ReadReply();
            var fields = reply.StartsWith(RecompiledArtifactCodeGen.ProtocolCop0ReplyPrefix, StringComparison.Ordinal)
                ? reply[RecompiledArtifactCodeGen.ProtocolCop0ReplyPrefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                : [];
            if (fields.Length != 6)
            {
                throw new ProtocolFaultException(ArtifactProtocolDiagnostics.ReplyMismatch(RecompiledArtifactCodeGen.ProtocolCop0ReplyPrefix, reply));
            }

            // intEntry is a boolean on the wire: exactly "0" or "1"; anything else fails closed, never reads as false.
            var intEntry = fields[5] switch
            {
                "0" => false,
                "1" => true,
                _ => throw new ProtocolFaultException(ArtifactProtocolDiagnostics.MalformedPayload("intEntry", reply)),
            };

            return new Cop0Query(
                new BiosExceptionContext(ParseUInt(fields[0]), ParseUInt(fields[1]), ParseUInt(fields[2]), ParseUInt(fields[3]), ParseUInt(fields[4])),
                intEntry);
        }

        /// <summary>
        /// The artifact reached the unpopulated RAM general exception vector after taking an INT (Issue #662).
        /// The CPU state is the artifact's: it is read with <c>E</c>, the shared kernel exception handler decides,
        /// and a handled exception is applied back to the artifact's CPU (registers, HI/LO, RFE) before the
        /// decision. An unhandled one stops the run with the contract's diagnostic.
        /// </summary>
        private void HandleKernelException(uint[] gpr, BiosExceptionContext context)
        {
            var outcome = BiosExceptionHandler.Handle(
                new GuestMemoryReader(ReadPhysicalByte),
                new GuestMemoryWriter(WritePhysicalByte),
                _devices!.InterruptControllerAdapter,
                gpr,
                context,
                _exceptionChain);
            if (!outcome.Handled)
            {
                DiagnosticCode = outcome.DiagnosticCode;
                DiagnosticMessage = outcome.DiagnosticMessage;
                Send($"{RecompiledArtifactCodeGen.ProtocolDecisionPrefix}{(byte)RecompilerIrTerminationReason.UnresolvedIndirectFlow} 0 0 0");
                return;
            }

            ApplyCpuState(gpr, outcome.CpuState);

            // The handler's return to EPC is the real kernel's `jr k0; rfe`: a runtime-resolved target, so the fallback may take it.
            SendResume(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{RecompiledArtifactCodeGen.ProtocolDecisionPrefix}{(byte)RecompilerIrTerminationReason.Success} {outcome.NextPc} 0 0"),
                indirect: true);
        }

        /// <summary>
        /// Writes a CPU-state replacement onto the artifact's own machine: the register file, HI/LO, the
        /// restored SR followed by the RFE pop, and then the controller's interrupt line as the just-restored
        /// SR judges it (Issue #664).
        /// </summary>
        /// <remarks>
        /// One implementation serves both routes into a replaced CPU state — the kernel exception completion
        /// and a BIOS service that replaces the state (B0:17) — so the generated host cannot restore a
        /// different subset than the interpreter does. The artifact's own protocol carries each part
        /// separately; the continuation PC then rides the usual decision line.
        /// </remarks>
        private void ApplyCpuState(uint[] gpr, BiosCpuStateMutation cpuState)
        {
            for (var i = BiosCpuStateMutation.FirstRestoredGpr; i < gpr.Length; i++)
            {
                if (cpuState.Gpr[i] != gpr[i])
                {
                    Send(string.Create(CultureInfo.InvariantCulture, $"{RecompiledArtifactCodeGen.ProtocolGprCommand} {i} {cpuState.Gpr[i]}"));
                }
            }

            Send(string.Create(CultureInfo.InvariantCulture, $"{RecompiledArtifactCodeGen.ProtocolHiLoCommand} {cpuState.Hi} {cpuState.Lo}"));
            if (cpuState.RestoredSr is uint sr)
            {
                Send(string.Create(CultureInfo.InvariantCulture, $"{RecompiledArtifactCodeGen.ProtocolCop0SrCommand} {sr}"));
                Send(RecompiledArtifactCodeGen.ProtocolRfeCommand);
            }

            // A chain may have acknowledged I_STAT, and an RFE changes which interrupts the CPU may now take:
            // the artifact's line is the controller's, not what the last guest-time ack said. It still takes the
            // next INT only if its own SR (just restored) allows it.
            Send(string.Create(
                CultureInfo.InvariantCulture,
                $"{RecompiledArtifactCodeGen.ProtocolInterruptLineCommand} {(_devices!.InterruptControllerAdapter.HasPendingInterrupts ? 1 : 0)}"));
        }

        /// <summary>
        /// A SYSCALL exception offered by the artifact (Issue #663): <c>fault_pc a0 sr</c>.
        /// The shared kernel contract decides; a serviced call answers with the SR the
        /// handler leaves and a resume at the instruction after the SYSCALL, an
        /// unimplemented one stops the run with the contract's diagnostic.
        /// </summary>
        private void HandleSyscall(string fields)
        {
            var parts = fields.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (_biosRuntime is null || parts.Length != 3)
            {
                Decline();
                return;
            }

            var faultPc = ParseUInt(parts[0]);
            var outcome = BiosKernelSyscallDispatch.Dispatch(ParseUInt(parts[1]), ParseUInt(parts[2]));
            if (!outcome.Handled)
            {
                DiagnosticCode = outcome.DiagnosticCode;
                DiagnosticMessage = outcome.DiagnosticMessage;
                Send($"{RecompiledArtifactCodeGen.ProtocolDecisionPrefix}{(byte)RecompilerIrTerminationReason.UnresolvedIndirectFlow} 0 0 0");
                return;
            }

            if (outcome.V0 is uint v0)
            {
                Send(string.Create(CultureInfo.InvariantCulture, $"{RecompiledArtifactCodeGen.ProtocolGprCommand} {(int)R3000aRegister.V0} {v0}"));
            }

            Send(string.Create(
                CultureInfo.InvariantCulture,
                $"{RecompiledArtifactCodeGen.ProtocolCop0SrCommand} {outcome.SrAtReturn}"));
            Send(string.Create(
                CultureInfo.InvariantCulture,
                $"{RecompiledArtifactCodeGen.ProtocolDecisionPrefix}{(byte)RecompilerIrTerminationReason.Success} {unchecked(faultPc + 4u)} 0 0"));
        }

        /// <summary>
        /// One guest MMIO access offered by the artifact (Issue #678): <c>width physical</c>
        /// for a read, <c>width physical value</c> for a write. It is relayed to the
        /// Runtime device graph as one access of that width. A malformed request is a
        /// protocol fault (the pump classifies it as <c>ARTIFACT_HOST_PROTOCOL_FAILED</c>);
        /// an address the Runtime does not model, or a device failure, is refused with
        /// <c>X</c> and a classified diagnostic — never a silent 0 or a dropped write.
        /// </summary>
        private void HandleMmio(string fields, bool isWrite)
        {
            MmioAccesses++;
            var parts = fields.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            uint value = 0;
            if (_devices is null
                || parts.Length != (isWrite ? 3 : 2)
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
                || width is not (1 or 2 or 4)
                || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var physical)
                || (isWrite && !uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out value))
                || (width < 4 && value >> (8 * width) != 0))
            {
                throw new InvalidOperationException(ArtifactProtocolDiagnostics.MalformedPayload("MMIO request", fields));
            }

            LastMmioAccess = new HostMmioAccess(physical, width, isWrite);
            var kind = isWrite ? "write" : "read";
            PsxDeviceAccessStatus status;
            uint result = 0;
            try
            {
                status = isWrite
                    ? _devices.TryWrite(physical, width, value)
                    : _devices.TryRead(physical, width, out result);
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"MMIO device access failed: {exception}");
                Refuse("ARTIFACT_MMIO_DEVICE_FAILED", $"The Runtime device failed a {width}-byte MMIO {kind} at physical 0x{physical:X8}.");
                return;
            }

            if (status != PsxDeviceAccessStatus.Completed)
            {
                Refuse("ARTIFACT_MMIO_UNSUPPORTED", $"The Runtime does not model the {width}-byte MMIO {kind} at physical 0x{physical:X8}.");
                return;
            }

            Send(string.Create(
                CultureInfo.InvariantCulture,
                $"{RecompiledArtifactCodeGen.ProtocolMmioValueReply} {result}"));
        }

        /// <summary>
        /// A GTE access from generated code (Issue #447), applied to this graph's GTE — the one the fallback
        /// interpreter executes against. An unimplemented command is refused and named.
        /// </summary>
        private void HandleGte(string fields)
        {
            var parts = fields.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (_devices is null || parts.Length < 2
                || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var a)
                || (parts[0] is "R" or "W" && a >= 64))
            {
                throw new InvalidOperationException(ArtifactProtocolDiagnostics.MalformedPayload("GTE request", fields));
            }

            var gte = _devices.Gte;
            uint result = 0;
            switch (parts[0])
            {
                case "R":
                    result = a < 32 ? gte.ReadDataRegister((int)a) : gte.ReadControlRegister((int)a - 32);
                    break;
                case "W" when parts.Length == 3 && uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var value):
                    if (a < 32) gte.WriteDataRegister((int)a, value);
                    else gte.WriteControlRegister((int)a - 32, value);
                    break;
                case "C":
                    if (!gte.ExecuteCommand(a))
                    {
                        Refuse("GTE_COMMAND_UNSUPPORTED", GteRefusalMessage(a));
                        return;
                    }

                    break;
                default:
                    throw new InvalidOperationException(ArtifactProtocolDiagnostics.MalformedPayload("GTE request", fields));
            }

            Send(string.Create(CultureInfo.InvariantCulture, $"{RecompiledArtifactCodeGen.ProtocolMmioValueReply} {result}"));
        }

        /// <summary>
        /// Guest time offered by the artifact (Issue #679): <c>count</c> guest instructions retired
        /// since its previous report. The elapsed device time is <c>count × CyclesPerInstruction</c> —
        /// the interpreter's own contract (one retired instruction, one fixed cost), so this
        /// backend and that one advance devices by the same time for the same retired instructions —
        /// handed to the existing <see cref="DeviceScheduler"/> in chunks that fit its 32-bit
        /// argument. A device that moves data into guest RAM during the advance reaches
        /// artifact_ram through <see cref="ArtifactDeviceRam"/>, which the artifact serves while it
        /// waits for the reply. A count that is not a positive integer is a protocol fault (the
        /// artifact never reports 0); a scheduler or device failure refuses the report.
        /// </summary>
        /// <summary>
        /// A guest-time report, optionally fused with the MMIO access that follows it (Issue #768):
        /// <c>count</c> alone, or <c>count R width physical</c> / <c>count W width physical value</c>. A fused request is
        /// handled as the two requests it replaces, in the same order — the report (device advance, credit, ack), then
        /// the access — and a refused report ends it without performing the access, as a refused stand-alone report
        /// did. The replies go out in that order, so the artifact reads exactly the lines it read before.
        /// </summary>
        private void HandleRetiredMessage(string fields)
        {
            var space = fields.IndexOf(' ');
            if (space < 0)
            {
                HandleRetired(fields);
                return;
            }

            // Validated whole before any effect: a malformed line must not advance the devices and then fail.
            if (!FusedRetiredRequest.TryParse(fields, out var retired, out var isWrite, out var access))
            {
                throw new ProtocolFaultException(ArtifactProtocolDiagnostics.MalformedPayload("fused guest-time report", fields));
            }

            // A refused report ends the request without the access, exactly as a refused stand-alone report did.
            if (HandleRetired(retired)) HandleMmio(access, isWrite);
        }

        private bool HandleRetired(string fields)
        {
            TimeReports++;
            if (_scheduler is null
                || _deviceRam is null
                || !ulong.TryParse(fields, NumberStyles.None, CultureInfo.InvariantCulture, out var retired)
                || retired == 0
                || retired > MaxRetiredPerReport)
            {
                throw new ProtocolFaultException(ArtifactProtocolDiagnostics.MalformedPayload("guest-time report", fields));
            }

            // Overflow is a refusal, never a wrapped (shorter) elapsed time.
            if (!TryScale(retired, InterpreterTitleExecutionEngine.CyclesPerInstruction, out var cycles))
            {
                RefuseRetired("ARTIFACT_CYCLES_OVERFLOW", $"{retired} retired instructions overflow the 64-bit device-cycle count.");
                return false;
            }

            if (retired > _issuedCredit)
            {
                RefuseRetired("ARTIFACT_EVENT_DEADLINE_EXCEEDED", "The artifact retired instructions beyond its issued device-event deadline.");
                return false;
            }

            NativeRetiredInstructions += retired;
            var deviceStart = Profile is null ? 0 : Stopwatch.GetTimestamp();
            var advanceFailure = AdvanceDevices(cycles);
            if (Profile is not null) Profile.Device(Stopwatch.GetTimestamp() - deviceStart);
            if (advanceFailure is { } failure)
            {
                RefuseRetired(failure.Code, failure.Message);
                return false;
            }

            // The Interrupt Controller's aggregate line (I_STAT & I_MASK != 0) after the advance, read from the
            // same native core the interpreter's CPU reads it from. Whether the CPU takes it is the artifact's
            // own SR decision at its next dispatch boundary (Issue #680).
            bool interruptLine;
            try
            {
                interruptLine = _devices!.Core.GetInterruptPending();
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"Interrupt controller read failed: {exception}");
                RefuseRetired("ARTIFACT_INTERRUPT_CONTROLLER_FAILED", "The Runtime interrupt controller failed reporting its pending line.");
                return false;
            }

            SendTimeCredit();
            Send(interruptLine
                ? RecompiledArtifactCodeGen.ProtocolRetiredAckInterruptReply
                : RecompiledArtifactCodeGen.ProtocolRetiredAckReply);
            return true;
        }

        private void SendTimeCredit()
        {
            var cycles = Math.Min(_scheduler!.NextEventCycles, uint.MaxValue);
            var instructions = cycles / InterpreterTitleExecutionEngine.CyclesPerInstruction;
            if (instructions == 0) throw new ProtocolFaultException("The scheduler returned no executable retirement credit.");
            _issuedCredit = instructions;
            Send(string.Create(CultureInfo.InvariantCulture, $"T {instructions}"));
        }

        /// <summary>
        /// Advances the existing <see cref="DeviceScheduler"/> at event deadlines, serving device-originated RAM
        /// requests against artifact_ram meanwhile. Null on success, else the classified failure. Shared by
        /// guest-time reports and a pending blocking call's poll (Issue #717).
        /// </summary>
        private (string Code, string Message)? AdvanceDevices(ulong cycles)
        {
            try
            {
                _deviceRam!.BeginServing();
                for (var remaining = cycles; remaining != 0;)
                {
                    var chunk = (uint)Math.Min(remaining, MaxAdvanceCycles);
                    _scheduler!.AdvanceExact(chunk);
                    remaining -= chunk;
                }
            }
            catch (ProtocolFaultException)
            {
                throw;
            }
            catch (ArtifactDeviceRam.UnroutableException exception)
            {
                Trace.WriteLine($"Device RAM access refused: {exception}");
                return ("ARTIFACT_DEVICE_RAM_UNROUTABLE", exception.Message);
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"DeviceScheduler advance failed: {exception}");
                return ("ARTIFACT_SCHEDULER_FAILED", $"The Runtime device scheduler failed advancing {cycles} cycles.");
            }
            finally
            {
                _deviceRam!.EndServing();
            }

            return null;
        }

        private static bool TryScale(ulong count, uint factor, out ulong product)
        {
            try
            {
                product = checked(count * factor);
                return true;
            }
            catch (OverflowException)
            {
                product = 0;
                return false;
            }
        }

        private void RefuseRetired(string code, string message)
        {
            RetiredFailureCode = code;
            RetiredFailureMessage = message;
            Send(RecompiledArtifactCodeGen.ProtocolRetiredRefusedReply);
        }

        private void Refuse(string code, string message)
        {
            MmioFailureCode = code;
            MmioFailureMessage = message;
            Send(RecompiledArtifactCodeGen.ProtocolMmioRefusedReply);
        }

        private void Decline() => Send(RecompiledArtifactCodeGen.ProtocolDeclineReply);

        private byte ReadPhysicalByte(uint physicalAddress)
        {
            Send($"{RecompiledArtifactCodeGen.ProtocolReadCommand} {physicalAddress.ToString(CultureInfo.InvariantCulture)}");
            var reply = ReadReply();
            if (!reply.StartsWith(RecompiledArtifactCodeGen.ProtocolDataPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(ArtifactProtocolDiagnostics.ReplyMismatch(RecompiledArtifactCodeGen.ProtocolDataPrefix, reply));
            }

            return (byte)ParseUInt(reply[RecompiledArtifactCodeGen.ProtocolDataPrefix.Length..].Trim());
        }

        private void WritePhysicalByte(uint physicalAddress, byte value)
        {
            Send($"{RecompiledArtifactCodeGen.ProtocolWriteCommand} {physicalAddress.ToString(CultureInfo.InvariantCulture)} {value.ToString(CultureInfo.InvariantCulture)}");
            // The artifact answers a RAM write with exactly RHOST_OK. Anything else means the write is not
            // known to have reached artifact_ram (BIOS HLE seeding and device DMA both land here), so it
            // is a protocol fault, never a completed write.
            var reply = ReadReply();
            if (!string.Equals(reply, RecompiledArtifactCodeGen.ProtocolWriteAck, StringComparison.Ordinal))
            {
                throw new ProtocolFaultException(ArtifactProtocolDiagnostics.WriteNotAcknowledged(RecompiledArtifactCodeGen.ProtocolWriteAck, reply));
            }
        }

        private string ReadReply() =>
            _fromArtifact?.ReadLine() ?? throw new ProtocolFaultException("The artifact closed its output mid-protocol.");

        private void Send(string line)
        {
            if (_scheduler is not null && (line == RecompiledArtifactCodeGen.ProtocolDeclineReply || line.StartsWith(RecompiledArtifactCodeGen.ProtocolDecisionPrefix, StringComparison.Ordinal)))
                SendTimeCredit();
            WriteLine(line);
        }

        private void WriteLine(string line)
        {
            var toArtifact = _toArtifact ?? throw new ProtocolFaultException("The host-transfer bridge is not attached to a process.");
            if (Profile is null)
            {
                toArtifact.WriteLine(line);
                return;
            }

            var start = Stopwatch.GetTimestamp();
            toArtifact.WriteLine(line);
            Profile.Wrote(line.Length + toArtifact.NewLine.Length, Stopwatch.GetTimestamp() - start); // AutoFlush: one syscall per line
        }

        private static uint ParseUInt(string value) => uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

        /// <summary>The largest single <see cref="DeviceScheduler.Advance"/> argument: well inside <see cref="uint"/>, so no chunk can wrap.</summary>
        private const ulong MaxAdvanceCycles = int.MaxValue;

        /// <summary>The most one report can honestly carry: the artifact's 32-bit dispatch budget times the largest fused block (three instructions).</summary>
        private const ulong MaxRetiredPerReport = 3ul * uint.MaxValue;

        /// <summary>The wire protocol itself broke (a malformed or missing message): never a device or guest failure.</summary>
        private sealed class ProtocolFaultException(string message) : InvalidOperationException(message);
    }
}