using FluentAssertions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Tests.Recompiler;
using Xunit;
using static PSXRecomp.Tests.Infrastructure.MixedFallbackTestSupport;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #447: the generated host executes COP2 against its device graph's one GTE — the same instance the
/// mixed-execution fallback interpreter executes against. A ROM (native blocks) and a routine it writes to RAM
/// (no block, fallback) hand GTE state to each other; the firmware interpreter is the reference.
/// </summary>
[Test]
public sealed class GteGeneratedHostTests
{
    private const uint RomBase = 0xBFC00000u;
    private const uint RamRoutine = 0x80001000u;

    private static uint Mfc2(R3000aRegister rt, int rd) => 0x4800_0000u | ((uint)rt << 16) | ((uint)rd << 11);
    private static uint Mtc2(R3000aRegister rt, int rd) => 0x4880_0000u | ((uint)rt << 16) | ((uint)rd << 11);
    private static uint Cop2(uint command) => 0x4A00_0000u | command;

    /// <summary>
    /// SR.CU2 on; the RAM routine (fallback) writes LZCS and screen XY points, the ROM (native) runs NCLIP on them
    /// and reads LZCR and MAC0 back; then the ROM sets LZCS itself and the RAM routine's second half reads it.
    /// </summary>
    private static uint[] Rom()
    {
        var reset = new Block(RomBase);
        reset.Emit(Li(T0, 0x4000_0000u), [Mtc0(T0, 12), Nop]);
        reset.Emit(Li(A0, RamRoutine));
        uint[] routine =
        [
            Ori(T1, Zero, 0x77), Mtc2(T1, 30),       // LZCS = 0x77
            Mtc2(Zero, 15),                           // SXY (0,0)
            Ori(T1, Zero, 10), Mtc2(T1, 15),          // (10,0)
            Jr(Ra), Nop,
        ];
        for (var i = 0; i < routine.Length; i++)
        {
            reset.Emit(Li(T5, routine[i]), [Sw(T5, A0, (short)(i * 4))]);
        }

        reset.Emit(Li(T9, RamRoutine), [Jalr(T9), Nop]);
        reset.Emit(Li(T2, 10u << 16), [Mtc2(T2, 15)]);   // native: (0,10)
        reset.Emit(Cop2(0x0140_0006), Mfc2(S0, 24), Mfc2(S1, 31), Nop);
        reset.Emit(MipsEncoding.Branch(0x04, 0, 0, reset.Here, reset.Here), Nop);

        var words = new uint[0x800 / 4];
        for (var i = 0; i < reset.Words.Count; i++) words[i] = reset.Words[i];
        return words;
    }

    [Fact]
    public void NativeBlocksAndTheFallback_ShareOneGte_AndMatchTheInterpreter()
    {
        var rom = Rom();
        var request = new TitleExecutionRequest(RomBase, new uint[TitleExecutionRequest.GprCount], 0, 0, [], outerBudget: 1, segmentBudget: 2_000);
        using var dir = new TempDirectory();
        var image = ReachableProgramBuilder.BuildFirmwareImage(RomBase, rom[..0x100], RomBase, []);
        image.Program.Blocks.SelectMany(block => block.Operations).Select(op => op.Kind)
            .Should().Contain([RecompilerIrOperationKind.Cop2Command, RecompilerIrOperationKind.ReadCop2],
                "the ROM's COP2 instructions are native blocks, not fallback");
        using var engine = new RecompiledHostExecutionEngine(
            image.Program, rom, RomBase, new GeneratedHostBuildService(), dir.FullPath,
            mixedFallback: new MixedFallbackOptions(), guestFirmware: true);

        var host = new ExecutionOrchestrator().Execute(engine, handoff: null, request);

        using var interpreter = new InterpreterTitleExecutionEngine(rom, RomBase, allowRuntimeRamExecution: true);
        var reference = new ExecutionOrchestrator().Execute(interpreter, handoff: null, request);

        host.FinalSnapshot.Should().NotBeNull(host.DiagnosticMessage);
        var gpr = host.FinalSnapshot!.Gpr;
        gpr[(int)S0].Should().Be(100u, "NCLIP (native) saw the screen points the fallback pushed");
        gpr[(int)S1].Should().Be(25u, "LZCR of the LZCS the fallback wrote");
        gpr[(int)S0].Should().Be(reference.FinalSnapshot!.Gpr[(int)S0]);
        gpr[(int)S1].Should().Be(reference.FinalSnapshot!.Gpr[(int)S1]);
        engine.NativeRetiredInstructions.Should().BeGreaterThan(0);
        engine.FallbackEvidence!.Targets.Select(t => t.Target).Should().Contain(RamRoutine);
    }

    [Fact]
    public void AnUnimplementedGteCommand_StopsTheGeneratedHost_NamingTheCommand()
    {
        var reset = new Block(RomBase);
        reset.Emit(Li(T0, 0x4000_0000u), [Mtc0(T0, 12), Nop]);
        reset.Emit(Cop2(0x0028_0030), Nop);   // RTPT: not implemented
        reset.Emit(MipsEncoding.Branch(0x04, 0, 0, reset.Here, reset.Here), Nop);
        var rom = new uint[0x800 / 4];
        for (var i = 0; i < reset.Words.Count; i++) rom[i] = reset.Words[i];
        using var dir = new TempDirectory();
        using var engine = new RecompiledHostExecutionEngine(
            ReachableProgramBuilder.BuildFirmwareImage(RomBase, rom[..0x40], RomBase, []).Program, rom, RomBase,
            new GeneratedHostBuildService(), dir.FullPath, mixedFallback: new MixedFallbackOptions(), guestFirmware: true);

        var result = new ExecutionOrchestrator().Execute(engine, handoff: null,
            new TitleExecutionRequest(RomBase, new uint[TitleExecutionRequest.GprCount], 0, 0, [], outerBudget: 1, segmentBudget: 200));

        result.DiagnosticCode.Should().Be("GTE_COMMAND_UNSUPPORTED");
        result.DiagnosticMessage.Should().Contain("0x30");
    }
}
