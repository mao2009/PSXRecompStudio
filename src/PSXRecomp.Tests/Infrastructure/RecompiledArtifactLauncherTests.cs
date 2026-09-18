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
            initialMemory: [], outerBudget: 4, segmentBudget: 64);

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

    private sealed class ProgramEndHandoff(uint programEnd) : ITitleExecutionHandoff
    {
        public TitleExecutionHandoffResult? Decide(RecompilerStateSnapshot segmentState) =>
            segmentState.PC == programEnd ? TitleExecutionHandoffResult.Exit() : null;
    }
}
