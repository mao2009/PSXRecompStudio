using System.Diagnostics;
using System.Globalization;
using PSXRecomp.Core.Recompiler;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

/// <summary>
/// Issue #734 (R1): <c>recompiler_code_current</c> must notice a write into any page a RAM-placed block spans, not only
/// the first and last ones. The check previously summed the generations of the first and last page only, so a write
/// into a middle page left the sum unchanged and stale code was accepted. The C guard is exercised here through gcc on
/// the real generated driver source (the slice from the RAM model up to the BIOS window), with no ROM and no Runtime.
/// </summary>
[Test]
public sealed class ArtifactCodeGuardSpanTests
{
    // 1100 words = 4400 bytes starting 4 bytes before a page end: the block covers pages 0x10, 0x11 (entirely) and 0x12.
    private const uint BlockVa = 0x80010FFCu; // KSEG0 alias of physical 0x00010FFC
    private const int BlockWords = 1100;

    [Theory]
    [InlineData(0x00010FFCu, "first")]  // page 0x10, first word of the block
    [InlineData(0x00011064u, "middle")] // page 0x11, fully inside the block (the case the old check missed)
    [InlineData(0x00012100u, "last")]   // page 0x12, last page of the block
    public void CodeCurrent_WriteIntoAnySpannedPage_RejectsStaleCode(uint writeAddress, string page)
    {
        var source = RecompiledArtifactCodeGen.Generate(GenerateDispatch()).Source!;

        var output = RunGuardHarness(source, writeAddress);

        output.Should().Be("0", $"a write into the {page} page of the block changed its compiled words, so the block must not run");
    }

    /// <summary>
    /// Issue #734 review (N1/N2): a zero-length load and a count whose <c>4u * count</c> wraps must fail closed. The
    /// generator never emits an empty unit (the host requires <c>Words.Count == RetiredInstructionCount</c> and lowering
    /// always emits the entry instruction), so neither input is reachable from generated code; 0 means "not current".
    /// Before the review fix, the zero-length case returned 1 unconditionally (even outside RAM) and the wrapped count
    /// returned 1 with no word compared, so stale code could run.
    /// </summary>
    [Theory]
    [InlineData(0xBFC00000u, 0u, ulong.MaxValue, "zero-length load outside RAM (the BIOS window)")]
    [InlineData(0x80011000u, 0u, ulong.MaxValue, "zero-length load inside RAM")]
    [InlineData(0x80011000u, 0x40000000u, 0ul, "count 2^30: 4u * count wraps to 0, so no page is summed and no word is compared")]
    public void CodeCurrent_InvalidLength_FailsClosed(uint pc, uint count, ulong seen, string why)
    {
        var source = RecompiledArtifactCodeGen.Generate(GenerateDispatch()).Source!;

        var output = RunCompiled(BuildProbeHarness(source), pc.ToString(CultureInfo.InvariantCulture), count.ToString(CultureInfo.InvariantCulture), seen.ToString(CultureInfo.InvariantCulture));

        output.Should().Be("0", why);
    }

    private static RecompilerHostCodeGenResult GenerateDispatch()
    {
        var instruction = PSXRecomp.Core.Cpu.R3000aDecoder.Decode(0x34020000u); // ori $v0, $zero, 0
        var program = PSXRecomp.Core.Recompiler.MipsToIrLowerer.LowerProgram([(instruction, 0x80010000u)]);
        return RecompilerHostCodeGen.Generate(program);
    }

    private const string GuardSliceStart = "#define PSX_RAM_SIZE";
    private const string GuardSliceEnd = "/* BIOS ROM window";

    private static string GuardSlice(string source)
    {
        var start = source.IndexOf(GuardSliceStart, StringComparison.Ordinal);
        var end = source.IndexOf(GuardSliceEnd, StringComparison.Ordinal);
        if (start < 0 || end <= start) throw new InvalidOperationException("RAM/guard slice not found in the generated driver source.");
        return source[start..end];
    }

    /// <summary>Calls the guard once with the given pc, count and seen value (argv), with RAM zeroed; prints the result.</summary>
    private static string BuildProbeHarness(string source) =>
        $$"""
            #include <stdint.h>
            #include <stdio.h>
            #include <stdlib.h>
            {{GuardSlice(source)}}

            int main(int argc, char** argv) {
                static const uint32_t words[4] = { 0x34020000u, 0x34020001u, 0x34020002u, 0x34020003u };
                uint32_t pc = (uint32_t)strtoul(argv[1], NULL, 10);
                uint32_t count = (uint32_t)strtoul(argv[2], NULL, 10);
                uint64_t seen = (uint64_t)strtoull(argv[3], NULL, 10);
                printf("%d\n", recompiler_code_current(NULL, pc, words, count, &seen));
                return 0;
            }
            """;

    private static string BuildHarness(string source, uint writeAddress)
    {
        var slice = GuardSlice(source);

        return $$"""
            #include <stdint.h>
            #include <stdio.h>
            #include <stdlib.h>
            {{slice}}
            #define BLOCK_VA 0x{{BlockVa:x}}u
            #define BLOCK_PA 0x{{BlockVa & 0x1FFFFFFFu:x}}u
            #define BLOCK_WORDS {{BlockWords}}u

            static void put32(uint32_t pa, uint32_t v) {
                artifact_ram[pa] = (uint8_t)v;
                artifact_ram[pa + 1u] = (uint8_t)(v >> 8);
                artifact_ram[pa + 2u] = (uint8_t)(v >> 16);
                artifact_ram[pa + 3u] = (uint8_t)(v >> 24);
            }

            int main(int argc, char** argv) {
                uint32_t words[BLOCK_WORDS];
                uint32_t i;
                uint32_t write_pa = (uint32_t)strtoul(argv[1], NULL, 16);
                uint64_t seen = UINT64_MAX; /* never trusted before the first full compare */
                for (i = 0; i < BLOCK_WORDS; i++) {
                    words[i] = 0x34020000u + i;
                    put32(BLOCK_PA + 4u * i, words[i]);
                }
                if (recompiler_code_current(NULL, BLOCK_VA, words, BLOCK_WORDS, &seen) != 1) {
                    printf("PRIME_FAILED\n");
                    return 2;
                }
                artifact_ram[write_pa] ^= 0x01u; /* a guest/host write that changes one compiled word */
                ARTIFACT_WROTE(write_pa, 1u);
                printf("%d\n", recompiler_code_current(NULL, BLOCK_VA, words, BLOCK_WORDS, &seen));
                return 0;
            }
            """;
    }

#pragma warning disable AARC003 // Test-only gcc harness: writes a temp C file and runs the compiled guard; production process I/O stays in Infrastructure.
    private static string RunGuardHarness(string source, uint writeAddress) =>
        RunCompiled(BuildHarness(source, writeAddress), writeAddress.ToString("x", CultureInfo.InvariantCulture));

    private static string RunCompiled(string cSource, params string[] args)
    {
        var dir = Path.Combine(Path.GetTempPath(), "psx-code-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cPath = Path.Combine(dir, "guard.c");
            var exePath = Path.Combine(dir, "guard.exe");
            File.WriteAllText(cPath, cSource);

            var compile = Run("gcc", ["-std=c99", "-O0", "-Wall", "-Werror=implicit-function-declaration", "-o", exePath, cPath], dir);
            if (compile.ExitCode != 0) throw new InvalidOperationException("gcc failed: " + compile.Stderr);

            var run = Run(exePath, args, dir);
            if (run.ExitCode != 0) throw new InvalidOperationException($"harness exited {run.ExitCode}: {run.Stdout}{run.Stderr}");
            return run.Stdout.Trim();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(string fileName, IReadOnlyList<string> args, string workDir)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {fileName}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{fileName} did not exit within 60 s");
        }
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
#pragma warning restore AARC003
}
