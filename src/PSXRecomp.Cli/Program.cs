using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// The minimal headless CLI introduced by Issue #460. It exposes the production
/// recompiled-artifact build path (#458) and the runnable-artifact launch contract
/// (#459) as two commands over a legally supplied input — a PS-X EXE or, since
/// Issue #457, a supported CHD disc image (see <see cref="CliInput.Load"/>):
/// <c>recompile</c> and <c>run</c>. This assembly is Infrastructure-layer
/// (<c>PSXRecomp.Infrastructure.Cli</c>) and only composes production contracts —
/// it adds no compiler, build, Runtime, or execution-loop semantics of its own.
/// <para>
/// Exit codes are the stable #459 contract (<see cref="RecompiledArtifactExitCode"/>):
/// <c>0</c> success; <c>1</c> tooling/input/build/launcher failure; <c>2</c> (run
/// only) the runtime stopped at an explicit unsupported/blocked boundary.
/// </para>
/// <para>
/// <see cref="Main"/> is a thin process shell; <see cref="Execute"/> is the
/// in-process command boundary the integration tests drive directly.
/// </para>
/// </summary>
[Infrastructure]
public static class Program
{
    private const string UsageRecompile = "usage: psxrecomp recompile <input.exe|input.chd> --output <dir> [--entry-root <0xPC>]... [--json]";
    private const string UsageDoctor = "usage: psxrecomp doctor [--json]";
    private const string UsageOpenBiosProbe = "usage: psxrecomp openbios-probe <locally-built-openbios.bin> [--segment-budget <n>] [--segments <n>] [--json]";
    private const string UsageRun = "usage: psxrecomp run <input.exe|input.chd> [--output <dir>] [--segment-budget <n>] [--entry-root <0xPC>]... [--mixed-fallback|--no-mixed-fallback] [--fallback-segment-budget <n>] [--fallback-max-transitions <n>] [--report] [--frame-evidence] [--json]";

    public static int Main(string[] args) => Execute(args, Console.Out, Console.Error);

    /// <summary>
    /// Parses <paramref name="args"/>, dispatches to the requested command, and
    /// returns the process exit code. Writes targeted output to
    /// <paramref name="standardOutput"/> and diagnostics to
    /// <paramref name="standardError"/>.
    /// </summary>
    public static int Execute(string[] args, TextWriter standardOutput, TextWriter standardError)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
        {
            WriteUsage(standardError);
            return RecompiledArtifactExitCode.Failure;
        }

        var command = args[0];
        var rest = args[1..];
        return command switch
        {
            "recompile" => Dispatch("recompile", rest, allowSegmentBudget: false, allowReport: false, allowFrameEvidence: false, requireOutput: true, standardOutput, standardError),
            "run" => Dispatch("run", rest, allowSegmentBudget: true, allowReport: true, allowFrameEvidence: true, requireOutput: false, standardOutput, standardError),
            "doctor" => Doctor(rest, standardOutput, standardError),
            "openbios-probe" => OpenBiosProbeCommand.Run(rest, standardOutput, standardError),
            "--help" or "-h" => Usage(standardOutput),
            _ => UnknownCommand(command, standardError),
        };
    }

    private static int Dispatch(
        string command,
        IReadOnlyList<string> arguments,
        bool allowSegmentBudget,
        bool allowReport,
        bool allowFrameEvidence,
        bool requireOutput,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        if (!TryParse(arguments, allowSegmentBudget, allowReport, allowFrameEvidence, requireOutput, out var parsed, out var error))
        {
            standardError.WriteLine($"psxrecomp {command}: {error}");
            WriteCommandUsage(command, standardError);
            return RecompiledArtifactExitCode.Failure;
        }

        if (parsed.Help)
        {
            WriteCommandUsage(command, standardOutput);
            return RecompiledArtifactExitCode.Success;
        }

        return command == "recompile"
            ? RecompileCommand.Run(parsed, standardOutput, standardError)
            : RunCommand.Run(parsed, standardOutput, standardError);
    }

    /// <summary>
    /// Hand-rolled parsing for the two small command grammars — deliberately
    /// limited, because this is #460's smallest useful surface, not the full #15
    /// CLI framework.
    /// </summary>
    private static bool TryParse(
        IReadOnlyList<string> arguments,
        bool allowSegmentBudget,
        bool allowReport,
        bool allowFrameEvidence,
        bool requireOutput,
        out ParsedArguments parsed,
        out string? error)
    {
        string? input = null;
        string? outputDirectory = null;
        var json = false;
        var report = false;
        var frameEvidence = false;
        uint? segmentBudget = null;
        // Mixed execution is on by default for 'run' (ADR-012 amendment, Issue #693); the engine and
        // launcher keep their own explicit opt-in for embedders, and '--no-mixed-fallback' restores
        // the fail-closed in-image stop on the command line. The two flags are mutually exclusive,
        // so an explicit choice is tracked separately from the derived default.
        bool? explicitMixedFallback = null;
        uint? fallbackSegmentBudget = null;
        uint? fallbackMaxTransitions = null;
        var entryRoots = new SortedSet<uint>();
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
                case "--segment-budget":
                    if (!allowSegmentBudget)
                    {
                        parsed = default;
                        error = $"'{token}' is only valid for 'run'.";
                        return false;
                    }
                    if (i + 1 >= arguments.Count)
                    {
                        parsed = default;
                        error = "missing value for option '--segment-budget'.";
                        return false;
                    }
                    var budgetText = arguments[++i];
                    if (!uint.TryParse(budgetText, out var budget) || budget == 0)
                    {
                        parsed = default;
                        error = $"invalid segment budget '{budgetText}': expected a positive integer.";
                        return false;
                    }
                    segmentBudget = budget;
                    break;
                case "--mixed-fallback":
                    if (!allowSegmentBudget)
                    {
                        parsed = default;
                        error = $"'{token}' is only valid for 'run'.";
                        return false;
                    }
                    if (explicitMixedFallback == false)
                    {
                        parsed = default;
                        error = "'--mixed-fallback' and '--no-mixed-fallback' cannot be used together.";
                        return false;
                    }
                    explicitMixedFallback = true;
                    break;
                case "--no-mixed-fallback":
                    if (!allowSegmentBudget)
                    {
                        parsed = default;
                        error = $"'{token}' is only valid for 'run'.";
                        return false;
                    }
                    if (explicitMixedFallback == true)
                    {
                        parsed = default;
                        error = "'--mixed-fallback' and '--no-mixed-fallback' cannot be used together.";
                        return false;
                    }
                    explicitMixedFallback = false;
                    break;
                case "--fallback-segment-budget" or "--fallback-max-transitions":
                    if (!allowSegmentBudget)
                    {
                        parsed = default;
                        error = $"'{token}' is only valid for 'run'.";
                        return false;
                    }
                    if (i + 1 >= arguments.Count)
                    {
                        parsed = default;
                        error = $"missing value for option '{token}'.";
                        return false;
                    }
                    var fallbackText = arguments[++i];
                    if (!uint.TryParse(fallbackText, out var fallbackBudget) || fallbackBudget == 0)
                    {
                        parsed = default;
                        error = $"invalid value '{fallbackText}' for '{token}': expected a positive integer.";
                        return false;
                    }
                    if (token == "--fallback-segment-budget")
                    {
                        fallbackSegmentBudget = fallbackBudget;
                    }
                    else
                    {
                        fallbackMaxTransitions = fallbackBudget;
                    }
                    break;
                case "--entry-root":
                    if (i + 1 >= arguments.Count)
                    {
                        parsed = default;
                        error = "missing value for option '--entry-root'.";
                        return false;
                    }
                    var rootText = arguments[++i];
                    if (!TryParseGuestPc(rootText, out var root))
                    {
                        parsed = default;
                        error = $"invalid entry root '{rootText}': expected a hexadecimal guest PC with a 0x prefix, such as 0x80025350.";
                        return false;
                    }
                    entryRoots.Add(root);
                    break;
                case "--report":
                    if (!allowReport)
                    {
                        parsed = default;
                        error = "'--report' is only valid for 'run'.";
                        return false;
                    }
                    report = true;
                    break;
                case "--frame-evidence":
                    if (!allowFrameEvidence)
                    {
                        parsed = default;
                        error = "'--frame-evidence' is only valid for 'run'.";
                        return false;
                    }
                    frameEvidence = true;
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

        var mixedFallback = explicitMixedFallback ?? allowSegmentBudget;

        if ((fallbackSegmentBudget is not null || fallbackMaxTransitions is not null) && !mixedFallback && !help)
        {
            parsed = default;
            error = "'--fallback-segment-budget' and '--fallback-max-transitions' require mixed execution ('--mixed-fallback' is the default; drop '--no-mixed-fallback').";
            return false;
        }

        if (help)
        {
            parsed = new ParsedArguments(input, outputDirectory, json, segmentBudget, report, frameEvidence, entryRoots.ToArray(), Help: true, MixedFallback: mixedFallback, FallbackSegmentBudget: fallbackSegmentBudget, FallbackMaxTransitions: fallbackMaxTransitions);
            error = null;
            return true;
        }

        if (requireOutput && outputDirectory is null)
        {
            parsed = default;
            error = "missing required option '--output <dir>'.";
            return false;
        }

        if (input is null)
        {
            parsed = default;
            error = "missing input path.";
            return false;
        }

        parsed = new ParsedArguments(input, outputDirectory, json, segmentBudget, report, frameEvidence, entryRoots.ToArray(), Help: false, MixedFallback: mixedFallback, FallbackSegmentBudget: fallbackSegmentBudget, FallbackMaxTransitions: fallbackMaxTransitions);
        error = null;
        return true;
    }

    /// <summary>
    /// A guest PC is written in hexadecimal with a mandatory <c>0x</c> prefix, so a decimal
    /// value can never be silently read as hex. Range/alignment/image checks belong to the
    /// reachable-program builder, which owns the text image.
    /// </summary>
    private static bool TryParseGuestPc(string text, out uint value)
    {
        value = 0;
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static int Doctor(string[] arguments, TextWriter standardOutput, TextWriter standardError)
    {
        if (arguments.Contains("--help") || arguments.Contains("-h"))
        {
            standardOutput.WriteLine(UsageDoctor);
            WriteExitCodes(standardOutput);
            return RecompiledArtifactExitCode.Success;
        }

        var unknown = arguments.FirstOrDefault(a => a != "--json");
        if (unknown is not null)
        {
            standardError.WriteLine($"psxrecomp doctor: unexpected argument '{unknown}'.");
            standardError.WriteLine(UsageDoctor);
            return RecompiledArtifactExitCode.Failure;
        }

        return DoctorCommand.Run(arguments.Length > 0, standardOutput, DoctorCommand.HostProbes());
    }

    private static int Usage(TextWriter writer)
    {
        WriteUsage(writer);
        return RecompiledArtifactExitCode.Success;
    }

    private static int UnknownCommand(string command, TextWriter standardError)
    {
        standardError.WriteLine($"psxrecomp: unknown command '{command}'.");
        WriteUsage(standardError);
        return RecompiledArtifactExitCode.Failure;
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("usage: psxrecomp <command> [options]");
        writer.WriteLine();
        writer.WriteLine("commands:");
        writer.WriteLine("  recompile   build a runnable recompiled host artifact from a PS-X EXE or CHD");
        writer.WriteLine("  run         build and run a recompiled artifact from a PS-X EXE or CHD");
        writer.WriteLine("  doctor      check the OS, .NET, native runtime, and C compiler this CLI needs");
        writer.WriteLine("  openbios-probe   execute a locally built OpenBIOS ROM on the shared native CPU (no HLE)");
        writer.WriteLine();
        writer.WriteLine(UsageRecompile);
        writer.WriteLine(UsageRun);
        writer.WriteLine(UsageDoctor);
        writer.WriteLine(UsageOpenBiosProbe);
        writer.WriteLine();
        WriteExitCodes(writer);
    }

    private static void WriteCommandUsage(string command, TextWriter writer)
    {
        writer.WriteLine(command == "recompile" ? UsageRecompile : UsageRun);
        WriteExitCodes(writer);
    }

    private static void WriteExitCodes(TextWriter writer)
    {
        writer.WriteLine("exit codes: 0 success; 1 tooling/input/build/launcher failure;");
        writer.WriteLine("            2 (run) runtime stopped at an explicit unsupported/blocked boundary;");
        writer.WriteLine("            2 (doctor) unsupported OS/architecture");
    }
}

/// <summary>
/// The parsed, validated surface of one command invocation. Shared by both
/// commands; the run-only <c>--segment-budget</c> and <c>--report</c> options are gated during parsing.
/// </summary>
[Infrastructure]
internal sealed record ParsedArguments(
    string? Input,
    string? OutputDirectory,
    bool Json,
    uint? SegmentBudget,
    bool Report,
    bool FrameEvidence,
    IReadOnlyList<uint> EntryRoots,
    bool Help,
    bool MixedFallback = false,
    uint? FallbackSegmentBudget = null,
    uint? FallbackMaxTransitions = null);