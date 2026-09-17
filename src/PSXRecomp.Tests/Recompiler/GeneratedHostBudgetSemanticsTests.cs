using System.Diagnostics;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

[Test]
public sealed class GeneratedHostBudgetSemanticsTests
{
    [Fact]
    public void Dispatch_Budget_Is_A_Strict_PreDispatch_Limit_For_Zero_One_And_N()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0, new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 1),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 0, register: 8),
            }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4)),
            new RecompilerIrBlock(4, new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 0, immediate: 0x80000000u),
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 1, immediate: 0xAABBCCDDu),
                new RecompilerIrOperation(RecompilerIrOperationKind.Store32, inputValueA: 0, inputValueB: 1),
                new RecompilerIrOperation(RecompilerIrOperationKind.Constant, resultValueId: 2, immediate: 2),
                new RecompilerIrOperation(RecompilerIrOperationKind.WriteGpr, inputValueA: 2, register: 9),
            }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 8)),
        });

        var generated = RecompilerHostCodeGen.Generate(program);
        generated.Success.Should().BeTrue();

        var exitCode = CompileAndRun(generated.Source!, @"
#include <string.h>
int main(void) {
    RecompilerState zero = {0};
    zero.pc = 0u;
    int32_t zero_rc = recompiler_dispatch(&zero, 0u);
    if (zero_rc != RECOMPILER_REASON_EXECUTION_BUDGET_EXCEEDED) return 10;
    if (zero.pc != 0u || zero.gpr[8] != 0u || zero.gpr[9] != 0u) return 11;
    if (recompiler_read_mem32(0, 0x80000000u) != 0u) return 12;

    RecompilerState one = {0};
    one.pc = 0u;
    int32_t one_rc = recompiler_dispatch(&one, 1u);
    if (one_rc != RECOMPILER_REASON_EXECUTION_BUDGET_EXCEEDED) return 20;
    if (one.pc != 4u || one.gpr[8] != 1u || one.gpr[9] != 0u) return 21;
    if (recompiler_read_mem32(0, 0x80000000u) != 0u) return 22;

    RecompilerState two = {0};
    two.pc = 0u;
    int32_t two_rc = recompiler_dispatch(&two, 2u);
    if (two_rc != RECOMPILER_REASON_SUCCESS) return 30;
    if (two.pc != 8u || two.gpr[8] != 1u || two.gpr[9] != 2u) return 31;
    if (recompiler_read_mem32(0, 0x80000000u) != 0xAABBCCDDu) return 32;

    return 0;
}");

        exitCode.Should().Be(0,
            "budget 0 must retire nothing, budget 1 exactly one block, and budget N at most N blocks without post-limit register/RAM mutation");
    }

    [Fact]
    public void Dispatch_Does_Not_Invoke_StateMutating_HostTransfer_After_Budget_Exhaustion()
    {
        var program = new RecompilerIrProgram(new[]
        {
            new RecompilerIrBlock(0, new[]
            {
                new RecompilerIrOperation(RecompilerIrOperationKind.Nop),
            }, new RecompilerIrExit(RecompilerIrTerminationReason.Success, 4)),
        });

        var generated = RecompilerHostCodeGen.Generate(program);
        generated.Success.Should().BeTrue();

        var exitCode = CompileAndRun(generated.Source!, @"
static uint32_t callback_calls = 0u;
static int32_t mutating_host_transfer(RecompilerState* state) {
    callback_calls++;
    state->gpr[8] = 0xDEADBEEFu;
    state->next_pc = 0u;
    state->termination_reason = RECOMPILER_REASON_SUCCESS;
    return 0;
}
int main(void) {
    RecompilerState state = {0};
    state.pc = 0x00001000u;
    state.host_transfer = mutating_host_transfer;
    int32_t rc = recompiler_dispatch(&state, 0u);
    if (rc != RECOMPILER_REASON_EXECUTION_BUDGET_EXCEEDED) return 40;
    if (callback_calls != 0u) return 41;
    if (state.gpr[8] != 0u || state.pc != 0x00001000u) return 42;
    return 0;
}");

        exitCode.Should().Be(0,
            "the strict budget guard must run before a host-transfer callback can mutate guest state");
    }

#pragma warning disable AARC003
    private static int CompileAndRun(string generatedSource, string mainSource)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "psxrecomp-budget-semantics", Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var sourcePath = Path.Combine(tempDir, "budget_test.c");
            var outputPath = Path.Combine(tempDir, OperatingSystem.IsWindows() ? "budget_test.exe" : "budget_test");
            File.WriteAllText(sourcePath, generatedSource + "\n" + MemoryHelperStubs + "\n" + mainSource);

            var compile = Run("gcc", $"-std=c11 -O0 -Wall -Wextra {sourcePath} -o {outputPath}", 30000);
            if (compile.ExitCode != 0)
            {
                throw new InvalidOperationException($"Generated-host budget test failed to compile:\n{compile.Stderr}");
            }

            return Run(outputPath, string.Empty, 10000).ExitCode;
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(string fileName, string arguments, int timeoutMs)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"Host process timed out: {fileName} {arguments}");
        }
        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
#pragma warning restore AARC003

    private const string MemoryHelperStubs = @"
#define PSX_TEST_RAM_SIZE (2u * 1024u * 1024u)
static uint8_t test_ram[PSX_TEST_RAM_SIZE];
static uint32_t test_translate(uint32_t va) {
    if (va <= 0x7FFFFFFFu) return va;
    if (va <= 0xBFFFFFFFu) return va & 0x1FFFFFFFu;
    return 0xFFFFFFFFu;
}
uint8_t recompiler_read_mem8(void* core, uint32_t address) {
    (void)core;
    uint32_t pa = test_translate(address);
    return pa < PSX_TEST_RAM_SIZE ? test_ram[pa] : 0u;
}
uint16_t recompiler_read_mem16(void* core, uint32_t address) {
    (void)core;
    uint32_t pa = test_translate(address);
    if (pa > PSX_TEST_RAM_SIZE - 2u) return 0u;
    return (uint16_t)(test_ram[pa] | ((uint16_t)test_ram[pa + 1u] << 8));
}
uint32_t recompiler_read_mem32(void* core, uint32_t address) {
    (void)core;
    uint32_t pa = test_translate(address);
    if (pa > PSX_TEST_RAM_SIZE - 4u) return 0u;
    return (uint32_t)(test_ram[pa]
        | ((uint32_t)test_ram[pa + 1u] << 8)
        | ((uint32_t)test_ram[pa + 2u] << 16)
        | ((uint32_t)test_ram[pa + 3u] << 24));
}
void recompiler_write_mem8(void* core, uint32_t address, uint8_t value) {
    (void)core;
    uint32_t pa = test_translate(address);
    if (pa < PSX_TEST_RAM_SIZE) test_ram[pa] = value;
}
void recompiler_write_mem16(void* core, uint32_t address, uint16_t value) {
    (void)core;
    uint32_t pa = test_translate(address);
    if (pa > PSX_TEST_RAM_SIZE - 2u) return;
    test_ram[pa] = (uint8_t)value;
    test_ram[pa + 1u] = (uint8_t)(value >> 8);
}
void recompiler_write_mem32(void* core, uint32_t address, uint32_t value) {
    (void)core;
    uint32_t pa = test_translate(address);
    if (pa > PSX_TEST_RAM_SIZE - 4u) return;
    test_ram[pa] = (uint8_t)value;
    test_ram[pa + 1u] = (uint8_t)(value >> 8);
    test_ram[pa + 2u] = (uint8_t)(value >> 16);
    test_ram[pa + 3u] = (uint8_t)(value >> 24);
}
";
}
