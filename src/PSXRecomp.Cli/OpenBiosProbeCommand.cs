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

    internal static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        const string usage = "usage: psxrecomp openbios-probe <openbios.bin> [--segment-budget <n>] [--segments <n>] [--disc <image.chd|image.bin>] [--capture-at <hex-pc>] " +
                             "[--engine interpreter|generated-host] [--roots <file>] [--code-bytes <n>] [--differential] [--compare-at <hex-pc>]... " +
                             "[--stop-at <hex-pc>] [--symbols <nm-output>] [--load-images <manifest>] [--json]";
        if (args.Count == 0 || args.Count > 64 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            error.WriteLine(usage);
            return 1;
        }

        uint segmentBudget = 100_000, segments = 10, codeBytes = 0;
        bool json = false, differential = false;
        string? discPath = null, rootsPath = null, symbolsPath = null, loadImagesPath = null, engineKind = "interpreter";
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
            if (option is "--capture-at" or "--compare-at" or "--stop-at")
            {
                if (++i >= args.Count || !uint.TryParse(args[i].Replace("0x", ""), NumberStyles.HexNumber, null, out var pc))
                {
                    error.WriteLine($"openbios-probe: {option} requires a hexadecimal PC");
                    return 1;
                }

                if (option == "--capture-at") capturePc = pc;
                else if (option == "--stop-at") stopAt = pc;
                else compareAt.Add(pc);
                continue;
            }

            if (option is "--disc" or "--roots" or "--engine" or "--symbols" or "--load-images")
            {
                if (++i >= args.Count)
                {
                    error.WriteLine($"openbios-probe: {option} requires a value");
                    return 1;
                }

                if (option == "--disc") discPath = args[i];
                else if (option == "--roots") rootsPath = args[i];
                else if (option == "--load-images") loadImagesPath = args[i];
                else if (option == "--symbols") symbolsPath = args[i];
                else engineKind = args[i];
                continue;
            }

            if (option != "--segment-budget" && option != "--segments" && option != "--code-bytes")
            {
                error.WriteLine($"openbios-probe: unknown option {option}");
                return 1;
            }

            if (++i >= args.Count || !uint.TryParse(args[i], out var number) || number == 0)
            {
                error.WriteLine($"openbios-probe: {option} requires a positive integer");
                return 1;
            }

            if (option == "--segments") segments = number;
            else if (option == "--code-bytes") codeBytes = number;
            else segmentBudget = number;
        }

        if (engineKind is not ("interpreter" or "generated-host") || (engineKind == "generated-host" && rootsPath is null)
            || (engineKind != "generated-host" && (differential || compareAt.Count != 0 || stopAt is not null || symbolsPath is not null || loadImagesPath is not null))
            || (compareAt.Count != 0 && !differential))
        {
            error.WriteLine("openbios-probe: --engine generated-host requires --roots; --differential, --stop-at and --symbols require --engine generated-host; " +
                            "--compare-at requires --differential");
            error.WriteLine(usage);
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
            var symbols = symbolsPath is null ? null : ProbeSymbols.Parse(File.ReadLines(symbolsPath));
            return engineKind == "generated-host"
                ? RunGeneratedHost(new GeneratedHostProbe(
                    firmware, hash, disc?.Source, ReadRoots(rootsPath!), codeBytes, segments, segmentBudget, differential, compareAt, stopAt, symbols, loadImagesPath), json, output, error)
                : RunInterpreter(firmware, hash, disc?.Source, segments, segmentBudget, capturePc, json, output);
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or InvalidDataException
                or FileNotFoundException or DirectoryNotFoundException or DllNotFoundException or FormatException)
        {
            error.WriteLine($"openbios-probe: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int RunInterpreter(
        OpenBiosFirmware firmware, string hash, ICdSectorSource? disc, uint segments, uint segmentBudget, uint? capturePc, bool json, TextWriter output)
    {
        var backend = new OpenBiosBootBackend(firmware, disc);
        using var engine = backend.CreateEngine();
        object? captured = null;
        var observed = engine as InterpreterTitleExecutionEngine;
        var monitor = new OpenBiosBootMonitor(observed is null ? null : () => (observed.Cop0Diagnostics.Cause, observed.Cop0Diagnostics.Epc, observed.Cop0Diagnostics.BadVAddr));
        if (observed is not null)
        {
            observed.FetchObserver = pc =>
            {
                monitor.OnFetch(pc);
                if (pc == capturePc && captured is null)
                {
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
                captured
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
        uint Segments, uint SegmentBudget, bool Differential, IReadOnlySet<uint> CompareAt, uint? StopAt, ProbeSymbols? Symbols, string? LoadImagesPath);

    /// <summary>
    /// Issue #732: runs the ROM through the generated host. The ROM's code (its first <c>CodeBytes</c> bytes, or all of
    /// it) is compiled from the reset vector plus the explicit roots; everything that has no block — kernel code copied
    /// to RAM, vectors, the shell, a loaded executable — runs in the mixed-execution interpreter fallback over the same
    /// devices, is reported as fallback and is classified by <see cref="OpenBiosProbeAccounting"/>. With
    /// <c>Differential</c> the interpreter backend runs the same ROM and disc first and both are compared
    /// (<see cref="ProbeGuestState"/>) at the shell and executable entries plus every <c>CompareAt</c> PC. <c>StopAt</c>
    /// ends each run before it executes that PC the first time (the host sees only fallback PCs).
    /// </summary>
    private static int RunGeneratedHost(GeneratedHostProbe probe, bool json, TextWriter output, TextWriter error)
    {
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
        var boundaryPcs = new HashSet<uint>(probe.CompareAt) { OpenBiosBootMonitor.ShellLoadAddress, ExecutableBoundary };
        if (probe.StopAt is { } boundary) boundaryPcs.Add(boundary);

        Dictionary<uint, ProbeGuestState>? reference = null;
        OpenBiosBootReport? referenceMilestones = null;
        ProbeGuestState? referenceInterrupt = null, hostInterrupt = null;
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
            : loadedCode.Contains(pc) ? "version-mismatch"
            : manifest.Images.Any(i => pc >= i.LoadAddress && (ulong)pc < (ulong)i.LoadAddress + (ulong)i.Words.Length * 4) ? "aot-coverage-gap:loaded-image"
            : pc >= OpenBiosFirmware.ResetVector && (ulong)pc < (ulong)OpenBiosFirmware.ResetVector + (ulong)codeWords.Count() * 4 ? "aot-coverage-gap:rom"
            : "unknown-code";
        var nativeAtBoundary = new Dictionary<uint, object>();
        var budget = (uint)Math.Min((ulong)probe.Segments * probe.SegmentBudget, uint.MaxValue);
        try
        {
            clock.Restart();
            using var engine = new RecompiledHostExecutionEngine(
                image.Program,
                firmware.Words,
                OpenBiosFirmware.ResetVector,
                new GeneratedHostBuildService(TimeSpan.FromMinutes(3)),
                directory,
                biosRuntimeFactory: null,
                // One fallback segment may run as long as the whole probe: the shell and a loaded executable legitimately
                // run in RAM for most of a boot, so a shorter segment bound would stop a correct run.
                mixedFallback: new MixedFallbackOptions(budget, uint.MaxValue),
                guestFirmware: true,
                runTimeout: TimeSpan.FromMinutes(30),
                disc: probe.Disc, loadedCode: loadedCode)
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
                        nativeAtBoundary[pc] = new { nativeInstructions = hostEngine?.NativeRetiredInstructions, seconds = clock.Elapsed.TotalSeconds,
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
                        error.WriteLine($"openbios-probe: generated host: {transitions} transitions, {hostEngine?.NativeRetiredInstructions} native instructions, {clock.Elapsed.TotalSeconds:F0} s");
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

            var lastRam = boundaries.Values.OrderBy(static b => b.AtFetch).LastOrDefault()?.Ram;
            var report = monitor.Evaluate(address => lastRam is null || (address & 0x1FFFFFFFu) > ProbeGuestState.RamBytes - 4
                ? 0u
                : BitConverter.ToUInt32(lastRam, (int)(address & 0x1FFFFFFFu)));
            var kernelBooted = report.KernelBooted;
            var evidence = engine.FallbackEvidence;
            var costs = engine.FallbackTimings;
            var document = new
            {
                kind = "openbios-probe",
                engine = "generated-host",
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
                    compileMs = Math.Round(compileMs, 1),
                    referenceMs = Math.Round(referenceMs, 1),
                    runMs = Math.Round(runMs, 1),
                    fallbackMs = costs is null ? (double?)null : Math.Round(costs.FallbackMilliseconds, 1),
                    transferMs = costs is null ? (double?)null : Math.Round(costs.TransferMilliseconds, 1),
                    nativeAndHostMs = Math.Round(runMs - (costs?.FallbackMilliseconds ?? 0) - (costs?.TransferMilliseconds ?? 0), 1),
                },
                boundaries = boundaries.OrderBy(static b => b.Key).ToDictionary(static b => Hex(b.Key), static b => b.Value.Describe()),
                differential = reference?.OrderBy(static b => b.Key).ToDictionary(
                    static b => Hex(b.Key),
                    b => boundaries.TryGetValue(b.Key, out var host)
                        ? ProbeGuestState.Compare(b.Value, host)
                        : (object)"not reached by the generated host (a PC inside a compiled block is not observable)"),
                referenceBoundariesMissing = reference is null ? null : boundaries.Keys.Except(reference.Keys).Order().Select(Hex).ToArray(),
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

    /// <summary>A differential pass requires every requested boundary, matching device/CPU/RAM state and milestones.</summary>
    internal static JsonNode Consistency(JsonObject document, IReadOnlySet<uint> expected, uint? stopAt)
    {
        if (document["differential"] is not JsonObject differential)
            return new JsonObject { ["differentialPass"] = null };
        var matched = differential.Count(static b => b.Value is JsonObject o && o["match"]?.GetValue<bool>() == true);
        // A stop before the EXE deliberately requests a shorter gate; explicit --compare-at points remain required.
        var required = expected.Where(pc => stopAt != OpenBiosBootMonitor.ShellLoadAddress || pc != ExecutableBoundary).ToArray();
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

    /// <summary>The compact human summary printed ahead of the indented document without <c>--json</c>.</summary>
    internal static IEnumerable<string> Summarize(JsonNode document)
    {
        var a = document["accounting"]!;
        var native = a["native"]!;
        var fallback = a["fallback"]!;
        var transitions = a["transitions"]!;
        yield return $"OpenBIOS generated-host probe: {document["executionState"]} ({document["diagnosticCode"]}); kernelBooted={document["kernelBooted"]}, titleStarted={document["titleStarted"]}";
        yield return $"Instructions: native {native["instructions"]} ({native["region"]?.ToString() ?? "unattributed"}); fallback {fallback["retiredInstructions"]} retired, " +
                     $"{fallback["fetches"]} fetched; transitions {transitions["total"]} ({transitions["indirect"]} indirect)";
        yield return "Regions (fallback fetches / transitions): " + string.Join(", ", a["regions"]!.AsArray()
            .Select(r => $"{r!["region"]} {Percent(r["fallbackFetchShare"])}/{Percent(r["transitionShare"])}"));
        foreach (var r in transitions["byReason"]!.AsArray())
        {
            yield return $"  reason {r!["reason"]}: {r["transitions"]} transitions ({Percent(r["transitionShare"])}), {r["retiredInstructions"]} retired ({Percent(r["retiredShare"])})";
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
        yield return $"Timings ms: build {t["buildMs"]}, compile {t["compileMs"]}, reference {t["referenceMs"]}, run {t["runMs"]} " +
                     $"(fallback {t["fallbackMs"]}, transfer {t["transferMs"]}, native+host {t["nativeAndHostMs"]})";
    }

    private static string Percent(JsonNode? share) => share is null ? "-" : $"{share.GetValue<double>() * 100:F1}%";

    /// <summary>One hexadecimal block-entry PC per line (blank lines ignored); an explicit input, never guessed.</summary>
    private static IReadOnlyList<uint> ReadRoots(string path) =>
        File.ReadAllLines(path)
            .Select(static line => line.Trim())
            .Where(static line => line.Length != 0)
            .Select(static line => uint.Parse(line.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? line[2..] : line, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            .Distinct()
            .ToArray();

    private static string Hex(uint v) => $"0x{v:X8}";

    /// <summary>A CHD image (by its "MComprHD" magic) or a raw single-track image of 2352-byte sectors.</summary>
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
        // ponytail: a raw image is read whole into memory; stream it when full-size .bin dumps are probed.
        return new OpenedDisc(new RawCdSectorSource(File.ReadAllBytes(path)), null);
    }

    private sealed record OpenedDisc(ICdSectorSource Source, Stream? Stream) : IDisposable
    {
        public void Dispose() => Stream?.Dispose();
    }

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
