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
    /// <c>PSXRecomp.RuntimeHost [ runtime-host.json | --help | -h ]</c>.
    /// Returns the classified run result code (0 success, 1 tooling/failure,
    /// 2 runtime stop) or 78 when the manifest / runtime cannot be set up.
    /// </summary>
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            WriteUsage(Console.Error);
            return 1;
        }

        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            WriteUsage(Console.Error);
            return 1;
        }

        if (args.Length != 1)
        {
            WriteUsage(Console.Error);
            return 78;
        }

        var manifestPath = Path.GetFullPath(args[0]);
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine(
                $"runtime host: manifest not found: {manifestPath}");
            return 78;
        }

        var manifestDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidOperationException(
                "The runtime host manifest has no directory component.");

        try
        {
            var manifest = RuntimeHostManifestJson.Parse(File.ReadAllText(manifestPath));

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
            if (FirmwareRuntimeLauncher.IsFirmwareDistribution(manifestDirectory))
            {
                return FirmwareRuntimeLauncher.Run(manifest, manifestDirectory, prebuiltArtifactPath, imagePath, Console.Error);
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

            var request = new TitleExecutionRequest(
                entryPc: manifest.EntryPc,
                initialGpr: new uint[TitleExecutionRequest.GprCount],
                initialHi: 0,
                initialLo: 0,
                initialMemory: Array.Empty<RecompilerInitialMemoryItem>(),
                outerBudget: 1,
                segmentBudget: uint.MaxValue);

            var execution = new ExecutionOrchestrator().Execute(
                engine,
                new ProgramEndHandoff(manifest.ProgramEnd),
                request);

            var result = RecompiledArtifactResult.From(
                execution, manifest.ResultRegister);

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

            Console.Error.WriteLine($"runtime host: classified as {result.Outcome} (state {execution.State});" +
                $" guest pc 0x{execution.FinalSnapshot.PC:X8}, exit {result.ExitCode}");
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

            return 78;
        }
    }

    private static void WriteUsage(TextWriter error)
    {
        error.WriteLine("usage: PSXRecomp.RuntimeHost <runtime-host.json>");
        error.WriteLine("  Executes the packaged game distribution described by the manifest.");
        error.WriteLine("  Returns 0 on a completed run, 1 for tooling failure, 2 for an " +
            "explicit runtime stop, and 78 when the manifest, the runtime host,");
        error.WriteLine("  or the prebuilt artifact cannot be set up.");
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