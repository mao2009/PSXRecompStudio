using System.Text.Json;
using FluentAssertions;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.Cli;

/// <summary>
/// Issue #644: caller-supplied explicit entry roots through <c>psxrecomp run</c> and
/// <c>recompile</c>, and the in-image-uncompiled <c>UNRESOLVED_TRANSFER</c> refinement. The
/// fixture is a synthetic PS-X EXE whose only route to the block at <see cref="Target"/> is a
/// register-indirect <c>jalr</c>, so the block exists in the artifact only when it is a root.
/// </summary>
[Test]
public sealed class CliEntryRootTests
{
    private const uint Load = 0x80010000u;
    private const uint Target = 0x8001001Cu;
    private const uint ProgramEnd = 0x80010028u;
    private const uint OutsideImageTarget = 0x80020000u;

    private static uint I(byte opcode, byte rs, byte rt, uint imm) =>
        (uint)opcode << 26 | (uint)rs << 21 | (uint)rt << 16 | (imm & 0xFFFFu);

    /// <summary>
    /// <c>jalr $ra,$t0</c> to <paramref name="target"/>; the callee sets S1 and returns; the
    /// return site sets S2 and jumps to the end of the image (a natural exit).
    /// </summary>
    private static uint[] IndirectCallProgram(uint target) =>
    [
        I(0x0F, 0, 8, target >> 16),                 // 00 lui  $t0, hi
        I(0x0D, 8, 8, target & 0xFFFFu),             // 04 ori  $t0, $t0, lo
        8u << 21 | 31u << 11 | 0x09u,                // 08 jalr $ra, $t0
        0u,                                          // 0C delay slot
        I(0x0D, 0, 18, 0x77),                        // 10 ori  $s2, $zero, 0x77 (return site)
        0x08000000u | ((ProgramEnd & 0x0FFFFFFFu) >> 2), // 14 j end
        0u,                                          // 18 delay slot
        I(0x0D, 0, 17, 0x1234),                      // 1C ori  $s1, $zero, 0x1234 (callee)
        31u << 21 | 0x08u,                           // 20 jr   $ra
        0u,                                          // 24 delay slot
    ];

    private static byte[] BuildSyntheticExe(uint[] words)
    {
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

        return file;
    }

    private static string WriteExe(TempDirectory dir, uint target = Target) =>
        dir.WriteFile("program.exe", BuildSyntheticExe(IndirectCallProgram(target)));

    private static (int Exit, string Output, string Error) Invoke(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = Program.Execute(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public void Run_WithFallbackDisabledAndNoRoot_ReportsInImageUncompiledDiagnostic()
    {
        using var dir = new TempDirectory();

        var (exit, output, _) = Invoke(
            "run", WriteExe(dir), "--no-mixed-fallback", "--output", dir.CreateSubdirectory("out"), "--json");

        exit.Should().Be(RecompiledArtifactExitCode.Blocked);
        using var json = JsonDocument.Parse(output);
        json.RootElement.TryGetProperty("entryRoots", out _).Should().BeFalse("no roots were supplied");
        var result = json.RootElement.GetProperty("result");
        result.GetProperty("state").GetInt32().Should().Be((int)TitleExecutionState.UnsupportedTransfer);
        result.GetProperty("guestPc").GetUInt32().Should().Be(Target);
        result.GetProperty("diagnosticCode").GetString().Should().Be("UNRESOLVED_TRANSFER_IN_IMAGE");
        result.GetProperty("diagnosticMessage").GetString().Should()
            .Contain("0x8001001C").And.Contain("no block was compiled").And.Contain("--entry-root 0x8001001C");
    }

    [Fact]
    public void Run_OutsideImageIndirectTarget_KeepsTheOriginalDiagnostic()
    {
        using var dir = new TempDirectory();

        var (exit, output, _) = Invoke(
            "run", WriteExe(dir, OutsideImageTarget), "--output", dir.CreateSubdirectory("out"), "--json");

        exit.Should().Be(RecompiledArtifactExitCode.Blocked);
        using var json = JsonDocument.Parse(output);
        var result = json.RootElement.GetProperty("result");
        result.GetProperty("guestPc").GetUInt32().Should().Be(OutsideImageTarget);
        result.GetProperty("diagnosticCode").GetString().Should().Be("UNRESOLVED_TRANSFER");
        result.GetProperty("diagnosticMessage").GetString().Should().Be(
            "Guest control transferred to 0x80020000, which the engine has no compiled code for and the " +
            "handoff has no continuation rule for. Dynamic overlay recompilation (Issue #249) is the upgrade path.");
    }

    [Fact]
    public void Run_WithTheTargetAsExplicitRoot_ContinuesPastTheFormerStopAndEchoesTheRoot()
    {
        using var dir = new TempDirectory();

        var (exit, output, error) = Invoke(
            "run", WriteExe(dir), "--entry-root", "0x8001001C", "--output", dir.CreateSubdirectory("out"), "--json");

        error.Should().BeEmpty();
        exit.Should().Be(RecompiledArtifactExitCode.Success);
        using var json = JsonDocument.Parse(output);
        json.RootElement.GetProperty("result").GetProperty("state").GetInt32()
            .Should().Be((int)TitleExecutionState.Completed);
        json.RootElement.GetProperty("entryRoots").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("0x8001001C");
    }

    [Fact]
    public void Run_RootsAreEchoedCanonically_AscendingAndDistinct()
    {
        using var dir = new TempDirectory();

        var (exit, output, _) = Invoke(
            "run", WriteExe(dir),
            "--entry-root", "0x80010020", "--entry-root", "0X8001001c", "--entry-root", "0x80010020",
            "--output", dir.CreateSubdirectory("out"), "--json");

        exit.Should().Be(RecompiledArtifactExitCode.Success);
        using var json = JsonDocument.Parse(output);
        json.RootElement.GetProperty("entryRoots").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("0x8001001C", "0x80010020");
    }

    [Fact]
    public void Recompile_WithExplicitRoot_CompilesTheRootBlockAndEchoesTheRoot()
    {
        using var dir = new TempDirectory();
        var exePath = WriteExe(dir);
        var withoutRoot = dir.CreateSubdirectory("without");
        var withRoot = dir.CreateSubdirectory("with");

        var (exit0, _, _) = Invoke("recompile", exePath, "--output", withoutRoot);
        var (exit1, output, error) = Invoke(
            "recompile", exePath, "--entry-root", "0x8001001C", "--output", withRoot, "--json");

        exit0.Should().Be(RecompiledArtifactExitCode.Success);
        error.Should().BeEmpty();
        exit1.Should().Be(RecompiledArtifactExitCode.Success);
        using var json = JsonDocument.Parse(output);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("entryRoots").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("0x8001001C");
        ReadSource(withoutRoot).Should().NotContain("recompiler_block_0x8001001C");
        ReadSource(withRoot).Should().Contain("recompiler_block_0x8001001C");
    }

    [Fact]
    public void Recompile_WithoutRoots_JsonHasNoEntryRootsProperty()
    {
        using var dir = new TempDirectory();

        var (exit, output, _) = Invoke("recompile", WriteExe(dir), "--output", dir.CreateSubdirectory("out"), "--json");

        exit.Should().Be(RecompiledArtifactExitCode.Success);
        using var json = JsonDocument.Parse(output);
        json.RootElement.TryGetProperty("entryRoots", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("run")]
    [InlineData("recompile")]
    public void InvalidRootValue_ThroughEitherCommand_FailsClosedNamingTheRoot(string command)
    {
        using var dir = new TempDirectory();

        var (exit, _, error) = Invoke(
            command, WriteExe(dir), "--entry-root", "0x8001001E", "--output", dir.CreateSubdirectory("out"));

        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        error.Should().Contain("0x8001001E").And.Contain("not 4-byte aligned");
    }

    [Theory]
    [InlineData("run")]
    [InlineData("recompile")]
    public void RootOutsideTheImage_ThroughEitherCommand_FailsClosedNamingTheRoot(string command)
    {
        using var dir = new TempDirectory();

        var (exit, _, error) = Invoke(
            command, WriteExe(dir), "--entry-root", "0x80020000", "--output", dir.CreateSubdirectory("out"));

        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        error.Should().Contain("0x80020000").And.Contain("outside the supplied text image");
    }

    [Theory]
    [InlineData("80025350")]      // no 0x prefix: never guess between decimal and hex
    [InlineData("0x")]
    [InlineData("0xZZ")]
    [InlineData("0x100000000")]   // does not fit a 32-bit PC
    [InlineData("-1")]
    public void MalformedRoot_IsACliInputError(string value)
    {
        using var dir = new TempDirectory();

        var (exit, _, error) = Invoke("run", WriteExe(dir), "--entry-root", value);

        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        error.Should().Contain("invalid entry root").And.Contain($"'{value}'");
    }

    [Theory]
    [InlineData("run")]
    [InlineData("recompile")]
    public void MissingRootValue_IsACliInputError(string command)
    {
        using var dir = new TempDirectory();

        var (exit, _, error) = Invoke(command, WriteExe(dir), "--output", dir.CreateSubdirectory("out"), "--entry-root");

        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        error.Should().Contain("missing value for option '--entry-root'");
    }

#pragma warning disable AARC003 // Test-only inspection of the generated artifact source.
    private static string ReadSource(string outputDirectory) =>
        File.ReadAllText(Path.Combine(outputDirectory, "recompiled-artifact.c"));
#pragma warning restore AARC003
}
