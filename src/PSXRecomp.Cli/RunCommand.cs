using System.Text;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// <c>psxrecomp run</c>: builds and launches one runnable recompiled artifact from a
/// legally supplied PS-X EXE through the #459 <see cref="RecompiledArtifactLauncher"/>,
/// reusing the natural-end handoff semantics <c>TitleExecutionService</c> establishes
/// (running off the end of the guest's own text image is a natural exit; every other
/// unresolved transfer is classified as unsupported). The launcher is single-shot
/// (it builds and runs in one operation and does not accept an already-built binary),
/// so <c>run</c> rebuilds from the EXE; persisted-artifact-only relaunch is outside
/// Issue #460. The production result maps onto the process exit code as-is:
/// Success → 0, Blocked → 2, Failure/tooling → 1.
/// </summary>
[Infrastructure]
public static class RunCommand
{
    /// <summary>
    /// Default per-segment guest budget when <c>--segment-budget</c> is not given:
    /// generous enough for the supported fixtures, bounded so a runaway guest still
    /// stops at an explicit budget boundary instead of running until the artifact
    /// timeout kills it.
    /// </summary>
    public const uint DefaultSegmentBudget = 1_000_000u;

    internal static int Run(ParsedArguments arguments, TextWriter standardOutput, TextWriter standardError)
    {
        var outputDirectory = Path.GetFullPath(arguments.OutputDirectory ?? ".");
        var segmentBudget = arguments.SegmentBudget ?? DefaultSegmentBudget;

        try
        {
            var input = CliInput.LoadExe(arguments.Input!, outerBudget: 1, segmentBudget);
            var program = CliInput.Lower(input);
            var programEnd = unchecked(input.LoadAddress + (uint)input.InstructionWords.Count * 4u);

            var outcome = new RecompiledArtifactLauncher().Launch(
                program,
                input.Request,
                new ProgramEndHandoff(programEnd),
                outputDirectory,
                resultRegister: (int)R3000aRegister.V0);

            if (arguments.Json)
            {
                standardOutput.WriteLine(CliJson.Serialize(new CliJson.RunResult(
                    Kind: CliJson.RunKind,
                    Success: outcome.Result.Outcome == RecompiledArtifactOutcome.Success,
                    Artifact: ResolveArtifactPath(outputDirectory),
                    Output: outcome.Output,
                    Result: outcome.Result)));
            }
            else
            {
                WriteHumanOutcome(outcome, ResolveArtifactPath(outputDirectory), standardOutput);
            }

            return outcome.Result.ExitCode;
        }
        catch (Exception ex) when (
            ex is DirectoryNotFoundException or FileNotFoundException
                or UnauthorizedAccessException or IOException or InvalidDataException
                or ArgumentException or InvalidOperationException)
        {
            // No production RecompiledArtifactResult exists for a tooling/input/
            // launcher failure, so the diagnostic goes to stderr (never into the
            // stdout JSON document) and the exit class is 1.
            standardError.WriteLine($"psxrecomp run: {ex.Message}");
            return RecompiledArtifactExitCode.Failure;
        }
    }

    /// <summary>
    /// The artifact path a launcher run just produced in <paramref name="outputDirectory"/>
    /// — the binary the production engine builds and launches. POSIX toolchains
    /// produce the requested name verbatim while MinGW appends <c>.exe</c>; resolve
    /// whichever this platform actually produced (mirrors
    /// <c>GeneratedHostBuildService</c>'s resolution of the same facts).
    /// </summary>
    private static string ResolveArtifactPath(string outputDirectory)
    {
        var requested = Path.Combine(outputDirectory, RecompileCommand.ArtifactBinaryName);
        if (File.Exists(requested))
        {
            return requested;
        }
        var withExeSuffix = requested + ".exe";
        return File.Exists(withExeSuffix) ? withExeSuffix : requested;
    }

    private static void WriteHumanOutcome(RecompiledArtifactLauncher.LaunchOutcome outcome, string artifactPath, TextWriter standardOutput)
    {
        var result = outcome.Result;
        var header = result.Outcome switch
        {
            RecompiledArtifactOutcome.Success => "Execution completed",
            RecompiledArtifactOutcome.Blocked => "Execution stopped at an unsupported/blocked boundary",
            RecompiledArtifactOutcome.Failure => "Execution failed",
            _ => "Execution finished",
        };
        standardOutput.WriteLine($"{header} (state={result.State}, outcome={result.Outcome}).");
        if (result.GuestPc is { } guestPc)
        {
            standardOutput.WriteLine($"Guest PC: 0x{guestPc:X8}");
        }
        if (result.ResultValue is { } resultValue)
        {
            standardOutput.WriteLine($"Result: 0x{resultValue:X8}");
        }
        if (result.DiagnosticCode is { } code)
        {
            standardOutput.WriteLine($"Code: {code}");
            if (result.DiagnosticMessage is { Length: > 0 } message)
            {
                standardOutput.WriteLine($"Message: {message}");
            }
        }
        if (outcome.Output.Count > 0)
        {
            standardOutput.WriteLine($"TTY: {RenderTty(outcome.Output)}");
        }
        standardOutput.WriteLine($"Artifact: {artifactPath}");
    }

    private static string RenderTty(IReadOnlyList<byte> bytes)
    {
        var printable = bytes.Count > 0 && bytes.All(static b => b is >= 0x20 and < 0x7F);
        return printable
            ? Encoding.UTF8.GetString(bytes.ToArray())
            : Convert.ToHexString(bytes.ToArray());
    }

    /// <summary>
    /// Declines every unresolved transfer except "the guest ran off the end of its
    /// own text image", which is reported as a natural exit — the same handoff
    /// semantics <c>TitleExecutionService.ProgramEndHandoff</c> establishes for the interpreter
    /// path. This is caller-supplied continuation policy, not domain logic.
    /// </summary>
    private sealed class ProgramEndHandoff(uint programEnd) : ITitleExecutionHandoff
    {
        public TitleExecutionHandoffResult? Decide(RecompilerStateSnapshot segmentState) =>
            segmentState.PC == programEnd ? TitleExecutionHandoffResult.Exit() : null;
    }
}