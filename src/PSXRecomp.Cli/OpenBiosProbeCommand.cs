using System.Security.Cryptography;
using System.Text.Json;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// A bounded, source-neutral OpenBIOS firmware bring-up entrypoint. It never reports
/// a title-screen or game boot success and never silently enables host BIOS HLE.
/// </summary>
[Infrastructure]
internal static class OpenBiosProbeCommand
{
    internal static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        const string usage = "usage: psxrecomp openbios-probe <openbios.bin> [--segment-budget <n>] [--segments <n>] [--json]";
        if (args.Count == 0 || args.Count > 6 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            error.WriteLine(usage);
            return 1;
        }

        uint segmentBudget = 100_000, segments = 10;
        bool json = false;
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
            if (option != "--segment-budget" && option != "--segments")
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
            else segmentBudget = number;
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
            var backend = new OpenBiosBootBackend(OpenBiosFirmware.FromBytes(bytes));
            using var engine = backend.CreateEngine();
            var monitor = new OpenBiosBootMonitor();
            if (engine is InterpreterTitleExecutionEngine observed)
            {
                observed.FetchObserver = monitor.OnFetch;
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
                    bootVerified = false,
                    kernelBooted,
                    milestones = report,
                    titleStarted = false,
                    executionState = result.State.ToString(),
                    guestPc = result.FinalSnapshot?.PC,
                    diagnosticCode = result.DiagnosticCode,
                    segmentsRetired = result.SegmentsRetired,
                    stop
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
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException
                or FileNotFoundException or DirectoryNotFoundException or DllNotFoundException)
        {
            error.WriteLine($"openbios-probe: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static object BuildStop(InterpreterTitleExecutionEngine engine, RecompilerStateSnapshot? snapshot)
    {
        var cop0 = engine.Cop0Diagnostics;
        var trace = engine.RecentTrace;
        static string Hex(uint v) => $"0x{v:X8}";
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
