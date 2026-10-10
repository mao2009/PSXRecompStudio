using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
    internal const string Usage = "usage: psxrecomp screenshot <input.exe|input.chd> --output <dir> [--openbios <rom.bin>] [--disc <image.chd|image.bin>] [--segment-budget <n>] [--vblank-interval <n>] [--max-screenshots <n>] [--start-vblank <n>] [--timeout <seconds>] [--json]";

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
            standardOutput.WriteLine("  --output <dir>             required: directory to save PNG screenshots");
            standardOutput.WriteLine("  --openbios <rom.bin>       optional: OpenBIOS ROM file (512KB) for real BIOS execution");
            standardOutput.WriteLine("  --disc <image.chd|image.bin>  required with --openbios: CD-ROM image for the game");
            standardOutput.WriteLine("  --segment-budget <n>       per-segment instruction budget (default: 1000000)");
            standardOutput.WriteLine("  --vblank-interval <n>      VBlank interval between captures (default: 300)");
            standardOutput.WriteLine("  --max-screenshots <n>      maximum screenshots to capture (default: 10)");
            standardOutput.WriteLine("  --start-vblank <n>         first VBlank to capture (default: 300)");
            standardOutput.WriteLine("  --timeout <seconds>        wall-clock timeout in seconds (default: 300)");
            standardOutput.WriteLine("  --json                     emit machine-readable JSON result");
            standardOutput.WriteLine("  --help, -h                 show this help");
            return RecompiledArtifactExitCode.Success;
        }

        var outputDirectory = Path.GetFullPath(parsed.OutputDirectory!);
        var segmentBudget = parsed.SegmentBudget ?? 1_000_000u;
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
                    var resolved = CliInput.LoadWithIdentity(parsed.Input!, outerBudget: 64, segmentBudget);
                    input = resolved.Execution;
                    inputSha256 = resolved.Sha256;
                }
                else
                {
                    input = CliInput.Load(parsed.Input!, outerBudget: 64, segmentBudget);
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

            return result.ExecutionResult.State == TitleExecutionState.RuntimeFailure
                ? RecompiledArtifactExitCode.Failure
                : RecompiledArtifactExitCode.Success;
        }
        catch (Exception ex) when (
            ex is DirectoryNotFoundException or FileNotFoundException
                or UnauthorizedAccessException or IOException or InvalidDataException
                or ArgumentException or InvalidOperationException)
        {
            standardError.WriteLine($"psxrecomp screenshot: {ex.Message}");
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
        var stopwatch = Stopwatch.StartNew();
        var savedFiles = new List<string>();

        using var engine = new InterpreterTitleExecutionEngine(
            input.InstructionWords,
            input.LoadAddress,
            (reader, writer) => new BiosHleRuntime(new CollectedOutput(), reader, writer));

        var handoff = new ProgramEndHandoff(input.LoadAddress, input.InstructionWords.Count);
        var orchestrator = new ExecutionOrchestrator();

        var request = input.Request;
        TitleExecutionResult? executionResult = null;
        var stopReason = "Unknown";
        var lastVblank = 0ul;
        ScreenshotCapture? screenshotCapture = null;

        try
        {
            engine.Load(request);

            screenshotCapture = new ScreenshotCapture(
                engine.DiagnosticDevices.GpuDevice,
                engine.Scheduler!,
                outputDirectory,
                vblankInterval,
                maxScreenshots,
                startVblank);

            // Use ExecutionOrchestrator like TitleExecutionService does, but with periodic screenshot capture
            // We run the orchestrator with a small outer budget and capture screenshots between segments
            // by checking VBlank count after each segment completion
            var outerBudget = 1000u; // Large enough for full execution
            var currentRequest = request;

            while (true)
            {
                // Check wall-clock timeout
                if (stopwatch.Elapsed >= timeout)
                {
                    stopReason = "WallClockTimeout";
                    break;
                }

                // Check if screenshot capture is complete
                if (screenshotCapture.IsComplete)
                {
                    stopReason = "ScreenshotsComplete";
                    break;
                }

                // Check for hang (no VBlank progress for extended period)
                var currentVblank = engine.Scheduler!.VblankCount;
                if (currentVblank == lastVblank && stopwatch.Elapsed > TimeSpan.FromSeconds(60))
                {
                    stopReason = "HangDetected";
                    break;
                }
                lastVblank = currentVblank;

                // Run one segment via orchestrator
                var result = orchestrator.Execute(engine, handoff, currentRequest);
                executionResult = result;

                // Try to capture screenshot at segment boundary
                screenshotCapture.TryCapture();

                // Check termination
                if (result.State != TitleExecutionState.RuntimeHandoff)
                {
                    stopReason = result.State.ToString();
                    break;
                }

                // Continue from the handoff's continuation point
                // The orchestrator handles the continuation internally, so this shouldn't happen
                // But if it does, we need to update the request for the next iteration
                if (result.FinalSnapshot is not null)
                {
                    currentRequest = new TitleExecutionRequest(
                        result.FinalSnapshot.PC,
                        result.FinalSnapshot.Gpr.ToArray(),
                        result.FinalSnapshot.HI,
                        result.FinalSnapshot.LO,
                        [],
                        outerBudget,
                        segmentBudget);
                }
                else
                {
                    stopReason = "NoSnapshot";
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopReason = $"Crash: {ex.GetType().Name}";
            standardError.WriteLine($"psxrecomp screenshot: runtime crash: {ex}");
        }
        finally
        {
            stopwatch.Stop();
        }

        // Final capture if not complete
        if (screenshotCapture is not null && !screenshotCapture.IsComplete)
        {
            screenshotCapture.ForceCapture("persona_vblank_final.png");
        }

        // Collect saved files
        if (Directory.Exists(outputDirectory))
        {
            savedFiles.AddRange(Directory.GetFiles(outputDirectory, "*.png")
                .OrderBy(f => f)
                .Select(Path.GetFullPath));
        }

        var maxVblank = engine.Scheduler?.VblankCount ?? 0;

        return new HeadlessRunResult(
            executionResult ?? new TitleExecutionResult(
                TitleExecutionState.RuntimeFailure,
                null, 0, InterpreterTitleExecutionEngine.EngineName,
                "NO_RESULT", "Execution did not produce a result"),
            screenshotCapture?.ScreenshotsTaken ?? 0,
            maxVblank,
            savedFiles.AsReadOnly(),
            stopwatch.Elapsed,
            stopReason);
    }

    private static string SerializeResult(string? inputSha256, HeadlessRunResult result)
    {
        var sb = new StringBuilder();
        sb.Append("{");
        sb.Append($"\"kind\":\"screenshot\",");
        sb.Append($"\"success\":{result.ExecutionResult.State != TitleExecutionState.RuntimeFailure},");
        sb.Append($"\"screenshotsTaken\":{result.ScreenshotsTaken},");
        sb.Append($"\"maxVblankReached\":{result.MaxVblankReached},");
        sb.Append($"\"elapsedMs\":{result.ElapsedTime.TotalMilliseconds:F0},");
        sb.Append($"\"stopReason\":\"{result.StopReason}\",");
        sb.Append($"\"files\":[");
        for (int i = 0; i < result.SavedFiles.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append($"\"{result.SavedFiles[i].Replace("\\", "\\\\")}\"");
        }
        sb.Append("],");
        sb.Append($"\"executionResult\":{{");
        sb.Append($"\"state\":{result.ExecutionResult.State},");
        sb.Append($"\"engine\":\"{result.ExecutionResult.EngineName}\",");
        if (result.ExecutionResult.DiagnosticCode is { } code)
            sb.Append($"\"diagnosticCode\":\"{code}\",");
        if (result.ExecutionResult.DiagnosticMessage is { } msg)
            sb.Append($"\"diagnosticMessage\":\"{msg.Replace("\"", "\\\"")}\",");
        if (result.ExecutionResult.FinalSnapshot is { } snap)
            sb.Append($"\"guestPc\":\"0x{snap.PC:X8}\"");
        sb.Append("}}");
        return sb.ToString();
    }

    private static void WriteHumanResult(HeadlessRunResult result, TextWriter standardOutput)
    {
        standardOutput.WriteLine($"Screenshot capture: {result.StopReason}");
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
            parsed = new ParsedArguments(input, outputDirectory, json, segmentBudget, vblankInterval, maxScreenshots, startVblank, timeoutSeconds, Help: true, OpenBiosRomPath: openBiosRomPath, DiscPath: discPath);
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
            parsed = new ParsedArguments(input, outputDirectory, json, segmentBudget, vblankInterval, maxScreenshots, startVblank, timeoutSeconds, Help: false, OpenBiosRomPath: openBiosRomPath, DiscPath: discPath);
            error = null;
            return true;
        }

        if (input is null)
        {
            parsed = default;
            error = "missing input path.";
            return false;
        }

        parsed = new ParsedArguments(input, outputDirectory, json, segmentBudget, vblankInterval, maxScreenshots, startVblank, timeoutSeconds, Help: false, OpenBiosRomPath: null, DiscPath: null);
        error = null;
        return true;
    }

    internal sealed record ParsedArguments(
        string? Input,
        string? OutputDirectory,
        bool Json,
        uint? SegmentBudget,
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

    private static HeadlessRunResult RunScreenshotCaptureOpenBios(
        OpenBiosFirmware firmware,
        string firmwareHash,
        ICdSectorSource? disc,
        string outputDirectory,
        uint segmentBudget,
        uint vblankInterval,
        int maxScreenshots,
        ulong startVblank,
        TimeSpan timeout,
        TextWriter standardError)
    {
        var stopwatch = Stopwatch.StartNew();
        var savedFiles = new List<string>();

        var backend = new OpenBiosBootBackend(firmware, disc);
        using var engine = backend.CreateEngine();
        var interpreterEngine = (InterpreterTitleExecutionEngine)engine;

        var handoff = new OpenBiosHandoff();
        var orchestrator = new ExecutionOrchestrator();

        var request = new TitleExecutionRequest(
            backend.EntryPc,
            new uint[TitleExecutionRequest.GprCount],
            0, 0,
            [],
            1000,
            segmentBudget);

        var screenshotCapture = new ScreenshotCapture(
            interpreterEngine.DiagnosticDevices.GpuDevice,
            interpreterEngine.Scheduler!,
            outputDirectory,
            vblankInterval,
            maxScreenshots,
            startVblank);

        TitleExecutionResult? executionResult = null;
        var stopReason = "Unknown";
        var lastVblank = 0ul;

        try
        {
            interpreterEngine.Load(request);

            var outerBudget = 1000u;
            var currentRequest = request;

            while (true)
            {
                if (stopwatch.Elapsed >= timeout)
                {
                    stopReason = "WallClockTimeout";
                    break;
                }

                if (screenshotCapture.IsComplete)
                {
                    stopReason = "ScreenshotsComplete";
                    break;
                }

                var currentVblank = interpreterEngine.Scheduler!.VblankCount;
                if (currentVblank == lastVblank && stopwatch.Elapsed > TimeSpan.FromSeconds(60))
                {
                    stopReason = "HangDetected";
                    break;
                }
                lastVblank = currentVblank;

                var result = orchestrator.Execute(interpreterEngine, handoff, currentRequest);
                executionResult = result;

                screenshotCapture.TryCapture();

                if (result.State != TitleExecutionState.RuntimeHandoff)
                {
                    stopReason = result.State.ToString();
                    break;
                }

                if (result.FinalSnapshot is not null)
                {
                    currentRequest = new TitleExecutionRequest(
                        result.FinalSnapshot.PC,
                        result.FinalSnapshot.Gpr.ToArray(),
                        result.FinalSnapshot.HI,
                        result.FinalSnapshot.LO,
                        [],
                        outerBudget,
                        segmentBudget);
                }
                else
                {
                    stopReason = "NoSnapshot";
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopReason = $"Crash: {ex.GetType().Name}";
            standardError.WriteLine($"psxrecomp screenshot: runtime crash: {ex}");
        }
        finally
        {
            stopwatch.Stop();
        }

        if (screenshotCapture is not null && !screenshotCapture.IsComplete)
        {
            screenshotCapture.ForceCapture("persona_vblank_final.png");
        }

        if (Directory.Exists(outputDirectory))
        {
            savedFiles.AddRange(Directory.GetFiles(outputDirectory, "*.png")
                .OrderBy(f => f)
                .Select(Path.GetFullPath));
        }

        var maxVblank = interpreterEngine.Scheduler?.VblankCount ?? 0;

        return new HeadlessRunResult(
            executionResult ?? new TitleExecutionResult(
                TitleExecutionState.RuntimeFailure,
                null, 0, InterpreterTitleExecutionEngine.EngineName,
                "NO_RESULT", "Execution did not produce a result"),
            screenshotCapture?.ScreenshotsTaken ?? 0,
            maxVblank,
            savedFiles.AsReadOnly(),
            stopwatch.Elapsed,
            stopReason);
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

    private sealed class OpenBiosHandoff : ITitleExecutionHandoff
    {
        public TitleExecutionHandoffResult? Decide(RecompilerStateSnapshot segmentState)
        {
            ArgumentNullException.ThrowIfNull(segmentState);
            // OpenBIOS runs the firmware; we don't have a program end to detect.
            // Execution continues until budget/timeout/hang.
            return null;
        }
    }
}