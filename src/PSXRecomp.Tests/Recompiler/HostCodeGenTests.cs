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
        result.Source.Should().Contain("recompiler_sra32(v0, 4u)");
        result.Source.Should().NotContain("(int32_t)");
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

    #pragma warning disable PSXR005
    private static int CompileWithHostCompiler(string source)
    {
        var tempDir = CreateTempDir();
        try
        {
            var sourcePath = Path.Combine(tempDir, "test.c");
            var outputPath = Path.Combine(tempDir, "test.o");
            File.WriteAllText(sourcePath, source);

            var psi = new ProcessStartInfo("gcc", $"-std=c11 -O0 -Wall -Wextra -c {sourcePath} -o {outputPath}")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi)!;
            process.WaitForExit(30000);
            return process.ExitCode;
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

            var compilePsi = new ProcessStartInfo("gcc",
                $"-std=c11 -O0 -Wall -Wextra {combinedPath} -o {outputPath}")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using (var compileProcess = Process.Start(compilePsi)!)
            {
                compileProcess.WaitForExit(30000);
                if (compileProcess.ExitCode != 0)
                {
                    var stderr = compileProcess.StandardError.ReadToEnd();
                    throw new InvalidOperationException($"Compilation failed (exit {compileProcess.ExitCode}): {stderr}");
                }
            }

            var runPsi = new ProcessStartInfo(outputPath)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using (var runProcess = Process.Start(runPsi)!)
            {
                runProcess.WaitForExit(10000);
                return runProcess.ExitCode;
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
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
