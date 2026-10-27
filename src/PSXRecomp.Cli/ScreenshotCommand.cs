using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Diagnostics;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Core.Runtime.CdRom;
using PSXRecomp.Core.Runtime.Gpu;
using PSXRecomp.Infrastructure;
using PSXRecomp.Infrastructure.Execution;
using PSXRecomp.Infrastructure.Runtime.Gpu;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// <c>psxrecomp screenshot</c>: runs a title headless through the production interpreter
/// and captures PNG screenshots at specified VBlank intervals. This is a pure interpreter
/// path (no recompiled artifact) designed for visual regression testing and evidence
/// collection from real game execution.
/// Supports both HLE BIOS (default) and OpenBIOS (real BIOS ROM) execution paths.
/// </summary>
[Infrastructure]
public static class ScreenshotCommand
{
    internal const string Usage = "usage: psxrecomp screenshot <input.exe|input.chd> --output <dir> [--openbios <rom.bin>] [--disc <image.chd|image.bin>] [--segment-budget <n>] [--segments <n>] [--vblank-interval <n>] [--max-screenshots <n>] [--start-vblank <n>] [--timeout <seconds>] [--json]";

    /// <summary>Default whole-run segment budget for the HLE path (`--segments` overrides it).</summary>
    public const uint DefaultHleSegments = 64u;

    /// <summary>Default whole-run segment budget for the OpenBIOS path (`--segments` overrides it).</summary>
    public const uint DefaultOpenBiosSegments = 100u;

    /// <summary>Default VBlank interval between screenshots.</summary>
    public const uint DefaultVBlankInterval = 300;

    /// <summary>Default maximum number of screenshots.</summary>
    public const int DefaultMaxScreenshots = 10;

    /// <summary>Default first VBlank to capture.</summary>
    public const ulong DefaultStartVBlank = 300;

    /// <summary>Default wall-clock timeout in seconds.</summary>
    public const int DefaultTimeoutSeconds = 300;

    internal static int Run(string[] arguments, TextWriter standardOutput, TextWriter standardError)
    {
        if (!TryParse(arguments, out var parsed, out var error))
        {
            standardError.WriteLine($"psxrecomp screenshot: {error}");
            standardError.WriteLine(Usage);
            return RecompiledArtifactExitCode.Failure;
        }

        if (parsed.Help)
        {
            standardOutput.WriteLine(Usage);
            standardOutput.WriteLine();
            standardOutput.WriteLine("options:");
            standardOutput.WriteLine("  --output <dir>             required: new or empty directory for PNG screenshots (existing files are never overwritten)");
            standardOutput.WriteLine("  --openbios <rom.bin>       optional: OpenBIOS ROM file (512KB) for real BIOS execution");
            standardOutput.WriteLine("  --disc <image.chd|image.bin>  required with --openbios: CD-ROM image for the game");
            standardOutput.WriteLine("  --segment-budget <n>       per-segment instruction budget (default: 100000)");
            standardOutput.WriteLine($"  --segments <n>             whole-run segment budget on both paths; the run stops when it is spent (default: {DefaultHleSegments} HLE, {DefaultOpenBiosSegments} with --openbios)");
            standardOutput.WriteLine("  --vblank-interval <n>      VBlank interval between captures (default: 300)");
            standardOutput.WriteLine("  --max-screenshots <n>      maximum screenshots to capture (default: 10)");
            standardOutput.WriteLine("  --start-vblank <n>         first VBlank to capture (default: 300)");
            standardOutput.WriteLine("  --timeout <seconds>        wall-clock timeout in seconds (default: 300)");
            standardOutput.WriteLine("  --json                     emit machine-readable JSON result");
            standardOutput.WriteLine("  --help, -h                 show this help");
            return RecompiledArtifactExitCode.Success;
        }

        var outputDirectory = Path.GetFullPath(parsed.OutputDirectory!);
        // Owner decision (#798): the output directory must be new or empty. Checked before anything is written, so a refused
        // run leaves every existing file and the manifest untouched and no PNG is ever overwritten.
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            standardError.WriteLine($"psxrecomp screenshot: output directory must be new or empty (it already contains files): {outputDirectory}");
            return RecompiledArtifactExitCode.Failure;
        }

        var segmentBudget = parsed.SegmentBudget ?? 100_000u;
        var segments = parsed.Segments ?? DefaultOpenBiosSegments;
        var hleSegments = parsed.Segments ?? DefaultHleSegments;
        var vblankInterval = parsed.VBlankInterval ?? DefaultVBlankInterval;
        var maxScreenshots = parsed.MaxScreenshots ?? DefaultMaxScreenshots;
        var startVblank = parsed.StartVBlank ?? DefaultStartVBlank;
        var timeout = TimeSpan.FromSeconds(parsed.TimeoutSeconds ?? DefaultTimeoutSeconds);

        try
        {
            string? inputSha256 = null;
            HeadlessRunResult result;

            if (parsed.OpenBiosRomPath is not null)
            {
                // OpenBIOS execution path
                if (parsed.DiscPath is null)
                {
                    standardError.WriteLine("psxrecomp screenshot: --disc is required when using --openbios");
                    return RecompiledArtifactExitCode.Failure;
                }

                var openBiosBytes = File.ReadAllBytes(parsed.OpenBiosRomPath);
                if (openBiosBytes.Length != OpenBiosFirmware.ImageSize)
                {
                    standardError.WriteLine($"psxrecomp screenshot: OpenBIOS ROM must be exactly {OpenBiosFirmware.ImageSize} bytes (got {openBiosBytes.Length})");
                    return RecompiledArtifactExitCode.Failure;
                }

                var openBiosHash = Convert.ToHexString(SHA256.HashData(openBiosBytes)).ToLowerInvariant();
                var firmware = OpenBiosFirmware.FromBytes(openBiosBytes);
                var disc = OpenDisc(parsed.DiscPath);

                result = RunScreenshotCaptureOpenBios(
                    firmware,
                    openBiosHash,
                    disc?.Source,
                    outputDirectory,
                    segmentBudget,
                    segments,
                    vblankInterval,
                    maxScreenshots,
                    startVblank,
                    timeout,
                    standardError);
            }
            else
            {
                // HLE BIOS execution path (existing)
                PsxExeTitleExecution input;
                if (Path.GetExtension(parsed.Input).Equals(".chd", StringComparison.OrdinalIgnoreCase))
                {
                    var resolved = CliInput.LoadWithIdentity(parsed.Input!, outerBudget: hleSegments, segmentBudget);
                    input = resolved.Execution;
                    inputSha256 = resolved.Sha256;
                }
                else
                {
                    input = CliInput.Load(parsed.Input!, outerBudget: hleSegments, segmentBudget);
                }

                result = RunScreenshotCapture(
                    input,
                    outputDirectory,
                    segmentBudget,
                    vblankInterval,
                    maxScreenshots,
                    startVblank,
                    timeout,
                    standardError);
            }

            if (parsed.Json)
            {
                standardOutput.WriteLine(SerializeResult(inputSha256 ?? result.ExecutionResult.EngineName, result));
            }
            else
            {
                WriteHumanResult(result, standardOutput);
            }

            return IsSuccess(result)
                ? RecompiledArtifactExitCode.Success
                : RecompiledArtifactExitCode.Failure;
        }
        catch (Exception ex) when (
            ex is DirectoryNotFoundException or FileNotFoundException
                or UnauthorizedAccessException or IOException or InvalidDataException
                or ArgumentException or InvalidOperationException)
        {
            standardError.WriteLine($"psxrecomp screenshot: {ex.Message}");
            // Setup failed before any capture session existed: still leave a manifest that names the stop (Issue #751).
            // Best effort: a second I/O failure here must not hide the original error (or escape as an unhandled exception).
            try
            {
                ScreenshotCapture.WriteManifestFile(outputDirectory, "setup_failed", 0, 0, TimeSpan.Zero, null, [],
                    ex.GetType().Name);
            }
            catch (Exception secondary) when (secondary is IOException or UnauthorizedAccessException)
            {
                standardError.WriteLine($"psxrecomp screenshot: manifest.json not written: {secondary.Message}");
            }
            return RecompiledArtifactExitCode.Failure;
        }
    }

    private static HeadlessRunResult RunScreenshotCapture(
        PsxExeTitleExecution input,
        string outputDirectory,
        uint segmentBudget,
        uint vblankInterval,
        int maxScreenshots,
        ulong startVblank,
        TimeSpan timeout,
        TextWriter standardError)
    {
        using var engine = new InterpreterTitleExecutionEngine(
            input.InstructionWords,
            input.LoadAddress,
            (reader, writer) => new BiosHleRuntime(new CollectedOutput(), reader, writer));

        return RunScreenshotCaptureHle(
            engine,
            () => (engine.DiagnosticDevices.GpuDevice, engine.Scheduler!),
            new ProgramEndHandoff(input.LoadAddress, input.InstructionWords.Count),
            input.Request,
            segmentBudget,
            outputDirectory,
            vblankInterval,
            maxScreenshots,
            startVblank,
            timeout,
            standardError);
    }

    /// <summary>
    /// HLE capture core over any engine, so the stop rules can be exercised with a scripted engine (review of #751).
    /// <paramref name="devicesAfterLoad"/> reads the GPU and the scheduler the engine uses after its Load.
    /// </summary>
    internal static HeadlessRunResult RunScreenshotCaptureHle(
        IRecompiledExecutionEngine engine,
        Func<(GpuDevice Gpu, DeviceScheduler Scheduler)> devicesAfterLoad,
        ITitleExecutionHandoff? handoff,
        TitleExecutionRequest request,
        uint segmentBudget,
        string outputDirectory,
        uint vblankInterval,
        int maxScreenshots,
        ulong startVblank,
        TimeSpan timeout,
        TextWriter standardError)
    {
        MarkInProgress(outputDirectory);
        var stopwatch = Stopwatch.StartNew();

        var orchestrator = new ExecutionOrchestrator();

        TitleExecutionResult? executionResult = null;
        var stopReason = "Unknown";
        string? exceptionType = null;
        ScreenshotCapture? screenshotCapture = null;
        DeviceScheduler? scheduler = null;

        try
        {
            // One orchestrator run (it loads the engine exactly once). The capture is built after that Load, on the
            // scheduler the loaded engine uses (review of #751: a second Load would replace the scheduler under the capture).
            // Screenshots are taken at segment boundaries and the guard ends the run at the capture cap or the wall-clock
            // timeout (Issue #751). The outer budget still bounds a guest that never stops.
            uint? segmentPc = null;
            void Segment(RecompilerStateSnapshot snap)
            {
                segmentPc = snap.PC;
                screenshotCapture!.TryCapture();
                StopGuard(screenshotCapture, stopwatch, timeout)(snap);
            }

            string? boundaryStop = null;
            executionResult = orchestrator.Execute(engine, handoff, request, segmentCallback: Segment, postLoadAction: () =>
            {
                var devices = devicesAfterLoad();
                scheduler = devices.Scheduler;
                screenshotCapture = new ScreenshotCapture(
                    devices.Gpu,
                    devices.Scheduler,
                    outputDirectory,
                    vblankInterval,
                    maxScreenshots,
                    startVblank,
                    () => segmentPc,
                    guestPcTiming: "segment-boundary");
            });
            // A RuntimeHandoff is continued on the same live engine (no Load: RAM, GPU and scheduler are kept, review of #751).
            // The request's outer budget bounds the whole run: each continuation gets only what the earlier slices left.
            var remainingBudget = request.OuterBudget - Math.Min(request.OuterBudget, executionResult.SegmentsRetired);
            while (executionResult.State == TitleExecutionState.RuntimeHandoff && executionResult.FinalSnapshot is { } snapshot)
            {
                boundaryStop = CleanBoundaryStop(screenshotCapture, stopwatch, timeout);
                if (boundaryStop is not null)
                    break;
                executionResult = orchestrator.Resume(engine, handoff, snapshot, remainingBudget, segmentBudget, Segment);
                remainingBudget -= Math.Min(remainingBudget, executionResult.SegmentsRetired);
            }
            stopReason = boundaryStop ?? StopReasonOf(executionResult.State);
        }
        catch (CaptureStop stop)
        {
            stopReason = stop.Reason;
            executionResult = new TitleExecutionResult(
                TitleExecutionState.RuntimeHandoff, stop.Last, 0, engine.Name,
                stop.Reason.ToUpperInvariant(), $"Stopped by the screenshot command: {stop.Reason}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopReason = "runtime_failure";
            exceptionType = ex.GetType().Name;
            standardError.WriteLine($"psxrecomp screenshot: runtime crash: {ex}");
            executionResult = new TitleExecutionResult(
                TitleExecutionState.RuntimeFailure,
                null, 0, engine.Name,
                "CRASH", $"Runtime crash: {ex}");
        }
        finally
        {
            stopwatch.Stop();
        }

        return FinishCapture(screenshotCapture, executionResult, scheduler, outputDirectory, stopwatch, stopReason,
            exceptionType);
    }

    /// <summary>
    /// The <c>--json</c> result. Serialized with <see cref="JsonSerializer"/> so any text (diagnostics, paths, control
    /// characters) is escaped; numbers use the serializer's invariant formatting. Optional execution fields are omitted
    /// when absent, <c>exceptionType</c> is always present (null when none).
    /// </summary>
    internal static string SerializeResult(string? inputSha256, HeadlessRunResult result)
    {
        var execution = result.ExecutionResult;
        var document = new ScreenshotResultJson(
            Kind: "screenshot",
            Success: IsSuccess(result),
            ExecutionOk: !IsFailureState(execution.State),
            CaptureStatus: ScreenshotCapture.CaptureStatusOf(result.GoalReached, result.ScreenshotsTaken, result.WriteFailures),
            WriteFailures: result.WriteFailures,
            ScreenshotsTaken: result.ScreenshotsTaken,
            MaxVblankReached: result.MaxVblankReached,
            ElapsedMs: (long)Math.Round(result.ElapsedTime.TotalMilliseconds, MidpointRounding.AwayFromZero),
            StopReason: result.StopReason,
            GoalReached: result.GoalReached,
            ExceptionType: result.ExceptionType,
            Files: result.SavedFiles,
            ExecutionResult: new ScreenshotExecutionJson(
                State: execution.State.ToString(),
                Engine: execution.EngineName ?? string.Empty,
                DiagnosticCode: execution.DiagnosticCode,
                DiagnosticMessage: execution.DiagnosticMessage,
                GuestPc: execution.FinalSnapshot is { } snap ? $"0x{snap.PC:X8}" : null));
        return JsonSerializer.Serialize(document, ResultJsonOptions);
    }

    private static readonly JsonSerializerOptions ResultJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record ScreenshotResultJson(
        string Kind,
        bool Success,
        bool ExecutionOk,
        string CaptureStatus,
        int WriteFailures,
        int ScreenshotsTaken,
        ulong MaxVblankReached,
        long ElapsedMs,
        string StopReason,
        bool GoalReached,
        string? ExceptionType,
        IReadOnlyList<string> Files,
        ScreenshotExecutionJson ExecutionResult);

    private sealed record ScreenshotExecutionJson(
        string State,
        string Engine,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? DiagnosticCode,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? DiagnosticMessage,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? GuestPc);

    private static void WriteHumanResult(HeadlessRunResult result, TextWriter standardOutput)
    {
        standardOutput.WriteLine($"Screenshot capture: {result.StopReason}");
        standardOutput.WriteLine($"  Goal reached: {result.GoalReached}");
        standardOutput.WriteLine($"  Capture status: {ScreenshotCapture.CaptureStatusOf(result.GoalReached, result.ScreenshotsTaken, result.WriteFailures)}");
        if (result.WriteFailures > 0)
            standardOutput.WriteLine($"  PNG write failures: {result.WriteFailures}");
        if (result.ExceptionType is { } exceptionType)
            standardOutput.WriteLine($"  Exception type: {exceptionType}");
        standardOutput.WriteLine($"  Screenshots taken: {result.ScreenshotsTaken}");
        standardOutput.WriteLine($"  Max VBlank reached: {result.MaxVblankReached}");
        standardOutput.WriteLine($"  Elapsed time: {result.ElapsedTime.TotalSeconds:F1}s");
        standardOutput.WriteLine($"  Execution state: {result.ExecutionResult.State}");
        standardOutput.WriteLine($"  Engine: {result.ExecutionResult.EngineName}");
        if (result.ExecutionResult.DiagnosticCode is { } code)
            standardOutput.WriteLine($"  Diagnostic: {code}");
        if (result.ExecutionResult.FinalSnapshot is { } snap)
            standardOutput.WriteLine($"  Final PC: 0x{snap.PC:X8}");
        if (result.SavedFiles.Count > 0)
        {
            standardOutput.WriteLine($"  Saved files:");
            foreach (var file in result.SavedFiles)
                standardOutput.WriteLine($"    {file}");
        }
    }

    internal static bool TryParse(
        IReadOnlyList<string> arguments,
        out ParsedArguments parsed,
        out string? error)
    {
        string? input = null;
        string? outputDirectory = null;
        string? openBiosRomPath = null;
        string? discPath = null;
        var json = false;
        uint? segmentBudget = null;
        uint? segments = null;
        uint? vblankInterval = null;
        int? maxScreenshots = null;
        ulong? startVblank = null;
        int? timeoutSeconds = null;
        var help = false;

        for (var i = 0; i < arguments.Count; i++)
        {
            var token = arguments[i];
            switch (token)
            {
                case "--help" or "-h":
                    help = true;
                    break;
                case "--output":
                    if (i + 1 >= arguments.Count)
                    {
                        parsed = default;
                        error = "missing value for option '--output'.";
                        return false;
                    }
                    outputDirectory = arguments[++i];
                    break;
                case "--openbios":
                    if (i + 1 >= arguments.Count)
                    {
                        parsed = default;
                        error = "missing value for option '--openbios'.";
                        return false;
                    }
                    openBiosRomPath = arguments[++i];
                    break;
                case "--disc":
                    if (i + 1 >= arguments.Count)
                    {
                        parsed = default;
                        error = "missing value for option '--disc'.";
                        return false;
                    }
                    discPath = arguments[++i];
                    break;
                case "--segment-budget":
                    if (i + 1 >= arguments.Count)
                    {
                        parsed = default;
                        error = "missing value for option '--segment-budget'.";
                        return false;
                    }
                    if (!uint.TryParse(arguments[++i], out var budget) || budget == 0)
                    {
                        parsed = default;
                        error = $"invalid segment budget: expected a positive integer.";
                        return false;
                    }
                    segmentBudget = budget;
                    break;
                case "--segments":
                    if (i + 1 >= arguments.Count)
                    {
                        parsed = default;
                        error = "missing value for option '--segments'.";
                        return false;
                    }
                    if (!uint.TryParse(arguments[++i], out var seg) || seg == 0)
                    {
                        parsed = default;
                        error = $"invalid segments: expected a positive integer.";
                        return false;
                    }
                    segments = seg;
                    break;
                case "--vblank-interval":
                    if (i + 1 >= arguments.Count)
                    {
                        parsed = default;
                        error = "missing value for option '--vblank-interval'.";
                        return false;
                    }
                    if (!uint.TryParse(arguments[++i], out var vbi) || vbi == 0)
                    {
                        parsed = default;
                        error = $"invalid vblank interval: expected a positive integer.";
                        return false;
                    }
                    vblankInterval = vbi;
                    break;
                case "--max-screenshots":
                    if (i + 1 >= arguments.Count)
                    {
                        parsed = default;
                        error = "missing value for option '--max-screenshots'.";
                        return false;
                    }
                    if (!int.TryParse(arguments[++i], out var ms) || ms <= 0)
                    {
                        parsed = default;
                        error = $"invalid max screenshots: expected a positive integer.";
                        return false;
                    }
                    maxScreenshots = ms;
                    break;
                case "--start-vblank":
                    if (i + 1 >= arguments.Count)
                    {
                        parsed = default;
                        error = "missing value for option '--start-vblank'.";
                        return false;
                    }
                    if (!ulong.TryParse(arguments[++i], out var sv))
                    {
                        parsed = default;
                        error = $"invalid start vblank: expected a non-negative integer.";
                        return false;
                    }
                    startVblank = sv;
                    break;
                case "--timeout":
                    if (i + 1 >= arguments.Count)
                    {
                        parsed = default;
                        error = "missing value for option '--timeout'.";
                        return false;
                    }
                    if (!int.TryParse(arguments[++i], out var ts) || ts <= 0)
                    {
                        parsed = default;
                        error = $"invalid timeout: expected a positive integer (seconds).";
                        return false;
                    }
                    timeoutSeconds = ts;
                    break;
                case "--json":
                    json = true;
                    break;
                default:
                    if (token.StartsWith("--", StringComparison.Ordinal))
                    {
                        parsed = default;
                        error = $"unknown option '{token}'.";
                        return false;
                    }
                    if (input is not null)
                    {
                        parsed = default;
                        error = $"unexpected argument '{token}'.";
                        return false;
                    }
                    input = token;
                    break;
            }
        }

        if (help)
        {
            parsed = new ParsedArguments(input, outputDirectory, json, segmentBudget, segments, vblankInterval, maxScreenshots, startVblank, timeoutSeconds, Help: true, OpenBiosRomPath: openBiosRomPath, DiscPath: discPath);
            error = null;
            return true;
        }

        if (outputDirectory is null)
        {
            parsed = default;
            error = "missing required option '--output <dir>'.";
            return false;
        }

        if (openBiosRomPath is not null)
        {
            // OpenBIOS mode: input is optional (firmware is the ROM), disc is required
            if (discPath is null)
            {
                parsed = default;
                error = "--disc is required when using --openbios";
                return false;
            }
            // Input path is not needed for OpenBIOS mode, but we can accept it for consistency
            parsed = new ParsedArguments(input, outputDirectory, json, segmentBudget, segments, vblankInterval, maxScreenshots, startVblank, timeoutSeconds, Help: false, OpenBiosRomPath: openBiosRomPath, DiscPath: discPath);
            error = null;
            return true;
        }

        if (input is null)
        {
            parsed = default;
            error = "missing input path.";
            return false;
        }

        parsed = new ParsedArguments(input, outputDirectory, json, segmentBudget, segments, vblankInterval, maxScreenshots, startVblank, timeoutSeconds, Help: false, OpenBiosRomPath: null, DiscPath: null);
        error = null;
        return true;
    }

    internal sealed record ParsedArguments(
        string? Input,
        string? OutputDirectory,
        bool Json,
        uint? SegmentBudget,
        uint? Segments,
        uint? VBlankInterval,
        int? MaxScreenshots,
        ulong? StartVBlank,
        int? TimeoutSeconds,
        bool Help,
        string? OpenBiosRomPath = null,
        string? DiscPath = null);

    private sealed class CollectedOutput : IRuntimeOutputSink
    {
        public List<byte> Bytes { get; } = [];
        public void WriteByte(byte value) => Bytes.Add(value);
    }

    private sealed class ProgramEndHandoff(uint loadAddress, int instructionCount) : ITitleExecutionHandoff
    {
        private readonly uint _programEnd = unchecked(loadAddress + (uint)instructionCount * 4u);

        public TitleExecutionHandoffResult? Decide(RecompilerStateSnapshot segmentState)
        {
            ArgumentNullException.ThrowIfNull(segmentState);
            return segmentState.PC == _programEnd ? TitleExecutionHandoffResult.Exit() : null;
        }
    }

    /// <summary>Thrown from the segment callback to end the run cooperatively at a segment boundary (Issue #751).</summary>
    private sealed class CaptureStop(string reason, RecompilerStateSnapshot? last) : Exception(reason)
    {
        public string Reason { get; } = reason;
        public RecompilerStateSnapshot? Last { get; } = last;
    }

    /// <summary>
    /// Segment-boundary stop check: capture cap reached or wall-clock timeout (the guest is bounded per segment by the
    /// segment budget, so this bounds the whole run even when VBlank/display never advances).
    /// </summary>
    private static Action<RecompilerStateSnapshot> StopGuard(ScreenshotCapture capture, Stopwatch stopwatch, TimeSpan timeout) =>
        snap =>
        {
            // Review of #751 (A2): only a budget-cut segment is a progress boundary. Any other end (a CPU/runtime abnormal
            // stop, a handoff, completion) is classified by the orchestrator, so a goal frame in that segment cannot hide it.
            if (snap.Termination != RecompilerIrTerminationReason.ExecutionBudgetExceeded) return;
            if (capture.WriteFailures > 0) throw new CaptureStop("capture_write_failed", snap);
            if (capture.IsComplete) throw new CaptureStop("goal_reached", snap);
            if (stopwatch.Elapsed >= timeout) throw new CaptureStop("timeout", snap);
        };

    /// <summary>
    /// Stop rules at a clean Runtime boundary (a RuntimeHandoff the orchestrator already classified as clean): the goal,
    /// a write failure or the wall-clock timeout ends the run here; otherwise the run may continue (review of #751, A1/A2).
    /// </summary>
    private static string? CleanBoundaryStop(ScreenshotCapture? capture, Stopwatch stopwatch, TimeSpan timeout)
    {
        if (capture is null) return null;
        if (capture.WriteFailures > 0) return "capture_write_failed";
        if (capture.IsComplete) return "goal_reached";
        return stopwatch.Elapsed >= timeout ? "timeout" : null;
    }

    /// <summary>
    /// States that are not a clean end of the run: they set a non-zero exit code and <c>success:false</c>.
    /// BudgetExhausted and clean guest stops stay successful.
    /// </summary>
    internal static bool IsFailureState(TitleExecutionState state) =>
        state is TitleExecutionState.RuntimeFailure or TitleExecutionState.InvalidState or TitleExecutionState.UnsupportedTransfer;

    /// <summary>
    /// Overall success needs the execution to end cleanly, every requested PNG to be saved (goal reached) and no PNG
    /// write failure. A run that stops at the wall-clock timeout, the execution budget or a Runtime boundary before
    /// that is not success, even when it saved some frames (review of #751: a zero-PNG run must not exit 0).
    /// Execution and image results are reported separately in JSON and manifest.json.
    /// </summary>
    internal static bool IsSuccess(HeadlessRunResult result) =>
        !IsFailureState(result.ExecutionResult.State) && result.GoalReached && result.WriteFailures == 0;

    /// <summary>
    /// Records that this run has started. If the process is killed before FinishCapture, the leftover manifest says
    /// <c>in_progress</c> instead of describing an earlier run in the same directory (Issue #751).
    /// </summary>
    internal static void MarkInProgress(string outputDirectory) =>
        ScreenshotCapture.WriteManifestFile(outputDirectory, "in_progress", 0, 0, TimeSpan.Zero, null, []);

    /// <summary>
    /// Maps an orchestrator terminal state to the stop-reason vocabulary shared by stdout, JSON and manifest.json.
    /// Failure states keep their own name so a crash-like end is never reported as a clean guest stop (Issue #751).
    /// A Runtime-boundary handoff is its own reason: the guest did not finish (review of #751).
    /// </summary>
    internal static string StopReasonOf(TitleExecutionState state) => state switch
    {
        TitleExecutionState.BudgetExhausted => "execution_budget",
        TitleExecutionState.RuntimeFailure => "runtime_failure",
        TitleExecutionState.InvalidState => "invalid_state",
        TitleExecutionState.UnsupportedTransfer => "unsupported_transfer",
        TitleExecutionState.Completed => "completed",
        TitleExecutionState.Returned => "returned",
        TitleExecutionState.RuntimeHandoff => "runtime_handoff",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unmapped terminal state."),
    };

    internal static HeadlessRunResult RunScreenshotCaptureOpenBios(
        OpenBiosFirmware firmware,
        string firmwareHash,
        ICdSectorSource? disc,
        string outputDirectory,
        uint segmentBudget,
        uint segments,
        uint vblankInterval,
        int maxScreenshots,
        ulong startVblank,
        TimeSpan timeout,
        TextWriter standardError)
    {
        MarkInProgress(outputDirectory);
        var stopwatch = Stopwatch.StartNew();

        var backend = new OpenBiosBootBackend(firmware, disc);
        using var engine = backend.CreateEngine();
        var interpreterEngine = (InterpreterTitleExecutionEngine)engine;

        var request = new TitleExecutionRequest(
            backend.EntryPc,
            new uint[TitleExecutionRequest.GprCount],
            0, 0,
            [],
            segments, // outer budget: segments like openbios-probe
            segmentBudget);

        ScreenshotCapture? screenshotCapture = null;
        TitleExecutionResult? executionResult = null;
        var stopReason = "Unknown";
        string? exceptionType = null;

        try
        {
            var orchestrator = new ExecutionOrchestrator();
            executionResult = orchestrator.Execute(
                interpreterEngine,
                handoff: null,
                request,
                segmentCallback: snap => StopGuard(screenshotCapture!, stopwatch, timeout)(snap),
                postLoadAction: () =>
                {
                    // Patch OpenBIOS ROM: jump to shell entry (0x80030000) after the exception handler.
                    var core = interpreterEngine.DiagnosticDevices.Core;
                    core.WriteMemory32(0x00000284, 0x0A000C00); // j 0x80030000
                    core.WriteMemory32(0x00000288, 0x00000000); // nop

                    screenshotCapture = new ScreenshotCapture(
                        interpreterEngine.DiagnosticDevices.GpuDevice,
                        interpreterEngine.Scheduler!,
                        outputDirectory,
                        vblankInterval,
                        maxScreenshots,
                        startVblank,
                        () => interpreterEngine.DiagnosticDevices.Core.Pc,
                        guestPcTiming: "capture-instant");

                    interpreterEngine.Scheduler!.VblankCallback = vblank => screenshotCapture.TryCaptureAtVBlank(vblank);
                });
            stopReason = executionResult.State == TitleExecutionState.RuntimeHandoff
                ? CleanBoundaryStop(screenshotCapture, stopwatch, timeout) ?? StopReasonOf(executionResult.State)
                : StopReasonOf(executionResult.State);
        }
        catch (CaptureStop stop)
        {
            stopReason = stop.Reason;
            executionResult = new TitleExecutionResult(
                TitleExecutionState.RuntimeHandoff, stop.Last, 0, InterpreterTitleExecutionEngine.EngineName,
                stop.Reason.ToUpperInvariant(), $"Stopped by the screenshot command: {stop.Reason}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopReason = "runtime_failure";
            exceptionType = ex.GetType().Name;
            standardError.WriteLine($"psxrecomp screenshot: runtime crash: {ex}");
            executionResult = new TitleExecutionResult(
                TitleExecutionState.RuntimeFailure,
                null, 0, InterpreterTitleExecutionEngine.EngineName,
                "CRASH", $"Runtime crash: {ex}");
        }
        finally
        {
            stopwatch.Stop();
        }

        return FinishCapture(screenshotCapture, executionResult, interpreterEngine.Scheduler, outputDirectory, stopwatch, stopReason,
            exceptionType);
    }

    /// <summary>
    /// Keeps the frames already written, adds a final frame when the cap was not reached, writes manifest.json with the
    /// stop reason and collects the result. Shared by both execution paths so a partial run always leaves a manifest.
    /// <paramref name="stopReason"/> is the stop vocabulary only; <paramref name="exceptionType"/> is kept in its own field.
    /// </summary>
    private static HeadlessRunResult FinishCapture(
        ScreenshotCapture? capture,
        TitleExecutionResult? executionResult,
        DeviceScheduler? scheduler,
        string outputDirectory,
        Stopwatch stopwatch,
        string stopReason,
        string? exceptionType = null)
    {
        // Not after a write failure: a write_failed run must not gain a final PNG (review of #751).
        if (capture is not null && !capture.IsComplete && capture.WriteFailures == 0)
            capture.ForceCapture("persona_vblank_final.png");

        // Only the PNGs this run wrote: a reused output directory may hold stale files that the manifest does not list.
        IReadOnlyList<string> savedFiles = capture is null ? [] : capture.SavedFiles;
        var maxVblank = scheduler?.VblankCount ?? 0;
        var goalReached = capture?.AllTargetsSaved ?? false;
        if (capture is not null)
            capture.WriteManifest(stopReason, maxVblank, stopwatch.Elapsed, executionResult?.FinalSnapshot?.PC,
                exceptionType, goalReached, executionResult?.EngineName);
        else
            ScreenshotCapture.WriteManifestFile(outputDirectory, stopReason, 0, maxVblank, stopwatch.Elapsed,
                executionResult?.FinalSnapshot?.PC, [], exceptionType, goalReached, engine: executionResult?.EngineName);

        return new HeadlessRunResult(
            executionResult ?? new TitleExecutionResult(
                TitleExecutionState.RuntimeFailure,
                null, 0, InterpreterTitleExecutionEngine.EngineName,
                "NO_RESULT", "Execution did not produce a result"),
            capture?.ScreenshotsTaken ?? 0,
            maxVblank,
            savedFiles,
            stopwatch.Elapsed,
            stopReason,
            exceptionType,
            goalReached,
            capture?.WriteFailures ?? 0);
    }

    private static OpenedDisc OpenDisc(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var magic = new byte[8];
            if (stream.Read(magic) == magic.Length && System.Text.Encoding.ASCII.GetString(magic) == "MComprHD")
            {
                stream.Position = 0;
                return new OpenedDisc(new ChdCdSectorSource(ChdReader.Open(stream)), stream);
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        stream.Dispose();
        return new OpenedDisc(new RawCdSectorSource(File.ReadAllBytes(path)), null);
    }

    private sealed record OpenedDisc(ICdSectorSource Source, Stream? Stream) : IDisposable
    {
        public void Dispose() => Stream?.Dispose();
    }
}