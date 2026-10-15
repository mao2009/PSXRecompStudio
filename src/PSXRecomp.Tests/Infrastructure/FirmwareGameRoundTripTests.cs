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
/// Issue #732 (Phase C): one guest state, firmware and game. Synthetic ROM code loads a synthetic game into RAM and
/// calls it; the game calls back into a ROM routine and returns; the firmware resumes. The firmware's own COP0 write
/// and the RAM it initialised are still in place after the game ran, and the same instruction stream reaches the same
/// state on the interpreter. ROM code with a block, and the pre-generated game, run natively; nothing is answered by
/// a BIOS HLE Runtime (none is attached to a guest firmware).
/// </summary>
[Test]
public sealed class FirmwareGameRoundTripTests
{
    private const uint RomBase = 0xBFC00000u;
    private const uint BiosRoutine = RomBase + 0x200;
    private const uint GameEntry = 0x80010000u;
    private const uint Marker = 0x80020000u;
    private static readonly R3000aRegister K0 = (R3000aRegister)26;

    private static uint[] Rom(uint[] game, out uint[] code)
    {
        var rom = new Block(RomBase);
        // Firmware work before the game: SR (COP0 12) is set to a value the game must not disturb, and a RAM word is initialised.
        rom.Emit(Li(T0, 0x00400000u), [Mtc0(T0, 12), Nop]);
        rom.Emit(Li(T1, Marker), Li(T2, 0xB105u), [Sw(T2, T1, 0)]);
        for (var i = 0; i < game.Length; i++)
        {
            rom.Emit(Li(A0, GameEntry + (uint)i * 4), Li(T5, game[i]), [Sw(T5, A0, 0)]);
        }

        rom.Emit(Li(T9, GameEntry), [Jalr(T9), Nop]);
        rom.Emit(Ori(S1, Zero, 0x66), Mfc0(S3, 12), Nop, Lw(S4, T1, 0), Nop); // runs only if the game returned into the firmware
        rom.Emit(MipsEncoding.Branch(0x04, 0, 0, rom.Here, rom.Here), Nop);

        // The BIOS routine the game calls: a result in S6 and a return.
        var routine = new Block(BiosRoutine);
        routine.Emit(Ori(S6, Zero, 0x42), Jr(Ra), Nop);

        var words = new uint[0x800 / 4];
        for (var i = 0; i < rom.Words.Count; i++) words[i] = rom.Words[i];
        for (var i = 0; i < routine.Words.Count; i++) words[(BiosRoutine - RomBase) / 4 + (uint)i] = routine.Words[i];
        code = words;
        return words;
    }

    /// <summary>The game: remember RA, call the BIOS routine, record a result, return to the firmware.</summary>
    private static uint[] Game()
    {
        var game = new List<uint> { Addu(S7, Ra, Zero) };
        game.AddRange(Li(T9, BiosRoutine));
        game.AddRange([Jalr(T9), Nop, Ori(S2, Zero, 0x11), Jr(S7), Nop]);
        return [.. game];
    }

    private static TitleExecutionRequest Request(uint budget) =>
        new(RomBase, new uint[TitleExecutionRequest.GprCount], 0, 0, [], outerBudget: 1, segmentBudget: budget);

    [Fact]
    public void GameCallsABiosRoutineAndReturns_OnOneGuestState_LikeTheInterpreter()
    {
        var game = Game();
        var rom = Rom(game, out var code);
        var loaded = new LoadedCodeTable([ReachableProgramBuilder.BuildLoadedImage(GameEntry, game, [GameEntry], new HashSet<uint>())]);
        var fetched = new List<uint>();

        using var dir = new TempDirectory();
        using var engine = new RecompiledHostExecutionEngine(
            ReachableProgramBuilder.BuildFirmwareImage(RomBase, code, RomBase, [BiosRoutine]).Program,
            rom, RomBase, new GeneratedHostBuildService(), dir.FullPath,
            mixedFallback: new MixedFallbackOptions(), guestFirmware: true, loadedCode: loaded, embedImage: true)
        {
            FallbackFetchObserver = (_, pc) => fetched.Add(pc),
        };
        var host = new ExecutionOrchestrator().Execute(engine, handoff: null, Request(2_000));

        using var interpreter = new InterpreterTitleExecutionEngine(rom, RomBase, allowRuntimeRamExecution: true);
        var reference = new ExecutionOrchestrator().Execute(interpreter, handoff: null, Request(2_000));

        host.FinalSnapshot.Should().NotBeNull(host.DiagnosticMessage);
        var gpr = host.FinalSnapshot!.Gpr;
        gpr[(int)S6].Should().Be(0x42u, "the game reached the ROM's own routine");
        gpr[(int)S2].Should().Be(0x11u, "the game continued after the BIOS call");
        gpr[(int)S1].Should().Be(0x66u, "control returned from the game into the firmware");
        gpr[(int)S3].Should().Be(0x00400000u, "the firmware's COP0 SR survives the game's run");
        gpr[(int)S4].Should().Be(0xB105u, "RAM the firmware initialised is still there after the game ran");
        gpr.Should().Equal(reference.FinalSnapshot!.Gpr, "firmware and game share one state: same registers as the interpreter");

        engine.NativeRetiredInstructions.Should().BeGreaterThan(0);
        fetched.Should().NotContain(GameEntry, "the pre-generated game runs natively, not as counted fallback");
        fetched.Should().NotContain(BiosRoutine, "the ROM routine has a block and is never answered by the host");
        (engine.FallbackEvidence?.Targets.Select(t => t.Target) ?? []).Should().NotContain([GameEntry, BiosRoutine]);
    }

    [Fact]
    public void WithoutAPreGeneratedGame_TheSameStateIsReachedThroughCountedFallback()
    {
        var game = Game();
        var rom = Rom(game, out var code);
        var fetched = new List<uint>();

        using var dir = new TempDirectory();
        using var engine = new RecompiledHostExecutionEngine(
            ReachableProgramBuilder.BuildFirmwareImage(RomBase, code, RomBase, [BiosRoutine]).Program,
            rom, RomBase, new GeneratedHostBuildService(), dir.FullPath,
            mixedFallback: new MixedFallbackOptions(), guestFirmware: true, embedImage: true)
        {
            FallbackFetchObserver = (_, pc) => fetched.Add(pc),
        };
        var host = new ExecutionOrchestrator().Execute(engine, handoff: null, Request(2_000));

        using var interpreter = new InterpreterTitleExecutionEngine(rom, RomBase, allowRuntimeRamExecution: true);
        var reference = new ExecutionOrchestrator().Execute(interpreter, handoff: null, Request(2_000));

        host.FinalSnapshot!.Gpr.Should().Equal(reference.FinalSnapshot!.Gpr);
        fetched.Should().Contain(GameEntry, "unknown RAM code is explicit, counted fallback and never silently native");
        engine.FallbackEvidence!.FallbackInstructions.Should().BeGreaterThan(0);
    }
}
