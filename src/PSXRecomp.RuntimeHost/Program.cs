using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Infrastructure;
using PSXRecomp.Infrastructure.Standalone;
using System.Buffers.Binary;

namespace PSXRecomp.Infrastructure.RuntimeHost;

/// <summary>
/// The shared managed Runtime host for packaged game distributions
/// (Issue #739 Phase 2 Step C, ADR-027 Option B). It reads
/// <c>runtime/runtime-host.json</c>, locates the prebuilt embedded-image
/// artifact named in the manifest, and drives it through the same engine,
/// orchestration, classification and timing path that <c>psxrecomp run
/// --standalone</c> uses — so a packaged distribution runs on the end user's
/// machine with no Studio, no CLI and no development repository present.
/// </summary>
[Infrastructure]
public static class Program
{
    /// <summary>
    /// Entry point for the runtime host. Invocation:
    /// <c>PSXRecomp.RuntimeHost [--diagnostics &lt;path&gt;] [ runtime-host.json | --help | -h ]</c>.
    /// <c>--diagnostics</c> writes the Issue #763 stop diagnostic (best-effort, off by default, exit code
    /// unchanged) for HLE and firmware distributions alike. Returns the classified run result code
    /// (0 success, 1 tooling/failure, 2 runtime stop) or 78 when the manifest / runtime cannot be set up.
    /// </summary>
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            WriteUsage(Console.Error);
            return 1;
        }

        // Optional diagnostics sink (Issue #763): everything that is not it is the
        // manifest path. Off by default, so the run's exit code and output are unchanged.
        string? diagnosticsPath = null;
        string? frameOutPath = null;
        // Issue #773 Phase B: opt-in report of RAM-placed code the artifact refused. Off by default.
        string? loadedCodeMismatchPath = null;
        var positionals = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--diagnostics")
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("runtime host: --diagnostics requires a path.");
                    WriteUsage(Console.Error);
                    return 78;
                }

                diagnosticsPath = Path.GetFullPath(args[++i]);
                continue;
            }

            if (args[i] == "--loaded-code-diagnostics")
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("runtime host: --loaded-code-diagnostics requires a path.");
                    WriteUsage(Console.Error);
                    return 78;
                }

                try
                {
                    loadedCodeMismatchPath = Path.GetFullPath(args[++i]);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    Console.Error.WriteLine($"runtime host: --loaded-code-diagnostics path is not valid: {ex.Message}");
                    WriteUsage(Console.Error);
                    return 78;
                }

                continue;
            }

            if (args[i] == "--frame-out")
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("runtime host: --frame-out requires a path.");
                    WriteUsage(Console.Error);
                    return 78;
                }

                // Defer path resolution to the best-effort frame writer so an invalid optional
                // output path cannot prevent the guest from executing.
                frameOutPath = args[++i];
                continue;
            }

            positionals.Add(args[i]);
        }

        if (positionals.Count == 1 && (positionals[0] == "--help" || positionals[0] == "-h"))
        {
            WriteUsage(Console.Error);
            return 1;
        }

        if (positionals.Count != 1)
        {
            WriteUsage(Console.Error);
            return 78;
        }

        var manifestPath = Path.GetFullPath(positionals[0]);

        // Issue #786 review M-C: a report never outlives its run. The previous one goes first, so a run that is killed or
        // fails before it writes leaves no report that could be read as this run's.
        if (loadedCodeMismatchPath is not null)
        {
            try
            {
                File.Delete(loadedCodeMismatchPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"runtime host: could not remove the previous loaded-code report {loadedCodeMismatchPath}: {ex.Message}");
                return 78;
            }
        }

        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine(
                $"runtime host: manifest not found: {manifestPath}");
            return 78;
        }

        var manifestDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidOperationException(
                "The runtime host manifest has no directory component.");

        // Issue #786 review M-A: only a firmware distribution carries the RAM-placed code table this report describes. Anywhere
        // else the report would be empty and look complete, so the flag fails closed before the run instead.
        if (loadedCodeMismatchPath is not null && !FirmwareRuntimeLauncher.IsFirmwareDistribution(manifestDirectory))
        {
            Console.Error.WriteLine(
                "runtime host: --loaded-code-diagnostics needs a firmware distribution (firmware-boot.json); this manifest has no RAM-placed code table to report on.");
            return 78;
        }

        RuntimeHostManifest? manifest = null;
        // Issue #773: written from the finally below, so a failed or stopped run still leaves its refusals behind.
        var loadedCodeMismatch = loadedCodeMismatchPath is null ? null : new LoadedCodeMismatchDiagnostics();
        try
        {
            manifest = RuntimeHostManifestJson.Parse(File.ReadAllText(manifestPath));

            if (!Directory.Exists(manifestDirectory))
            {
                Console.Error.WriteLine(
                    $"runtime host: manifest directory not found: {manifestDirectory}");
                return 78;
            }

            // Resolve and confine both manifest paths before anything is read or launched
            // (fail closed: an out-of-distribution or linked path never reaches Process.Start).
            var prebuiltArtifactPath = RuntimeHostLayout.ResolveArtifactPath(manifestDirectory, manifest.Artifact);
            var imagePath = RuntimeHostLayout.ResolveImagePath(manifestDirectory, manifest.Image);

            // A firmware-booted distribution (OpenBIOS ROM + disc, Issue #732) is its own run: the disc and the
            // RAM-placed code table come from firmware-boot.json, and no BIOS HLE Runtime exists in that mode.
            // --diagnostics is honored here too (Issue #732 P2-1): the same stop diagnostic the HLE path writes.
            if (FirmwareRuntimeLauncher.IsFirmwareDistribution(manifestDirectory))
            {
                return FirmwareRuntimeLauncher.Run(
                    manifest, manifestDirectory, prebuiltArtifactPath, imagePath, Console.Error, diagnosticsPath, frameOutPath, loadedCodeMismatch);
            }

            // Read the program image for the host-transfer bridge / interpreter fallback
            // (for a prebuilt artifact this is the same image the artifact embeds).
            var imageBytes = File.ReadAllBytes(imagePath);

            var imageWords = new uint[imageBytes.Length / 4];
            for (var i = 0; i < imageWords.Length; i++)
            {
                imageWords[i] = BinaryPrimitives.ReadUInt32LittleEndian(
                    imageBytes.AsSpan(i * 4));
            }

            // Choose the launch mode:
            // - Guest firmware (OpenBIOS) gets NO BIOS HLE Runtime (it owns its kernel
            //   and vectors), but it does get the device-MMIO HostTransferBridge: the
            //   engine launches the artifact with --runtime-host (stops the launcher
            //   re-delegating to this host), --host-transfer (the only thing that
            //   enables the artifact's MMIO bridge; without it guest MMIO exits 96),
            //   --exact-device-time (device-time sync) and --guest-exceptions (the
            //   firmware's own exception handling). Mixed fallback follows the
            //   manifest (unresolved transfers may reach the interpreter fallback).
            // - HLE games use the bridge for BIOS service calls and mixed-fallback
            //   transfers.
            var needsBridge = !manifest.GuestFirmware;

            IBiosRuntime BiosRuntime(IGuestMemoryReader reader, IGuestMemoryWriter writer)
                => new BiosHleRuntime(new ConsoleRuntimeOutputSink(), reader, writer);

            // Mixed fallback is honored regardless of GuestFirmware.
            // For guest firmware: the artifact runs in RuntimeHost mode (no bridge)
            // but unresolved transfers can still reach the interpreter fallback.
            // For HLE games: the artifact runs in HostTransfer mode (with bridge)
            // and mixed fallback works through the bridge.
            var mixedFallback = manifest.MixedFallback
                ? new MixedFallbackOptions(
                    manifest.FallbackSegmentInstructionBudget,
                    manifest.FallbackMaxTransitions)
                : null;

            var launchMode = needsBridge
                ? RecompiledHostExecutionEngine.PrebuiltArtifactLaunchMode.HostTransfer
                : RecompiledHostExecutionEngine.PrebuiltArtifactLaunchMode.RuntimeHost;

            using var engine = RecompiledHostExecutionEngine.ForPrebuiltArtifact(
                prebuiltArtifactPath,
                (manifest.BlockEntryPcs ?? Array.Empty<uint>()).ToHashSet(),
                guestFirmware: manifest.GuestFirmware,
                biosRuntimeFactory: needsBridge ? BiosRuntime : null,
                mixedFallback: mixedFallback,
                runTimeout: TimeSpan.FromMilliseconds(manifest.RunTimeoutMs),
                imageWords: imageWords,
                imageLoadAddress: manifest.ImageLoadAddress,
                launchMode: launchMode);
            engine.LoadedCodeMismatchDiagnostics = loadedCodeMismatch;

            var request = new TitleExecutionRequest(
                entryPc: manifest.EntryPc,
                initialGpr: new uint[TitleExecutionRequest.GprCount],
                initialHi: 0,
                initialLo: 0,
                initialMemory: Array.Empty<RecompilerInitialMemoryItem>(),
                outerBudget: 1,
                segmentBudget: uint.MaxValue);

            var wall = System.Diagnostics.Stopwatch.StartNew();
            var execution = new ExecutionOrchestrator().Execute(
                engine,
                new ProgramEndHandoff(manifest.ProgramEnd),
                request);
            wall.Stop();
            var result = RecompiledArtifactResult.From(
                execution, manifest.ResultRegister);
            // Issue #786 review MINOR-1: marked after the run is classified, so a report says executed only once it has an end.
            RecompiledHostExecutionEngine.MarkLoadedCodeReportFinished(loadedCodeMismatch, execution);

            // Forward console output for native runs: the guest's C stdout contains
            // the boot/TTY text up to the engine's RSNAPSHOT diagnostic block; the
            // bridge-pumped path writes TTY directly through the sink above.
            if (engine.ConsoleOutput is { Length: > 0 } consoleOutput)
            {
                var end = consoleOutput.IndexOf(
                    RecompiledArtifactCodeGen.SnapshotBeginMarker,
                    StringComparison.Ordinal);
                if (end >= 0) consoleOutput = consoleOutput[..end];
                Console.Write(consoleOutput);
            }

            if (frameOutPath is not null)
            {
                RuntimeHostFrameOutput.TryWrite(engine, frameOutPath, Console.Error);
            }

            // A timeout or an engine failure ends the run with no snapshot: the guest pc is then unknown, not guessed.
            var guestPc = execution.FinalSnapshot is { } snapshot ? $"0x{snapshot.PC:X8}" : "unknown";
            Console.Error.WriteLine($"runtime host: classified as {result.Outcome} (state {execution.State});" +
                $" guest pc {guestPc}, exit {result.ExitCode}");

            if (diagnosticsPath is not null)
            {
                WriteDiagnostics(
                    diagnosticsPath,
                    () => RuntimeHostDiagnostic.From(
                        manifest,
                        execution,
                        result.ExitCode,
                        engine.NativeRetiredInstructions,
                        engine.FallbackEvidence?.FallbackInstructions,
                        engine.FallbackEvidence?.Transitions,
                        engine.LastDeclinedTransfer,
                        engine.FallbackEvidence?.Targets,
                        RuntimeHostRunMetrics.From(
                        wall.Elapsed.TotalMilliseconds, engine.HostRoundTrips, engine.FallbackTimings, engine.FallbackEvidence, engine.LastMmioAccess)));
            }

            return RecompiledArtifactExitCode.ExitCodeFor(execution.State);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"runtime host: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is not null)
            {
                Console.Error.WriteLine(
                    $"runtime host: inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }

            // Only the manifest's static facts exist once the run itself threw.
            if (diagnosticsPath is not null && manifest is not null)
            {
                WriteDiagnostics(diagnosticsPath, () => RuntimeHostDiagnostic.FromFailure(manifest, 78, ex));
            }

            return 78;
        }
        finally
        {
            WriteLoadedCodeMismatch(loadedCodeMismatchPath, loadedCodeMismatch);
        }
    }

    private static void WriteUsage(TextWriter error)
    {
        error.WriteLine("usage: PSXRecomp.RuntimeHost [--diagnostics <path>] [--loaded-code-diagnostics <path>] [--frame-out <path.png>] <runtime-host.json>");
        error.WriteLine("  Executes the packaged game distribution described by the manifest.");
        error.WriteLine("  --diagnostics writes a machine-readable stop diagnostic (off by default).");
        error.WriteLine("  --loaded-code-diagnostics writes the RAM-placed code refusals as JSON (off by default).");
        error.WriteLine("  --frame-out writes the display the guest left in VRAM at the end of the run as a PNG (off by default).");
        error.WriteLine("  Returns 0 on a completed run, 1 for tooling failure, 2 for an " +
            "explicit runtime stop, and 78 when the manifest, the runtime host,");
        error.WriteLine("  or the prebuilt artifact cannot be set up.");
    }

    /// <summary>
    /// Writes the Issue #763 stop diagnostic. Best-effort: a diagnostics write failure is
    /// reported on stderr but never changes the run's classified exit code.
    /// </summary>
    private static void WriteDiagnostics(string path, Func<RuntimeHostDiagnostic> build)
    {
        try
        {
            File.WriteAllText(path, RuntimeHostDiagnosticJson.Serialize(build()));
        }
        catch (Exception ex)
        {
            // Best-effort observation: whatever fails here (I/O, assembly, serialization) must not alter the run result.
            Console.Error.WriteLine($"runtime host: could not write diagnostics to {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes the Issue #773 loaded-code report when it was requested. Best-effort like <see cref="WriteDiagnostics"/>: a write
    /// failure is reported on stderr and never changes the run's exit code.
    /// </summary>
    private static void WriteLoadedCodeMismatch(string? path, LoadedCodeMismatchDiagnostics? diagnostics)
    {
        if (path is null || diagnostics is null) return;
        try
        {
            // Temp file then move: a reader never sees a half-written report that could pass for a short, valid one.
            var temp = path + ".tmp";
            File.WriteAllText(temp, diagnostics.ToJson() + "\n", new System.Text.UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"runtime host: could not write loaded-code diagnostics to {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Runtime output sink that writes directly to the console: printable bytes
    /// are forwarded verbatim so boot/TTY messages are visible, and non-printable
    /// bytes are emitted as <c>\xNN</c> escapes. Deterministic byte-for-byte
    /// output (no buffering of the byte sequence beyond the console stream).
    /// </summary>
    private sealed class ConsoleRuntimeOutputSink : IRuntimeOutputSink
    {
        private readonly object _lock = new();
        private readonly TextWriter _out;

        public ConsoleRuntimeOutputSink() : this(Console.Out) { }

        public ConsoleRuntimeOutputSink(TextWriter output) => _out = output;

        public void WriteByte(byte value)
        {
            if (value >= 32 && value < 127)
            {
                lock (_lock)
                {
                    _out.Write((char)value);
                }
            }
            else
            {
                lock (_lock)
                {
                    _out.Write("\\x");
                    _out.Write(((int)value).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }
    }
}