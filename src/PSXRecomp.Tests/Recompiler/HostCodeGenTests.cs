using System.Diagnostics;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

[Test]
public class HostCodeGenTests
{
    [Fact]
    public void Generation_Constant_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 42),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v0 = (42u);");
        result.Source.Should().Contain("state->gpr[8] = v0;");
    }

    [Fact]
    public void Generation_ReadGpr_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 0, register: 5),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 10),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v0 = state->gpr[5];");
        result.Source.Should().Contain("state->gpr[10] = v0;");
    }

    [Fact]
    public void Generation_Add_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 2),
            new RecompilerIrOperation(RecompilerIrOperationKind.Add, resultValueId: 2, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v2 = (uint32_t)v0 + v1;");
    }

    [Fact]
    public void Generation_Sub_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 10),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 3),
            new RecompilerIrOperation(RecompilerIrOperationKind.Subtract, resultValueId: 2, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v2 = (uint32_t)v0 - v1;");
    }

    [Fact]
    public void Generation_And_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0xFF),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 0x0F),
            new RecompilerIrOperation(RecompilerIrOperationKind.And, resultValueId: 2, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v2 = (uint32_t)v0 & v1;");
    }

    [Fact]
    public void Generation_Or_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0xF0),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 0x0F),
            new RecompilerIrOperation(RecompilerIrOperationKind.Or, resultValueId: 2, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v2 = (uint32_t)v0 | v1;");
    }

    [Fact]
    public void Generation_Xor_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0xFF),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 0x0F),
            new RecompilerIrOperation(RecompilerIrOperationKind.Xor, resultValueId: 2, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v2 = (uint32_t)v0 ^ v1;");
    }

    [Fact]
    public void Generation_Nor_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0xF0),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 0x0F),
            new RecompilerIrOperation(RecompilerIrOperationKind.Nor, resultValueId: 2, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v2 = ~(v0 | v1);");
    }

    [Fact]
    public void Generation_LUI_Equivalent_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0x1234u << 16),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v0 = (305397760u);");
    }

    [Fact]
    public void Generation_SLL_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.ShiftLeftLogical, resultValueId: 1, inputValueA: 0, shiftAmount: 4),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 1, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v1 = (uint32_t)v0 << (4u & 31u);");
    }

    [Fact]
    public void Generation_SRL_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 256),
            new RecompilerIrOperation(RecompilerIrOperationKind.ShiftRightLogical, resultValueId: 1, inputValueA: 0, shiftAmount: 4),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 1, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v1 = (uint32_t)v0 >> (4u & 31u);");
    }

    [Fact]
    public void Generation_SRA_Op_Renders_Correctly()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0x80000000),
            new RecompilerIrOperation(RecompilerIrOperationKind.ShiftRightArithmetic, resultValueId: 1, inputValueA: 0, shiftAmount: 4),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 1, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v1 = recompiler_sra32(v0, 4u);");
    }

    [Fact]
    public void Determinism_Same_IR_Produces_ByteIdentical_Source()
    {
        var program = CreateRepresentativeGprProgram();
        var first = RecompilerHostCodeGen.Generate(program);
        var second = RecompilerHostCodeGen.Generate(program);

        first.Success.Should().BeTrue();
        second.Success.Should().BeTrue();
        first.Source.Should().Be(second.Source);
    }

    [Fact]
    public void ABI_Generated_Function_Signature_Is_Stable()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("static int32_t recompiler_block_0x00000000(RecompilerState* state)");
    }

    [Fact]
    public void FixedWidth_Source_Uses_Uint32T_And_No_Long()
    {
        var program = CreateRepresentativeGprProgram();
        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();

        result.Source.Should().Contain("uint32_t");
        result.Source.Should().NotContain(" long ");
        result.Source.Should().NotContain(" long\t");
        result.Source.Should().Contain("recompiler_sra32");
    }

    [Fact]
    public void UB_AddU_SubU_Uses_Unsigned_Wrap_Forms()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 100),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 50),
            new RecompilerIrOperation(RecompilerIrOperationKind.Add, resultValueId: 2, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.Subtract, resultValueId: 3, inputValueA: 0, inputValueB: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 8),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 3, register: 9),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("uint32_t v2 = (uint32_t)v0 + v1;");
        result.Source.Should().Contain("uint32_t v3 = (uint32_t)v0 - v1;");
    }

    [Fact]
    public void UB_SRA_Uses_Defined_Helper_Not_Bare_RShift()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0x80000000),
            new RecompilerIrOperation(RecompilerIrOperationKind.ShiftRightArithmetic, resultValueId: 1, inputValueA: 0, shiftAmount: 4),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 1, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        // The SRA operation itself must lower to the well-defined helper, not a
        // bare implementation-defined signed right-shift cast. (The dispatcher
        // legitimately uses "(int32_t)" for termination-reason returns, so the
        // negative assertion is scoped to the SRA operation statement only.)
        var sraLine = result.Source!.Split('\n').Single(line => line.Contains("recompiler_sra32(v0, 4u)"));
        sraLine.Should().NotContain("(int32_t)");
        result.Source.Should().Contain("recompiler_sra32(v0, 4u)");
    }

    [Fact]
    public void Unsupported_Unknown_Operation_Kind_Returns_Failure()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0, new[]
            {
                new RecompilerIrOperation((RecompilerIrOperationKind)255),
            }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4)),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().NotBeNull();
        result.Source.Should().BeNull();
    }

    [Fact]
    public void Unsupported_Validator_Invalid_IR_Is_Refused()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0, new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.Add, resultValueId: 2, inputValueA: 999, inputValueB: 1000),
            }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4)),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be("IR_VALIDATION_FAILED");
        result.Source.Should().BeNull();
    }

    [Fact]
    public void Unsupported_Zero_Register_Write_Is_Refused()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0, new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 0),
            }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4)),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be("IR_VALIDATION_FAILED");
        result.Source.Should().BeNull();
    }

    [Fact]
    public void Compile_Representative_Gpr_Program_Passes_Host_Compiler()
    {
        var program = CreateRepresentativeGprProgram();
        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();

        var exitCode = CompileWithHostCompiler(result.Source!);
        exitCode.Should().Be(0, "generated source must compile with the host compiler");
    }

    [Fact]
    public void Runtime_AddU_SubU_Wrap_SRL_SRA_EndToEnd()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            // v0 = 0xFFFFFFFF (4294967295)
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0xFFFFFFFF),
            // v1 = 1
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 1),
            // v2 = v0 + v1 = 0 (wrapping add of 0xFFFFFFFF + 1)
            new RecompilerIrOperation(RecompilerIrOperationKind.Add, resultValueId: 2, inputValueA: 0, inputValueB: 1),
            // v3 = v0 - v1 = 0xFFFFFFFE (wrapping sub)
            new RecompilerIrOperation(RecompilerIrOperationKind.Subtract, resultValueId: 3, inputValueA: 0, inputValueB: 1),
            // v4 = v0 >> 28 (logical, shift by 4) = 0x0F
            new RecompilerIrOperation(RecompilerIrOperationKind.ShiftRightLogical, resultValueId: 4, inputValueA: 0, shiftAmount: 28),
            // v5 = v0 SRA 28 (arithmetic) = 0xFFFFFFFF (sign-extended)
            new RecompilerIrOperation(RecompilerIrOperationKind.ShiftRightArithmetic, resultValueId: 5, inputValueA: 0, shiftAmount: 28),
            // Write results to gpr[8..11]
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 8),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 3, register: 9),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 4, register: 10),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 5, register: 11),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();

        var testResult = CompileAndRun(result.Source!, @"
int main() {
    RecompilerState state = {0};
    state.gpr[0] = 0;
    recompiler_block_0x00000000(&state);
    int ok = 1;
    if (state.gpr[8] != 0u) { ok = 0; }
    if (state.gpr[9] != 0xFFFFFFFEu) { ok = 0; }
    if (state.gpr[10] != 0x0Fu) { ok = 0; }
    if (state.gpr[11] != 0xFFFFFFFFu) { ok = 0; }
    return ok ? 0 : 1;
}");
        testResult.Should().Be(0, "runtime test must pass (ADD/SUB wrap, SRL, SRA correctness)");
    }

    [Fact]
    public void Exit_Success_Sets_NextPc_And_TerminationReason()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("state->next_pc = 4; state->termination_reason = 0; return 0;");
    }

    [Fact]
    public void Exit_NonSuccess_Sets_TerminationReason_And_Returns_Code()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0, Array.Empty<RecompilerIrOperation>(),
                new RecompilerIrExit(RecompilerIrTerminationReason.UnsupportedMemory)),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("state->termination_reason = 3; return (int32_t)3u;");
    }

    [Fact]
    public void Unsupported_Empty_Program_Is_Refused()
    {
        var result = RecompilerHostCodeGen.Generate(new RecompilerIrProgram(Array.Empty<RecompilerIrBlock>()));
        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be("UNSUPPORTED_EMPTY_PROGRAM");
        result.Source.Should().BeNull();
    }

    [Fact]
    public void MultiBlock_Program_Is_Generated_With_Budgeted_Dispatcher()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0x80000000u, new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 5),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 8),
            }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 0x80000004u)),
            new RecompilerIrBlock(0x80000004u, new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 7),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 9),
            }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 0x80000008u)),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeTrue();
        result.Source.Should().Contain("int32_t recompiler_dispatch(RecompilerState* state, uint32_t budget)");
        result.Source.Should().Contain("recompiler_block_0x80000000(state)");
        result.Source.Should().Contain("recompiler_block_0x80000004(state)");
        result.Source.Should().Contain("RECOMPILER_REASON_EXECUTION_BUDGET_EXCEEDED");
    }

    [Fact]
    public void Unsupported_Duplicate_Result_Value_Id_Is_Refused()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 2),
        });

        var result = RecompilerHostCodeGen.Generate(program);
        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be("DUPLICATE_RESULT_VALUE_ID");
        result.Source.Should().BeNull();
    }

    [Fact]
    public void RunHostProcess_TimesOut_When_Process_Exceeds_Budget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        #pragma warning disable PSXR005
        Action act = () => RunHostProcess("sleep", "30", 500);
        #pragma warning restore PSXR005
        act.Should().Throw<TimeoutException>();
    }

    #pragma warning disable PSXR005
    private static int CompileWithHostCompiler(string source)
    {
        var tempDir = CreateTempDir();
        try
        {
            var sourcePath = Path.Combine(tempDir, "test.c");
            var outputPath = Path.Combine(tempDir, "test.o");
            File.WriteAllText(sourcePath, source);

            var (exitCode, _, stderr) = RunHostProcess(
                "gcc", $"-std=c11 -O0 -Wall -Wextra -c {sourcePath} -o {outputPath}", 30000);

            if (exitCode != 0)
                throw new InvalidOperationException($"Host compilation failed (exit {exitCode}):\n{stderr}");

            return exitCode;
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
#pragma warning restore PSXR005

#pragma warning disable PSXR005
    private static int CompileAndRun(string generatedSource, string mainSource)
    {
        var tempDir = CreateTempDir();
        try
        {
            var combinedPath = Path.Combine(tempDir, "combined.c");
            var outputPath = Path.Combine(tempDir, "test_bin");

            File.WriteAllText(combinedPath, generatedSource + "\n" + mainSource);

            var (compileExit, _, compileErr) = RunHostProcess(
                "gcc", $"-std=c11 -O0 -Wall -Wextra {combinedPath} -o {outputPath}", 30000);

            if (compileExit != 0)
                throw new InvalidOperationException($"Compilation failed (exit {compileExit}):\n{compileErr}");

            var (runExit, _, _) = RunHostProcess(outputPath, "", 10000);
            return runExit;
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunHostProcess(string fileName, string arguments, int timeoutMs)
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

            var timedOutOut = stdoutTask.Result;
            var timedOutErr = stderrTask.Result;
            throw new TimeoutException(
                $"Host process timed out after {timeoutMs} ms: {fileName} {arguments}\nstdout:\n{timedOutOut}\nstderr:\n{timedOutErr}");
        }

        process.WaitForExit();
        var exitCode = process.ExitCode;
        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;

        return (exitCode, stdout, stderr);
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
#pragma warning restore PSXR005

    private static string CreateTempDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "psxrecomp-host-codegen", Path.GetRandomFileName());
#pragma warning disable PSXR005
        Directory.CreateDirectory(tempDir);
#pragma warning restore PSXR005
        return tempDir;
    }

    [Theory]
    [InlineData(RecompilerIrOperationKind.Load8)]
    [InlineData(RecompilerIrOperationKind.Load16)]
    [InlineData(RecompilerIrOperationKind.Load32)]
    public void Generation_Rejects_MemoryLoad_Instead_Of_Dropping_It(RecompilerIrOperationKind kind)
    {
        // The lowering stage now emits memory operations; this backend has no
        // emission for them, so it must fail rather than generate a block that
        // silently omits the access.
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0x1000),
            new RecompilerIrOperation(kind, resultValueId: 1, inputValueA: 0),
            new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 1, register: 8),
        });

        var result = RecompilerHostCodeGen.Generate(program);

        result.Success.Should().BeFalse();
        result.Source.Should().BeNull();
        result.DiagnosticCode.Should().Be("UNSUPPORTED_OPERATION_KIND");
    }

    [Fact]
    public void Generation_Rejects_MemoryStore_Instead_Of_Dropping_It()
    {
        var program = CreateSingleBlockProgram(new[]
        {
            new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0x1000),
            new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 1, register: 8),
            new RecompilerIrOperation(RecompilerIrOperationKind.Store32, inputValueA: 0, inputValueB: 1),
        });

        var result = RecompilerHostCodeGen.Generate(program);

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be("UNSUPPORTED_OPERATION_KIND");
    }

    [Fact]
    public void Generation_Rejects_A_BranchFlow_Instead_Of_Falling_Through()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(
                0,
                new[]
                {
                    new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 0, register: 8),
                    new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 1, register: 9),
                    new RecompilerIrOperation(RecompilerIrOperationKind.CompareEqual, resultValueId: 2, inputValueA: 0, inputValueB: 1),
                },
                new RecompilerIrExit(
                    RecompilerIrTerminationReason.Success,
                    nextPc: 8,
                    flow: new RecompilerIrFlow(RecompilerIrFlowKind.Branch, target: 0x20, conditionValueId: 2))),
        });

        var result = RecompilerHostCodeGen.Generate(program);

        result.Success.Should().BeFalse();
        // The compare operation is reported first; both are unemittable here.
        result.DiagnosticCode.Should().Be("UNSUPPORTED_OPERATION_KIND");
    }

    [Fact]
    public void Generation_Rejects_A_JumpFlow_Instead_Of_Falling_Through()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(
                0,
                new[] { new RecompilerIrOperation(RecompilerIrOperationKind.Nop) },
                new RecompilerIrExit(
                    RecompilerIrTerminationReason.Success,
                    flow: new RecompilerIrFlow(RecompilerIrFlowKind.Jump, target: 0x20))),
        });

        var result = RecompilerHostCodeGen.Generate(program);

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be("UNSUPPORTED_FLOW_KIND");
    }

    [Fact]
    public void Generation_Rejects_A_CallFlow_Instead_Of_Dropping_The_Transfer()
    {
        // Lowering emits Call for JAL; the Phase 3A backend does not implement a
        // transfer, and must say so rather than fall through to the next PC.
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(
                0,
                new[] { new RecompilerIrOperation(RecompilerIrOperationKind.Nop) },
                new RecompilerIrExit(
                    RecompilerIrTerminationReason.Success,
                    nextPc: 8,
                    flow: new RecompilerIrFlow(RecompilerIrFlowKind.Call, target: 0x20))),
        });

        var result = RecompilerHostCodeGen.Generate(program);

        result.Success.Should().BeFalse();
        result.DiagnosticCode.Should().Be("UNSUPPORTED_FLOW_KIND");
    }

    [Fact]
    public void Generation_Accepts_An_Explicit_SequentialFlow()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(
                0,
                new[] { new RecompilerIrOperation(RecompilerIrOperationKind.Nop) },
                new RecompilerIrExit(
                    RecompilerIrTerminationReason.Success,
                    nextPc: 4,
                    flow: new RecompilerIrFlow(RecompilerIrFlowKind.Sequential))),
        });

        RecompilerHostCodeGen.Generate(program).Success.Should().BeTrue();
    }

    private static RecompilerIrProgram CreateSingleBlockProgram(RecompilerIrOperation[] operations)
    {
        return new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0, operations, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4)),
        });
    }

    private static RecompilerIrProgram CreateRepresentativeGprProgram()
    {
        return new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0, new[]
            {
                // ADDIU t0, zero, 1  →  Constant(1) → WriteGpr(t0=8)
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 8),
                // ADDIU t1, zero, 2  →  Constant(2) → WriteGpr(t1=9)
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 2),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 1, register: 9),
                // ADDU t2, t0, t1    →  ReadGpr(t0) + ReadGpr(t1) → WriteGpr(t2=10)
                new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 2, register: 8),
                new RecompilerIrOperation(RecompilerIrOperationKind.ReadGpr, resultValueId: 3, register: 9),
                new RecompilerIrOperation(RecompilerIrOperationKind.Add, resultValueId: 4, inputValueA: 2, inputValueB: 3),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 4, register: 10),
            }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4)),
        });
    }
}
