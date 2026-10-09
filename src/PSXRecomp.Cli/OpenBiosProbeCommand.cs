using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
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
                             "[--engine interpreter|generated-host] [--roots <file>] [--code-bytes <n>] [--differential] [--json]";
        if (args.Count == 0 || args.Count > 20 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            error.WriteLine(usage);
            return 1;
        }

        uint segmentBudget = 100_000, segments = 10, codeBytes = 0;
        bool json = false, differential = false;
        string? discPath = null, rootsPath = null, engineKind = "interpreter";
        uint? capturePc = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Count; i++)
        {
            var option = args[i];
            if (!seen.Add(option))
            {
                error.WriteLine($"openbios-probe: duplicate option {option}");
                return 1;
            }

            if (option == "--json") { json = true; continue; }
            if (option == "--differential") { differential = true; continue; }
            if (option == "--capture-at")
            {
                if (++i >= args.Count || !uint.TryParse(args[i].Replace("0x", ""), NumberStyles.HexNumber, null, out var cap))
                {
                    error.WriteLine("openbios-probe: --capture-at requires a hexadecimal PC");
                    return 1;
                }
                capturePc = cap;
                continue;
            }

            if (option is "--disc" or "--roots" or "--engine")
            {
                if (++i >= args.Count)
                {
                    error.WriteLine($"openbios-probe: {option} requires a value");
                    return 1;
                }

                if (option == "--disc") discPath = args[i];
                else if (option == "--roots") rootsPath = args[i];
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
            || (differential && engineKind != "generated-host"))
        {
            error.WriteLine("openbios-probe: --engine generated-host requires --roots; --differential requires --engine generated-host");
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
            return engineKind == "generated-host"
                ? RunGeneratedHost(firmware, hash, disc?.Source, ReadRoots(rootsPath!), codeBytes, segments, segmentBudget, differential, json, output)
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

    /// <summary>
    /// Issue #732: runs the ROM through the generated host. The ROM's code (its first <paramref name="codeBytes"/> bytes,
    /// or all of it) is compiled from the reset vector plus the explicit <paramref name="roots"/>; everything that has no
    /// block — kernel code copied to RAM, vectors, the shell, a loaded executable — runs in the mixed-execution
    /// interpreter fallback over the same devices, and is reported as fallback. With <paramref name="differential"/> the
    /// interpreter backend runs the same ROM and disc first and both are compared at the shell and executable boundaries.
    /// </summary>
    private static int RunGeneratedHost(
        OpenBiosFirmware firmware, string hash, ICdSectorSource? disc, IReadOnlyList<uint> roots, uint codeBytes,
        uint segments, uint segmentBudget, bool differential, bool json, TextWriter output)
    {
        var codeWords = codeBytes == 0 ? firmware.Words : firmware.Words.Take((int)Math.Min(codeBytes / 4, (uint)firmware.Words.Count)).ToArray();
        var image = ReachableProgramBuilder.BuildFirmwareImage(OpenBiosFirmware.ResetVector, codeWords, OpenBiosFirmware.ResetVector, roots);

        Dictionary<uint, GuestState>? reference = null;
        if (differential)
        {
            reference = [];
            var backend = new OpenBiosBootBackend(firmware, disc);
            using var interpreter = (InterpreterTitleExecutionEngine)backend.CreateEngine();
            ulong fetches = 0;
            interpreter.FetchObserver = pc =>
            {
                fetches++;
                if (pc is OpenBiosBootMonitor.ShellLoadAddress or ExecutableBoundary && !reference.ContainsKey(pc))
                {
                    reference[pc] = GuestState.Capture(interpreter, pc, fetches);
                }
            };
            new ExecutionOrchestrator().Execute(interpreter, handoff: null, new TitleExecutionRequest(
                backend.EntryPc, new uint[TitleExecutionRequest.GprCount], 0, 0, [], segments, segmentBudget));
        }

        var directory = Path.Combine(Path.GetTempPath(), "psxrecomp-openbios-host-" + Guid.NewGuid().ToString("N"));
        var boundaries = new Dictionary<uint, GuestState>();
        InterpreterTitleExecutionEngine? current = null;
        var monitor = new OpenBiosBootMonitor(() => (current!.Cop0Diagnostics.Cause, current.Cop0Diagnostics.Epc, current.Cop0Diagnostics.BadVAddr));
        ulong fallbackFetches = 0;
        var budget = (uint)Math.Min((ulong)segments * segmentBudget, uint.MaxValue);
        try
        {
            using var engine = new RecompiledHostExecutionEngine(
                image.Program,
                firmware.Words,
                OpenBiosFirmware.ResetVector,
                new GeneratedHostBuildService(),
                directory,
                biosRuntimeFactory: null,
                // One fallback segment may run as long as the whole probe: the shell and a loaded executable legitimately
                // run in RAM for most of a boot, so a shorter segment bound would stop a correct run.
                mixedFallback: new MixedFallbackOptions(budget, uint.MaxValue),
                guestFirmware: true,
                runTimeout: TimeSpan.FromMinutes(30),
                disc: disc)
            {
                FallbackFetchObserver = (interpreter, pc) =>
                {
                    current = interpreter;
                    fallbackFetches++;
                    monitor.OnFetch(pc);
                    if (pc is OpenBiosBootMonitor.ShellLoadAddress or ExecutableBoundary && !boundaries.ContainsKey(pc))
                    {
                        boundaries[pc] = GuestState.Capture(interpreter, pc, fallbackFetches);
                    }
                },
            };

            // The artifact's first dispatch unit is the reset-vector block (it is always the build's entry); fallback
            // fetches are the only other fetches this engine can observe, so fetch indices below count only those.
            monitor.OnFetch(OpenBiosFirmware.ResetVector);
            var result = new ExecutionOrchestrator().Execute(engine, handoff: null, new TitleExecutionRequest(
                OpenBiosFirmware.ResetVector, new uint[TitleExecutionRequest.GprCount], 0, 0, [], 1, budget));

            var lastRam = boundaries.Values.OrderBy(static b => b.AtFetch).LastOrDefault()?.Ram;
            var report = monitor.Evaluate(address => lastRam is null || (address & 0x1FFFFFFFu) > RamBytes - 4
                ? 0u
                : BitConverter.ToUInt32(lastRam, (int)(address & 0x1FFFFFFFu)));
            var kernelBooted = report.KernelBooted;
            var evidence = engine.FallbackEvidence;
            var document = new
            {
                kind = "openbios-probe",
                engine = "generated-host",
                sha256 = hash,
                bootVerified = kernelBooted && report.TitleStarted,
                kernelBooted,
                milestones = report,
                fetchIndices = "fallback-interpreter fetches only (native blocks are not observed per instruction)",
                titleStarted = report.TitleStarted,
                executionState = result.State.ToString(),
                guestPc = result.FinalSnapshot is { } s ? $"0x{s.PC:X8}" : null,
                diagnosticCode = result.DiagnosticCode,
                diagnosticMessage = result.DiagnosticMessage,
                build = new
                {
                    imageInstructions = image.ImageInstructionCount,
                    nativeInstructions = image.NativeInstructionCount,
                    blocks = image.Program.Blocks.Count,
                    roots = roots.Count,
                    staticFallbackTargets = image.FallbackTargets.Count,
                },
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
                        .Select(static t => $"0x{t.Target:X8}:entries={t.Entries}:instructions={t.Instructions}").ToArray(),
                    timings = engine.FallbackTimings,
                },
                boundaries = boundaries.OrderBy(static b => b.Key).ToDictionary(static b => $"0x{b.Key:X8}", static b => b.Value.Describe()),
                differential = reference?.OrderBy(static b => b.Key).ToDictionary(
                    static b => $"0x{b.Key:X8}",
                    b => boundaries.TryGetValue(b.Key, out var host) ? GuestState.Compare(b.Value, host) : (object)"not reached by the generated host"),
                referenceBoundariesMissing = reference is null ? null : boundaries.Keys.Except(reference.Keys).Select(static k => $"0x{k:X8}").ToArray(),
            };
            output.WriteLine(JsonSerializer.Serialize(document, json ? null : new JsonSerializerOptions { WriteIndented = true }));
            return kernelBooted ? 0 : 2;
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>One hexadecimal block-entry PC per line (blank lines ignored); an explicit input, never guessed.</summary>
    private static IReadOnlyList<uint> ReadRoots(string path) =>
        File.ReadAllLines(path)
            .Select(static line => line.Trim())
            .Where(static line => line.Length != 0)
            .Select(static line => uint.Parse(line.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? line[2..] : line, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            .Distinct()
            .ToArray();

    /// <summary>The guest state when an engine was about to fetch a boundary PC: CPU registers, COP0 and all of RAM.</summary>
    private sealed record GuestState(uint Pc, uint[] Gpr, uint Sr, uint Cause, uint Epc, uint BadVAddr, byte[] Ram, ulong AtFetch)
    {
        public static GuestState Capture(InterpreterTitleExecutionEngine engine, uint pc, ulong atFetch)
        {
            var ram = new byte[RamBytes];
            for (uint address = 0; address < RamBytes; address += 4)
            {
                BitConverter.TryWriteBytes(ram.AsSpan((int)address, 4), engine.ReadGuestWord(address));
            }

            var cop0 = engine.Cop0Diagnostics;
            return new GuestState(pc, Enumerable.Range(0, 32).Select(engine.ReadGuestGpr).ToArray(), cop0.Sr, cop0.Cause, cop0.Epc, cop0.BadVAddr, ram, atFetch);
        }

        public object Describe() => new
        {
            atFetch = AtFetch,
            sr = Hex(Sr), cause = Hex(Cause), epc = Hex(Epc),
            ramSha256 = Convert.ToHexString(SHA256.HashData(Ram)).ToLowerInvariant(),
        };

        public static object Compare(GuestState interpreter, GuestState host)
        {
            var gpr = Enumerable.Range(0, 32).Where(r => interpreter.Gpr[r] != host.Gpr[r])
                .Select(r => $"r{r}: 0x{interpreter.Gpr[r]:X8} vs 0x{host.Gpr[r]:X8}").ToArray();
            var cop0 = new List<string>();
            if (interpreter.Sr != host.Sr) cop0.Add($"SR: {Hex(interpreter.Sr)} vs {Hex(host.Sr)}");
            if (interpreter.Cause != host.Cause) cop0.Add($"CAUSE: {Hex(interpreter.Cause)} vs {Hex(host.Cause)}");
            if (interpreter.Epc != host.Epc) cop0.Add($"EPC: {Hex(interpreter.Epc)} vs {Hex(host.Epc)}");
            if (interpreter.BadVAddr != host.BadVAddr) cop0.Add($"BadVAddr: {Hex(interpreter.BadVAddr)} vs {Hex(host.BadVAddr)}");

            int? firstMismatch = null;
            var bytes = 0;
            var pages = new SortedSet<int>();
            for (var i = 0; i < RamBytes; i++)
            {
                if (interpreter.Ram[i] == host.Ram[i]) continue;
                firstMismatch ??= i;
                bytes++;
                pages.Add(i >> 12);
            }

            return new
            {
                match = gpr.Length == 0 && cop0.Count == 0 && bytes == 0,
                interpreterRamSha256 = Convert.ToHexString(SHA256.HashData(interpreter.Ram)).ToLowerInvariant(),
                hostRamSha256 = Convert.ToHexString(SHA256.HashData(host.Ram)).ToLowerInvariant(),
                gprMismatches = gpr,
                cop0Mismatches = cop0,
                ramFirstMismatch = firstMismatch is { } f ? $"0x{f:X8}: 0x{interpreter.Ram[f]:X2} vs 0x{host.Ram[f]:X2}" : null,
                ramMismatchBytes = bytes,
                ramMismatchPages = pages.Select(static p => $"0x{p << 12:X6}").ToArray(),
            };
        }
    }

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
