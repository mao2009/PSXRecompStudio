using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;

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
public sealed class RecompiledHostExecutionEngine : IRecompiledExecutionEngine
{
    public const string EngineName = "recompiled-host-artifact";

    /// <summary>Mixed-execution evidence of the last run, or null when mixed execution was not enabled (Issue #693).</summary>
    public MixedFallbackEvidence? FallbackEvidence { get; private set; }

    /// <summary>Wall-clock costs of the last run's mixed execution; measurement only (Issue #693).</summary>
    public MixedFallbackTimings? FallbackTimings { get; private set; }

    /// <summary>Reported when control reaches the general exception vector with no generated code there (Issue #680).</summary>
    public const string ExceptionVectorUnhandledDiagnosticCode = "ARTIFACT_EXCEPTION_VECTOR_UNHANDLED";

    private const int RunTimeoutMs = 30000;
    private const int PumpDrainTimeoutMs = 5000;

    private readonly Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime>? _biosRuntimeFactory;
    private readonly Action<PsxDeviceGraph>? _configureDevices;
    private readonly BiosExceptionChain? _exceptionChain;
    private readonly IReadOnlySet<uint> _blockEntryPcs;
    private readonly MixedFallbackOptions? _mixedFallback;
    private readonly string _binaryPath;
    private readonly string _inputPath;
    private readonly string _imagePath;
    private readonly uint _imageLoadAddress;
    private readonly IReadOnlyList<uint> _imageWords;

    private IReadOnlyList<RecompilerInitialMemoryItem> _initialMemory =
        Array.Empty<RecompilerInitialMemoryItem>();

    private bool _loaded;
    private bool _ran;

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
        MixedFallbackOptions? mixedFallback = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(imageWords);
        ArgumentNullException.ThrowIfNull(buildService);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (imageWords.Count == 0)
        {
            // Mirrors InterpreterTitleExecutionEngine: no program image, nothing to run.
            throw new ArgumentException("The artifact needs a non-empty program image.", nameof(imageWords));
        }

        _biosRuntimeFactory = biosRuntimeFactory;
        _configureDevices = configureDevices;
        if (mixedFallback is { IsValid: false })
        {
            throw new ArgumentException("Mixed fallback needs positive instruction and transition budgets.", nameof(mixedFallback));
        }

        _mixedFallback = mixedFallback;
        _exceptionChain = exceptionChain;
        _blockEntryPcs = program.Blocks.Select(static block => block.EntryPc).ToHashSet();
        _inputPath = Path.Combine(outputDirectory, "artifact-input.txt");
        _imagePath = Path.Combine(outputDirectory, "artifact-image.bin");
        _imageLoadAddress = imageLoadAddress;
        _imageWords = imageWords;

        var dispatch = RecompilerHostCodeGen.Generate(program);
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

        // The artifact must run from the state THIS call names — segmentRequest's
        // PC, GPRs, HI/LO and budget — not the state Load was handed, so the input
        // file is written here, immediately before launch. InitialMemory is the
        // sole input a TitleExecutionSegmentRequest carries no field for: it is
        // retained from Load and re-applied to this rewrite.
        WriteInputFile(segmentRequest.Pc, segmentRequest.Gpr, segmentRequest.Hi, segmentRequest.Lo, _initialMemory, segmentRequest.Budget);

        using var bridge = _biosRuntimeFactory is null ? null : new HostTransferBridge(_biosRuntimeFactory, _blockEntryPcs, _configureDevices, _exceptionChain, _mixedFallback, _imageWords, _imageLoadAddress);
        var arguments = new List<string>(3) { _inputPath, _imagePath };
        if (bridge is not null)
        {
            arguments.Add(RecompiledArtifactCodeGen.HostTransferFlag);
        }

        var (exit, stdout, _, hostProtocolFaulted) = RunProcess(_binaryPath, arguments, RunTimeoutMs, out var timedOut, bridge);

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
                RecompilerExecutionStatus.TimedOut, "ARTIFACT_TIMEOUT", "The runnable artifact exceeded its bounded execution timeout.");
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

        FallbackEvidence = bridge?.FallbackEvidence;
        FallbackTimings = bridge?.FallbackTimings;
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
            var message = stdout.Length <= 2000 ? stdout : stdout[..2000] + "...";
            return RecompilerExecutionResult.Failed(RecompilerExecutionStatus.MalformedResult, "MALFORMED_ARTIFACT_SNAPSHOT", message);
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

    public void Dispose()
    {
        GC.SuppressFinalize(this);
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
    /// Runs one process, optionally pumping <paramref name="bridge"/>'s
    /// host-transfer protocol. Arguments travel as a collection through
    /// <see cref="ProcessStartInfo.ArgumentList"/> — never as a hand-quoted raw
    /// <see cref="ProcessStartInfo.Arguments"/> string — so an input path
    /// containing spaces or quotes is received by the artifact unchanged as one
    /// argument.
    /// </summary>
    /// <returns>The process exit code, captured stdout/stderr, and whether the
    /// host-transfer pump faulted. A pump fault (a BIOS factory exception, a
    /// protocol parse failure, a stdin/stdout read-or-write failure, or a
    /// transfer send failure) is a classified protocol failure, not a
    /// caller-visible exception: the child is killed and drained here and the
    /// fault indicator lets <see cref="RunSegment"/> map it to
    /// <c>ARTIFACT_HOST_PROTOCOL_FAILED</c> without leaking the underlying
    /// exception into the result contract.</returns>
    private static (int ExitCode, string Stdout, string Stderr, bool HostProtocolFaulted) RunProcess(
        string fileName, IReadOnlyList<string> arguments, int timeoutMs, out bool timedOut, HostTransferBridge? bridge)
    {
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

        using var process = Process.Start(psi)!;
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (bridge is null)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                TryKillTree(process);
                timedOut = true;
                return (int.MinValue, DrainWithinCleanupBudget(stdoutTask), DrainWithinCleanupBudget(stderrTask), false);
            }

            process.WaitForExit();
            timedOut = false;
            return (process.ExitCode, stdoutTask.Result, stderrTask.Result, false);
        }

        process.StandardInput.AutoFlush = true;
        bridge.Attach(process.StandardOutput, process.StandardInput);

        var output = new StringBuilder();
        var pump = Task.Run(() =>
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                if (!bridge.TryHandle(line))
                {
                    output.AppendLine(line);
                }
            }
        });

        bool pumpCompleted;
        try
        {
            pumpCompleted = pump.Wait(timeoutMs);
        }
        catch (Exception exception)
        {
            // The host-transfer pump faulted. Kill and drain the child, keep the
            // exception out of the result contract (log it locally for
            // diagnostics), and report the fault so RunSegment classifies it as
            // ARTIFACT_HOST_PROTOCOL_FAILED instead of letting a raw exception
            // escape the launcher. This is distinct from a timeout or a normal
            // process-failure classification.
            TryKillTree(process);
            try { pump.Wait(PumpDrainTimeoutMs); } catch { /* the fault verdict below stands regardless */ }
            Trace.WriteLine($"Host-transfer pump failed: {exception}");
            timedOut = false;
            return (int.MinValue, output.ToString(), DrainWithinCleanupBudget(stderrTask), true);
        }

        if (!pumpCompleted)
        {
            TryKillTree(process);
            try { pump.Wait(PumpDrainTimeoutMs); } catch { /* the timeout verdict below stands regardless */ }
            timedOut = true;
            return (int.MinValue, output.ToString(), DrainWithinCleanupBudget(stderrTask), false);
        }

        if (!process.WaitForExit(timeoutMs))
        {
            TryKillTree(process);
            timedOut = true;
            return (int.MinValue, output.ToString(), DrainWithinCleanupBudget(stderrTask), false);
        }

        process.WaitForExit();
        timedOut = false;
        return (process.ExitCode, output.ToString(), stderrTask.Result, false);
    }

    /// <summary>
    /// Consumes a redirected stream within a bounded budget for the pump-fault
    /// path. After the child is killed, a descendant that inherited the pipe
    /// handle could otherwise keep the stream open past the promised drain
    /// budget; anything that does not close in time is dropped. Diagnostics only —
    /// the fault verdict stands and these bytes never enter the result contract.
    /// </summary>
    private static string DrainWithinCleanupBudget(Task<string> readTask)
    {
        try
        {
            return readTask.Wait(PumpDrainTimeoutMs) && readTask.IsCompletedSuccessfully
                ? readTask.Result
                : string.Empty;
        }
        catch
        {
            // A stream read failure on the drain path is likewise diagnostic-only.
            return string.Empty;
        }
    }

    private static void TryKillTree(Process process)
    {
        try { process.Kill(true); } catch (InvalidOperationException) { }
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

        private readonly Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime> _biosRuntimeFactory;
        private readonly IReadOnlySet<uint> _blockEntryPcs;

        private TextReader? _fromArtifact;
        private TextWriter? _toArtifact;
        private IBiosRuntime? _biosRuntime;
        private PsxDeviceGraph? _devices;
        private DeviceScheduler? _scheduler;
        private ArtifactDeviceRam? _deviceRam;
        private readonly Action<PsxDeviceGraph>? _configureDevices;
        private readonly BiosExceptionChain? _exceptionChain;
        private readonly MixedFallbackOptions? _mixedFallback;
        private readonly IReadOnlyList<uint> _imageWords;
        private readonly uint _imageLoadAddress;
        private ArtifactFallbackSession? _fallback;

        /// <summary>Evidence of this run's mixed execution, or null when it was not enabled (Issue #693).</summary>
        public MixedFallbackEvidence? FallbackEvidence => _fallback?.Evidence;

        /// <summary>Wall-clock costs of this run's mixed execution (Issue #693).</summary>
        public MixedFallbackTimings? FallbackTimings => _fallback?.Timings;

        /// <summary>Why the host refused the artifact's guest-time report, or null when it did not (Issue #679).</summary>
        public string? RetiredFailureCode { get; private set; }
        public string? RetiredFailureMessage { get; private set; }

        public string? DiagnosticCode { get; private set; }
        public string? DiagnosticMessage { get; private set; }

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

        public void Dispose()
        {
            _fallback?.Dispose();
            _devices?.Dispose();
        }

        public HostTransferBridge(
            Func<IGuestMemoryReader, IGuestMemoryWriter, IBiosRuntime> biosRuntimeFactory,
            IReadOnlySet<uint> blockEntryPcs,
            Action<PsxDeviceGraph>? configureDevices,
            BiosExceptionChain? exceptionChain,
            MixedFallbackOptions? mixedFallback,
            IReadOnlyList<uint> imageWords,
            uint imageLoadAddress)
        {
            _mixedFallback = mixedFallback;
            _imageWords = imageWords;
            _imageLoadAddress = imageLoadAddress;
            _biosRuntimeFactory = biosRuntimeFactory;
            _blockEntryPcs = blockEntryPcs;
            _configureDevices = configureDevices;
            _exceptionChain = exceptionChain;
        }

        public void Attach(TextReader fromArtifact, TextWriter toArtifact)
        {
            _fromArtifact = fromArtifact;
            _toArtifact = toArtifact;
        }

        public bool TryHandle(string line)
        {
            var trimmed = line.TrimEnd();
            if (trimmed == RecompiledArtifactCodeGen.ProtocolInitLine)
            {
                // The existing Runtime device graph the artifact's non-RAM accesses are
                // relayed to (Issue #678). Guest RAM is the artifact's alone: the graph's own
                // native RAM is never used for it, and a device that moves data into RAM
                // (CD-ROM DMA3) does so through _deviceRam, i.e. into artifact_ram (Issue #679).
                _deviceRam = new ArtifactDeviceRam(ReadPhysicalByte, WritePhysicalByte);
                _devices = new PsxDeviceGraph(_deviceRam);
                _configureDevices?.Invoke(_devices);
                // The same wiring the interpreter engine builds (InterpreterTitleExecutionEngine.Load):
                // device time, order and interrupt delivery to the controller are the existing
                // scheduler's, fed by the guest time the artifact reports.
                _scheduler = new DeviceScheduler(
                    _devices.Core, _devices.InterruptControllerAdapter, _devices.GpuAdapter, _devices.CdRomDevice, _devices.CdRomDmaTransfer);
                _biosRuntime = _biosRuntimeFactory(new GuestMemoryReader(ReadPhysicalByte), new GuestMemoryWriter(WritePhysicalByte));
                if (_mixedFallback is not null)
                {
                    // Mixed execution (Issue #693): the interpreter steps this graph's own core, so the devices and scheduler
                    // above stay the only ones; only RAM and CPU state are copied.
                    _fallback = new ArtifactFallbackSession(
                        _mixedFallback, _imageWords, _imageLoadAddress, _blockEntryPcs, _devices, _scheduler, _deviceRam,
                        _biosRuntimeFactory, _exceptionChain, Send, ReadReply);
                }
                // The Runtime's construction above already issued whatever R/W
                // seeding it needed; this initial handshake itself claims no pc.
                Decline();
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
                HandleRetired(trimmed[RecompiledArtifactCodeGen.ProtocolRetiredPrefix.Length..]);
                return true;
            }

            if (trimmed.StartsWith(RecompiledArtifactCodeGen.ProtocolSyscallPrefix, StringComparison.Ordinal))
            {
                HandleSyscall(trimmed[RecompiledArtifactCodeGen.ProtocolSyscallPrefix.Length..]);
                return true;
            }

            if (trimmed.StartsWith(RecompiledArtifactCodeGen.ProtocolTransferPrefix, StringComparison.Ordinal))
            {
                HandleTransfer(trimmed[RecompiledArtifactCodeGen.ProtocolTransferPrefix.Length..]);
                return true;
            }

            return false;
        }

        private void HandleTransfer(string fields)
        {
            var parts = fields.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (_biosRuntime is null || parts.Length != TransferFieldCount)
            {
                Decline();
                return;
            }

            var pc = ParseUInt(parts[0]);
            var gpr = new uint[TitleExecutionRequest.GprCount];
            for (var i = 0; i < gpr.Length; i++) gpr[i] = ParseUInt(parts[i + 1]);

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

                Decline();
                return;
            }

            var outcome = BiosVectorDispatch.Dispatch(_biosRuntime, family, gpr);
            if (!outcome.ContinueExecution)
            {
                DiagnosticCode = outcome.DiagnosticCode;
                DiagnosticMessage = outcome.DiagnosticMessage;
                Send($"{RecompiledArtifactCodeGen.ProtocolDecisionPrefix}{(byte)RecompilerIrTerminationReason.UnresolvedIndirectFlow} 0 0 0");
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

            Send(string.Create(
                CultureInfo.InvariantCulture,
                $"{RecompiledArtifactCodeGen.ProtocolDecisionPrefix}{(byte)RecompilerIrTerminationReason.Success} {outcome.NextPc} " +
                $"{(outcome.ReturnValue is null ? 0 : 1)} {outcome.ReturnValue ?? 0}"));
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
                throw new ProtocolFaultException($"Expected a '{RecompiledArtifactCodeGen.ProtocolCop0ReplyPrefix}' reply, received '{reply}'.");
            }

            // intEntry is a boolean on the wire: exactly "0" or "1"; anything else fails closed, never reads as false.
            var intEntry = fields[5] switch
            {
                "0" => false,
                "1" => true,
                _ => throw new ProtocolFaultException($"Malformed intEntry in '{reply}'."),
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

            for (var i = 1; i < gpr.Length; i++)
            {
                if (outcome.Gpr[i] != gpr[i])
                {
                    Send(string.Create(CultureInfo.InvariantCulture, $"{RecompiledArtifactCodeGen.ProtocolGprCommand} {i} {outcome.Gpr[i]}"));
                }
            }

            if (outcome.Hi != context.Hi || outcome.Lo != context.Lo)
            {
                Send(string.Create(CultureInfo.InvariantCulture, $"{RecompiledArtifactCodeGen.ProtocolHiLoCommand} {outcome.Hi} {outcome.Lo}"));
            }

            if (outcome.RestoredSr is uint sr)
            {
                Send(string.Create(CultureInfo.InvariantCulture, $"{RecompiledArtifactCodeGen.ProtocolCop0SrCommand} {sr}"));
                Send(RecompiledArtifactCodeGen.ProtocolRfeCommand);
            }

            // The chain may have acknowledged I_STAT: the artifact's line is the controller's now, not what the last
            // guest-time ack said. The artifact still takes the next INT only if its own SR (just restored) allows it.
            Send(string.Create(
                CultureInfo.InvariantCulture,
                $"{RecompiledArtifactCodeGen.ProtocolInterruptLineCommand} {(_devices.InterruptControllerAdapter.HasPendingInterrupts ? 1 : 0)}"));

            Send(string.Create(
                CultureInfo.InvariantCulture,
                $"{RecompiledArtifactCodeGen.ProtocolDecisionPrefix}{(byte)RecompilerIrTerminationReason.Success} {outcome.NextPc} 0 0"));
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
                throw new InvalidOperationException($"Malformed MMIO request '{fields}'.");
            }

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
        private void HandleRetired(string fields)
        {
            if (_scheduler is null
                || _deviceRam is null
                || !ulong.TryParse(fields, NumberStyles.None, CultureInfo.InvariantCulture, out var retired)
                || retired == 0
                || retired > MaxRetiredPerReport)
            {
                throw new ProtocolFaultException($"Malformed guest-time report '{fields}'.");
            }

            // Overflow is a refusal, never a wrapped (shorter) elapsed time.
            if (!TryScale(retired, InterpreterTitleExecutionEngine.CyclesPerInstruction, out var cycles))
            {
                RefuseRetired("ARTIFACT_CYCLES_OVERFLOW", $"{retired} retired instructions overflow the 64-bit device-cycle count.");
                return;
            }

            try
            {
                _deviceRam.BeginServing();
                for (var remaining = cycles; remaining != 0;)
                {
                    var chunk = (uint)Math.Min(remaining, MaxAdvanceCycles);
                    _scheduler.Advance(chunk);
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
                RefuseRetired("ARTIFACT_DEVICE_RAM_UNROUTABLE", exception.Message);
                return;
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"DeviceScheduler advance failed: {exception}");
                RefuseRetired("ARTIFACT_SCHEDULER_FAILED", $"The Runtime device scheduler failed advancing {cycles} cycles.");
                return;
            }
            finally
            {
                _deviceRam.EndServing();
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
                return;
            }

            Send(interruptLine
                ? RecompiledArtifactCodeGen.ProtocolRetiredAckInterruptReply
                : RecompiledArtifactCodeGen.ProtocolRetiredAckReply);
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
                throw new InvalidOperationException($"Expected a '{RecompiledArtifactCodeGen.ProtocolDataPrefix}' reply, received '{reply}'.");
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
                throw new ProtocolFaultException($"Expected a '{RecompiledArtifactCodeGen.ProtocolWriteAck}' reply to a RAM write, received '{reply}'.");
            }
        }

        private string ReadReply() =>
            _fromArtifact?.ReadLine() ?? throw new ProtocolFaultException("The artifact closed its output mid-protocol.");

        private void Send(string line) =>
            (_toArtifact ?? throw new ProtocolFaultException("The host-transfer bridge is not attached to a process.")).WriteLine(line);

        private static uint ParseUInt(string value) => uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

        /// <summary>The largest single <see cref="DeviceScheduler.Advance"/> argument: well inside <see cref="uint"/>, so no chunk can wrap.</summary>
        private const ulong MaxAdvanceCycles = int.MaxValue;

        /// <summary>The most one report can honestly carry: the artifact's 32-bit dispatch budget times the largest fused block (three instructions).</summary>
        private const ulong MaxRetiredPerReport = 3ul * uint.MaxValue;

        /// <summary>The wire protocol itself broke (a malformed or missing message): never a device or guest failure.</summary>
        private sealed class ProtocolFaultException(string message) : InvalidOperationException(message);
    }
}
