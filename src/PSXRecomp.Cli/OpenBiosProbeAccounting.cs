using System.Globalization;
using System.Runtime.InteropServices;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// Issue #732: where a generated-host firmware run spends its instructions and its artifact/interpreter transitions,
/// by region, by reason and by PC. Each number is exactly what one source observed, never an estimate:
/// <list type="bullet">
/// <item><c>native.instructions</c> — the sum of the artifact's guest-time reports (instructions its compiled blocks
/// retired). It has no PC, so it is attributed to a region only when every compiled block lies in that one region.</item>
/// <item><c>fallback.fetches</c> — PCs the fallback interpreter was about to execute. An instruction that takes an
/// exception (an IRQ, SYSCALL or fault) is fetched but not retired, so fetches ≥ retired; the difference is reported.</item>
/// <item><c>fallback.retiredInstructions</c> — the mixed-execution session's retired count.</item>
/// <item>transitions — artifact-to-interpreter handoffs, classified by their entry PC and by how the segment ended.</item>
/// </list>
/// </summary>
[Infrastructure]
internal sealed class OpenBiosProbeAccounting
{
    public const int TopCount = 20;

    public const string Rom = "rom", KernelRam = "kernel-ram", Shell = "shell", UserRam = "user-ram", Other = "other";
    public static readonly string[] Regions = [Rom, KernelRam, Shell, UserRam, Other];

    private const uint ExceptionVector = 0x80u;
    private const uint ReservedInstruction = 10, CoprocessorUnusable = 11;

    private readonly Dictionary<uint, ulong> _fetches = [];
    private readonly Dictionary<uint, (ulong Transitions, ulong Indirect, ulong Retired)> _entries = [];
    private readonly SortedDictionary<string, ulong> _exits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ulong Transitions, ulong Retired)> _aotClasses = new(StringComparer.Ordinal);
    private readonly SortedDictionary<(uint ExcCode, uint Opcode), (uint Pc, uint Word, ulong AtFetch)> _unsupported = [];

    public ulong FallbackFetches { get; private set; }

    /// <summary>The region of a PC, by physical address: ROM, kernel RAM (&lt; 0x10000), the shell image, other user RAM, or other.</summary>
    public static string RegionOf(uint pc)
    {
        var physical = pc & 0x1FFFFFFFu;
        if (physical is >= 0x1FC00000u and < 0x1FC80000u) return Rom;
        if (physical >= 0x00800000u) return Other;
        var ram = physical & 0x001FFFFFu;
        if (ram < 0x10000u) return KernelRam;
        return ram >= (OpenBiosBootMonitor.ShellLoadAddress & 0x1FFFFFFFu) && ram < (OpenBiosBootMonitor.ShellImageEnd & 0x1FFFFFFFu) ? Shell : UserRam;
    }

    /// <summary>
    /// Why the artifact handed a PC to the interpreter. Every PC reaching a handoff has no compiled block; the reason says
    /// which kind of code it is: the exception vector, an A0/B0/C0 kernel-call vector, ROM code the build did not reach,
    /// RAM code (no RAM code is compiled before the run, so all of it), or anything else.
    /// </summary>
    public static string ReasonOf(uint pc)
    {
        var physical = pc & 0x1FFFFFFFu;
        if (physical == ExceptionVector) return "exception-vector";
        if (physical is 0xA0u or 0xB0u or 0xC0u) return "kernel-call-vector";
        return RegionOf(pc) switch
        {
            Rom => "rom-no-block",
            Other => "other-no-block",
            _ => "ram-no-code-image",
        };
    }

    /// <summary>Records one fallback fetch; at the exception vector, the first RI/CpU fault of each opcode.</summary>
    public void OnFetch(uint pc, Func<(uint Cause, uint Epc)> readCop0, Func<uint, uint> readWord)
    {
        FallbackFetches++;
        CollectionsMarshal.GetValueRefOrAddDefault(_fetches, pc, out _)++;
        if ((pc & 0x1FFFFFFFu) != ExceptionVector)
        {
            return;
        }

        var (cause, epc) = readCop0();
        var code = (cause >> 2) & 0x1Fu;
        if (code is not (ReservedInstruction or CoprocessorUnusable))
        {
            return;
        }

        var at = (cause & 0x80000000u) != 0 ? epc + 4 : epc; // CAUSE.BD: the faulting instruction is in the delay slot
        var word = readWord(at);
        _unsupported.TryAdd((code, word >> 26), (at, word, FallbackFetches));
    }

    /// <summary>
    /// The code image a PC belongs to, by load range: the ROM, the kernel image the ROM copies to low RAM, the shell, and
    /// the PS-X EXE (with its overlays: everything else the firmware loads into RAM). Anything else is unknown.
    /// </summary>
    public static string CodeImageOfRegion(string region) => region switch
    {
        Rom => "rom",
        KernelRam => "kernel-ram-image",
        Shell => "shell",
        UserRam => "ps-x-exe",
        _ => "unknown",
    };

    /// <summary>
    /// The AOT class of a handoff when its producer did not say (<see cref="MixedFallbackTransition.AotClass"/>): ROM code
    /// is a known image without a block; this build compiles no other image, so anything else is in no AOT image.
    /// </summary>
    public static string AotClassOf(MixedFallbackTransition transition) =>
        transition.AotClass ?? (RegionOf(transition.EntryPc) == Rom ? MixedFallbackAotClass.KnownNotYetAot : MixedFallbackAotClass.NotInAnyImage);

    public void OnTransition(MixedFallbackTransition transition)
    {
        ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_entries, transition.EntryPc, out _);
        entry = (entry.Transitions + 1, entry.Indirect + (transition.Indirect ? 1UL : 0), entry.Retired + transition.RetiredInstructions);
        ref var aot = ref CollectionsMarshal.GetValueRefOrAddDefault(_aotClasses, AotClassOf(transition), out _);
        aot = (aot.Transitions + 1, aot.Retired + transition.RetiredInstructions);
        var exit = transition.Status switch
        {
            FallbackSegmentStatus.Returned => "returned-to-block",
            FallbackSegmentStatus.BudgetExhausted => "budget-exhausted",
            _ => "stopped:" + transition.DiagnosticCode,
        };
        _exits[exit] = _exits.GetValueOrDefault(exit) + 1;
    }

    public object Report(ulong? nativeInstructions, ulong? fallbackRetired, IEnumerable<uint> blockEntryPcs, ProbeSymbols? symbols, Func<uint, string>? fallbackCause = null)
    {
        var blockRegions = blockEntryPcs.Select(RegionOf).Distinct().ToArray();
        var nativeRegion = blockRegions.Length == 1 ? blockRegions[0] : null;
        var transitions = _entries.Values.Aggregate(0UL, static (sum, e) => sum + e.Transitions);
        var retired = _entries.Values.Aggregate(0UL, static (sum, e) => sum + e.Retired);
        var fetchesByRegion = _fetches.GroupBy(static f => RegionOf(f.Key)).ToDictionary(static g => g.Key, static g => g.Aggregate(0UL, static (s, f) => s + f.Value));
        var entriesByRegion = _entries.GroupBy(static e => RegionOf(e.Key)).ToDictionary(static g => g.Key, static g => g.Aggregate(0UL, static (s, e) => s + e.Value.Transitions));
        var retiredByRegion = _entries.GroupBy(static e => RegionOf(e.Key)).ToDictionary(static g => g.Key, static g => g.Aggregate(0UL, static (s, e) => s + e.Value.Retired));

        return new
        {
            native = new { instructions = nativeInstructions, region = nativeRegion },
            fallback = new
            {
                fetches = FallbackFetches,
                retiredInstructions = fallbackRetired,
                fetchesNotRetired = fallbackRetired is { } r && FallbackFetches >= r ? FallbackFetches - r : (ulong?)null,
            },
            regions = Regions.Select(region => new
            {
                region,
                codeImage = CodeImageOfRegion(region),
                nativeInstructions = region == nativeRegion ? nativeInstructions : (nativeRegion is null ? null : 0UL),
                fallbackFetches = fetchesByRegion.GetValueOrDefault(region),
                fallbackFetchShare = Share(fetchesByRegion.GetValueOrDefault(region), FallbackFetches),
                transitionsEntered = entriesByRegion.GetValueOrDefault(region),
                transitionShare = Share(entriesByRegion.GetValueOrDefault(region), transitions),
                retiredInSegmentsEntered = retiredByRegion.GetValueOrDefault(region),
            }).ToArray(),
            transitions = new
            {
                total = transitions,
                indirect = _entries.Values.Aggregate(0UL, static (sum, e) => sum + e.Indirect),
                retiredInstructions = retired,
                byReason = _entries.GroupBy(e => fallbackCause?.Invoke(e.Key) ?? ReasonOf(e.Key))
                    .Select(g => (Reason: g.Key, Transitions: g.Aggregate(0UL, static (s, e) => s + e.Value.Transitions),
                        Indirect: g.Aggregate(0UL, static (s, e) => s + e.Value.Indirect), Retired: g.Aggregate(0UL, static (s, e) => s + e.Value.Retired)))
                    .OrderByDescending(static g => g.Transitions).ThenBy(static g => g.Reason, StringComparer.Ordinal)
                    .Select(g => new
                    {
                        reason = g.Reason, transitions = g.Transitions, indirect = g.Indirect, retiredInstructions = g.Retired,
                        transitionShare = Share(g.Transitions, transitions), retiredShare = Share(g.Retired, retired),
                    }).ToArray(),
                byAotClass = _aotClasses.OrderByDescending(static a => a.Value.Transitions).ThenBy(static a => a.Key, StringComparer.Ordinal)
                    .Select(a => new
                    {
                        aotClass = a.Key,
                        preDeterminable = a.Key != MixedFallbackAotClass.RuntimeGenerated,
                        transitions = a.Value.Transitions, retiredInstructions = a.Value.Retired,
                        transitionShare = Share(a.Value.Transitions, transitions), retiredShare = Share(a.Value.Retired, retired),
                    }).ToArray(),
                byExit = _exits.Select(static e => new { exit = e.Key, transitions = e.Value }).ToArray(),
            },
            hotEntries = _entries.OrderByDescending(static e => e.Value.Transitions).ThenByDescending(static e => e.Value.Retired).ThenBy(static e => e.Key)
                .Take(TopCount)
                .Select(e => new
                {
                    pc = Hex(e.Key), region = RegionOf(e.Key), reason = fallbackCause?.Invoke(e.Key) ?? ReasonOf(e.Key), symbol = symbols?.Lookup(e.Key),
                    transitions = e.Value.Transitions, indirect = e.Value.Indirect, retiredInstructions = e.Value.Retired,
                    transitionShare = Share(e.Value.Transitions, transitions), retiredShare = Share(e.Value.Retired, retired),
                }).ToArray(),
            hotPcs = _fetches.OrderByDescending(static f => f.Value).ThenBy(static f => f.Key).Take(TopCount)
                .Select(f => new { pc = Hex(f.Key), region = RegionOf(f.Key), symbol = symbols?.Lookup(f.Key), fetches = f.Value, share = Share(f.Value, FallbackFetches) })
                .ToArray(),
            hotSymbols = symbols is null ? null : _fetches.GroupBy(f => symbols.Lookup(f.Key) is { } name ? $"{RegionOf(f.Key)}:{name}" : $"{RegionOf(f.Key)}:(no symbol)")
                .Select(static g => (Key: g.Key, Fetches: g.Aggregate(0UL, static (s, f) => s + f.Value)))
                .OrderByDescending(static g => g.Fetches).ThenBy(static g => g.Key, StringComparer.Ordinal).Take(TopCount)
                .Select(g => new { symbol = g.Key, fetches = g.Fetches, share = Share(g.Fetches, FallbackFetches) })
                .ToArray(),
            unsupportedOpcodes = _unsupported.Select(static u => new
            {
                excCode = u.Key.ExcCode, opcode = u.Key.Opcode, pc = Hex(u.Value.Pc), word = Hex(u.Value.Word), atFallbackFetch = u.Value.AtFetch,
            }).ToArray(),
        };
    }

    private static double Share(ulong part, ulong whole) => whole == 0 ? 0 : Math.Round((double)part / whole, 4);

    private static string Hex(uint value) => $"0x{value:X8}";
}

/// <summary>
/// Function symbols from <c>nm</c> output (<c>address type name</c> per line; text types T/t/W/w only), looked up by
/// physical address so a KSEG0/KSEG1 alias finds the same function. A PC resolves to the nearest symbol at or below it
/// in the same <see cref="OpenBiosProbeAccounting.RegionOf"/> region, else to nothing.
/// </summary>
[Infrastructure]
internal sealed class ProbeSymbols
{
    private readonly uint[] _addresses;
    private readonly string[] _names;

    private ProbeSymbols(uint[] addresses, string[] names) => (_addresses, _names) = (addresses, names);

    public int Count => _addresses.Length;

    public static ProbeSymbols Parse(IEnumerable<string> lines)
    {
        var symbols = new SortedDictionary<uint, string>();
        foreach (var line in lines)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts[1] is "T" or "t" or "W" or "w"
                && uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
            {
                symbols.TryAdd(address & 0x1FFFFFFFu, parts[2]); // the first name at an address wins (nm -n order)
            }
        }

        return new ProbeSymbols(symbols.Keys.ToArray(), symbols.Values.ToArray());
    }

    public string? Lookup(uint pc)
    {
        var physical = pc & 0x1FFFFFFFu;
        var index = Array.BinarySearch(_addresses, physical);
        if (index < 0) index = ~index - 1;
        return index >= 0 && OpenBiosProbeAccounting.RegionOf(_addresses[index]) == OpenBiosProbeAccounting.RegionOf(pc) ? _names[index] : null;
    }
}
