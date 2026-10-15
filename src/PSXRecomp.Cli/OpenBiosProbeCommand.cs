using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PSXRecomp.Architecture;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime.CdRom;
using PSXRecomp.Infrastructure.Standalone;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// A bounded, source-neutral OpenBIOS firmware bring-up entrypoint. It never reports
/// a title-screen or game boot success and never silently enables host BIOS HLE.
/// </summary>
[Infrastructure]
internal static class OpenBiosProbeCommand
{
    /// <summary>The PS-X EXE entry of the synthetic test disc; a differential boundary, not a title-specific constant.</summary>
    private const uint ExecutableBoundary = 0x80010000u;

    private const int RamBytes = 0x200000;

    internal const string Usage = "usage: psxrecomp openbios-probe <openbios.bin> [--segment-budget <n>] [--segments <n>] [--disc <image.chd|image.bin>] [--capture-at <hex-pc>] [--dump-ram <file>] [--dump-frame <file.ppm>] [--pc-histogram <file.json>] [--list-disc <file.json>] [--fallback-targets <file.json>]" +
                             "[--engine interpreter|generated-host] [--roots <file>] [--code-bytes <n>] [--differential] [--compare-at <hex-pc>]... " +
                             "[--stop-at <hex-pc>] [--symbols <nm-output>] [--load-images <manifest>] [--standalone] [--json]";

    internal static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        if (args.Count == 0 || args.Count > 64 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            error.WriteLine(Usage);
            return 1;
        }

        uint segmentBudget = 100_000, segments = 10, codeBytes = 0;
        bool json = false, differential = false, standalone = false;
        string? dumpRamPath = null, dumpFramePath = null, histogramPath = null, listDiscPath = null, discPath = null, rootsPath = null, symbolsPath = null, loadImagesPath = null, fallbackTargetsPath = null, engineKind = "interpreter";
        uint? capturePc = null, stopAt = null;
        var compareAt = new SortedSet<uint>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Count; i++)
        {
            var option = args[i];
            if (option != "--compare-at" && !seen.Add(option))
            {
                error.WriteLine($"openbios-probe: duplicate option {option}");
                return 1;
            }

            if (option == "--json") { json = true; continue; }
            if (option == "--differential") { differential = true; continue; }
            if (option == "--standalone") { standalone = true; continue; }
            if (option is "--capture-at" or "--compare-at" or "--stop-at")
            {
                if (++i >= args.Count || !uint.TryParse(args[i].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? args[i][2..] : args[i], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var pc))
                {
                    error.WriteLine($"openbios-probe: {option} requires a hexadecimal PC");
                    return 1;
                }

                if (option == "--capture-at") capturePc = pc;
                else if (option == "--stop-at") stopAt = pc;
                else compareAt.Add(pc);
                continue;
            }

            if (option is "--disc" or "--roots" or "--engine" or "--symbols" or "--load-images" or "--dump-ram" or "--dump-frame" or "--pc-histogram" or "--list-disc" or "--fallback-targets")
            {
                if (++i >= args.Count || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    error.WriteLine($"openbios-probe: {option} requires a value");
                    return 1;
                }

                if (option == "--dump-ram") dumpRamPath = args[i];
                else if (option == "--dump-frame") dumpFramePath = args[i];
                else if (option == "--pc-histogram") histogramPath = args[i];
                else if (option == "--list-disc") listDiscPath = args[i];
                else if (option == "--disc") discPath = args[i];
                else if (option == "--roots") rootsPath = args[i];
                else if (option == "--symbols") symbolsPath = args[i];
                else if (option == "--load-images") loadImagesPath = args[i];
                else if (option == "--fallback-targets") fallbackTargetsPath = args[i];
                else engineKind = args[i];
                continue;
            }

            if (option != "--segment-budget" && option != "--segments" && option != "--code-bytes")
            {
                error.WriteLine($"openbios-probe: unknown option {option}");
                return 1;
            }

            if (++i >= args.Count || !uint.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || (number == 0 && option != "--code-bytes"))
            {
                error.WriteLine($"openbios-probe: {option} requires a positive integer (or zero for --code-bytes)");
                return 1;
            }

            if (option == "--segments") segments = number;
            else if (option == "--code-bytes") codeBytes = number;
            else segmentBudget = number;
        }

        // Validate incompatible option combinations (Issue #772 m1)
        if (engineKind == "generated-host" && dumpRamPath is not null)
        {
            error.WriteLine("openbios-probe: --dump-ram is not supported with --engine generated-host (use --engine interpreter for RAM capture)");
            error.WriteLine(Usage);
            return 1;
        }
        if (engineKind == "generated-host" && dumpFramePath is not null)
        {
            error.WriteLine("openbios-probe: --dump-frame is not supported with --engine generated-host (use --engine interpreter for frame capture)");
            error.WriteLine(Usage);
            return 1;
        }
        if (engineKind == "generated-host" && histogramPath is not null)
        {
            error.WriteLine("openbios-probe: --pc-histogram is not supported with --engine generated-host (use --engine interpreter for PC histogram)");
            error.WriteLine(Usage);
            return 1;
        }
        if (fallbackTargetsPath is not null && engineKind != "generated-host")
        {
            error.WriteLine("openbios-probe: --fallback-targets requires --engine generated-host (the export records the mixed-execution fallback)");
            error.WriteLine(Usage);
            return 1;
        }
        if (listDiscPath is not null && (dumpRamPath is not null || dumpFramePath is not null || histogramPath is not null))
        {
            error.WriteLine("openbios-probe: --list-disc cannot be combined with --dump-ram, --dump-frame, or --pc-histogram");
            error.WriteLine(Usage);
            return 1;
        }
        if (dumpRamPath is not null && capturePc is null)
        {
            error.WriteLine("openbios-probe: --dump-ram requires --capture-at to specify the PC at which to capture RAM");
            error.WriteLine(Usage);
            return 1;
        }
        if (listDiscPath is not null && discPath is null)
        {
            error.WriteLine("openbios-probe: --list-disc requires --disc to specify the disc image to list");
            error.WriteLine(Usage);
            return 1;
        }

        if (engineKind is not ("interpreter" or "generated-host") || (engineKind == "generated-host" && rootsPath is null)
            || (engineKind != "generated-host" && (differential || compareAt.Count != 0 || stopAt is not null || symbolsPath is not null || loadImagesPath is not null || rootsPath is not null || standalone || seen.Contains("--code-bytes")))
            || (engineKind == "generated-host" && capturePc is not null)
            || (compareAt.Count != 0 && !differential))
        {
            error.WriteLine("openbios-probe: --engine generated-host requires --roots; --roots, --code-bytes, --differential, --compare-at, --stop-at, --symbols, --load-images and --standalone require --engine generated-host; --capture-at requires --engine interpreter; " +
                            "--compare-at requires --differential");
            error.WriteLine(Usage);
            return 1;
        }

        try
        {
            // Firmware must be built/provided locally from audited OpenBIOS sources.
            // Never download or embed ROM bytes, and do not reveal the user path in JSON.
            var size = new FileInfo(args[0]).Length;
            if (size != OpenBiosFirmware.ImageSize)
            {
                throw new InvalidDataException($"Expected {OpenBiosFirmware.ImageSize} ROM bytes; got {size}.");
            }
            var bytes = File.ReadAllBytes(args[0]);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var firmware = OpenBiosFirmware.FromBytes(bytes);
            using var disc = discPath is null ? null : OpenDisc(discPath);
            if (listDiscPath is not null && disc is not null)
            {
                WriteDiscListing(disc.Source, listDiscPath);
                return 0;
            }
            var symbols = symbolsPath is null ? null : ProbeSymbols.Parse(File.ReadLines(symbolsPath));
            return engineKind == "generated-host"
                ? RunGeneratedHost(new GeneratedHostProbe(
                    firmware, hash, disc?.Source, ReadRoots(rootsPath!), codeBytes, segments, segmentBudget, differential, compareAt, stopAt, symbols, loadImagesPath, standalone, fallbackTargetsPath), json, output, error)
                : RunInterpreter(firmware, hash, disc?.Source, segments, segmentBudget, capturePc, dumpRamPath, dumpFramePath, histogramPath, json, output);
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or InvalidDataException
                or FileNotFoundException or DirectoryNotFoundException or DllNotFoundException or FormatException)
        {
            error.WriteLine($"openbios-probe: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Local diagnostic: every file the disc's ISO 9660 filesystem holds, with the PS-X EXE header fields of those that are
    /// executables (load address, size, entry), so the executables a boot program may stage in RAM can be told apart from data.
    /// </summary>
    private static void WriteDiscListing(ICdSectorSource disc, string path)
    {
        var iso = PSXRecomp.Core.Recompiler.LoadImageManifest.OpenIso(disc);
        var rows = new List<object>();
        foreach (var file in iso.EnumerateFiles())
        {
            object? exe = null;
            // Use the directory entry directly to read the exact file version,
            // avoiding normalized path ambiguity when multiple versions exist (e.g., GAME.EXE;1 vs GAME.EXE;2)
            // Only the 0x800-byte header and next 0x1000 bytes are needed for metadata and hash.
            var data = iso.ReadEnumeratedFilePrefix(file.Entry, 0x1800);
            if (data.Length >= 0x800)
            {
                // Validate the PS-X EXE header without loading its complete text segment.
                try
                {
                    var header = PsxExeHeader.Parse(data);
                    exe = new
                    {
                        entry = $"0x{header.EntryPoint:X8}",
                        loadAddress = $"0x{header.TextStart:X8}",
                        textSize = header.TextSize,
                        head4KSha256 = data.Length >= 0x1800
                            ? Convert.ToHexString(SHA256.HashData(data.AsSpan(0x800, 0x1000))).ToLowerInvariant()
                            : null,
                    };
                }
                catch
                {
                    // Not a valid PS-X EXE, ignore
                }
            }

            // Include the original on-disc filename (with version) for disambiguation
            // Use VersionedPath which preserves all directory and file version numbers
            rows.Add(new { path = file.VersionedPath, originalName = file.Entry.FileName, size = file.Entry.Size, exe });
        }

        File.WriteAllText(path, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static int RunInterpreter(
        OpenBiosFirmware firmware, string hash, ICdSectorSource? disc, uint segments, uint segmentBudget, uint? capturePc, string? dumpRamPath, string? dumpFramePath, string? histogramPath, bool json, TextWriter output)
    {
        var backend = new OpenBiosBootBackend(firmware, disc);
        using var engine = backend.CreateEngine();
        object? captured = null;
        var observed = engine as InterpreterTitleExecutionEngine;
        var histogram = histogramPath is null ? null : new ulong[RamBytes / 4];
        var monitor = new OpenBiosBootMonitor(observed is null ? null : () => (observed.Cop0Diagnostics.Cause, observed.Cop0Diagnostics.Epc, observed.Cop0Diagnostics.BadVAddr));
        if (observed is not null)
        {
            observed.FetchObserver = pc =>
            {
                monitor.OnFetch(pc);
                if (histogram is not null && (pc & 0x1FFFFFFFu) < RamBytes) histogram[(pc & 0x1FFFFFFFu) >> 2]++;
                if (pc == capturePc && captured is null)
                {
                    if (dumpRamPath is not null)
                    {
                        // Raw 2 MiB main RAM (KSEG0) for offline disassembly; local diagnostic output only.
                        var ram = new byte[RamBytes];
                        for (var a = 0; a < RamBytes; a += 4) BitConverter.TryWriteBytes(ram.AsSpan(a), observed.ReadGuestWord(0x80000000u + (uint)a));
                        File.WriteAllBytes(dumpRamPath, ram);
                    }
                    captured = new
                    {
                        pc = $"0x{pc:X8}", word = $"0x{observed.ReadGuestWord(pc):X8}",
                        gpr = Enumerable.Range(0, 32).Select(r => $"0x{observed.ReadGuestGpr(r):X8}").ToArray(),
                        lastTransfers = observed.RecentTrace.Transfers.TakeLast(8).Select(t => $"0x{t.From.Pc:X8}->0x{t.To:X8}").ToArray()
                    };
                }
            };
        }
        var request = new TitleExecutionRequest(
            backend.EntryPc,
            new uint[TitleExecutionRequest.GprCount],
            0, 0,
            Array.Empty<RecompilerInitialMemoryItem>(),
            segments, segmentBudget);
        var result = new ExecutionOrchestrator().Execute(engine, handoff: null, request);
        var stop = (engine as InterpreterTitleExecutionEngine) is { } interp
            ? BuildStop(interp, result.FinalSnapshot)
            : null;
        var report = (engine as InterpreterTitleExecutionEngine) is { } reader
            ? monitor.Evaluate(reader.ReadGuestWord)
            : null;
        if (histogram is not null)
        {
            // Local diagnostic only: guest-RAM fetch counts, the top PCs plus 64-byte buckets, for wait-loop classification.
            ulong total = 0;
            foreach (var c in histogram) total += c;
            var top = Enumerable.Range(0, histogram.Length).Where(i => histogram[i] != 0).OrderByDescending(i => histogram[i]).Take(64)
                .Select(i => new { pc = $"0x{0x80000000u + (uint)i * 4:X8}", count = histogram[i] });
            var buckets = Enumerable.Range(0, histogram.Length / 16).Select(b => (b, c: histogram.Skip(b * 16).Take(16).Aggregate(0UL, (a, v) => a + v)))
                .Where(t => t.c != 0).OrderByDescending(t => t.c).Take(48)
                .Select(t => new { start = $"0x{0x80000000u + (uint)t.b * 64:X8}", count = t.c });
            File.WriteAllText(histogramPath!, JsonSerializer.Serialize(new { totalRamFetches = total, top, buckets }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (dumpFramePath is not null && observed?.CaptureFrameEvidence() is { } shot)
        {
            // Local diagnostic only (never committed): the displayed frame as a binary PPM. GP1(08h) bit 4 selects 24-bit
            // colour (MDEC movies), which is three bytes per pixel in VRAM, not the 15-bit halfwords FrameSnapshot holds.
            var gpu = observed.DiagnosticDevices.GpuDevice;
            var state = gpu.GetState();
            var rgb = new List<byte>();
            var width = shot.Width;
            var height = shot.Height;
            if ((state.DisplayMode & 0x10) != 0)
            {
                var vram = gpu.GetVramBytes();
                var startX = (int)(state.DisplayVramStart & 0x3FF); // halfword (16-bit) units
                var startY = (int)((state.DisplayVramStart >> 10) & 0x1FF);
                for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                    {
                        // 24-bit mode: 3 bytes per pixel. VRAM row stride is 2048 bytes (1024 halfwords).
                        // startX is in halfwords, so byte offset = startX * 2. Each pixel advances by 3 bytes.
                        var rowOffset = ((startY + y) & 0x1FF) * 2048;
                        var offset = rowOffset + startX * 2 + x * 3;
                        if (offset + 2 < vram.Length) { rgb.Add(vram[offset]); rgb.Add(vram[offset + 1]); rgb.Add(vram[offset + 2]); }
                        else { rgb.Add(0); rgb.Add(0); rgb.Add(0); }
                    }
            }
            else
            {
                foreach (var px in shot.Pixels)
                {
                    rgb.Add((byte)((px & 31) << 3)); rgb.Add((byte)(((px >> 5) & 31) << 3)); rgb.Add((byte)(((px >> 10) & 31) << 3));
                }
            }
            var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
            File.WriteAllBytes(dumpFramePath, header.Concat(rgb).ToArray());
        }
        // The kernel reaching its shell is the strongest claim this command makes; a title has not started.
        var kernelBooted = report?.KernelBooted ?? false;
        // No reliable OpenBIOS boot-completion contract is implemented yet. A
        // clean budget exhaustion is evidence of execution, never boot PASS.
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "openbios-probe",
                backend = backend.Id,
                sha256 = hash,
                bootVerified = kernelBooted && (report?.TitleStarted ?? false),
                kernelBooted,
                milestones = report,
                titleStarted = report?.TitleStarted ?? false,
                executionState = result.State.ToString(),
                guestPc = result.FinalSnapshot?.PC,
                diagnosticCode = result.DiagnosticCode,
                segmentsRetired = result.SegmentsRetired,
                stop,
                captured,
                // Issue #447: GTE work done and the displayed frame, as evidence beyond a PC.
                gte = observed is null ? null : new { commandsExecuted = observed.Gte.CommandsExecuted, lastUnsupportedCommand = observed.Gte.LastUnsupportedCommand },
                frame = observed?.CaptureFrameEvidence() is { } frame
                    ? new { width = frame.Width, height = frame.Height, nonZeroPixels = frame.Pixels.Count(p => p != 0), sha256 = Convert.ToHexString(frame.ComputeStableHash()).ToLowerInvariant() }
                    : null
            }));
        }
        else
        {
            output.WriteLine($"OpenBIOS execution probe (sha256:{hash}, backend={backend.Id})");
            output.WriteLine($"State: {result.State}; PC: 0x{result.FinalSnapshot?.PC ?? 0u:X8}; code: {result.DiagnosticCode ?? "none"}");
            if (stop is not null)
            {
                output.WriteLine($"Stop: {JsonSerializer.Serialize(stop)}");
            }
            output.WriteLine($"Kernel boot milestones: {JsonSerializer.Serialize(report)} => kernelBooted={kernelBooted}");
            output.WriteLine("Title boot: NOT VERIFIED (kernel boot is not a game boot).");
        }

        // Exit 0 means only that the kernel booted to its shell (see OpenBiosBootMonitor); a title has not
        // started, so bootVerified stays false. Anything less fails closed.
        return kernelBooted ? 0 : 2;
    }

    /// <summary>Progress is reported on stderr every 2^25 observed fetches.</summary>
    private const ulong ProgressMask = (1UL << 25) - 1;

    private sealed record GeneratedHostProbe(
        OpenBiosFirmware Firmware, string Hash, ICdSectorSource? Disc, IReadOnlyList<uint> Roots, uint CodeBytes,
        uint Segments, uint SegmentBudget, bool Differential, IReadOnlySet<uint> CompareAt, uint? StopAt, ProbeSymbols? Symbols, string? LoadImagesPath,
        bool Standalone, string? FallbackTargetsPath);

    /// <summary>
    /// Issue #732: runs the ROM through the generated host. The ROM's code (its first <c>CodeBytes</c> bytes, or all of
    /// it) is compiled from the reset vector plus the explicit roots. A <c>LoadImagesPath</c> manifest adds guarded
    /// AOT versions of RAM-placed code; everything with no current compiled block runs in interpreter fallback over the same
    /// devices, is reported as fallback and is classified by <see cref="OpenBiosProbeAccounting"/>. With
    /// <c>Differential</c> the interpreter backend runs the same ROM and disc first and both are compared
    /// (<see cref="ProbeGuestState"/>) at the shell and executable entries plus every <c>CompareAt</c> PC. <c>StopAt</c>
    /// ends each run before it executes that PC the first time (the host sees only fallback PCs).
    /// <c>Standalone</c> (Phase 2 Step B, Issue #739) runs the same firmware through the standalone embedded-image
    /// artifact: the ROM image and the run's initial state are compiled into the generated C source (no
    /// artifact-input.txt/artifact-image.bin are written) and the artifact attaches the shared Runtime host bridge over
    /// the existing <c>--host-transfer</c> path. It changes only how the ROM is delivered to the engine, never whether
    /// the result is a verified boot.
    /// </summary>
    private static int RunGeneratedHost(GeneratedHostProbe probe, bool json, TextWriter output, TextWriter error)
    {
        var wall = Stopwatch.StartNew();
        var clock = Stopwatch.StartNew();
        var firmware = probe.Firmware;
        var codeWords = probe.CodeBytes == 0 ? firmware.Words : firmware.Words.Take((int)Math.Min(probe.CodeBytes / 4, (uint)firmware.Words.Count)).ToArray();
        var image = ReachableProgramBuilder.BuildFirmwareImage(OpenBiosFirmware.ResetVector, codeWords, OpenBiosFirmware.ResetVector, probe.Roots);
        var manifest = probe.LoadImagesPath is null ? LoadImageManifest.None : LoadImageManifest.Read(probe.LoadImagesPath, firmware, probe.Disc);
        var observedPcs = new HashSet<uint>(manifest.Interpreted) { OpenBiosBootMonitor.ShellLoadAddress, ExecutableBoundary, 0x80000080, 0x000026A4 };
        observedPcs.UnionWith(probe.CompareAt);
        if (probe.StopAt is { } stopPc) observedPcs.Add(stopPc);
        var loadedImages = manifest.Images.Select(i => (i.Name, Build: ReachableProgramBuilder.BuildLoadedImage(i.LoadAddress, i.Words, i.Roots, observedPcs))).ToArray();
        var loadedCode = new LoadedCodeTable(loadedImages.Select(static i => i.Build));
        var buildMs = clock.Elapsed.TotalMilliseconds;
        var boundaryPcs = RequiredBoundaries(probe.CompareAt, probe.StopAt);

        Dictionary<uint, ProbeGuestState>? reference = null;
        OpenBiosBootReport? referenceMilestones = null;
        ProbeGuestState? referenceInterrupt = null, hostInterrupt = null;
        var referenceSeconds = new Dictionary<uint, double>();
        var referenceStopped = false;
        double referenceMs = 0;
        if (probe.Differential)
        {
            clock.Restart();
            reference = [];
            var backend = new OpenBiosBootBackend(firmware, probe.Disc);
            using var interpreter = (InterpreterTitleExecutionEngine)backend.CreateEngine();
            var referenceMonitor = new OpenBiosBootMonitor(() => (interpreter.Cop0Diagnostics.Cause, interpreter.Cop0Diagnostics.Epc, interpreter.Cop0Diagnostics.BadVAddr));
            ulong fetches = 0;
            interpreter.FetchObserver = pc =>
            {
                fetches++;
                referenceMonitor.OnFetch(pc);
                if (pc == 0x80000080 && ((interpreter.Cop0Diagnostics.Cause >> 2) & 31) == 0 && referenceInterrupt is null)
                    referenceInterrupt = ProbeGuestState.Capture(interpreter, pc, fetches);
                if (boundaryPcs.Contains(pc) && !reference.ContainsKey(pc))
                {
                    reference[pc] = ProbeGuestState.Capture(interpreter, pc, fetches);
                    referenceSeconds[pc] = clock.Elapsed.TotalSeconds;
                }

                if (pc == probe.StopAt)
                {
                    interpreter.StopRequested = referenceStopped = true;
                }

                if ((fetches & ProgressMask) == 0)
                {
                    error.WriteLine($"openbios-probe: reference interpreter: {fetches} fetches, {clock.Elapsed.TotalSeconds:F0} s");
                }
            };
            new ExecutionOrchestrator().Execute(interpreter, handoff: null, new TitleExecutionRequest(
                backend.EntryPc, new uint[TitleExecutionRequest.GprCount], 0, 0, [], probe.Segments, probe.SegmentBudget));
            referenceMilestones = referenceMonitor.Evaluate(interpreter.ReadGuestWord);
            referenceMs = clock.Elapsed.TotalMilliseconds;
        }

        var directory = Path.Combine(Path.GetTempPath(), "psxrecomp-openbios-host-" + Guid.NewGuid().ToString("N"));
        var boundaries = new Dictionary<uint, ProbeGuestState>();
        InterpreterTitleExecutionEngine? current = null;
        var monitor = new OpenBiosBootMonitor(() => (current!.Cop0Diagnostics.Cause, current.Cop0Diagnostics.Epc, current.Cop0Diagnostics.BadVAddr));
        var accounting = new OpenBiosProbeAccounting();
        Func<(uint, uint)> readCop0 = () => (current!.Cop0Diagnostics.Cause, current.Cop0Diagnostics.Epc);
        Func<uint, uint> readWord = address => current!.ReadGuestWord(address);
        var hostStopped = false;
        ulong transitions = 0;
        RecompiledHostExecutionEngine? hostEngine = null;
        string FallbackCause(uint pc) => observedPcs.Contains(pc) ? "observation-point"
            : ClassifyFallback(pc, manifest, loadedCode, codeWords.Count());
        var nativeAtBoundary = new Dictionary<uint, object>();
        var budget = (uint)Math.Min((ulong)probe.Segments * probe.SegmentBudget, uint.MaxValue);
        try
        {
            var measuredBuild = new MeasuredBuild(new GeneratedHostBuildService(TimeSpan.FromMinutes(3)));
            // The embedded artifact (Phase 2 Step B) is generated inside RunSegment, so its gcc build is timed inside
            // this clock's window rather than the construction window compileMs covers. It is known by the time any
            // fallback boundary fires (the build precedes execution). The offset is subtracted from the reported run
            // and boundary seconds so they exclude compilation and stay comparable with the build-free reference run;
            // the file-driven path is a no-op (offset 0).
            double BuildOffsetMs() => probe.Standalone ? measuredBuild.Milliseconds : 0;
            clock.Restart();
            using var engine = new RecompiledHostExecutionEngine(
                image.Program,
                firmware.Words,
                OpenBiosFirmware.ResetVector,
                measuredBuild,
                directory,
                biosRuntimeFactory: null,
                // One fallback segment may run as long as the whole probe: the shell and a loaded executable legitimately
                // run in RAM for most of a boot, so a shorter segment bound would stop a correct run.
                mixedFallback: new MixedFallbackOptions(budget, uint.MaxValue),
                guestFirmware: true,
                runTimeout: TimeSpan.FromMinutes(30),
                disc: probe.Disc, loadedCode: loadedCode,
                // Phase 2 Step B (Issue #739): the probe can run the firmware through
                // the standalone embedded-image artifact (Phase 1 / Step A path):
                // the ROM image and the run's initial state are embedded in the
                // generated C source, no artifact-input.txt/artifact-image.bin are
                // written, and the artifact attaches the shared Runtime host bridge
                // over the existing --host-transfer path.
                embedImage: probe.Standalone)
            {
                FallbackFetchObserver = (interpreter, pc) =>
                {
                    current = interpreter;
                    monitor.OnFetch(pc);
                    accounting.OnFetch(pc, readCop0, readWord);
                    if (pc == 0x80000080 && ((interpreter.Cop0Diagnostics.Cause >> 2) & 31) == 0 && hostInterrupt is null)
                        hostInterrupt = ProbeGuestState.Capture(interpreter, pc, accounting.FallbackFetches);
                    var fetches = accounting.FallbackFetches;
                    if (boundaryPcs.Contains(pc) && !boundaries.ContainsKey(pc))
                    {
                        boundaries[pc] = ProbeGuestState.Capture(interpreter, pc, fetches);
                        nativeAtBoundary[pc] = new { nativeInstructions = hostEngine?.NativeRetiredInstructions, seconds = RunSecondsExcludingBuild(clock.Elapsed.TotalSeconds, BuildOffsetMs()),
                            mmioRoundTrips = hostEngine?.HostRoundTrips?.MmioAccesses, timeReportRoundTrips = hostEngine?.HostRoundTrips?.TimeReports };
                    }

                    if (pc == probe.StopAt)
                    {
                        interpreter.StopRequested = hostStopped = true;
                    }

                    if ((fetches & ProgressMask) == 0)
                    {
                        error.WriteLine($"openbios-probe: generated host: {fetches} fallback fetches, {clock.Elapsed.TotalSeconds:F0} s");
                    }
                },
                FallbackTransitionObserver = transition =>
                {
                    accounting.OnTransition(transition);
                    if (++transitions % 10000 == 0)
                        error.WriteLine($"openbios-probe: {transitions} artifact/interpreter handoffs, {clock.Elapsed.TotalSeconds:F0} s");
                },
            };
            hostEngine = engine;
            var compileMs = clock.Elapsed.TotalMilliseconds;

            // The artifact's first dispatch unit is the reset-vector block (it is always the build's entry); fallback
            // fetches are the only other fetches this engine can observe, so fetch indices below count only those.
            monitor.OnFetch(OpenBiosFirmware.ResetVector);
            clock.Restart();
            var result = new ExecutionOrchestrator().Execute(engine, handoff: null, new TitleExecutionRequest(
                OpenBiosFirmware.ResetVector, new uint[TitleExecutionRequest.GprCount], 0, 0, [], 1, budget));
            var runMs = clock.Elapsed.TotalMilliseconds;

            // The embedded-image artifact (Phase 2 Step B) is generated at the first RunSegment, so its gcc build is
            // timed inside runMs rather than the construction window compileMs covers. Attribute it back to codegen/gcc
            // so codegenMs stays non-negative and nativeAndHostMs is not inflated by compilation.
            var reportedCompileMs = compileMs + BuildOffsetMs();
            var reportedRunMs = runMs - BuildOffsetMs();

            var lastRam = boundaries.Values.OrderBy(static b => b.AtFetch).LastOrDefault()?.Ram;
            var report = monitor.Evaluate(address => lastRam is null || (address & 0x1FFFFFFFu) > ProbeGuestState.RamBytes - 4
                ? 0u
                : BitConverter.ToUInt32(lastRam, (int)(address & 0x1FFFFFFFu)));
            var kernelBooted = report.KernelBooted;
            var evidence = engine.FallbackEvidence;
            var costs = engine.FallbackTimings;
            if (probe.FallbackTargetsPath is not null)
            {
                // Issue #773: every distinct fallback target, in PC order, as its own document. The top-12 summary in the
                // report above is unchanged; this file is written only when requested.
                var allTargets = (evidence?.Targets ?? []).Select(t => new
                {
                    pc = Hex(t.Target),
                    cause = FallbackCause(t.Target),
                    entries = t.Entries,
                    instructions = t.Instructions,
                    lastReturnPc = Hex(t.LastReturnPc),
                }).ToArray();
                File.WriteAllText(probe.FallbackTargetsPath, JsonSerializer.Serialize(
                    new { format = PSXRecomp.Core.Recompiler.ObservedRootPlanner.TargetFormat, sha256 = probe.Hash, targets = allTargets },
                    new JsonSerializerOptions { WriteIndented = true }));
            }

            var document = new
            {
                kind = "openbios-probe",
                engine = "generated-host",
                standalone = probe.Standalone,
                sha256 = probe.Hash,
                bootVerified = kernelBooted && report.TitleStarted,
                kernelBooted,
                milestones = report,
                fetchIndices = "fallback-interpreter fetches only (native blocks are not observed per instruction)",
                titleStarted = report.TitleStarted,
                executionState = result.State.ToString(),
                guestPc = result.FinalSnapshot is { } s ? $"0x{s.PC:X8}" : null,
                diagnosticCode = result.DiagnosticCode,
                diagnosticMessage = result.DiagnosticMessage,
                stopAt = probe.StopAt is { } stop
                    ? new { pc = Hex(stop), reachedByInterpreter = probe.Differential ? referenceStopped : (bool?)null, reachedByHost = hostStopped }
                    : null,
                build = new
                {
                    imageInstructions = image.ImageInstructionCount,
                    nativeInstructions = image.NativeInstructionCount,
                    blocks = image.Program.Blocks.Count,
                    roots = probe.Roots.Count,
                    staticFallbackTargets = image.FallbackTargets.Count,
                    symbols = probe.Symbols?.Count,
                },
                loadedImages = loadedImages.Select(static i => new { name = i.Name, blocks = i.Build.Blocks.Count, nativeInstructions = i.Build.NativeInstructionCount, skippedEntries = i.Build.SkippedEntries.Select(Hex).ToArray() }).ToArray(),
                loadedCodeVersions = loadedCode.Blocks.Count,
                interpretedEntries = observedPcs.Order().Select(Hex).ToArray(),
                precompileMilliseconds = buildMs + compileMs,
                atBoundary = nativeAtBoundary.ToDictionary(static b => Hex(b.Key), static b => b.Value),
                execution = new
                {
                    nativeInstructions = engine.NativeRetiredInstructions,
                    fallbackInstructions = evidence?.FallbackInstructions,
                    fallbackTransitions = evidence?.Transitions,
                    fallbackReturns = evidence?.Returns,
                    pagesToInterpreter = evidence?.PagesToInterpreter,
                    pagesToArtifact = evidence?.PagesToArtifact,
                    distinctFallbackTargets = evidence?.Targets.Count,
                    topFallbackTargets = evidence?.Targets.OrderByDescending(static t => t.Instructions).Take(12)
                        .Select(t => $"0x{t.Target:X8}:entries={t.Entries}:instructions={t.Instructions}:cause={FallbackCause(t.Target)}").ToArray(),
                    timings = costs,
                    mmioRoundTrips = engine.HostRoundTrips?.MmioAccesses,
                    timeReportRoundTrips = engine.HostRoundTrips?.TimeReports,
                },
                accounting = accounting.Report(
                    engine.NativeRetiredInstructions, evidence?.FallbackInstructions, image.Program.Blocks.Select(static b => b.EntryPc).Concat(loadedCode.Blocks.Select(static b => b.Block.EntryPc)), probe.Symbols, FallbackCause),
                timings = new
                {
                    buildMs = Math.Round(buildMs, 1),
                    compileMs = Math.Round(reportedCompileMs, 1),
                    codegenMs = Math.Round(reportedCompileMs - measuredBuild.Milliseconds, 1),
                    gccMs = Math.Round(measuredBuild.Milliseconds, 1),
                    referenceMs = Math.Round(referenceMs, 1),
                    runMs = Math.Round(reportedRunMs, 1),
                    fallbackMs = costs is null ? (double?)null : Math.Round(costs.FallbackMilliseconds, 1),
                    transferMs = costs is null ? (double?)null : Math.Round(costs.TransferMilliseconds, 1),
                    nativeAndHostMs = Math.Round(reportedRunMs - (costs?.FallbackMilliseconds ?? 0) - (costs?.TransferMilliseconds ?? 0), 1),
                    totalMs = Math.Round(wall.Elapsed.TotalMilliseconds, 1),
                },
                aot = new { generatedCBytes = measuredBuild.SourceBytes, artifactBytes = measuredBuild.ArtifactBytes },
                boundaries = boundaries.OrderBy(static b => b.Key).ToDictionary(static b => Hex(b.Key), static b => b.Value.Describe()),
                differential = reference?.OrderBy(static b => b.Key).ToDictionary(
                    static b => Hex(b.Key),
                    b => boundaries.TryGetValue(b.Key, out var host)
                        ? ProbeGuestState.Compare(b.Value, host)
                        : (object)"not reached by the generated host (a PC inside a compiled block is not observable)"),
                referenceBoundariesMissing = reference is null ? null : boundaries.Keys.Except(reference.Keys).Order().Select(Hex).ToArray(),
                referenceSecondsAtBoundary = referenceSeconds.OrderBy(static b => b.Key).ToDictionary(static b => Hex(b.Key), static b => b.Value),
                firstInterrupt = new { interpreter = referenceInterrupt?.Describe(), host = hostInterrupt?.Describe() },
                firstInterruptDifferential = referenceInterrupt is null ? null : hostInterrupt is null
                    ? (object)"not reached by generated host" : ProbeGuestState.Compare(referenceInterrupt, hostInterrupt),
                milestoneComparison = referenceMilestones is null ? null : new
                {
                    match = WithoutFetchIndices(referenceMilestones) == WithoutFetchIndices(report),
                    interpreter = referenceMilestones,
                },
            };
            var node = JsonSerializer.SerializeToNode(document)!.AsObject();
            node["consistency"] = Consistency(node, boundaryPcs, probe.StopAt);
            var parityPassed = node["consistency"]?["differentialPass"]?.GetValue<bool>() ?? !probe.Differential;
            var text = node.ToJsonString(json ? null : new JsonSerializerOptions { WriteIndented = true });
            if (!json)
            {
                foreach (var line in Summarize(JsonNode.Parse(text)!))
                {
                    output.WriteLine(line);
                }
            }

            output.WriteLine(text);
            return kernelBooted && parityPassed ? 0 : 2;
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Seconds elapsed on the run clock, excluding the embedded artifact's gcc build when it is timed inside the run
    /// window (Phase 2 Step B). The reported run and boundary seconds both use this so they stay comparable with the
    /// build-free reference run; the file-driven path passes a zero offset and is unchanged.
    /// </summary>
    internal static double RunSecondsExcludingBuild(double elapsedSeconds, double buildOffsetMilliseconds) =>
        elapsedSeconds - buildOffsetMilliseconds / 1000.0;

    /// <summary>Retains explicit comparisons while choosing defaults appropriate for the requested stop.</summary>
    internal static HashSet<uint> RequiredBoundaries(IEnumerable<uint> compareAt, uint? stopAt)
    {
        var required = new HashSet<uint>(compareAt) { OpenBiosBootMonitor.ShellLoadAddress };
        if (stopAt != OpenBiosBootMonitor.ShellLoadAddress) required.Add(ExecutableBoundary);
        if (stopAt is { } stop) required.Add(stop);
        return required;
    }

    /// <summary>A differential pass requires every requested boundary, matching device/CPU/RAM state and milestones.</summary>
    internal static JsonNode Consistency(JsonObject document, IReadOnlySet<uint> expected, uint? stopAt)
    {
        if (document["differential"] is not JsonObject differential)
            return new JsonObject { ["differentialPass"] = null };
        var matched = differential.Count(static b => b.Value is JsonObject o && o["match"]?.GetValue<bool>() == true);
        var required = expected.ToArray();
        var missing = required.Where(pc => !differential.ContainsKey(Hex(pc))).Select(Hex).ToArray();
        var milestones = document["milestoneComparison"]?["match"]?.GetValue<bool>() ?? false;
        var hostOnly = document["referenceBoundariesMissing"]?.AsArray().Count ?? 0;
        var interrupt = document["firstInterruptDifferential"];
        var interruptMatches = interrupt is null || interrupt is JsonObject irq && irq["match"]?.GetValue<bool>() == true;
        return new JsonObject
        {
            ["differentialPass"] = differential.Count != 0 && matched == differential.Count && missing.Length == 0 && hostOnly == 0 && milestones && interruptMatches,
            ["boundariesCompared"] = differential.Count,
            ["boundariesMatched"] = matched,
            ["requiredBoundariesMissing"] = JsonSerializer.SerializeToNode(missing),
            ["milestonesMatch"] = milestones,
            ["firstInterruptMatches"] = interruptMatches,
        };
    }

    /// <summary>Fetch indices count different things in the two runs (all fetches vs fallback fetches only); the milestones themselves must agree.</summary>
    private static OpenBiosBootReport WithoutFetchIndices(OpenBiosBootReport report) => report with
    {
        ShellEnteredAtFetch = null,
        FirstUnexpectedException = report.FirstUnexpectedException is { } e ? e with { AtFetch = 0 } : null,
    };

    /// <summary>Compatibility helper for reports whose expected boundaries are their observed entries.</summary>
    internal static JsonNode Consistency(JsonObject document) => Consistency(
        document,
        document["differential"] is JsonObject observed
            ? observed.Select(static b => uint.Parse(b.Key[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToHashSet()
            : new HashSet<uint>(),
        stopAt: null);

    /// <summary>Times the artifact's gcc build and records the generated C and binary sizes (Issue #732 measurement).</summary>
    private sealed class MeasuredBuild(IGeneratedHostBuildService inner) : IGeneratedHostBuildService
    {
        public double Milliseconds { get; private set; }
        public long SourceBytes { get; private set; }
        public long? ArtifactBytes { get; private set; }

        public GeneratedHostBuildResult Build(GeneratedHostBuildRequest request)
        {
            var clock = Stopwatch.StartNew();
            var result = inner.Build(request);
            Milliseconds = clock.Elapsed.TotalMilliseconds;
            SourceBytes = System.Text.Encoding.UTF8.GetByteCount(request.Source);
            ArtifactBytes = result.Artifact is { } artifact ? new FileInfo(artifact.BinaryPath).Length : null;
            return result;
        }
    }

    /// <summary>The compact human summary printed ahead of the indented document without <c>--json</c>.</summary>
    internal static IEnumerable<string> Summarize(JsonNode document)
    {
        var a = document["accounting"]!;
        var native = a["native"]!;
        var fallback = a["fallback"]!;
        var transitions = a["transitions"]!;
        yield return $"OpenBIOS generated-host probe: {document["executionState"]} ({document["diagnosticCode"]}); kernelBooted={document["kernelBooted"]}, titleStarted={document["titleStarted"]}, standalone={document["standalone"]}";
        yield return $"Instructions: native {native["instructions"]} ({native["region"]?.ToString() ?? "unattributed"}); fallback {fallback["retiredInstructions"]} retired, " +
                     $"{fallback["fetches"]} fetched; transitions {transitions["total"]} ({transitions["indirect"]} indirect)";
        yield return "Regions (fallback fetches / transitions): " + string.Join(", ", a["regions"]!.AsArray()
            .Select(r => $"{r!["region"]} {Percent(r["fallbackFetchShare"])}/{Percent(r["transitionShare"])}"));
        foreach (var r in transitions["byReason"]!.AsArray())
        {
            yield return $"  reason {r!["reason"]}: {r["transitions"]} transitions ({Percent(r["transitionShare"])}), {r["retiredInstructions"]} retired ({Percent(r["retiredShare"])})";
        }

        foreach (var c in transitions["byAotClass"]!.AsArray())
        {
            yield return $"  aot-class {c!["aotClass"]}: {c["transitions"]} transitions ({Percent(c["transitionShare"])}), {c["retiredInstructions"]} retired ({Percent(c["retiredShare"])})";
        }

        foreach (var e in a["hotEntries"]!.AsArray().Take(5))
        {
            yield return $"  entry {e!["pc"]} {e["symbol"]} [{e["reason"]}]: {e["transitions"]} transitions, {e["retiredInstructions"]} retired";
        }

        foreach (var p in a["hotPcs"]!.AsArray().Take(5))
        {
            yield return $"  pc {p!["pc"]} {p["symbol"]}: {p["fetches"]} fetches ({Percent(p["share"])})";
        }

        if (document["differential"] is JsonObject differential)
        {
            foreach (var (pc, value) in differential)
            {
                yield return $"Differential {pc}: " + (value is JsonObject o
                    ? o["match"]!.GetValue<bool>() ? "match" : "MISMATCH, first " + o["firstMismatch"]!.ToJsonString()
                    : value?.ToString());
            }
        }

        var t = document["timings"]!;
        yield return $"Timings ms: analysis {t["buildMs"]}, codegen {t["codegenMs"]}, gcc {t["gccMs"]}, reference {t["referenceMs"]}, run {t["runMs"]} " +
                     $"(fallback {t["fallbackMs"]}, transfer {t["transferMs"]}, native+host {t["nativeAndHostMs"]}), total {t["totalMs"]}";
        yield return $"AOT code: {document["aot"]?["generatedCBytes"]} bytes of C, {document["aot"]?["artifactBytes"]} bytes of artifact; " +
                     $"differentialPass={document["consistency"]?["differentialPass"]?.ToString() ?? "n/a"}";
    }

    private static string Percent(JsonNode? share) => share is null ? "-" : $"{share.GetValue<double>() * 100:F1}%";

    /// <summary>Classifies code coverage using guest physical aliases while retaining the caller's original PC.</summary>
    internal static string ClassifyFallback(uint pc, LoadImageManifest manifest, LoadedCodeTable loadedCode, int romWords)
    {
        static uint Physical(uint address)
        {
            if (address >= 0xC0000000u) return address;
            var physical = address <= 0x7FFFFFFFu ? address : address & 0x1FFFFFFFu;
            return physical < 0x00800000u ? physical & 0x001FFFFFu : physical;
        }

        var at = Physical(pc);
        if (manifest.Interpreted.Any(point => Physical(point) == at)) return "observation-point";
        if (loadedCode.Contains(pc)) return "version-mismatch";
        if (manifest.Images.Any(image => at >= Physical(image.LoadAddress)
            && (ulong)at < (ulong)Physical(image.LoadAddress) + (ulong)image.Words.Length * 4))
            return "aot-coverage-gap:loaded-image";
        var rom = Physical(OpenBiosFirmware.ResetVector);
        return at >= rom && (ulong)at < (ulong)rom + (ulong)romWords * 4
            ? "aot-coverage-gap:rom" : "unknown-code";
    }

    /// <summary>One hexadecimal block-entry PC per line (blank lines ignored); an explicit input, never guessed.</summary>
    private static IReadOnlyList<uint> ReadRoots(string path) =>
        File.ReadAllLines(path)
            .Select(static line => line.Trim())
            .Where(static line => line.Length != 0)
            .Select(static line => uint.Parse(line.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? line[2..] : line, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            .Distinct()
            .ToArray();

    private static string Hex(uint v) => $"0x{v:X8}";

    internal static OpenedDisc OpenDisc(string path) => DiscSectorSourceOpener.Open(path);

    private static object BuildStop(InterpreterTitleExecutionEngine engine, RecompilerStateSnapshot? snapshot)
    {
        var cop0 = engine.Cop0Diagnostics;
        var trace = engine.RecentTrace;
        return new
        {
            pc = Hex(snapshot?.PC ?? 0),
            sr = Hex(cop0.Sr), cause = Hex(cop0.Cause), epc = Hex(cop0.Epc), badVAddr = Hex(cop0.BadVAddr),
            gpr = snapshot?.Gpr.Select(Hex).ToArray(),
            lastFetches = trace.Fetches.TakeLast(16).Select(e => $"{Hex(e.Pc)}:{Hex(e.Word)}").ToArray(),
            lastTransfers = trace.Transfers.TakeLast(16).Select(t => $"{Hex(t.From.Pc)}:{Hex(t.From.Word)}->{Hex(t.To)}").ToArray()
        };
    }
}
