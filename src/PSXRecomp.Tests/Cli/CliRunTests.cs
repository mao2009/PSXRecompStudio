using System.Text.Json;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Infrastructure;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.Cli;

/// <summary>
/// Integration coverage for Issue #460's minimal headless CLI
/// (<c>psxrecomp recompile</c> / <c>psxrecomp run</c>), exercised through the
/// real in-process entrypoint <see cref="Program.Execute"/>. These fixtures are
/// synthetic PS-X EXEs and always run — no ROM/BIOS dependency, only a real gcc
/// toolchain. The CLI hardwires the production #458 build service and #459
/// launcher, so every artifact path below is produced by real generated code,
/// built with gcc and launched as a real native process — evidence that the CLI
/// composes the production contracts rather than substituting mocks.
/// </summary>
[Test]
public sealed class CliRunTests
{
    private const uint EntryPc = 0x80010000u;
    private const uint UncompiledTarget = 0x80020000u;
    private const byte OriOpcode = 0x0D;
    private const byte JalOpcode = 0x03;
    private const byte JumpOpcode = 0x02;
    private const byte MarkerRegister = (byte)R3000aRegister.S1;
    private const uint DiagnosticMarker = 0x1234u;
    private const byte DiagnosticCharacter = (byte)'P';

    private static uint Immediate(byte opcode, byte rt, uint immediate) =>
        (uint)opcode << 26 | (uint)rt << 16 | (immediate & 0xFFFFu);

    /// <summary>
    /// The successful fixture: putchar('P') through the shared BIOS A0 vector,
    /// then a marker in S1, then the guest runs off the end of its own text image
    /// — the natural end the CLI's handoff reports as a legitimate exit.
    /// </summary>
    private static uint[] SuccessfulProgram() => new uint[]
    {
        Immediate(OriOpcode, (byte)R3000aRegister.T1, BiosHleRuntime.PutCharFunction),
        Immediate(OriOpcode, (byte)R3000aRegister.A0, DiagnosticCharacter),
        (uint)JalOpcode << 26 | (BiosJumpTables.A0VectorAddress & 0x0FFFFFFFu) >> 2,
        0u, // branch delay slot
        Immediate(OriOpcode, MarkerRegister, DiagnosticMarker),
    };

    /// <summary>An unconditional jump to an address no block is compiled for.</summary>
    private static uint[] UnresolvedJumpProgram() => new uint[]
    {
        (uint)JumpOpcode << 26 | ((UncompiledTarget & 0x0FFFFFFCu) >> 2),
        0u, // delay slot
    };

    private static byte[] BuildSyntheticExe(uint[] words, uint textStart = EntryPc, uint? entryPoint = null)
    {
        var fileContent = new byte[PsxExeHeader.HeaderSize + words.Length * 4];

        Buffer.BlockCopy(BitConverter.GetBytes(PsxExeHeader.Magic), 0, fileContent, 0, 8);
        BitConverter.GetBytes(entryPoint ?? textStart).CopyTo(fileContent, 0x10);      // entry
        BitConverter.GetBytes(0u).CopyTo(fileContent, 0x14);                            // gp
        BitConverter.GetBytes(textStart).CopyTo(fileContent, 0x18);                     // text start
        BitConverter.GetBytes((uint)(words.Length * 4)).CopyTo(fileContent, 0x1C);      // text size
        BitConverter.GetBytes(0x801FFF00u).CopyTo(fileContent, 0x30);                   // sp

        for (var i = 0; i < words.Length; i++)
        {
            BitConverter.GetBytes(words[i]).CopyTo(fileContent, PsxExeHeader.HeaderSize + i * 4);
        }

        return fileContent;
    }

    private static string WriteSyntheticExe(TempDirectory dir, string name, uint[] words)
    {
        return dir.WriteFile(name, BuildSyntheticExe(words));
    }

    private static (int Exit, string Output, string Error) Invoke(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = Program.Execute(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private static bool ArtifactExists(string artifactPath) => new FileInfo(artifactPath).Exists;

    [Fact]
    public void Recompile_SyntheticExe_BuildsArtifactAtCallerSelectedOutputDirectory()
    {
        using var dir = new TempDirectory();
        var exePath = WriteSyntheticExe(dir, "program.exe", SuccessfulProgram());
        var outDir = dir.CreateSubdirectory("caller-chosen");

        var (exit, output, error) = Invoke("recompile", exePath, "--output", outDir);

        exit.Should().Be(RecompiledArtifactExitCode.Success);
        error.Should().BeEmpty();
        output.Should().Contain("Build succeeded.");
        output.Should().Contain("Artifact:");

        var artifactPath = output[(output.IndexOf("Artifact:", StringComparison.Ordinal) + "Artifact:".Length)..].Trim();
        Path.GetDirectoryName(artifactPath).Should().Be(Path.GetFullPath(outDir));
        ArtifactExists(artifactPath).Should().BeTrue();
        new FileInfo(artifactPath).Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Recompile_Json_IsDeterministicAndCarriesSchema()
    {
        using var dir = new TempDirectory();
        var exePath = WriteSyntheticExe(dir, "program.exe", SuccessfulProgram());
        var outDir = dir.CreateSubdirectory("out");

        var (exit, output, error) = Invoke("recompile", exePath, "--output", outDir, "--json");

        exit.Should().Be(RecompiledArtifactExitCode.Success);
        error.Should().BeEmpty();

        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        root.EnumerateObject().Select(static p => p.Name).Should().Equal(
            "kind", "success", "status", "artifact", "errorCode", "message");
        root.GetProperty("kind").GetString().Should().Be("recompile");
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("status").GetString().Should().Be("Succeeded");
        root.GetProperty("artifact").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("errorCode").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("message").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Recompile_InvalidInput_JsonCarriesErrorFields()
    {
        using var dir = new TempDirectory();
        var garbage = dir.WriteFile("garbage.exe", new byte[PsxExeHeader.HeaderSize]);

        var (exit, output, _) = Invoke("recompile", garbage, "--output", dir.CreateSubdirectory("out"), "--json");

        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        root.GetProperty("kind").GetString().Should().Be("recompile");
        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("errorCode").GetString().Should().Be("INVALID_INPUT");
        root.GetProperty("message").GetString().Should().Contain("Invalid PS-X EXE magic");
    }

    [Fact]
    public void Recompile_MissingInputFile_ExitCodeOneWithDiagnosticOnStderr()
    {
        using var dir = new TempDirectory();
        var missing = Path.Combine(dir.FullPath, "nonexistent.exe");

        var (exit, output, error) = Invoke("recompile", missing, "--output", dir.FullPath);

        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        output.Should().BeEmpty();
        error.Should().Contain("psxrecomp recompile:");
    }

    [Fact]
    public void Run_SyntheticExe_JsonSucceedsWithMarkerAndTtyEvidence()
    {
        using var dir = new TempDirectory();
        var exePath = WriteSyntheticExe(dir, "program.exe", SuccessfulProgram());
        var outDir = dir.CreateSubdirectory("out");

        var (exit, output, error) = Invoke("run", exePath, "--output", outDir, "--json");

        exit.Should().Be(RecompiledArtifactExitCode.Success);
        error.Should().BeEmpty();

        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        root.GetProperty("kind").GetString().Should().Be("run");
        root.GetProperty("success").GetBoolean().Should().BeTrue();

        var artifact = root.GetProperty("artifact").GetString();
        artifact.Should().NotBeNullOrEmpty();
        Path.GetDirectoryName(artifact!).Should().Be(Path.GetFullPath(outDir));
        ArtifactExists(artifact!).Should().BeTrue();

        var result = root.GetProperty("result");
        // Production engine produced the run — the CLI composes #459, not a mock.
        result.GetProperty("engineName").GetString().Should().Be(RecompiledHostExecutionEngine.EngineName);
        result.GetProperty("exitCode").GetInt32().Should().Be(RecompiledArtifactExitCode.Success);
        result.GetProperty("state").GetInt32().Should().Be((int)TitleExecutionState.Completed);
        result.GetProperty("outcome").GetInt32().Should().Be((int)RecompiledArtifactOutcome.Success);
        result.GetProperty("diagnosticCode").ValueKind.Should().Be(JsonValueKind.Null);

        // The guest actually executed and reached the shared Runtime: putchar('P').
        root.GetProperty("output").EnumerateArray().Select(static e => (byte)e.GetInt32()).Should().Equal(DiagnosticCharacter);
    }

    [Fact]
    public void Run_SyntheticExe_HumanOutputReportsCompletionAndArtifactPath()
    {
        using var dir = new TempDirectory();
        var exePath = WriteSyntheticExe(dir, "program.exe", SuccessfulProgram());
        var outDir = dir.CreateSubdirectory("out");

        var (exit, output, error) = Invoke("run", exePath, "--output", outDir);

        exit.Should().Be(RecompiledArtifactExitCode.Success);
        error.Should().BeEmpty();
        output.Should().Contain("Execution completed");
        output.Should().Contain("Guest PC:");
        output.Should().Contain("Artifact:");
    }

    [Fact]
    public void Run_SyntheticExe_ExplicitSegmentBudgetIsHonored()
    {
        using var dir = new TempDirectory();
        var exePath = WriteSyntheticExe(dir, "program.exe", SuccessfulProgram());

        var (exit, output, error) = Invoke("run", exePath, "--segment-budget", "64", "--json");

        exit.Should().Be(RecompiledArtifactExitCode.Success);
        error.Should().BeEmpty();
        using var json = JsonDocument.Parse(output);
        json.RootElement.GetProperty("result").GetProperty("state").GetInt32().Should().Be((int)TitleExecutionState.Completed);
    }

    [Fact]
    public void Run_UnresolvedJumpToUncompiledTarget_ExitCodeTwo()
    {
        using var dir = new TempDirectory();
        var exePath = WriteSyntheticExe(dir, "jump.exe", UnresolvedJumpProgram());

        var (exit, output, _) = Invoke("run", exePath, "--output", dir.CreateSubdirectory("out"), "--json");

        // An explicit runtime boundary (control left the compiled image with no
        // continuation rule) is the "blocked" class, distinct from tooling failure.
        exit.Should().Be(RecompiledArtifactExitCode.Blocked);
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeFalse();
        var result = root.GetProperty("result");
        result.GetProperty("state").GetInt32().Should().Be((int)TitleExecutionState.UnsupportedTransfer);
        result.GetProperty("outcome").GetInt32().Should().Be((int)RecompiledArtifactOutcome.Blocked);
        result.GetProperty("exitCode").GetInt32().Should().Be(RecompiledArtifactExitCode.Blocked);
        result.GetProperty("guestPc").GetUInt32().Should().Be(UncompiledTarget);
    }

    [Fact]
    public void Run_Json_IsDeterministicAcrossEquivalentInvocations()
    {
        using var dir = new TempDirectory();
        var exePath = WriteSyntheticExe(dir, "program.exe", SuccessfulProgram());
        var outDir = dir.CreateSubdirectory("out");

        var (exit1, output1, error1) = Invoke("run", exePath, "--output", outDir, "--json");
        var (exit2, output2, error2) = Invoke("run", exePath, "--output", outDir, "--json");

        exit1.Should().Be(RecompiledArtifactExitCode.Success);
        exit2.Should().Be(RecompiledArtifactExitCode.Success);
        error1.Should().BeEmpty();
        error2.Should().BeEmpty();
        output2.Should().Be(output1);
    }

    [Fact]
    public void Run_Json_PreservesProductionTerminationFields()
    {
        using var dir = new TempDirectory();
        var exePath = WriteSyntheticExe(dir, "program.exe", SuccessfulProgram());

        var (exit, output, _) = Invoke("run", exePath, "--output", dir.CreateSubdirectory("out"), "--json");

        exit.Should().Be(RecompiledArtifactExitCode.Success);
        using var json = JsonDocument.Parse(output);
        var result = json.RootElement.GetProperty("result");
        result.EnumerateObject().Select(static p => p.Name).Should().BeEquivalentTo(
            "outcome", "exitCode", "state", "guestPc", "resultValue", "engineName", "diagnosticCode", "diagnosticMessage");
    }

    [Fact]
    public void Run_MissingInputFile_ExitCodeOneWithoutInventionOfResultJson()
    {
        using var dir = new TempDirectory();
        var missing = Path.Combine(dir.FullPath, "nonexistent.exe");

        var (exit, output, error) = Invoke("run", missing, "--json");

        // A tooling/input failure has no production RecompiledArtifactResult to
        // serialize; the diagnostic goes to stderr and the exit class is 1.
        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        output.Should().BeEmpty();
        error.Should().Contain("psxrecomp run:");
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("--help-typo")]
    public void Execute_UnknownCommandOrTopLevelOption_ExitCodeOne(string args)
    {
        var (exit, _, error) = Invoke(args);
        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        error.Should().Contain("psxrecomp:");
    }

    [Fact]
    public void Execute_NoArguments_ExitCodeOneWithUsageOnStderr()
    {
        var (exit, _, error) = Invoke();
        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        error.Should().Contain("usage:");
    }

    [Fact]
    public void Run_UnknownOption_ExitCodeOne()
    {
        var (exit, _, error) = Invoke("run", "input.exe", "--wat");
        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        error.Should().Contain("unknown option '--wat'.");
    }

    [Fact]
    public void Run_MissingOptionValue_ExitCodeOne()
    {
        var (exit, _, error) = Invoke("run", "input.exe", "--output");
        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        error.Should().Contain("missing value for option '--output'.");
    }

    [Fact]
    public void Run_MalformedSegmentBudget_ExitCodeOne()
    {
        var (exit, _, error) = Invoke("run", "input.exe", "--segment-budget", "abc");
        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        error.Should().Contain("invalid segment budget 'abc'");
    }

    [Fact]
    public void Run_ZeroSegmentBudget_ExitCodeOne()
    {
        var (exit, _, error) = Invoke("run", "input.exe", "--segment-budget", "0");
        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        error.Should().Contain("expected a positive integer");
    }

    [Fact]
    public void Run_MissingOutputValue_MissingInput_ExitCodeOne()
    {
        var (exit, _, error) = Invoke("run");
        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        error.Should().Contain("missing input PS-X EXE path.");
    }

    [Fact]
    public void Recompile_SegmentBudgetOptionIsRejectedWithExitCodeOne()
    {
        var (exit, _, error) = Invoke("recompile", "input.exe", "--output", "out", "--segment-budget", "16");
        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        error.Should().Contain("'--segment-budget' is only valid for 'run'.");
    }

    [Fact]
    public void Recompile_MissingRequiredOutput_ExitCodeOne()
    {
        var (exit, _, error) = Invoke("recompile", "input.exe");
        exit.Should().Be(RecompiledArtifactExitCode.Failure);
        error.Should().Contain("missing required option '--output <dir>'.");
    }

    [Fact]
    public void Help_ExitsZero() => Invoke("--help").Exit.Should().Be(RecompiledArtifactExitCode.Success);
}