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
        var program = Lower(entryPc, [Immediate(OriOpcode, (byte)R3000aRegister.V0, DiagnosticMarker)]);

        using var dir = new TempDirectory();
        var request = new TitleExecutionRequest(
            entryPc, new uint[TitleExecutionRequest.GprCount], initialHi: 0, initialLo: 0,
            initialMemory: [], outerBudget: 1, segmentBudget: 8);

        var outcome = new RecompiledArtifactLauncher().Launch(
            program, request, handoff: null, dir.FullPath, resultRegister: (int)R3000aRegister.V0);

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
            program, request, new ProgramEndHandoff(programEnd), dir.FullPath, resultRegister: (int)R3000aRegister.S1);

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
        var program = Lower(entryPc, [Immediate(OriOpcode, (byte)R3000aRegister.V0, DiagnosticMarker)]);

        using var dir = new TempDirectory();
        using var engine = new RecompiledHostExecutionEngine(program, new GeneratedHostBuildService(), dir.FullPath);
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
        var program = Lower(entryPc, [Immediate(OriOpcode, (byte)R3000aRegister.V0, DiagnosticMarker)]);

        using var dir = new TempDirectory();
        var request = new TitleExecutionRequest(
            entryPc, new uint[TitleExecutionRequest.GprCount], initialHi: 0, initialLo: 0,
            initialMemory: [], outerBudget: 2, segmentBudget: 8);

        // The launcher is one-shot (RecompiledHostExecutionEngine carries no
        // guest-RAM continuity across repeated launches), so an OuterBudget above
        // 1 is rejected up front — it is a launcher precondition, not a
        // mid-orchestration engine surprise.
        var act = () => new RecompiledArtifactLauncher().Launch(
            program, request, handoff: null, dir.FullPath);
        act.Should().Throw<InvalidOperationException>().WithMessage("*OuterBudget must be 1*");
    }

    [Fact]
    public void RunSegment_ExecutesFromSegmentRequestState_NotFromStaleLoadState()
    {
        const uint entryPc = 0x80050000u;
        var program = Lower(entryPc, [Immediate(OriOpcode, (byte)R3000aRegister.V0, DiagnosticMarker)]);

        using var dir = new TempDirectory();
        using var engine = new RecompiledHostExecutionEngine(program, new GeneratedHostBuildService(), dir.FullPath);

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
            program, request, new ProgramEndHandoff(programEnd), dir.FullPath, resultRegister: (int)R3000aRegister.S1);

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
        var program = Lower(entryPc, [Immediate(OriOpcode, (byte)R3000aRegister.V0, DiagnosticMarker)]);

        using var dir = new TempDirectory();
        // A quote cannot be part of a Windows path; everywhere else both a space
        // and a quote must survive the launch untouched (Windows-style quoting
        // would have treated the quote as a quoting delimiter).
        var working = OperatingSystem.IsWindows()
            ? dir.CreateSubdirectory("path with space")
            : dir.CreateSubdirectory("path with \"quote\" and space");
        using var engine = new RecompiledHostExecutionEngine(program, new GeneratedHostBuildService(), working);
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
        var program = Lower(entryPc, [Immediate(OriOpcode, (byte)R3000aRegister.V0, DiagnosticMarker)]);

        using var dir = new TempDirectory();
        var request = new TitleExecutionRequest(
            entryPc, new uint[TitleExecutionRequest.GprCount], initialHi: 0, initialLo: 0,
            initialMemory: [], outerBudget: 1, segmentBudget: 8);

        // The Runtime factory is invoked as the very first host-transfer
        // handshake: a faulting factory exercises RunProcess's pump-fault path.
        var outcome = new RecompiledArtifactLauncher().Launch(
            program,
            request,
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

    private sealed class ProgramEndHandoff(uint programEnd) : ITitleExecutionHandoff
    {
        public TitleExecutionHandoffResult? Decide(RecompilerStateSnapshot segmentState) =>
            segmentState.PC == programEnd ? TitleExecutionHandoffResult.Exit() : null;
    }
}
