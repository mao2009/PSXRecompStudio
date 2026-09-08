using System.Text;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

#pragma warning disable AARC003

[Test]
// Failure artifacts for the differential harness (Issue #211, B6). Everything
// in this class is pure test-side file I/O; the Domain layer stays I/O-free.
// File/Directory/DateTime/Random I/O lives here for the same reason as
// RecompilerHostExecutor (pragma-disable AARC003): artifact capture is a test
// concern, not a Domain one.
//
// Artifact layout per failing run (one directory per run):
//   fixture.txt                fixture identity + program + budgets + memory window
//   ir.json                    lowered Recompiler IR of the host side (when available)
//   reference-snapshot.json    interpreter state snapshot
//   actual-snapshot.json       recompiled state snapshot
//   diff.txt                   human-readable field-by-field diff
//   diff.machine-readable.txt  stable machine-readable diff
//   checkpoint.txt             interpreter PC trace vs host block traces + termination
//   generated-source.c         generated C the host compiled (when available)
//   program.c / program.stdout.txt / program.stderr.txt   from the preserved build
//                                                         dir on executor failure
public static class RecompilerDifferentialArtifacts
{
    public const string RootDirectoryEnvironmentVariable = "RECOMPILER_ARTIFACT_ROOT";

    /// <summary>
    /// Returns the artifact bundle directory for a failing differential run. On a
    /// mismatch or executor failure the call also emits the full artifact set and
    /// returns its path; on a clean match it returns null and writes nothing.
    /// </summary>
    public static string? Write(
        RecompilerDifferentialResult result,
        IRecompilerExecutor? actual = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsMatch) return null;

        var runDirectory = CreateRunDirectory(Sanitize(result.Fixture.Name));
        try
        {
            WriteText(runDirectory, "fixture.txt", DescribeFixture(result.Fixture));
            if (actual is RecompilerHostExecutor host)
            {
                if (host.LastGeneratedProgram is not null)
                {
                    WriteText(runDirectory, "ir.json", RecompilerIrSerializer.Serialize(host.LastGeneratedProgram));
                }
                if (host.LastGeneratedSource is not null)
                {
                    WriteText(runDirectory, "generated-source.c", host.LastGeneratedSource);
                }
                if (host.LastArtifactsPath is not null)
                {
                    foreach (var file in Directory.EnumerateFiles(host.LastArtifactsPath))
                    {
                        var fileName = Path.GetFileName(file);
                        if (IsBuildArtifact(fileName))
                        {
                            File.Copy(file, Path.Combine(runDirectory, fileName), overwrite: true);
                        }
                    }
                }
            }

            if (result.Reference.Snapshot is not null)
            {
                WriteText(runDirectory, "reference-snapshot.json", RecompilerIrSerializer.Serialize(result.Reference.Snapshot));
            }
            if (result.Actual.Snapshot is not null)
            {
                WriteText(runDirectory, "actual-snapshot.json", RecompilerIrSerializer.Serialize(result.Actual.Snapshot));
            }
            if (result.Diff is not null)
            {
                WriteText(runDirectory, "diff.txt", result.Diff.Describe());
                WriteText(runDirectory, "diff.machine-readable.txt", result.Diff.ToMachineReadable());
            }
            WriteText(runDirectory, "checkpoint.txt", DescribeCheckpoints(result));

            if (!result.BothCompleted)
            {
                WriteText(runDirectory, "executor-status.txt",
                    $"reference={result.Reference.Status} ({result.Reference.DiagnosticCode})\n" +
                    $"actual={result.Actual.Status} ({result.Actual.DiagnosticCode})\n" +
                    result.Actual.DiagnosticMessage);
            }
            return runDirectory;
        }
        catch (Exception ex)
        {
            // Artifact capture is best-effort: a broken bundle must never mask the
            // real assertion failure. Still note why to stderr so a CI run isn't
            // left silently without its failure artifacts.
            Console.Error.WriteLine($"RecompilerDifferentialArtifacts: failed to write artifact bundle: {ex}");
            TryDelete(runDirectory);
            return null;
        }
    }

    /// <summary>Assert message for a differential failure; materializes the artifact bundle.</summary>
    public static string FailureMessage(RecompilerDifferentialResult result, IRecompilerExecutor? actual = null)
    {
        var artifacts = Write(result, actual);
        var baseMessage = result.IsMatch
            ? result.Diff!.Describe()
            : result.Diff is not null
                ? result.Diff.Describe()
                : $"Interpreter {result.Reference.Status} ({result.Reference.DiagnosticCode}) vs " +
                  $"host {result.Actual.Status} ({result.Actual.DiagnosticCode}): {result.Actual.DiagnosticMessage}";
        return artifacts is null
            ? baseMessage
            : baseMessage + $"\nDifferential artifacts: {artifacts}";
    }

    private static string CreateRunDirectory(string fixtureName)
    {
        var root = RootDirectory();
        var runDirectory = Path.Combine(
            root,
            $"{fixtureName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x100000):X6}");
        Directory.CreateDirectory(runDirectory);
        return runDirectory;
    }

    private static string RootDirectory()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(RootDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }
        return Path.Combine(Path.GetTempPath(), "psxrecomp-differential-artifacts");
    }

    private static string DescribeFixture(RecompilerDifferentialFixture fixture)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"name={fixture.Name}");
        sb.AppendLine($"entryPc=0x{fixture.EntryPc:X8}");
        sb.AppendLine($"stepBudget={fixture.StepBudget}");
        sb.AppendLine($"referenceStepBudget={fixture.ReferenceStepBudget}");
        sb.AppendLine("instructions=");
        for (var i = 0; i < fixture.Instructions.Count; i++)
        {
            sb.AppendLine($"  0x{fixture.PcOfInstruction(i):X8}: 0x{fixture.Instructions[i]:X8}");
        }
        sb.Append("memoryWindow=");
        sb.AppendLine(string.Join(",", fixture.MemoryWindow.Select(pc => "0x" + pc.ToString("X8"))));
        sb.Append($"initialMemoryItems={fixture.InitialMemory.Count}");
        return sb.ToString();
    }

    private static string DescribeCheckpoints(RecompilerDifferentialResult result)
    {
        var referencePcs = result.Reference.Snapshot?.PcTrace ?? Array.Empty<uint>();
        var actualPcs = result.Actual.Snapshot?.PcTrace ?? Array.Empty<uint>();
        var termination = result.Actual.Snapshot?.Termination.ToString() ?? result.Actual.Status.ToString();
        return "interpreterPcTrace=" + FormatTrace(referencePcs) +
               "\nhostBlockTrace=" + FormatTrace(actualPcs) +
               $"\ntermination={termination}";
    }

    private static string FormatTrace(IReadOnlyList<uint> trace) =>
        string.Join(",", trace.Select(pc => "0x" + pc.ToString("X8")));

    private static void WriteText(string directory, string fileName, string content) =>
        File.WriteAllText(Path.Combine(directory, fileName), content);

    private static bool IsBuildArtifact(string fileName) =>
        fileName is "program.c" or "program.stdout.txt" or "program.stderr.txt" or "input.txt";

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        catch
        {
            // Best effort: a stale artifact dir on test failure is noise, not data.
        }
    }
}
#pragma warning restore AARC003