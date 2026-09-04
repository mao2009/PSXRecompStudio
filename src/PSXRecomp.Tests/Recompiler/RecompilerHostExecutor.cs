using System.Diagnostics;
using System.Text;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

#pragma warning disable PSXR005

[Test]
// Host-side build/run executor for the differential harness (Issue #211).
// It lowers a fixture to Recompiler IR, generates deterministic C with
// RecompilerHostCodeGen, compiles it with the fixed recipe, and runs it
// bounded, then parses the emitted state snapshot back.
//
// This class intentionally lives in the Test assembly rather than the Domain
// layer (PSXRecomp.Core/Recompiler): compiler invocation and file I/O are
// forbidden in the Domain layer by the architecture analyzer, and the existing
// host-compilation tests already follow this pattern (pragma-disable PSXR005).
public sealed class RecompilerHostExecutor : IRecompilerExecutor
{
    public const string ExecutorName = "recompiled-host-gcc";

    private const string Compiler = "gcc";
    private const string CompilerArgs = "-std=c11 -O0 -Wall -Wextra";
    private const int BuildTimeoutMs = 30000;
    private const int RunTimeoutMs = 5000;

    public string Name => ExecutorName;

    public RecompilerExecutionResult Execute(RecompilerDifferentialFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        RecompilerIrProgram? program;
        try
        {
            program = LowerFixture(fixture);
        }
        catch (Exception ex)
        {
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.GenerationFailed,
                "LOWER_FAILED",
                ex.Message);
        }

        var validation = RecompilerIrValidator.Validate(program);
        if (!validation.IsValid)
        {
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.GenerationFailed,
                "IR_VALIDATION_FAILED",
                $"IR validation failed with {validation.Diagnostics.Count} diagnostic(s).");
        }

        var generated = RecompilerHostCodeGen.Generate(program);
        if (!generated.Success)
        {
            return RecompilerExecutionResult.Failed(
                RecompilerExecutionStatus.GenerationFailed,
                generated.DiagnosticCode ?? "HOST_CODEGEN_FAILED",
                generated.DiagnosticMessage ?? "Host code generation failed.");
        }

        var run = BuildAndRun(fixture, generated.Source!);
        return run;
    }

    /// <summary>The on-disk product of a recompiled fixture (Issue #209 vertical slice).</summary>
    public readonly record struct CompiledBinary(string BinaryPath, string InputPath, string DirectoryPath);

    /// <summary>
    /// Compiles a fixture into a reusable executable and returns the artifact
    /// locations. The caller owns the returned directory's lifetime. Enables the
    /// #209 vertical-slice "compare the produced test binary" check.
    /// </summary>
    public CompiledBinary CompileRecompiledBinary(RecompilerDifferentialFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var tempDir = CreateTempDir();

        try
        {
            var program = LowerFixture(fixture);
            var validation = RecompilerIrValidator.Validate(program);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(
                    $"IR validation failed with {validation.Diagnostics.Count} diagnostic(s).");
            }

            var generated = RecompilerHostCodeGen.Generate(program);
            if (!generated.Success)
            {
                throw new InvalidOperationException(
                    generated.DiagnosticMessage ?? "Host code generation failed.");
            }

            var sourcePath = Path.Combine(tempDir, "program.c");
            var inputPath = Path.Combine(tempDir, "input.txt");
            var binaryPath = Path.Combine(tempDir, "program");

            File.WriteAllText(sourcePath, generated.Source + "\n" + DriverSource);
            WriteInputFile(inputPath, fixture);

            var (exit, _, stderr) = RunProcess(
                Compiler, $"{CompilerArgs} {sourcePath} -o {binaryPath}", BuildTimeoutMs, out var timedOut);
            if (timedOut || exit != 0)
            {
                throw new InvalidOperationException(
                    $"Host compilation failed." + (string.IsNullOrEmpty(stderr) ? "" : "\n" + Truncate(stderr, 2000)));
            }

            return new CompiledBinary(binaryPath, inputPath, tempDir);
        }
        catch
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            throw;
        }
    }

    /// <summary>Runs a previously compiled recompiled binary against its fixture input and returns stdout.</summary>
    public string RunRecompiledBinary(CompiledBinary binary)
    {
        var (exit, stdout, _) = RunProcess(binary.BinaryPath, binary.InputPath, RunTimeoutMs, out var timedOut);
        if (timedOut)
        {
            throw new InvalidOperationException("Generated executable exceeded the bounded execution budget.");
        }
        if (exit != 0 && !stdout.Contains("RSNAPSHOT_BEGIN"))
        {
            throw new InvalidOperationException($"Generated executable failed (exit {exit}).");
        }
        return stdout;
    }

    private static RecompilerIrProgram LowerFixture(RecompilerDifferentialFixture fixture)
    {
        var instructions = new List<(R3000aInstruction Instruction, uint EntryPc)>();
        for (var i = 0; i < fixture.Instructions.Count; i++)
        {
            var instruction = R3000aDecoder.Decode(fixture.Instructions[i]);
            instructions.Add((instruction, fixture.PcOfInstruction(i)));
        }
        return MipsToIrLowerer.LowerProgram(instructions);
    }

    private RecompilerExecutionResult BuildAndRun(RecompilerDifferentialFixture fixture, string generatedSource)
    {
        var tempDir = CreateTempDir();
        try
        {
            var sourcePath = Path.Combine(tempDir, "program.c");
            var inputPath = Path.Combine(tempDir, "input.txt");
            var outputPath = Path.Combine(tempDir, "program");

            File.WriteAllText(sourcePath, generatedSource + "\n" + DriverSource);
            WriteInputFile(inputPath, fixture);

            var (compileExit, _, compileErr) = RunProcess(
                Compiler, $"{CompilerArgs} {sourcePath} -o {outputPath}", BuildTimeoutMs, out var buildTimedOut);

            if (buildTimedOut)
            {
                return RecompilerExecutionResult.Failed(
                    RecompilerExecutionStatus.BuildFailed,
                    "BUILD_TIMEOUT",
                    "Host compilation exceeded the build timeout.");
            }

            if (compileExit != 0)
            {
                return RecompilerExecutionResult.Failed(
                    RecompilerExecutionStatus.BuildFailed,
                    "BUILD_FAILED",
                    $"Host compilation failed (exit {compileExit}):\n{Truncate(compileErr, 2000)}");
            }

            var (runExit, stdout, _) = RunProcess(outputPath, inputPath, RunTimeoutMs, out var runTimedOut);

            if (runTimedOut)
            {
                return RecompilerExecutionResult.Failed(
                    RecompilerExecutionStatus.TimedOut,
                    "EXECUTION_TIMEOUT",
                    "Generated executable exceeded the bounded execution budget.");
            }

            if (runExit != 0 && !stdout.Contains("RSNAPSHOT_BEGIN"))
            {
                return RecompilerExecutionResult.Failed(
                    RecompilerExecutionStatus.ExecutionFailed,
                    "EXECUTION_FAILED",
                    $"Generated executable failed (exit {runExit}).");
            }

            var snapshot = SnapshotParser.Parse(stdout);
            if (snapshot is null)
            {
                return RecompilerExecutionResult.Failed(
                    RecompilerExecutionStatus.MalformedResult,
                    "MALFORMED_SNAPSHOT",
                    Truncate(stdout, 2000));
            }

            return RecompilerExecutionResult.Completed(snapshot);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    private static void WriteInputFile(string path, RecompilerDifferentialFixture fixture)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < fixture.InitialGpr.Count; i++)
        {
            sb.Append(fixture.InitialGpr[i]).Append(' ');
        }
        sb.Append('\n');
        sb.Append(fixture.InitialHi).Append('\n');
        sb.Append(fixture.InitialLo).Append('\n');
        sb.Append(fixture.EntryPc).Append('\n');
        sb.Append(fixture.StepBudget).Append('\n');
        File.WriteAllText(path, sb.ToString());
    }

    private static (int ExitCode, string Stdout, string Stderr) RunProcess(
        string fileName, string arguments, int timeoutMs, out bool timedOut)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            TryKillTree(process);
            timedOut = true;
            return (int.MinValue, stdoutTask.Result, stderrTask.Result);
        }

        process.WaitForExit();
        timedOut = false;
        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string CreateTempDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "psxrecomp-differential", Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value.Substring(0, max) + "...";

    // Deterministic, self-contained C driver appended to the generated source.
    // Reads <gpr[0..31]> <hi> <lo> <pc> <budget> from the input file, runs the
    // bounded dispatch, then prints a stable, parseable state snapshot.
    private const string DriverSource = @"
#include <stdio.h>

int main(int argc, char** argv) {
    if (argc < 2) return 90; /* MissingInput */
    FILE* in = fopen(argv[1], ""r"");
    if (!in) return 91;      /* CannotOpenInput */
    RecompilerState state;
    unsigned long u;
    int i;
    for (i = 0; i < 32; i++) { if (fscanf(in, ""%lu"", &u) != 1) return 92; state.gpr[i] = (uint32_t)u; }
    if (fscanf(in, ""%lu"", &u) != 1) return 92; state.hi = (uint32_t)u;
    if (fscanf(in, ""%lu"", &u) != 1) return 92; state.lo = (uint32_t)u;
    if (fscanf(in, ""%lu"", &u) != 1) return 92; state.pc = (uint32_t)u;
    if (fscanf(in, ""%lu"", &u) != 1) return 92; unsigned long budget = u;
    fclose(in);
    state.gpr[0] = 0;
    recompiler_dispatch(&state, (uint32_t)budget);
    printf(""RSNAPSHOT_BEGIN\n"");
    printf(""termination=%d\n"", (int)state.termination_reason);
    printf(""pc=0x%08X\n"", state.pc);
    printf(""hi=0x%08X\n"", state.hi);
    printf(""lo=0x%08X\n"", state.lo);
    for (i = 0; i < 32; i++) printf(""gpr[%d]=0x%08X\n"", i, state.gpr[i]);
    printf(""RSNAPSHOT_END\n"");
    return (int)state.termination_reason;
}
";
}
#pragma warning restore PSXR005