using System.Diagnostics;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Synthetic production-path proof for Issue #459: a fixture's real generated
/// code, built through the production #458 <see cref="GeneratedHostBuildService"/>,
/// launched as a real native process by <see cref="RecompiledArtifactLauncher"/>
/// — never the differential harness's test-only build helper. These fixtures are
/// synthetic and always run — no ROM/BIOS dependency, only a real gcc toolchain.
/// </summary>
[Test]
public sealed class RecompiledArtifactLauncherTests
{
    private const byte OriOpcode = 0x0D;
    private const byte JalOpcode = 0x03;
    private const byte FunctionNumberRegister = (byte)R3000aRegister.T1;
    private const byte FirstArgumentRegister = (byte)R3000aRegister.A0;
    private const byte MarkerRegister = (byte)R3000aRegister.S1;
    private const uint DiagnosticMarker = 0x1234u;
    private const byte DiagnosticCharacter = (byte)'P';

    private static uint Immediate(byte opcode, byte rt, uint immediate) =>
        (uint)opcode << 26 | (uint)rt << 16 | (immediate & 0xFFFFu);

    private static RecompilerIrProgram Lower(uint entryPc, IReadOnlyList<uint> words)
    {
        var instructions = new List<(R3000aInstruction Instruction, uint EntryPc)>();
        for (var i = 0; i < words.Count; i++)
        {
            instructions.Add((R3000aDecoder.Decode(words[i]), entryPc + (uint)(i * 4)));
        }
        return MipsToIrLowerer.LowerProgram(instructions);
    }

    [Fact]
    public void Launch_SyntheticFixture_ExecutesGeneratedCodeAndStopsAtUnresolvedTransfer()
    {
        const uint entryPc = 0x80010000u;
        uint[] words = [Immediate(OriOpcode, (byte)R3000aRegister.V0, DiagnosticMarker)];
        var program = Lower(entryPc, words);

        using var dir = new TempDirectory();
        var request = new TitleExecutionRequest(
            entryPc, new uint[TitleExecutionRequest.GprCount], initialHi: 0, initialLo: 0,
            initialMemory: [], outerBudget: 1, segmentBudget: 8);

        var outcome = new RecompiledArtifactLauncher().Launch(
            program, new PsxExeTitleExecution(entryPc, words, request), handoff: null, dir.FullPath, resultRegister: (int)R3000aRegister.V0);

        // The generated block executed for real: V0 carries the value only the
        // recompiled instruction itself could have produced (the deterministic
        // observable marker), even though the run stops at the next, uncompiled
        // pc — a legitimate classified boundary, not a failure (Issue #459).
        outcome.Result.ResultValue.Should().Be(DiagnosticMarker);
        outcome.Result.State.Should().Be(TitleExecutionState.UnsupportedTransfer);
        outcome.Result.Outcome.Should().Be(RecompiledArtifactOutcome.Blocked);
        outcome.Result.ExitCode.Should().Be(RecompiledArtifactExitCode.Blocked);
        outcome.Result.GuestPc.Should().Be(entryPc + 4);
        outcome.Json.Should().Contain("\"outcome\"");
    }

    [Fact]
    public void Launch_UnresolvedTransfer_ReusesSharedBiosHleRuntimeAndReachesCompletion()
    {
        const uint entryPc = 0x80020000u;
        var words = new uint[]
        {
            Immediate(OriOpcode, FunctionNumberRegister, BiosHleRuntime.PutCharFunction),
            Immediate(OriOpcode, FirstArgumentRegister, DiagnosticCharacter),
            (uint)JalOpcode << 26 | (BiosJumpTables.A0VectorAddress & 0x0FFFFFFCu) >> 2,
            0u, // branch delay slot
            Immediate(OriOpcode, MarkerRegister, DiagnosticMarker),
        };
        var program = Lower(entryPc, words);
        var programEnd = entryPc + (uint)(words.Length * 4);

        using var dir = new TempDirectory();
        var request = new TitleExecutionRequest(
            entryPc, new uint[TitleExecutionRequest.GprCount], initialHi: 0, initialLo: 0,
            initialMemory: [], outerBudget: 1, segmentBudget: 64);

        var outcome = new RecompiledArtifactLauncher().Launch(
            program, new PsxExeTitleExecution(entryPc, words, request), new ProgramEndHandoff(programEnd), dir.FullPath, resultRegister: (int)R3000aRegister.S1);

        // The A0:3C putchar call was dispatched through the real, shared
        // BiosHleRuntime (ADR-014) — the artifact itself carries no BIOS
        // knowledge — and execution resumed past it to the marked end.
        outcome.Result.State.Should().Be(TitleExecutionState.Completed);
        outcome.Result.Outcome.Should().Be(RecompiledArtifactOutcome.Success);
        outcome.Result.ExitCode.Should().Be(RecompiledArtifactExitCode.Success);
        outcome.Result.ResultValue.Should().Be(DiagnosticMarker);
        outcome.Output.Should().Equal(DiagnosticCharacter);
    }

    [Fact]
    public void RunSegment_CalledTwice_ThrowsRatherThanSilentlyRestartingFromStaleMemory()
    {
        const uint entryPc = 0x80030000u;
        uint[] words = [Immediate(OriOpcode, (byte)R3000aRegister.V0, DiagnosticMarker)];
        var program = Lower(entryPc, words);

        using var dir = new TempDirectory();
        using var engine = new RecompiledHostExecutionEngine(program, words, entryPc, new GeneratedHostBuildService(), dir.FullPath);
        var request = new TitleExecutionRequest(
            entryPc, new uint[TitleExecutionRequest.GprCount], initialHi: 0, initialLo: 0,
            initialMemory: [], outerBudget: 1, segmentBudget: 8);
        engine.Load(request);

        var segmentRequest = new TitleExecutionSegmentRequest(request.InitialGpr, 0, 0, entryPc, request.SegmentBudget);
        engine.RunSegment(segmentRequest).Status.Should().Be(RecompilerExecutionStatus.Completed);

        // This milestone's engine carries no guest RAM across repeated launches
        // (see its class remarks), so a second call must fail loud rather than
        // silently restart from the first call's initial memory.
        var act = () => engine.RunSegment(segmentRequest);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Launch_OuterBudgetGreaterThanOne_ThrowsPreconditionBeforeEngineConstruction()
    {
        const uint entryPc = 0x80040000u;
        uint[] words = [Immediate(OriOpcode, (byte)R3000aRegister.V0, DiagnosticMarker)];
        var program = Lower(entryPc, words);

        using var dir = new TempDirectory();
        var request = new TitleExecutionRequest(
            entryPc, new uint[TitleExecutionRequest.GprCount], initialHi: 0, initialLo: 0,
            initialMemory: [], outerBudget: 2, segmentBudget: 8);

        // The launcher is one-shot (RecompiledHostExecutionEngine carries no
        // guest-RAM continuity across repeated launches), so an OuterBudget above
        // 1 is rejected up front — it is a launcher precondition, not a
        // mid-orchestration engine surprise.
        var act = () => new RecompiledArtifactLauncher().Launch(
            program, new PsxExeTitleExecution(entryPc, words, request), handoff: null, dir.FullPath);
        act.Should().Throw<InvalidOperationException>().WithMessage("*OuterBudget must be 1*");
    }

    [Fact]
    public void RunSegment_ExecutesFromSegmentRequestState_NotFromStaleLoadState()
    {
        const uint entryPc = 0x80050000u;
        uint[] words = [Immediate(OriOpcode, (byte)R3000aRegister.V0, DiagnosticMarker)];
        var program = Lower(entryPc, words);

        using var dir = new TempDirectory();
        using var engine = new RecompiledHostExecutionEngine(program, words, entryPc, new GeneratedHostBuildService(), dir.FullPath);

        // Load must not freeze the architectural state the artifact runs from:
        // its entry pc points at no compiled block and its budget of 0 would end
        // the run before the first instruction, so a run driven from Load's
        // input would never reach the generated block.
        var loadGpr = new uint[TitleExecutionRequest.GprCount];
        loadGpr[1] = 0x11111111u;
        engine.Load(new TitleExecutionRequest(
            entryPc + 0x1000, loadGpr, initialHi: 0x22222222, initialLo: 0x33333333,
            initialMemory: [], outerBudget: 1, segmentBudget: 0));

        // RunSegment's own named state — real block entry pc, live budget, and
        // distinctive registers/HI/LO — is what must drive the artifact.
        var segmentGpr = new uint[TitleExecutionRequest.GprCount];
        segmentGpr[1] = 0xAAAAAAAAu;
        var segment = new TitleExecutionSegmentRequest(segmentGpr, 0xBBBBBBBB, 0xCCCCCCCC, entryPc, 8);

        var result = engine.RunSegment(segment);

        // V0 holds the marker only the recompiled ori could produce, and the
        // GPR/HI/LO snapshot reflects the segment request's values — proving the
        // input file was rewritten from RunSegment's state, not Load's.
        result.Status.Should().Be(RecompilerExecutionStatus.Completed);
        result.Snapshot!.Gpr[(int)R3000aRegister.V0].Should().Be(DiagnosticMarker);
        result.Snapshot.Gpr[1].Should().Be(0xAAAAAAAAu);
        result.Snapshot.HI.Should().Be(0xBBBBBBBBu);
        result.Snapshot.LO.Should().Be(0xCCCCCCCCu);
    }

    [Fact]
    public void Launch_InitialMemorySeededByLoad_IsVisibleToRunSegmentExecutedArtifact()
    {
        const uint entryPc = 0x80060000u;
        const uint dataAddress = 0x80060F80u;
        var words = new uint[]
        {
            0x3C018006u, // lui $at, 0x8006
            0x34240F80u, // ori $a0, $at, 0x0F80  -> a0 = 0x80060F80
            Immediate(OriOpcode, FunctionNumberRegister, BiosHleRuntime.PutsFunction),
            (uint)JalOpcode << 26 | (BiosJumpTables.A0VectorAddress & 0x0FFFFFFCu) >> 2,
            0u, // branch delay slot
            Immediate(OriOpcode, MarkerRegister, DiagnosticMarker),
        };
        var program = Lower(entryPc, words);
        var programEnd = entryPc + (uint)(words.Length * 4);

        using var dir = new TempDirectory();
        var request = new TitleExecutionRequest(
            entryPc, new uint[TitleExecutionRequest.GprCount], initialHi: 0, initialLo: 0,
            initialMemory: [new RecompilerInitialMemoryItem(dataAddress, (byte)'P')],
            outerBudget: 1, segmentBudget: 64);

        var outcome = new RecompiledArtifactLauncher().Launch(
            program, new PsxExeTitleExecution(entryPc, words, request), new ProgramEndHandoff(programEnd), dir.FullPath, resultRegister: (int)R3000aRegister.S1);

        // Load's seed reached the artifact this RunSegment executed: A0:3E puts
        // read the NUL-terminated string from guest memory at a0, which only the
        // initial-memory item could have placed there.
        outcome.Result.State.Should().Be(TitleExecutionState.Completed);
        outcome.Result.Outcome.Should().Be(RecompiledArtifactOutcome.Success);
        outcome.Result.ResultValue.Should().Be(DiagnosticMarker);
        outcome.Output.Should().Equal((byte)'P');
    }

    [Fact]
    public void RunSegment_InputPathWithSpacesAndQuotes_IsReceivedUnchangedAsOneArgument()
    {
        const uint entryPc = 0x80070000u;
        uint[] words = [Immediate(OriOpcode, (byte)R3000aRegister.V0, DiagnosticMarker)];
        var program = Lower(entryPc, words);

        using var dir = new TempDirectory();
        // A quote cannot be part of a Windows path; everywhere else both a space
        // and a quote must survive the launch untouched (Windows-style quoting
        // would have treated the quote as a quoting delimiter).
        var working = OperatingSystem.IsWindows()
            ? dir.CreateSubdirectory("path with space")
            : dir.CreateSubdirectory("path with \"quote\" and space");
        using var engine = new RecompiledHostExecutionEngine(program, words, entryPc, new GeneratedHostBuildService(), working);
        var request = new TitleExecutionRequest(
            entryPc, new uint[TitleExecutionRequest.GprCount], initialHi: 0, initialLo: 0,
            initialMemory: [], outerBudget: 1, segmentBudget: 8);
        engine.Load(request);

        var result = engine.RunSegment(new TitleExecutionSegmentRequest(request.InitialGpr, 0, 0, entryPc, 8));

        result.Status.Should().Be(RecompilerExecutionStatus.Completed);
        result.Snapshot!.Gpr[(int)R3000aRegister.V0].Should().Be(DiagnosticMarker);
    }

    [Fact]
    public void Launch_HostTransferProtocolFault_IsClassifiedProtocolFailure_NotLeakedException()
    {
        const uint entryPc = 0x80080000u;
        uint[] words = [Immediate(OriOpcode, (byte)R3000aRegister.V0, DiagnosticMarker)];
        var program = Lower(entryPc, words);

        using var dir = new TempDirectory();
        var request = new TitleExecutionRequest(
            entryPc, new uint[TitleExecutionRequest.GprCount], initialHi: 0, initialLo: 0,
            initialMemory: [], outerBudget: 1, segmentBudget: 8);

        // The Runtime factory is invoked as the very first host-transfer
        // handshake: a faulting factory exercises RunProcess's pump-fault path.
        var outcome = new RecompiledArtifactLauncher().Launch(
            program,
            new PsxExeTitleExecution(entryPc, words, request),
            handoff: null,
            dir.FullPath,
            resultRegister: (int)R3000aRegister.V0,
            biosRuntimeFactory: (_, _) => throw new InvalidOperationException("simulated protocol fault"));

        // A pump fault is an engine mechanism failure, classified and returned —
        // the underlying exception must never escape the launcher nor leak its
        // message into the stable machine-readable result.
        outcome.Result.Outcome.Should().Be(RecompiledArtifactOutcome.Failure);
        outcome.Result.State.Should().Be(TitleExecutionState.RuntimeFailure);
        outcome.Result.DiagnosticCode.Should().Be("ARTIFACT_HOST_PROTOCOL_FAILED");
        outcome.Result.DiagnosticMessage.Should().Be("The artifact host-transfer protocol failed.");
        outcome.Json.Should().Contain("ARTIFACT_HOST_PROTOCOL_FAILED");
        outcome.Json.Should().NotContain("simulated protocol fault");
    }

    private const uint TextImageEntry = 0x80090000u;
    private const uint TextImageDataWord = 0x9A785634u;

    /// <summary>
    /// Issue #637 fixture: the very first guest instruction already reads the program
    /// image as data (a0 = the image base), before any guest store runs, so only a
    /// preloaded image can supply these values. Word 8 is a data word the code never
    /// executes; the J leaves the image so both engines end at the same program end.
    /// </summary>
    private static uint[] TextImageLoadWords(uint entry = TextImageEntry)
    {
        const byte a0 = (byte)R3000aRegister.A0;
        static uint Load(byte opcode, R3000aRegister rt, uint offset) =>
            (uint)opcode << 26 | (uint)a0 << 21 | (uint)rt << 16 | offset;
        return
        [
            Load(0x23, R3000aRegister.S0, 0x20), // lw  s0, 0x20(a0)  data word
            Load(0x20, R3000aRegister.S1, 0x23), // lb  s1, 0x23(a0)  sign-extended top byte
            Load(0x24, R3000aRegister.S2, 0x21), // lbu s2, 0x21(a0)
            Load(0x23, R3000aRegister.S3, 0x00), // lw  s3, 0(a0)     the first instruction word itself
            Load(0x21, R3000aRegister.S4, 0x22), // lh  s4, 0x22(a0)
            0x02u << 26 | ((entry + 0x24u) & 0x0FFFFFFCu) >> 2, // j program end
            0u, // delay slot
            0u, // unreachable padding
            TextImageDataWord,
        ];
    }

    private static TitleExecutionRequest TextImageRequest(
        uint entry = TextImageEntry, Action<uint[]>? seedGpr = null)
    {
        var gpr = new uint[TitleExecutionRequest.GprCount];
        gpr[(int)R3000aRegister.A0] = entry;
        seedGpr?.Invoke(gpr);
        return new TitleExecutionRequest(
            entry, gpr, initialHi: 0, initialLo: 0, initialMemory: [], outerBudget: 1, segmentBudget: 64);
    }

    /// <summary>Runs <paramref name="words"/> loaded at <paramref name="entry"/> through the
    /// artifact and the interpreter and returns both final GPR files.</summary>
    private static (uint[] Artifact, uint[] Interpreter) RunBothEngines(
        uint entry, uint[] words, TitleExecutionRequest request)
    {
        var handoff = new ProgramEndHandoff(entry + (uint)(words.Length * sizeof(uint)));
        var program = ReachableProgramBuilder.Build(entry, words, entry);

        using var dir = new TempDirectory();
        using var artifactEngine = new RecompiledHostExecutionEngine(
            program, words, entry, new GeneratedHostBuildService(), dir.FullPath);
        var artifact = new ExecutionOrchestrator().Execute(artifactEngine, handoff, request);

        using var interpreterEngine = new InterpreterTitleExecutionEngine(words, entry);
        var interpreter = new ExecutionOrchestrator().Execute(interpreterEngine, handoff, request);

        artifact.State.Should().Be(TitleExecutionState.Completed, artifact.DiagnosticMessage);
        interpreter.State.Should().Be(TitleExecutionState.Completed, interpreter.DiagnosticMessage);
        return (artifact.FinalSnapshot!.Gpr.ToArray(), interpreter.FinalSnapshot!.Gpr.ToArray());
    }

    // Issue #637 review: the image loads at a virtual address whose physical address
    // is beyond the 2 MiB RAM but inside the low-8-MiB mirror (0x80210000 -> RAM
    // offset 0x10000), and one whose word run straddles the 2 MiB seam (words 0..6
    // at the RAM tail, the rest aliasing to RAM offset 0). PSXMemory (the
    // interpreter) accepts both; the artifact must place the same bytes.
    [Theory]
    [InlineData(0x80210000u)]
    [InlineData(0xA0210000u)]
    [InlineData(0x801FFFE4u)]
    public void Artifact_ImageInLowEightMiBRamMirror_MatchesTheInterpreter(uint entry)
    {
        var words = TextImageLoadWords(entry);
        var (artifact, interpreter) = RunBothEngines(entry, words, TextImageRequest(entry));

        artifact[(int)R3000aRegister.S0].Should().Be(TextImageDataWord);
        artifact[(int)R3000aRegister.S1].Should().Be(0xFFFFFF9Au);
        artifact[(int)R3000aRegister.S2].Should().Be(0x56u);
        artifact[(int)R3000aRegister.S3].Should().Be(words[0]);
        artifact[(int)R3000aRegister.S4].Should().Be(0xFFFF9A78u);
        artifact.Should().Equal(interpreter);
    }

    // Width boundary: the check runs on the aliased RAM offset, so a mirror address
    // behaves exactly like its RAM twin, the last aligned word is valid and an access
    // past the mirror window is unmapped. (A guest access that overruns the RAM tail is
    // necessarily unaligned, which the interpreter turns into a CPU exception before
    // memory is reached, so that edge is not comparable through guest code.)
    [Fact]
    public void Artifact_RamMirrorAndWidthBoundaries_MatchTheInterpreter()
    {
        const uint entry = TextImageEntry;
        static uint I(byte op, R3000aRegister rs, R3000aRegister rt, uint imm) =>
            (uint)op << 26 | (uint)rs << 21 | (uint)rt << 16 | imm;
        const R3000aRegister t0 = R3000aRegister.T0;
        uint[] words =
        [
            I(0x0F, 0, t0, 0xA1B2),                                          // lui t0, 0xA1B2
            I(0x0D, t0, t0, 0xC3D4),                                         // ori t0, t0, 0xC3D4
            I(0x2B, R3000aRegister.A0, t0, 0),                               // sw  t0, 0(a0)  last aligned word
            I(0x23, R3000aRegister.A0, R3000aRegister.S0, 0),                // lw  s0, 0(a0)
            I(0x23, R3000aRegister.A2, R3000aRegister.S1, 0),                // lw  s1, 0(a2)  mirror twin of a0
            I(0x23, R3000aRegister.A3, R3000aRegister.S2, 0),                // lw  s2, 0(a3)  past the mirror window
            I(0x2B, R3000aRegister.A3, t0, 0),                               // sw  t0, 0(a3)  dropped
            I(0x20, R3000aRegister.A0, R3000aRegister.S3, 3),                // lb  s3, 3(a0)  last RAM byte
            I(0x21, R3000aRegister.A0, R3000aRegister.S4, 2),                // lh  s4, 2(a0)
            0x02u << 26 | ((entry + 0x2Cu) & 0x0FFFFFFCu) >> 2,             // j program end
            0u,                                                              // delay slot
        ];
        var request = TextImageRequest(entry, gpr =>
        {
            gpr[(int)R3000aRegister.A0] = 0x801FFFFCu;
            gpr[(int)R3000aRegister.A2] = 0x803FFFFCu;
            gpr[(int)R3000aRegister.A3] = 0x80800000u;
        });

        var (artifact, interpreter) = RunBothEngines(entry, words, request);

        artifact[(int)R3000aRegister.S0].Should().Be(0xA1B2C3D4u);
        artifact[(int)R3000aRegister.S1].Should().Be(0xA1B2C3D4u);
        artifact[(int)R3000aRegister.S2].Should().Be(0u);
        artifact[(int)R3000aRegister.S3].Should().Be(0xFFFFFFA1u);
        artifact[(int)R3000aRegister.S4].Should().Be(0xFFFFA1B2u);
        artifact.Should().Equal(interpreter);
    }

    [Fact]
    public void Artifact_LoadsFromTheTextSegment_MatchTheInterpreterFromTheFirstInstruction()
    {
        var words = TextImageLoadWords();
        var request = TextImageRequest();
        var handoff = new ProgramEndHandoff(TextImageEntry + (uint)(words.Length * sizeof(uint)));
        var program = ReachableProgramBuilder.Build(TextImageEntry, words, TextImageEntry);

        using var dir = new TempDirectory();
        using var artifactEngine = new RecompiledHostExecutionEngine(
            program, words, TextImageEntry, new GeneratedHostBuildService(), dir.FullPath);
        var artifact = new ExecutionOrchestrator().Execute(artifactEngine, handoff, request);

        using var interpreterEngine = new InterpreterTitleExecutionEngine(words, TextImageEntry);
        var interpreter = new ExecutionOrchestrator().Execute(interpreterEngine, handoff, request);

        artifact.State.Should().Be(TitleExecutionState.Completed, artifact.DiagnosticMessage);
        interpreter.State.Should().Be(TitleExecutionState.Completed, interpreter.DiagnosticMessage);

        // Explicit values first: a zero-filled artifact RAM (the pre-#637 bug) would
        // read 0 for every one of these.
        var gpr = artifact.FinalSnapshot!.Gpr;
        gpr[(int)R3000aRegister.S0].Should().Be(TextImageDataWord);
        gpr[(int)R3000aRegister.S1].Should().Be(0xFFFFFF9Au);
        gpr[(int)R3000aRegister.S2].Should().Be(0x56u);
        gpr[(int)R3000aRegister.S3].Should().Be(words[0]);
        gpr[(int)R3000aRegister.S4].Should().Be(0xFFFF9A78u);

        // Then parity: the interpreter is the reference for initial guest RAM.
        gpr.Should().Equal(interpreter.FinalSnapshot!.Gpr);
    }

    [Fact]
    public void Engine_EmptyProgramImage_IsRejected()
    {
        uint[] words = [Immediate(OriOpcode, (byte)R3000aRegister.V0, DiagnosticMarker)];
        var program = Lower(TextImageEntry, words);

        using var dir = new TempDirectory();
        var act = () => new RecompiledHostExecutionEngine(
            program, Array.Empty<uint>(), TextImageEntry, new GeneratedHostBuildService(), dir.FullPath);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("missing", RecompiledArtifactCodeGen.CannotOpenImageExitCode)]
    [InlineData("truncated", RecompiledArtifactCodeGen.InvalidImageExitCode)]
    [InlineData("oversized", RecompiledArtifactCodeGen.InvalidImageExitCode)]
    public void Artifact_ProgramImageNotMatchingTheInputContract_FailsClosedBeforeExecution(string defect, int expectedExit)
    {
        var words = TextImageLoadWords();
        var request = TextImageRequest();
        var program = ReachableProgramBuilder.Build(TextImageEntry, words, TextImageEntry);

        using var dir = new TempDirectory();
        using (var engine = new RecompiledHostExecutionEngine(
            program, words, TextImageEntry, new GeneratedHostBuildService(), dir.FullPath))
        {
            // One well-formed launch writes the input file and image sidecar the
            // driver contract describes (and proves they are accepted as written).
            engine.Load(request);
            engine.RunSegment(new TitleExecutionSegmentRequest(request.InitialGpr, 0, 0, TextImageEntry, 64))
                .Status.Should().Be(RecompilerExecutionStatus.Completed);
        }

        var imagePath = dir.Combine("artifact-image.bin");
#pragma warning disable AARC003 // Test-only tampering with the artifact's own sidecar.
        var image = File.ReadAllBytes(imagePath);
        switch (defect)
        {
            case "missing": File.Delete(imagePath); break;
            case "truncated": File.WriteAllBytes(imagePath, image[..^1]); break;
            default: File.WriteAllBytes(imagePath, [.. image, 0]); break;
        }

        var binary = File.Exists(dir.Combine("recompiled-artifact.exe"))
            ? dir.Combine("recompiled-artifact.exe")
            : dir.Combine("recompiled-artifact");
        var psi = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(dir.Combine("artifact-input.txt"));
        psi.ArgumentList.Add(imagePath);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(10000).Should().BeTrue();
        var exitCode = process.ExitCode;
#pragma warning restore AARC003

        // Fail closed: a distinct driver exit and no snapshot, never a run over
        // zero-filled (or partially filled) guest RAM.
        exitCode.Should().Be(expectedExit);
        stdout.Should().NotContain(RecompiledArtifactCodeGen.SnapshotBeginMarker);
    }

    private sealed class ProgramEndHandoff(uint programEnd) : ITitleExecutionHandoff
    {
        public TitleExecutionHandoffResult? Decide(RecompilerStateSnapshot segmentState) =>
            segmentState.PC == programEnd ? TitleExecutionHandoffResult.Exit() : null;
    }
}
