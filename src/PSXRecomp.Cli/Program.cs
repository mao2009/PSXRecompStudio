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
    private const string UsageRecompile = "usage: psxrecomp recompile <input.exe|input.chd> --output <dir> [--json]";
    private const string UsageRun = "usage: psxrecomp run <input.exe|input.chd> [--output <dir>] [--segment-budget <n>] [--report] [--json]";

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
            "recompile" => Dispatch("recompile", rest, allowSegmentBudget: false, allowReport: false, requireOutput: true, standardOutput, standardError),
            "run" => Dispatch("run", rest, allowSegmentBudget: true, allowReport: true, requireOutput: false, standardOutput, standardError),
            "--help" or "-h" => Usage(standardOutput),
            _ => UnknownCommand(command, standardError),
        };
    }

    private static int Dispatch(
        string command,
        IReadOnlyList<string> arguments,
        bool allowSegmentBudget,
        bool allowReport,
        bool requireOutput,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        if (!TryParse(arguments, allowSegmentBudget, allowReport, requireOutput, out var parsed, out var error))
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
        bool requireOutput,
        out ParsedArguments parsed,
        out string? error)
    {
        string? input = null;
        string? outputDirectory = null;
        var json = false;
        var report = false;
        uint? segmentBudget = null;
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
                case "--report":
                    if (!allowReport)
                    {
                        parsed = default;
                        error = "'--report' is only valid for 'run'.";
                        return false;
                    }
                    report = true;
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
            parsed = new ParsedArguments(input, outputDirectory, json, segmentBudget, report, Help: true);
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

        parsed = new ParsedArguments(input, outputDirectory, json, segmentBudget, report, Help: false);
        error = null;
        return true;
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
        writer.WriteLine();
        writer.WriteLine(UsageRecompile);
        writer.WriteLine(UsageRun);
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
        writer.WriteLine("            2 (run) runtime stopped at an explicit unsupported/blocked boundary");
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
    bool Help);