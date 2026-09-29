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
/// Runnable-artifact E2E proof. Repository-owned fixtures come from
/// <see cref="GeneratedPsxExeFixtures"/> and are driven through the production
/// parser/input/lowering/codegen/build/launch path. User-supplied real EXEs
/// remain a separate evidence layer in <see cref="RealExeE2ETests"/>.
/// </summary>
[Test]
public sealed class RecompiledArtifactE2ETests
{
    private const string ArtifactBinaryName = "recompiled-artifact";

    /// <summary>Decode + <see cref="MipsToIrLowerer.LowerProgram"/> — the CLI lowering stage.</summary>
    private static RecompilerIrProgram Lower(uint entryPc, IReadOnlyList<uint> words)
    {
        var instructions = new List<(R3000aInstruction Instruction, uint EntryPc)>();
        for (var i = 0; i < words.Count; i++)
        {
            instructions.Add((R3000aDecoder.Decode(words[i]), entryPc + (uint)(i * sizeof(uint))));
        }

        return MipsToIrLowerer.LowerProgram(instructions);
    }

    /// <summary>
    /// Production input stage for one generated EXE: <see cref="PsxExe.Load"/>,
    /// <see cref="PsxExeTitleInput.Build"/>, then production IR lowering.
    /// </summary>
    private static (RecompilerIrProgram Program, TitleExecutionRequest Request, uint LoadAddress, int WordCount)
        Compile(byte[] exeBytes)
    {
        var exe = PsxExe.Load(exeBytes, "generated-fixture.exe");
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
    public void GeneratedSuccessFixture_FullProductionChain_ExecutesExpectedEvidence()
    {
        var fixture = GeneratedPsxExeFixtures.BiosPutCharMarker;
        var exeBytes = fixture.Generate();

        // Stage 1 — production PS-X EXE parser.
        var exe = PsxExe.Load(exeBytes, $"{fixture.Id}.exe");
        exe.Header.TextStart.Should().Be(GeneratedPsxExeFixtures.EntryPc);
        exe.DecodedInstructionCount.Should().Be(fixture.InstructionWords.Count);

        // Stage 2 — production execution-input bridge.
        var input = PsxExeTitleInput.Build(exe, outerBudget: 1, segmentBudget: 64);
        input.LoadAddress.Should().Be(GeneratedPsxExeFixtures.EntryPc);
        input.Request.EntryPc.Should().Be(GeneratedPsxExeFixtures.EntryPc);
        input.InstructionWords.Should().Equal(fixture.InstructionWords);

        // Stage 3 — production lowering.
        var program = Lower(input.LoadAddress, input.InstructionWords);
        program.Blocks.Should().NotBeEmpty();

        // Stage 4 — production host + runnable-artifact code generation.
        var host = RecompilerHostCodeGen.Generate(program);
        host.Success.Should().BeTrue(host.DiagnosticMessage);
        host.Source.Should().NotBeNullOrEmpty();
        host.Source.Should().Contain($"recompiler_block_0x{GeneratedPsxExeFixtures.EntryPc:X8}");
        var artifact = RecompiledArtifactCodeGen.Generate(host);
        artifact.Success.Should().BeTrue(artifact.DiagnosticMessage);
        artifact.Source.Should().Contain(RecompiledArtifactCodeGen.HostTransferFlag);

        // Stage 5 — production artifact build.
        using var dir = new TempDirectory();
        var build = new GeneratedHostBuildService().Build(
            new GeneratedHostBuildRequest(artifact.Source!, dir.FullPath, ArtifactBinaryName));
        build.Status.Should().Be(GeneratedHostBuildStatus.Succeeded);
        ExistsAndNonEmpty(build.Artifact!.BinaryPath).Should().BeTrue();
        ExistsAndNonEmpty(build.Artifact.SourcePath).Should().BeTrue();

        // Stage 6 — production launch.
        var outcome = new RecompiledArtifactLauncher().Launch(
            program,
            input.Request,
            new ProgramEndHandoff(input.LoadAddress + (uint)(fixture.InstructionWords.Count * sizeof(uint))),
            dir.FullPath,
            resultRegister: (int)fixture.ResultRegister);

        outcome.Result.State.Should().Be(fixture.ExpectedState);
        outcome.Result.Outcome.Should().Be(fixture.ExpectedOutcome);
        outcome.Result.ExitCode.Should().Be(RecompiledArtifactExitCode.Success);
        outcome.Result.EngineName.Should().Be(RecompiledHostExecutionEngine.EngineName);
        outcome.Result.ResultValue.Should().Be(fixture.ExpectedResultValue);
        outcome.Output.Should().Equal(fixture.ExpectedOutputByte!.Value);
        outcome.Json.Should().Contain("\"engineName\"");
    }

    [Fact]
    public void GeneratedSuccessFixture_RepeatedChain_IsDeterministicSourceAndStableBuildInput()
    {
        var fixture = GeneratedPsxExeFixtures.BiosPutCharMarker;
        var exeBytes = fixture.Generate();

        var (programA, _, _, _) = Compile(exeBytes);
        var (programB, _, _, _) = Compile(fixture.Generate());
        var sourceA = RecompiledArtifactCodeGen.Generate(RecompilerHostCodeGen.Generate(programA));
        var sourceB = RecompiledArtifactCodeGen.Generate(RecompilerHostCodeGen.Generate(programB));
        sourceA.Success.Should().BeTrue(sourceA.DiagnosticMessage);
        sourceB.Success.Should().BeTrue(sourceB.DiagnosticMessage);
        sourceA.Source.Should().Be(sourceB.Source);

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

        ArtifactJson.Sha256Hex(exeBytes).Should().Be(fixture.ExpectedSha256);
    }

    [Fact]
    public void GeneratedBlockedFixture_ProductionChain_IsClassifiedNotCrash()
    {
        var fixture = GeneratedPsxExeFixtures.UnresolvedJump;
        var (program, request, _, _) = Compile(fixture.Generate());

        using var dir = new TempDirectory();
        var outcome = new RecompiledArtifactLauncher().Launch(
            program, request, handoff: null, dir.FullPath, resultRegister: (int)fixture.ResultRegister);

        outcome.Result.State.Should().Be(fixture.ExpectedState);
        outcome.Result.Outcome.Should().Be(fixture.ExpectedOutcome);
        outcome.Result.ExitCode.Should().Be(RecompiledArtifactExitCode.Blocked);
        outcome.Result.GuestPc.Should().Be(fixture.ExpectedGuestPc);
        outcome.Result.DiagnosticCode.Should().Be(fixture.ExpectedDiagnosticCode);
        outcome.Result.EngineName.Should().Be(RecompiledHostExecutionEngine.EngineName);
        outcome.Json.Should().Contain("\"outcome\"");
    }

    [Fact]
    public void SyntheticExe_UnsupportedBiosBoundary_IsClassifiedFailureWithDiagnostic()
    {
        const byte unregisteredFunction = 0x10;
        var words = new uint[]
        {
            GeneratedPsxExeFixtures.Immediate(0x0D, R3000aRegister.T1, unregisteredFunction),
            GeneratedPsxExeFixtures.Immediate(0x0D, R3000aRegister.A0, 0u),
            GeneratedPsxExeFixtures.Jump(0x03, BiosJumpTables.A0VectorAddress),
            0u, // JAL delay slot
        };
        var (program, request, _, _) = Compile(GeneratedPsxExeFixtures.BuildExe(words));

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

    [Theory]
    [InlineData(BiosJumpTables.A0VectorAddress, BiosHleRuntime.PutCharFunction)]
    [InlineData(BiosJumpTables.B0VectorAddress, BiosHleRuntime.PutCharAliasFunction)]
    [InlineData(BiosJumpTables.C0VectorAddress, (byte)0xBD)]
    public void SyntheticExe_JrBiosStub_ReachesTheService_AndReturnsToTheCaller(uint vector, byte function)
    {
        // Issue #635: the canonical PS1 BIOS stub reaches the vector through
        // JR $t2, not a direct JAL, over the production launch path.
        const uint marker = 0x7777u;
        var words = new uint[]
        {
            GeneratedPsxExeFixtures.Immediate(0x0D, R3000aRegister.A0, 'P'),                 // 0
            GeneratedPsxExeFixtures.Jump(0x03, GeneratedPsxExeFixtures.EntryPc + 0x18),       // 1 JAL stub
            0u,                                                                               // 2
            GeneratedPsxExeFixtures.Immediate(0x0D, R3000aRegister.S1, marker),               // 3 return lands here
            GeneratedPsxExeFixtures.Jump(0x02, GeneratedPsxExeFixtures.EntryPc + 0x24),       // 4 J program end
            0u,                                                                               // 5
            GeneratedPsxExeFixtures.Immediate(0x09, R3000aRegister.T2, vector),               // 6 stub
            (uint)R3000aRegister.T2 << 21 | 0x08u,                                            // 7 JR $t2
            GeneratedPsxExeFixtures.Immediate(0x09, R3000aRegister.T1, function),             // 8 delay slot
        };
        var (program, request, loadAddress, wordCount) = Compile(GeneratedPsxExeFixtures.BuildExe(words));

        using var dir = new TempDirectory();
        var outcome = new RecompiledArtifactLauncher().Launch(
            program,
            request,
            new ProgramEndHandoff(loadAddress + (uint)(wordCount * sizeof(uint))),
            dir.FullPath,
            resultRegister: (int)R3000aRegister.S1);

        outcome.Result.State.Should().Be(TitleExecutionState.Completed, outcome.Result.DiagnosticMessage);
        outcome.Result.DiagnosticCode.Should().BeNull();
        outcome.Result.ResultValue.Should().Be(marker);
        outcome.Output.Should().Equal((byte)'P');
    }

    [Fact]
    public void SyntheticExe_JrToAnUnresolvedTarget_FailsClosedWithADiagnostic()
    {
        // Issue #635: a register-indirect target that is neither a compiled block
        // nor a host-claimed vector is an unresolved transfer with a diagnostic,
        // never a silent RuntimeHandoff.
        var words = new uint[]
        {
            GeneratedPsxExeFixtures.Immediate(0x0F, R3000aRegister.T0, GeneratedPsxExeFixtures.UnresolvedJumpTarget >> 16),
            (uint)R3000aRegister.T0 << 21 | 0x08u, // JR $t0
            0u,                                    // delay slot
        };
        var (program, request, loadAddress, wordCount) = Compile(GeneratedPsxExeFixtures.BuildExe(words));

        using var dir = new TempDirectory();
        var outcome = new RecompiledArtifactLauncher().Launch(
            program,
            request,
            new ProgramEndHandoff(loadAddress + (uint)(wordCount * sizeof(uint))),
            dir.FullPath,
            resultRegister: (int)R3000aRegister.V0);

        outcome.Result.State.Should().Be(TitleExecutionState.UnsupportedTransfer);
        outcome.Result.Outcome.Should().Be(RecompiledArtifactOutcome.Blocked);
        outcome.Result.GuestPc.Should().Be(GeneratedPsxExeFixtures.UnresolvedJumpTarget);
        outcome.Result.DiagnosticCode.Should().Be("UNRESOLVED_TRANSFER");
        outcome.Result.DiagnosticMessage.Should().NotBeNullOrEmpty();
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
