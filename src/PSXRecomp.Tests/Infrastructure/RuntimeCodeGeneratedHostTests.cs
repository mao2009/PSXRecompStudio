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
/// Issue #732: code a firmware places in RAM at run time, pre-generated ahead of time and executed natively. A synthetic
/// ROM copies routines to RAM and calls them; the same routines, given as explicit images at their destination, are
/// compiled next to the ROM (<see cref="ReachableProgramBuilder.BuildLoadedImage"/>). The interpreter on the same ROM is
/// the reference; RAM that holds no pre-generated version must run in the fallback, never as stale native code.
/// </summary>
[Test]
public sealed class RuntimeCodeGeneratedHostTests
{
    private const uint RomBase = 0xBFC00000u;
    private const uint GeneralVector = 0x80000080u;
    private const R3000aRegister K0 = (R3000aRegister)26;

    private static uint Beq(uint pc, uint target) => MipsEncoding.Branch(0x04, 0, 0, pc, target);

    private static void Install(Block block, uint address, uint[] code)
    {
        block.Emit(Li(A0, address));
        for (var i = 0; i < code.Length; i++)
        {
            block.Emit(Li(T5, code[i]), [Sw(T5, A0, (short)(i * 4))]);
        }
    }

    private static void Call(Block block, uint address) => block.Emit(Li(T9, address), [Jalr(T9), Nop]);

    private static uint[] Rom(Block reset)
    {
        reset.Emit(Beq(reset.Here, reset.Here), Nop);
        var words = new uint[0x1000 / 4];
        for (var i = 0; i < reset.Words.Count; i++) words[i] = reset.Words[i];
        return words;
    }

    private static TitleExecutionRequest Request(uint budget) =>
        new(RomBase, new uint[TitleExecutionRequest.GprCount], 0, 0, [], outerBudget: 1, segmentBudget: budget);

    private static RecompilerStateSnapshot Reference(uint[] rom, uint budget)
    {
        using var interpreter = new InterpreterTitleExecutionEngine(rom, RomBase, allowRuntimeRamExecution: true);
        return new ExecutionOrchestrator().Execute(interpreter, handoff: null, Request(budget)).FinalSnapshot!;
    }

    private sealed record HostRun(RecompilerStateSnapshot Final, MixedFallbackEvidence Evidence, LoadedCodeTable Loaded, IReadOnlyList<MixedFallbackTransition> Transitions, IReadOnlyList<uint> Fetches);

    private static HostRun RunHost(uint[] rom, IEnumerable<(uint Dest, uint[] Code, uint[] Roots)> images, IReadOnlySet<uint> interpreted, uint budget)
    {
        using var dir = new TempDirectory();
        var romCode = ReachableProgramBuilder.BuildFirmwareImage(RomBase, rom, RomBase, []).Program;
        var loaded = new LoadedCodeTable(images.Select(i => ReachableProgramBuilder.BuildLoadedImage(i.Dest, i.Code, i.Roots, interpreted)));
        using var engine = new RecompiledHostExecutionEngine(
            romCode, rom, RomBase, new GeneratedHostBuildService(), dir.FullPath,
            mixedFallback: new MixedFallbackOptions(), guestFirmware: true, loadedCode: loaded);
        var fetches = new List<uint>();
        engine.FallbackFetchObserver = (_, pc) => fetches.Add(pc);
        var transitions = new List<MixedFallbackTransition>();
        engine.FallbackTransitionObserver = transitions.Add;
        var result = new ExecutionOrchestrator().Execute(engine, handoff: null, Request(budget));
        result.FinalSnapshot.Should().NotBeNull(result.DiagnosticMessage);
        engine.NativeRetiredInstructions.Should().BeGreaterThan(0ul);
        return new HostRun(result.FinalSnapshot!, engine.FallbackEvidence!, loaded, transitions, fetches);
    }

    [Theory]
    [InlineData(0x801FFFFCu, 1)]
    [InlineData(0x801FFFF8u, 2)]
    [InlineData(0x801FFFF4u, 3)]
    [InlineData(0x803FFFFCu, 1)]
    [InlineData(0xA01FFFFCu, 1)]
    public void FallbackReturnsToCurrentLoadedUnitAtRamEnd(uint entry, int words)
    {
        const uint trampoline = 0x80001000u;
        uint[] code = words switch
        {
            1 => [Nop],
            2 => [Jr(Ra), Nop],
            _ => [Addiu(S2, S2, 7), Jr(Ra), Nop],
        };
        uint[] bridge = [.. Li(T9, entry), Jr(T9), Nop];
        var reset = new Block(RomBase);
        Install(reset, trampoline, bridge);
        Install(reset, entry, code);
        Install(reset, 0x80000000u, [Jr(Ra), Nop]);
        Call(reset, trampoline);
        reset.Emit(Ori(S1, Zero, 0x7777));
        var rom = Rom(reset);
        var reference = Reference(rom, 1_000);
        var host = RunHost(rom, [(entry, code, [entry])], new HashSet<uint>(), 1_000);
        host.Final.Gpr.Should().Equal(reference.Gpr);
        host.Final.Gpr[(int)S1].Should().Be(0x7777u);
        host.Transitions.Should().Contain(t => t.ExitPc == entry && t.Status == FallbackSegmentStatus.Returned);
        host.Fetches.Should().NotContain(entry, "the current complete unit fits in RAM and must resume natively");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RamEndVersions_RejectOverrunAndMutationWithoutHidingCurrentShortVersion(bool mutate)
    {
        const uint entry = 0x801FFFFCu, trampoline = 0x80001000u;
        uint first = Nop;
        uint[] shortVersion = [first];
        uint[] overrunVersion = [Beq(entry, entry + 8), Nop];
        uint[] actual = [mutate ? Addiu(S2, S2, 1) : first];
        uint[] bridge = [.. Li(T9, entry), Jr(T9), Nop];
        var reset = new Block(RomBase);
        Install(reset, trampoline, bridge);
        Install(reset, entry, actual);
        Install(reset, 0x80000000u, [Jr(Ra), Nop]);
        Call(reset, trampoline);
        reset.Emit(Ori(S1, Zero, 0x7777));
        var rom = Rom(reset);
        var reference = Reference(rom, 1_000);
        var host = RunHost(rom, [(entry, overrunVersion, [entry]), (entry, shortVersion, [entry])], new HashSet<uint>(), 1_000);
        host.Final.Gpr.Should().Equal(reference.Gpr);
        host.Loaded.VersionsAt(entry).Should().HaveCount(2);
        if (mutate)
        {
            host.Fetches.Should().Contain(entry, "neither version matches the changed word");
            host.Transitions.Should().NotContain(t => t.ExitPc == entry);
        }
        else
        {
            host.Fetches.Should().NotContain(entry);
            host.Transitions.Should().Contain(t => t.ExitPc == entry && t.Status == FallbackSegmentStatus.Returned);
        }
    }

    [Fact]
    public void MatchingVersionThatCrossesRamEnd_RemainsInterpreted()
    {
        const uint entry = 0x801FFFFCu, trampoline = 0x80001000u;
        uint[] overrun = [Beq(entry, entry + 8), Nop];
        uint[] bridge = [.. Li(T9, entry), Jr(T9), Nop];
        var reset = new Block(RomBase);
        Install(reset, trampoline, bridge);
        Install(reset, entry, overrun);
        Install(reset, 0x80000000u, [Nop, Jr(Ra), Nop]);
        Call(reset, trampoline);
        reset.Emit(Ori(S1, Zero, 0x7777));
        var rom = Rom(reset);
        var reference = Reference(rom, 1_000);
        var host = RunHost(rom, [(entry, overrun, [entry])], new HashSet<uint>(), 1_000);
        host.Final.Gpr.Should().Equal(reference.Gpr);
        host.Fetches.Should().Contain(entry, "the matching delay word wraps RAM, beyond the artifact's contiguous unit span");
        host.Transitions.Should().NotContain(t => t.ExitPc == entry);
    }

    [Fact]
    public void LoadedRamCode_RunsNatively_WithDirectAndIndirectJumps_AnExceptionReturn_AndARamToRomReturn()
    {
        const uint r1 = 0x80001000u, r2 = 0x80001100u;
        uint[] handler = [Mfc0(K0, 14), Nop, Addiu(S4, S4, 1), Addiu(K0, K0, 4), Jr(K0), RecompilerCop0Tests.Rfe];
        uint[] routine1 =
        [
            Addu(S7, Ra, Zero),
            Beq(r1 + 4, r1 + 16), Nop,                 // a direct branch inside RAM code
            Ori(S0, Zero, 0xBAD),                      // skipped: never executed (compiled as the branch fall-through)
            .. Li(T8, r2), Jalr(T8), Nop,              // an indirect call from RAM to RAM
            MipsEncoding.Syscall(),                    // an exception from RAM whose handler (in RAM) returns to RAM
            Ori(S2, Zero, 0x55),
            Jr(S7), Nop,                               // back to the ROM
        ];
        uint[] routine2 = [Addiu(S3, S3, 7), Jr(Ra), Nop];
        var reset = new Block(RomBase);
        Install(reset, GeneralVector, handler);
        Install(reset, r1, routine1);
        Install(reset, r2, routine2);
        reset.Emit(Mtc0(Zero, 12));
        Call(reset, r1);
        reset.Emit(Ori(S1, Zero, 0x66));
        var rom = Rom(reset);

        var reference = Reference(rom, 3_000);
        var host = RunHost(rom, [(GeneralVector, handler, [GeneralVector]), (r1, routine1, [r1]), (r2, routine2, [r2])], new HashSet<uint>(), 3_000);

        foreach (var r in new[] { S0, S1, S2, S3, S4 })
        {
            host.Final.Gpr[(int)r].Should().Be(reference.Gpr[(int)r], $"{r} matches the firmware interpreter");
        }

        host.Final.Gpr[(int)S4].Should().Be(1u);
        host.Final.Gpr[(int)S3].Should().Be(7u);
        host.Evidence.Transitions.Should().Be(0u, "every routine the firmware ran was pre-generated and current");
        host.Evidence.FallbackInstructions.Should().Be(0ul);
    }

    [Theory]
    [InlineData(0x21)]
    [InlineData(0x25)]
    [InlineData(0x23)]
    [InlineData(0x29)]
    [InlineData(0x2B)]
    public void LoadedAlignedMemoryFaults_EnterFallbackHandlerAndReturnToNativeCode(byte opcode)
    {
        const uint routine = 0x80001000u;
        uint[] handler = [Mfc0(S1, 8), Nop, Mfc0(K0, 14), Nop, Addiu(K0, K0, 4), Jr(K0), RecompilerCop0Tests.Rfe];
        uint[] code = [.. Li(T0, 0xA0002001), MipsEncoding.I(opcode, (byte)S0, (byte)T0, 0), Nop, Ori(S2, Zero, 0x7777), Jr(Ra), Nop];
        var reset = new Block(RomBase);
        Install(reset, GeneralVector, handler);
        Install(reset, routine, code);
        reset.Emit(Mtc0(Zero, 12));
        Call(reset, routine);
        var rom = Rom(reset);
        var reference = Reference(rom, 1_000);
        var host = RunHost(rom, [(routine, code, [routine])], new HashSet<uint>(), 1_000);
        host.Final.Gpr.Should().Equal(reference.Gpr);
        host.Final.Gpr[(int)S1].Should().Be(0xA0002001u);
        host.Final.Gpr[(int)S2].Should().Be(0x7777u);
        host.Fetches.Should().Contain(GeneralVector).And.NotContain(routine);
        host.Transitions.Should().Contain(t => t.EntryPc == GeneralVector && t.Status == FallbackSegmentStatus.Returned);
    }

    [Fact]
    public void TwoPreGeneratedVersionsAtOneAddress_AreBothSelectedNatively_AndUnknownCodeThereFallsBack()
    {
        const uint slot = 0x80001000u;
        uint[] v1 = [Addiu(S2, S2, 1), Jr(Ra), Nop];
        uint[] v2 = [Addiu(S2, S2, 0x10), Jr(Ra), Nop];
        uint[] unknown = [Addiu(S2, S2, 0x100), Jr(Ra), Nop];
        var reset = new Block(RomBase);
        Install(reset, slot, v1);
        Call(reset, slot);
        Install(reset, slot, v2);
        Call(reset, slot);
        Install(reset, slot, unknown);
        Call(reset, slot);
        Install(reset, slot, v1);
        Call(reset, slot);
        var rom = Rom(reset);

        var reference = Reference(rom, 3_000);
        var host = RunHost(rom, [(slot, v1, [slot]), (slot, v2, [slot])], new HashSet<uint>(), 3_000);

        host.Final.Gpr[(int)S2].Should().Be(reference.Gpr[(int)S2]).And.Be(0x112u);
        host.Loaded.VersionsAt(slot).Should().HaveCount(2);
        // Only the code that matches no pre-generated version ran in the fallback: one instruction.
        host.Evidence.FallbackInstructions.Should().Be(1ul);
        host.Evidence.Targets.Should().ContainSingle().Which.Target.Should().Be(slot);
        host.Transitions.Should().ContainSingle();
        host.Transitions[0].EntryPc.Should().Be(slot);
        host.Transitions[0].RetiredInstructions.Should().Be(1ul);
        host.Transitions[0].ExitPc.Should().Be(slot + 4);
        host.Transitions[0].Status.Should().Be(FallbackSegmentStatus.Returned);
    }

    [Fact]
    public void MutableBranchSuccessor_ObservesTheOriginalDelaySlotLoadValue()
    {
        const uint slot = 0x80001000, data = 0x80002000;
        uint[] routine = [Beq(slot, slot + 12), Lw(T0, T1, 0), Nop, Nop, Jr(Ra), Nop];
        var reset = new Block(RomBase);
        Install(reset, slot, routine);
        reset.Emit(Li(T1, data), Li(T5, 0x22), [Sw(T5, T1, 0)]);
        reset.Emit(Li(A0, slot), Li(T5, Addu(S0, T0, Zero)), [Sw(T5, A0, 12)]);
        reset.Emit(Ori(T0, Zero, 0x11));
        Call(reset, slot);
        var rom = Rom(reset);
        var reference = Reference(rom, 3000);
        var host = RunHost(rom, [(slot, routine, [slot])], new HashSet<uint>(), 3000);

        reference.Gpr[(int)S0].Should().Be(0x11u);
        host.Final.Gpr[(int)S0].Should().Be(reference.Gpr[(int)S0]);
        host.Evidence.FallbackInstructions.Should().BeGreaterThan(0ul);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExplicitObservationPoint_IsFetchedByTheFallbackIncludingFusedUnits(int kind)
    {
        const uint slot = 0x80001000;
        uint[] routine = kind switch
        {
            0 => [Ori(S1, Zero, 1), Ori(S2, Zero, 2), Jr(Ra), Nop],
            1 => [Lw(T0, T1, 0), Addu(S1, T0, Zero), Jr(Ra), Nop],
            _ => [Beq(slot, slot + 8), Ori(S1, Zero, 1), Jr(Ra), Nop],
        };
        var reset = new Block(RomBase);
        Install(reset, slot, routine);
        reset.Emit(Li(T1, 0x80002000), [Ori(T0, Zero, 0x11)]);
        Call(reset, slot);
        var rom = Rom(reset);
        var host = RunHost(rom, [(slot, routine, [slot])], new HashSet<uint> { slot + 4 }, 3000);
        var reference = Reference(rom, 3000);

        host.Fetches.Should().Contain(slot + 4);
        host.Final.Gpr[(int)S1].Should().Be(reference.Gpr[(int)S1]);
    }

    [Fact]
    public void RamCode_ChangedAfterLoad_NeverRunsStale_AndOnlyTheChangedWordsAreCountedAsFallback()
    {
        const uint p = 0x80001000u, r = 0x80001100u, q = 0x80001200u;
        // p: rewritten by a native ROM store between two calls (the artifact's own identity check must catch it).
        uint[] pCode = [Addiu(S5, S5, 1), Jr(Ra), Nop];
        // r: entered in the fallback (interpreted entry); its second word changes, so the interpreter must not return there.
        uint[] rCode = [Addiu(S2, S2, 1), Addiu(S3, S3, 1), Jr(Ra), Nop];
        // q: its first word (fallback) stores $t6 over q+12, so the change arrives with the fallback's RAM write-back.
        uint[] qCode = [Sw(T6, T7, 12), Nop, Nop, Addiu(S6, S6, 1), Jr(Ra), Nop];
        var reset = new Block(RomBase);
        Install(reset, p, pCode);
        Install(reset, r, rCode);
        Install(reset, q, qCode);
        reset.Emit(Li(T7, q), Li(T6, Addiu(S6, S6, 1)));
        Call(reset, p);
        Call(reset, r);
        Call(reset, q);
        reset.Emit(Li(A0, p), Li(T5, Addiu(S5, S5, 0x100)), [Sw(T5, A0, 0)]);
        reset.Emit(Li(A0, r), Li(T5, Addiu(S3, S3, 0x100)), [Sw(T5, A0, 4)]);
        reset.Emit(Li(T6, Addiu(S6, S6, 0x100)));
        Call(reset, p);
        Call(reset, r);
        Call(reset, q);
        var rom = Rom(reset);

        var reference = Reference(rom, 3_000);
        var host = RunHost(rom, [(p, pCode, [p]), (r, rCode, [r, r + 4]), (q, qCode, [q, q + 4])], new HashSet<uint> { r, q }, 3_000);

        foreach (var reg in new[] { S2, S3, S5, S6 })
        {
            host.Final.Gpr[(int)reg].Should().Be(reference.Gpr[(int)reg], $"{reg} matches the firmware interpreter");
        }

        host.Final.Gpr[(int)S5].Should().Be(0x101u, "the rewritten p ran its new word, not the pre-generated one");
        host.Final.Gpr[(int)S3].Should().Be(0x101u);
        host.Final.Gpr[(int)S6].Should().Be(0x101u);
        // p: 1 (second call, stale). r: 1 + 2 (interpreted entry twice; the stale second word once). q: 1 + 1 (the
        // interpreted store twice) and q+12: 1 (stale after the second store).
        host.Evidence.FallbackInstructions.Should().Be(7ul);
        host.Evidence.Targets.Should().ContainSingle(t => t.Target == p).Which.Instructions.Should().Be(1ul);
        host.Evidence.Targets.Should().ContainSingle(t => t.Target == r).Which.Instructions.Should().Be(3ul);
        host.Evidence.Targets.Should().ContainSingle(t => t.Target == q + 12).Which.Instructions.Should().Be(1ul);
    }
}
