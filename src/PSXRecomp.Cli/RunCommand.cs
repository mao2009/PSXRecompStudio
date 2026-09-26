using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Diagnostics;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure;
using PSXRecomp.Infrastructure.Diagnostics;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// <c>psxrecomp run</c>: builds and launches one runnable recompiled artifact from a
/// legally supplied input (a PS-X EXE or, via <see cref="CliInput.Load"/>, a supported
/// CHD) through the #459 <see cref="RecompiledArtifactLauncher"/>,
/// reusing the natural-end handoff semantics <c>TitleExecutionService</c> establishes
/// (running off the end of the guest's own text image is a natural exit; every other
/// unresolved transfer is classified as unsupported). The launcher is single-shot
/// (it builds and runs in one operation and does not accept an already-built binary),
/// so <c>run</c> rebuilds from the input; persisted-artifact-only relaunch is outside
/// Issue #460. With <c>--report</c>, the command also packages the existing
/// privacy-safe #457 diagnostic contracts after a production result exists.
/// With <c>--frame-evidence</c>, it additionally runs the same resolved input
/// through the production interpreter and reports a deterministic snapshot of
/// that engine's real managed GPU/VRAM state (#575). This supplemental evidence
/// never changes the generated-host run's exit code or termination result.
/// The production result maps onto the process exit code as-is:
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
    /// <summary>Stable filename used by <c>run --report</c> inside the output directory.</summary>
    public const string DiagnosticBundleFileName = "diagnostic-report.zip";

    internal static int Run(ParsedArguments arguments, TextWriter standardOutput, TextWriter standardError)
    {
        var outputDirectory = Path.GetFullPath(arguments.OutputDirectory ?? ".");
        var segmentBudget = arguments.SegmentBudget ?? DefaultSegmentBudget;

        try
        {
            string? inputSha256 = null;
            PSXRecomp.Core.Execution.PsxExeTitleExecution input;
            if (arguments.Report)
            {
                var resolved = CliInput.LoadWithIdentity(arguments.Input!, outerBudget: 1, segmentBudget);
                input = resolved.Execution;
                inputSha256 = resolved.Sha256;
            }
            else
            {
                input = CliInput.Load(arguments.Input!, outerBudget: 1, segmentBudget);
            }

            var program = CliInput.Lower(input);
            var programEnd = unchecked(input.LoadAddress + (uint)input.InstructionWords.Count * 4u);

            var outcome = new RecompiledArtifactLauncher().Launch(
                program,
                input.Request,
                new ProgramEndHandoff(programEnd),
                outputDirectory,
                resultRegister: (int)R3000aRegister.V0);

            var artifactPath = ResolveArtifactPath(outputDirectory);
            var frameEvidence = arguments.FrameEvidence
                ? ProductionFrameEvidenceCollector.Collect(input)
                : null;
            string? diagnosticBundlePath = null;
            if (arguments.Report)
            {
                try
                {
                    diagnosticBundlePath = WriteDiagnosticBundle(
                        outputDirectory,
                        artifactPath,
                        inputSha256!,
                        segmentBudget,
                        outcome.Result);
                }
                catch (Exception ex) when (
                    ex is DirectoryNotFoundException or FileNotFoundException
                        or UnauthorizedAccessException or IOException or InvalidDataException
                        or ArgumentException or InvalidOperationException)
                {
                    standardError.WriteLine(
                        $"psxrecomp run: diagnostic report could not be written: {ex.Message}");
                }
            }

            if (arguments.Json)
            {
                var success = outcome.Result.Outcome == RecompiledArtifactOutcome.Success;
                if (diagnosticBundlePath is not null && frameEvidence is not null)
                {
                    standardOutput.WriteLine(CliJson.Serialize(new CliJson.RunResultWithDiagnosticBundleAndFrameEvidence(
                        Kind: CliJson.RunKind,
                        Success: success,
                        Artifact: artifactPath,
                        Output: outcome.Output,
                        Result: outcome.Result,
                        DiagnosticBundle: diagnosticBundlePath,
                        FrameEvidence: frameEvidence)));
                }
                else if (diagnosticBundlePath is not null)
                {
                    standardOutput.WriteLine(CliJson.Serialize(new CliJson.RunResultWithDiagnosticBundle(
                        Kind: CliJson.RunKind,
                        Success: success,
                        Artifact: artifactPath,
                        Output: outcome.Output,
                        Result: outcome.Result,
                        DiagnosticBundle: diagnosticBundlePath)));
                }
                else if (frameEvidence is not null)
                {
                    standardOutput.WriteLine(CliJson.Serialize(new CliJson.RunResultWithFrameEvidence(
                        Kind: CliJson.RunKind,
                        Success: success,
                        Artifact: artifactPath,
                        Output: outcome.Output,
                        Result: outcome.Result,
                        FrameEvidence: frameEvidence)));
                }
                else
                {
                    standardOutput.WriteLine(CliJson.Serialize(new CliJson.RunResult(
                        Kind: CliJson.RunKind,
                        Success: success,
                        Artifact: artifactPath,
                        Output: outcome.Output,
                        Result: outcome.Result)));
                }
            }
            else
            {
                WriteHumanOutcome(outcome, artifactPath, standardOutput);
                if (diagnosticBundlePath is not null)
                {
                    standardOutput.WriteLine($"Diagnostic report: {diagnosticBundlePath}");
                }
                if (frameEvidence is not null)
                {
                    WriteFrameEvidence(frameEvidence, standardOutput);
                }
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

    private static string WriteDiagnosticBundle(
        string outputDirectory,
        string artifactPath,
        string inputSha256,
        uint segmentBudget,
        RecompiledArtifactResult result)
    {
        var report = ExecutionDiagnosticReport.From(
            result,
            inputSha256,
            segmentBudget,
            productRevision: GetProductRevision(),
            artifactIdentity: $"sha256:{ComputeSha256(artifactPath)}");

        var environment = ExecutionEnvironmentCollector.Capture();
        var bundlePath = Path.Combine(outputDirectory, DiagnosticBundleFileName);

        using var destination = File.Create(bundlePath);
        ExecutionDiagnosticBundleWriter.Write(
            destination,
            report,
            environment,
            Array.Empty<Diagnostic>());

        return bundlePath;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string? GetProductRevision() =>
        typeof(RunCommand).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

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

    private static void WriteFrameEvidence(
        ProductionFrameEvidenceCollector.FrameEvidence evidence,
        TextWriter standardOutput)
    {
        if (evidence.Status == ProductionFrameEvidenceCollector.AvailableStatus)
        {
            standardOutput.WriteLine(
                $"Frame evidence: {evidence.Width}x{evidence.Height} sha256:{evidence.Sha256} " +
                $"(productionState={evidence.ProductionState}).");
            if (evidence.DiagnosticCode is { Length: > 0 } diagnosticCode)
            {
                standardOutput.WriteLine($"Frame production diagnostic: {diagnosticCode}");
            }
            return;
        }

        standardOutput.WriteLine($"Frame evidence: unavailable ({evidence.Reason}).");
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