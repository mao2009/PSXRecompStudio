using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.DiscImage.AnalysisArtifacts;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.E2E;

/// <summary>
/// The Issue #461 vertical proof: a synthetic PS-X EXE driven through the entire
/// <em>production</em> pipeline — <see cref="PsxExe.Load"/>, the #409 input bridge
/// <see cref="PsxExeTitleInput.Build"/>, decode + <see cref="MipsToIrLowerer"/>,
/// <see cref="RecompilerHostCodeGen"/>, <see cref="RecompiledArtifactCodeGen"/>,
/// the #458 <see cref="GeneratedHostBuildService"/>, and the #459
/// <see cref="RecompiledArtifactLauncher"/> — asserting the generated/recompiled code
/// actually executed (an observable result marker and TTY bytes only the artifact's
/// own generated code could produce), produced deterministically, and stopped at
/// classified boundaries rather than crashing.
///
/// These fixtures are synthetic and always run: no ROM/BIOS dependency, only a real
/// gcc toolchain. The user-facing <c>psxrecomp run</c> composition is separately
/// covered by <see cref="CliRunTests"/>; the real-input half of #461 is
/// <see cref="RealExeE2ETests"/>.
/// </summary>
[Test]
public sealed class RecompiledArtifactE2ETests
{
    private const uint EntryPc = 0x80010000u;
    private const byte OriOpcode = 0x0D;
    private const byte JalOpcode = 0x03;
    private const byte JumpOpcode = 0x02;
    private const byte FunctionNumberRegister = (byte)R3000aRegister.T1;
    private const byte FirstArgumentRegister = (byte)R3000aRegister.A0;
    private const byte MarkerRegister = (byte)R3000aRegister.S1;
    private const uint DiagnosticMarker = 0x7777u;
    private const byte DiagnosticCharacter = (byte)'P';
    private const string ArtifactBinaryName = "recompiled-artifact";

    private static uint Immediate(byte opcode, byte rt, uint immediate) =>
        (uint)opcode << 26 | (uint)rt << 16 | (immediate & 0xFFFFu);

    /// <summary>
    /// The success fixture: putchar('P') through the shared BIOS A0 vector, then the
    /// marker in S1 — the S1 value and the emitted byte are evidence only the
    /// recompiled host blocks could produce — then execution runs off the end of the
    /// text image, which the handoff reports as the natural, legitimate exit.
    /// </summary>
    private static uint[] SuccessfulProgram() => new uint[]
    {
        Immediate(OriOpcode, FunctionNumberRegister, BiosHleRuntime.PutCharFunction),
        Immediate(OriOpcode, FirstArgumentRegister, DiagnosticCharacter),
        (uint)JalOpcode << 26 | (BiosJumpTables.A0VectorAddress & 0x0FFFFFFFu) >> 2,
        0u, // branch delay slot
        Immediate(OriOpcode, MarkerRegister, DiagnosticMarker),
    };

    /// <summary>A real PS-X EXE image (header + text) loaded at <see cref="EntryPc"/>.</summary>
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

    /// <summary>Decode + <see cref="MipsToIrLowerer.LowerProgram"/> — the #460 CLI's lowering stage.</summary>
    private static RecompilerIrProgram Lower(uint entryPc, IReadOnlyList<uint> words)
    {
        var instructions = new List<(R3000aInstruction Instruction, uint EntryPc)>();
        for (var i = 0; i < words.Count; i++)
        {
            instructions.Add((R3000aDecoder.Decode(words[i]), entryPc + (uint)(i * 4)));
        }

        return MipsToIrLowerer.LowerProgram(instructions);
    }

    /// <summary>
    /// The full production input stage for one EXE image: <see cref="PsxExe.Load"/>,
    /// <see cref="PsxExeTitleInput.Build"/>, then lowering of the image words.
    /// </summary>
    private static (RecompilerIrProgram Program, TitleExecutionRequest Request, uint LoadAddress, int WordCount)
        Compile(byte[] exeBytes)
    {
        var exe = PsxExe.Load(exeBytes, "program.exe");
        var input = PsxExeTitleInput.Build(exe, outerBudget: 1, segmentBudget: 64);
        return (
            Lower(input.LoadAddress, input.InstructionWords),
            input.Request,
            input.LoadAddress,
            input.InstructionWords.Count);
    }

    private static bool ExistsAndNonEmpty(string path) =>
        new FileInfo(path) is { Exists: true, Length: > 0 };

    [Fact]
    public void SyntheticExe_FullProductionChain_ExecutesGeneratedCodeToCompletion()
    {
        var words = SuccessfulProgram();
        var exeBytes = BuildSyntheticExe(words);

        // Stage 1 — disc/EXE input: production PsxExe.Load (not a test fixture mock).
        var exe = PsxExe.Load(exeBytes, "program.exe");
        exe.Header.TextStart.Should().Be(EntryPc);
        exe.DecodedInstructionCount.Should().Be(words.Length);

        // Stage 2 — production execution-input bridge (Issue #409).
        var input = PsxExeTitleInput.Build(exe, outerBudget: 1, segmentBudget: 64);
        input.LoadAddress.Should().Be(EntryPc);
        input.Request.EntryPc.Should().Be(EntryPc);
        input.InstructionWords.Should().Equal(words);

        // Stage 3 — production lowering (decode + MipsToIrLowerer, the CLI's own stage).
        var program = Lower(input.LoadAddress, input.InstructionWords);
        program.Blocks.Should().NotBeEmpty();

        // Stage 4 — production host + runnable-artifact code generation.
        var host = RecompilerHostCodeGen.Generate(program);
        host.Success.Should().BeTrue(host.DiagnosticMessage);
        host.Source.Should().NotBeNullOrEmpty();
        host.Source.Should().Contain($"recompiler_block_0x{EntryPc:X8}");
        var artifact = RecompiledArtifactCodeGen.Generate(host);
        artifact.Success.Should().BeTrue(artifact.DiagnosticMessage);
        artifact.Source.Should().Contain(RecompiledArtifactCodeGen.HostTransferFlag);

        // Stage 5 — production artifact build (Issue #458): real gcc, real native output.
        using var dir = new TempDirectory();
        var build = new GeneratedHostBuildService().Build(
            new GeneratedHostBuildRequest(artifact.Source!, dir.FullPath, ArtifactBinaryName));
        build.Status.Should().Be(GeneratedHostBuildStatus.Succeeded);
        ExistsAndNonEmpty(build.Artifact!.BinaryPath).Should().BeTrue();
        ExistsAndNonEmpty(build.Artifact.SourcePath).Should().BeTrue();

        // Stage 6 — production launch (Issue #459): build + run in one classified result.
        var outcome = new RecompiledArtifactLauncher().Launch(
            program, input.Request, new ProgramEndHandoff(input.LoadAddress + (uint)(words.Length * 4)),
            dir.FullPath, resultRegister: (int)MarkerRegister);

        outcome.Result.State.Should().Be(TitleExecutionState.Completed);
        outcome.Result.Outcome.Should().Be(RecompiledArtifactOutcome.Success);
        outcome.Result.ExitCode.Should().Be(RecompiledArtifactExitCode.Success);
        outcome.Result.EngineName.Should().Be(RecompiledHostExecutionEngine.EngineName);
        // Generated/recompiled code really executed: the marker is in S1 (only the
        // recompiled ori could have written it) and the byte is on the TTY (only the
        // artifact's own host transfer could have reached the shared putchar).
        outcome.Result.ResultValue.Should().Be(DiagnosticMarker);
        outcome.Output.Should().Equal(DiagnosticCharacter);
        outcome.Json.Should().Contain("\"engineName\"");
    }

    [Fact]
    public void SyntheticExe_RepeatedChain_IsDeterministicSourceAndStableBuildInput()
    {
        var exeBytes = BuildSyntheticExe(SuccessfulProgram());

        // Two fully independent runs of the production input+lowering stages from the
        // same input bytes must regenerate the identical host source abstraction.
        var (programA, _, _, _) = Compile(exeBytes);
        var (programB, _, _, _) = Compile(exeBytes);
        var sourceA = RecompiledArtifactCodeGen.Generate(RecompilerHostCodeGen.Generate(programA));
        var sourceB = RecompiledArtifactCodeGen.Generate(RecompilerHostCodeGen.Generate(programB));
        sourceA.Success.Should().BeTrue(sourceA.DiagnosticMessage);
        sourceB.Success.Should().BeTrue(sourceB.DiagnosticMessage);
        sourceA.Source.Should().Be(sourceB.Source);

        // Two independent #458 builds in separate directories must leave byte-identical
        // generated sources and a real native binary in each (no timestamp or machine
        // metadata may leak into the source; the binary itself is not compared, because
        // toolchain embeds differ legitimately per platform — Issue #461 scope 5).
        using var dirA = new TempDirectory();
        using var dirB = new TempDirectory();
        var buildA = new GeneratedHostBuildService().Build(
            new GeneratedHostBuildRequest(sourceA.Source!, dirA.FullPath, ArtifactBinaryName));
        var buildB = new GeneratedHostBuildService().Build(
            new GeneratedHostBuildRequest(sourceB.Source!, dirB.FullPath, ArtifactBinaryName));
        buildA.Status.Should().Be(GeneratedHostBuildStatus.Succeeded);
        buildB.Status.Should().Be(GeneratedHostBuildStatus.Succeeded);
        ReadBytes(buildA.Artifact!.SourcePath).Should().Equal(ReadBytes(buildB.Artifact!.SourcePath));
        ExistsAndNonEmpty(buildA.Artifact.BinaryPath).Should().BeTrue();
        ExistsAndNonEmpty(buildB.Artifact.BinaryPath).Should().BeTrue();

        // The deterministic build-input identity is a pure function of the EXE bytes
        // (the same SHA-256 the real-ROM analysis records as the formal input identity).
        ArtifactJson.Sha256Hex(exeBytes).Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void SyntheticExe_UnresolvedJumpBoundary_IsClassifiedBlockedNotCrash()
    {
        const uint uncompiledTarget = 0x80020000u;
        var words = new uint[]
        {
            (uint)JumpOpcode << 26 | ((uncompiledTarget & 0x0FFFFFFFu) >> 2),
            0u, // delay slot
        };
        var (program, request, _, _) = Compile(BuildSyntheticExe(words));

        using var dir = new TempDirectory();
        var outcome = new RecompiledArtifactLauncher().Launch(
            program, request, handoff: null, dir.FullPath, resultRegister: (int)R3000aRegister.V0);

        // Control left the compiled image with no continuation rule: the classified
        // "blocked" class (exit 2) — never a crash, silent no-op, or ambiguous success.
        outcome.Result.State.Should().Be(TitleExecutionState.UnsupportedTransfer);
        outcome.Result.Outcome.Should().Be(RecompiledArtifactOutcome.Blocked);
        outcome.Result.ExitCode.Should().Be(RecompiledArtifactExitCode.Blocked);
        outcome.Result.GuestPc.Should().Be(uncompiledTarget);
        outcome.Result.DiagnosticCode.Should().Be("UNRESOLVED_TRANSFER");
        outcome.Result.EngineName.Should().Be(RecompiledHostExecutionEngine.EngineName);
        outcome.Json.Should().Contain("\"outcome\"");
    }

    [Fact]
    public void SyntheticExe_UnsupportedBiosBoundary_IsClassifiedFailureWithDiagnostic()
    {
        // A0 with a function number no HLE service is registered for (0x10): the guest
        // reaches a real BIOS boundary the shared Runtime cannot service, so the run
        // must stop with a stable classified diagnostic — the exit-1 class — rather
        // than crash or silently keep going.
        const byte unregisteredFunction = 0x10;
        var words = new uint[]
        {
            Immediate(OriOpcode, FunctionNumberRegister, unregisteredFunction),
            Immediate(OriOpcode, FirstArgumentRegister, 0u),
            (uint)JalOpcode << 26 | (BiosJumpTables.A0VectorAddress & 0x0FFFFFFFu) >> 2,
            0u, // branch delay slot
        };
        var (program, request, _, _) = Compile(BuildSyntheticExe(words));

        using var dir = new TempDirectory();
        var outcome = new RecompiledArtifactLauncher().Launch(
            program, request, handoff: null, dir.FullPath, resultRegister: (int)R3000aRegister.V0);

        outcome.Result.State.Should().Be(TitleExecutionState.RuntimeFailure);
        outcome.Result.Outcome.Should().Be(RecompiledArtifactOutcome.Failure);
        outcome.Result.ExitCode.Should().Be(RecompiledArtifactExitCode.Failure);
        outcome.Result.DiagnosticCode.Should().Be("BIOS_HLE_UNSUPPORTED_CALL");
        outcome.Result.DiagnosticMessage.Should().NotBeNullOrEmpty();
        outcome.Result.EngineName.Should().Be(RecompiledHostExecutionEngine.EngineName);
        outcome.Json.Should().Contain("BIOS_HLE_UNSUPPORTED_CALL");
    }

    private sealed class ProgramEndHandoff(uint programEnd) : ITitleExecutionHandoff
    {
        public TitleExecutionHandoffResult? Decide(RecompilerStateSnapshot segmentState) =>
            segmentState.PC == programEnd ? TitleExecutionHandoffResult.Exit() : null;
    }

#pragma warning disable AARC003
    private static byte[] ReadBytes(string path) => File.ReadAllBytes(path);
#pragma warning restore AARC003
}