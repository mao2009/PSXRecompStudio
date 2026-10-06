using System.Text.Json;
using FluentAssertions;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.Cli;

/// <summary>
/// Issue #693: the opt-in <c>--mixed-fallback</c> gate of <c>psxrecomp run</c>. The fixture is #644's synthetic PS-X
/// EXE whose only route to the callee is a register-indirect <c>jalr</c>, so the artifact has no block for it unless the
/// interpreter runs it. Off by default, the pre-existing in-image stop is untouched.
/// </summary>
[Test]
public sealed class CliMixedFallbackTests
{
    private const uint Load = 0x80010000u;
    private const uint Target = 0x8001001Cu;
    private const uint ProgramEnd = 0x80010028u;

    private static uint I(byte opcode, byte rs, byte rt, uint imm) =>
        (uint)opcode << 26 | (uint)rs << 21 | (uint)rt << 16 | (imm & 0xFFFFu);

    private static uint[] IndirectCallProgram(uint target) =>
    [
        I(0x0F, 0, 8, target >> 16),                 // lui  $t0, hi
        I(0x0D, 8, 8, target & 0xFFFFu),             // ori  $t0, $t0, lo
        8u << 21 | 31u << 11 | 0x09u,                // jalr $ra, $t0
        0u,                                          // delay slot
        I(0x0D, 0, 18, 0x77),                        // ori  $s2, $zero, 0x77 (return site)
        0x08000000u | ((ProgramEnd & 0x0FFFFFFFu) >> 2), // j end
        0u,                                          // delay slot
        I(0x0D, 0, 17, 0x1234),                      // ori  $s1, $zero, 0x1234 (callee)
        31u << 21 | 0x08u,                           // jr   $ra
        0u,                                          // delay slot
    ];

    private static string WriteExe(TempDirectory dir, uint target = Target)
    {
        var words = IndirectCallProgram(target);
        var file = new byte[PsxExeHeader.HeaderSize + words.Length * 4];
        BitConverter.GetBytes(PsxExeHeader.Magic).CopyTo(file, 0);
        BitConverter.GetBytes(Load).CopyTo(file, 0x10);
        BitConverter.GetBytes(Load).CopyTo(file, 0x18);
        BitConverter.GetBytes((uint)(words.Length * 4)).CopyTo(file, 0x1C);
        BitConverter.GetBytes(0x801FFF00u).CopyTo(file, 0x30);
        for (var i = 0; i < words.Length; i++)
        {
            BitConverter.GetBytes(words[i]).CopyTo(file, PsxExeHeader.HeaderSize + i * 4);
        }

        return dir.WriteFile("program.exe", file);
    }

    private static (int Exit, string Output, string Error) Invoke(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = Program.Execute(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public void Run_WithoutTheFlag_KeepsTheInImageStop_AndEmitsNoFallbackEvidence()
    {
        using var dir = new TempDirectory();

        var (exit, output, _) = Invoke("run", WriteExe(dir), "--output", dir.CreateSubdirectory("out"), "--json");

        exit.Should().Be(RecompiledArtifactExitCode.Blocked);
        using var json = JsonDocument.Parse(output);
        json.RootElement.TryGetProperty("mixedFallback", out _).Should().BeFalse("mixed execution is off by default");
        json.RootElement.GetProperty("result").GetProperty("diagnosticCode").GetString().Should().Be("UNRESOLVED_TRANSFER_IN_IMAGE");
    }

    [Fact]
    public void Run_WithTheFlag_RunsTheCalleeOnTheInterpreterAndReturns_WithoutAnExplicitRoot()
    {
        using var dir = new TempDirectory();

        var (exit, output, error) = Invoke(
            "run", WriteExe(dir), "--mixed-fallback", "--output", dir.CreateSubdirectory("out"), "--json");

        error.Should().BeEmpty();
        exit.Should().Be(RecompiledArtifactExitCode.Success);
        using var json = JsonDocument.Parse(output);
        json.RootElement.TryGetProperty("entryRoots", out _).Should().BeFalse("no root was added: the interpreter ran the callee");
        json.RootElement.GetProperty("result").GetProperty("state").GetInt32().Should().Be((int)TitleExecutionState.Completed);
        var fallback = json.RootElement.GetProperty("mixedFallback");
        fallback.GetProperty("transitions").GetUInt32().Should().Be(1);
        fallback.GetProperty("returns").GetUInt32().Should().Be(1);
        var target = fallback.GetProperty("targets").EnumerateArray().Should().ContainSingle().Subject;
        target.GetProperty("target").GetString().Should().Be("0x8001001C");
        target.GetProperty("lastReturnPc").GetString().Should().Be("0x80010010");
        fallback.TryGetProperty("transferMilliseconds", out _).Should().BeFalse("timings never enter the deterministic document");
    }

    [Fact]
    public void Run_WithTheFlag_TheEvidenceIsDeterministic()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();

        var a = Invoke("run", WriteExe(first), "--mixed-fallback", "--output", first.CreateSubdirectory("out"), "--json").Output;
        var b = Invoke("run", WriteExe(second), "--mixed-fallback", "--output", second.CreateSubdirectory("out"), "--json").Output;

        JsonDocument.Parse(a).RootElement.GetProperty("mixedFallback").GetRawText()
            .Should().Be(JsonDocument.Parse(b).RootElement.GetProperty("mixedFallback").GetRawText());
    }

    [Fact]
    public void Run_WithTheFlag_AnOutOfImageTargetStillFailsClosed()
    {
        using var dir = new TempDirectory();

        var (exit, output, _) = Invoke(
            "run", WriteExe(dir, 0x80020000u), "--mixed-fallback", "--output", dir.CreateSubdirectory("out"), "--json");

        exit.Should().Be(RecompiledArtifactExitCode.Blocked);
        using var json = JsonDocument.Parse(output);
        json.RootElement.GetProperty("result").GetProperty("diagnosticCode").GetString().Should().Be("UNRESOLVED_TRANSFER");
        json.RootElement.GetProperty("mixedFallback").GetProperty("transitions").GetUInt32().Should().Be(0);
    }

    [Fact]
    public void Run_TheTransitionBudgetIsHonoured()
    {
        using var dir = new TempDirectory();

        var (exit, output, _) = Invoke(
            "run", WriteExe(dir), "--mixed-fallback", "--fallback-max-transitions", "1", "--fallback-segment-budget", "1",
            "--output", dir.CreateSubdirectory("out"), "--json");

        exit.Should().Be(RecompiledArtifactExitCode.Failure, "a segment that cannot finish within one instruction stops the run");
        using var json = JsonDocument.Parse(output);
        json.RootElement.GetProperty("result").GetProperty("diagnosticCode").GetString().Should().Be(MixedFallbackDiagnostics.SegmentBudgetExhausted);
    }

    [Theory]
    [InlineData("recompile", "--mixed-fallback")]
    [InlineData("recompile", "--fallback-segment-budget", "5")]
    public void RecompileDoesNotAcceptTheRunOnlyFallbackOptions(params string[] args)
    {
        using var dir = new TempDirectory();

        var (exit, _, error) = Invoke([args[0], WriteExe(dir), "--output", dir.CreateSubdirectory("out"), .. args[1..]]);

        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        error.Should().Contain("only valid for 'run'");
    }

    [Theory]
    [InlineData("--fallback-segment-budget", "5", "require '--mixed-fallback'")]
    [InlineData("--fallback-max-transitions", "5", "require '--mixed-fallback'")]
    public void BudgetsWithoutTheFlag_AreRejected(string option, string value, string expected)
    {
        using var dir = new TempDirectory();

        var (exit, _, error) = Invoke("run", WriteExe(dir), option, value);

        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        error.Should().Contain(expected);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("-3")]
    public void BudgetValues_MustBePositiveIntegers(string value)
    {
        using var dir = new TempDirectory();

        var (exit, _, error) = Invoke("run", WriteExe(dir), "--mixed-fallback", "--fallback-segment-budget", value);

        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        error.Should().Contain("expected a positive integer");
    }
}
