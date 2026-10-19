using PSXRecomp.Core.Diagnostics;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure;
using PSXRecomp.Infrastructure.Cli;
using FluentAssertions;
using PSXRecomp.Core;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Tests.Recompiler;
using Xunit;
using static PSXRecomp.Tests.Infrastructure.MixedFallbackTestSupport;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #693: the generated-host artifact reaches an in-image indirect target it has no block for, hands the CPU to the
/// interpreter on the host's own device graph, and takes control back at a clean compiled block entry. Real artifacts
/// are built through the production build service and run over the production host protocol; every fail-closed
/// boundary and every pipeline-boundary case is compared with the pure interpreter as the reference.
/// </summary>
[Test]
public sealed class MixedFallbackTests
{
    private static readonly MixedFallbackOptions On = new();

    private static uint Gpr(TitleExecutionResult result, R3000aRegister register) => result.FinalSnapshot!.Gpr[(int)register];

    private static void AssertSameCpuAsInterpreter(TitleExecutionResult mixed, TitleExecutionResult reference)
    {
        mixed.State.Should().Be(reference.State);
        for (var i = 0; i < TitleExecutionRequest.GprCount; i++)
        {
            mixed.FinalSnapshot!.Gpr[i].Should().Be(reference.FinalSnapshot!.Gpr[i], $"GPR {i} must match the pure interpreter");
        }

        mixed.FinalSnapshot!.HI.Should().Be(reference.FinalSnapshot!.HI);
        mixed.FinalSnapshot.LO.Should().Be(reference.FinalSnapshot.LO);
    }

    [Theory]
    [InlineData(2)] // event during the native JALR's branch delay pair
    [InlineData(3)] // immediately before fallback
    [InlineData(4)] // first fallback instruction
    [InlineData(5)] // fallback JR, before its delay slot
    [InlineData(6)] // fallback return boundary
    [InlineData(7)] // first native instruction after fallback
    public void TimerIrqAcrossNativeFallbackTransitions_MatchesPureInterpreter(ushort deadline)
    {
        var main = new Block(Entry);
        main.Emit(Ori(T1, Zero, 0x401), 0x40896000u); // MTC0 t1,SR: guest-owned IRQ enable
        main.Emit(Li(T0, Target), [MipsEncoding.I(0x0F, (byte)T3, 0, 0x1F80)]);
        main.Emit(Ori(T1, Zero, 1 << 6), Sw(T1, T3, 0x1074));
        main.Emit(Ori(T1, Zero, deadline), Sw(T1, T3, 0x1128));
        main.Emit(Ori(T1, Zero, 0x10), Sw(T1, T3, 0x1124));
        main.Emit(Jalr(T0), Nop);
        main.Emit(Nop, Nop, Nop);
        main.Emit(End());
        var callee = new Block(Target);
        callee.Emit(Nop, Jr(Ra), Nop);
        var words = Image(main, callee);
        using var dir = new TempDirectory();
        ProbeGuestState? actual = null;
        var program = ReachableProgramBuilder.Build(Entry, words, Entry, []);
        using var host = new RecompiledHostExecutionEngine(program, words, Entry,
            new GeneratedHostBuildService(), dir.FullPath, mixedFallback: On, guestFirmware: true);
        host.FallbackFetchObserver = (engine, pc) =>
        {
            if (pc != 0x80000080u || actual is not null) return;
            actual = ProbeGuestState.Capture(engine, pc, 0);
            engine.StopRequested = true;
        };
        new ExecutionOrchestrator().Execute(host, null, Request(1000));

        ProbeGuestState? expected = null;
        using var interpreter = new InterpreterTitleExecutionEngine(words, Entry, allowRuntimeRamExecution: true);
        interpreter.FetchObserver = pc =>
        {
            if (pc != 0x80000080u || expected is not null) return;
            expected = ProbeGuestState.Capture(interpreter, pc, 0);
            interpreter.StopRequested = true;
        };
        new ExecutionOrchestrator().Execute(interpreter, null, Request(1000));
        actual.Should().NotBeNull("the generated CPU must accept the scheduled hardware IRQ");
        expected.Should().NotBeNull();
        var diff = System.Text.Json.JsonSerializer.SerializeToNode(ProbeGuestState.Compare(expected!, actual!))!;
        diff["match"]!.GetValue<bool>().Should().BeTrue(diff["firstMismatch"]?.ToJsonString() ?? "all CPU, RAM and device fields match");
        actual!.Cpu.Single(static v => v.Name == "cause").Value.Should().Be(0x400u);

    }

    // Issue #774 (B-4): the production generated host, traced, native -> fallback -> native. The joined sequence is checked
    // against an independent count: the interpreter's own fetches on the same program (no interrupts or exceptions occur here,
    // so one fetch is one retired instruction).
    [Fact]
    public void UnitTrace_EndToEnd_NativeFallbackNative_JoinsToTheInterpreterInstructionCount()
    {
        var (words, returnSite) = RoundTripProgram();
#pragma warning disable AARC003
        using var dir = new TempDirectory();
        var tracePath = Path.Combine(dir.FullPath, "unit.jsonl");

        MixedRun run;
        using (UnitTraceScope.Begin(tracePath))
        {
            run = RunArtifact(words, dir, On);
        }

        run.Result.State.Should().Be(TitleExecutionState.Completed);
        var native = File.ReadAllLines(tracePath);
        var fallback = File.ReadAllLines(UnitTraceScope.FallbackPathFor(tracePath));
#pragma warning restore AARC003

        var join = UnitTraceJoiner.Join(native, fallback);
        join.Status.Should().Be(UnitTraceJoinStatus.Complete,
            join.Reason ?? string.Join("; ", join.Errors));

        var fallbackSegment = join.Segments.Should().ContainSingle(s => s.Source == UnitTraceSegmentSource.Fallback).Subject;
        fallbackSegment.Handoff.Should().Be(1U);
        fallbackSegment.EntryPc.Should().Be($"0x{Target:X8}", "the fallback entered at the transfer pc");
        fallbackSegment.NextPc.Should().Be($"0x{returnSite:X8}", "the fallback returned to the artifact at the return site");
        var fallbackRetired = fallbackSegment.GlobalEnd - fallbackSegment.GlobalStart;
        fallbackRetired.Should().Be(run.Evidence!.Targets.Single().Instructions);

        // Native stretches sit on either side of the fallback, with no gap and no overlap.
        var stretches = join.Segments.Where(s => s.Source == UnitTraceSegmentSource.Native).ToList();
        stretches.Should().HaveCountGreaterThan(1);
        stretches.First().GlobalStart.Should().Be(0UL);
        stretches.Last().GlobalEnd.Should().Be(join.ComparableUpToRetired);
        var beforeFallback = join.Segments.TakeWhile(s => s.Source != UnitTraceSegmentSource.Fallback).Last();
        var afterFallback = join.Segments.SkipWhile(s => s.Source != UnitTraceSegmentSource.Fallback).Skip(1).First();
        beforeFallback.Source.Should().Be(UnitTraceSegmentSource.Native);
        fallbackSegment.GlobalStart.Should().Be(beforeFallback.GlobalEnd);
        afterFallback.GlobalStart.Should().Be(fallbackSegment.GlobalEnd);

        // The independent reference: the interpreter's fetches on the same program.
        // The same configuration as RunInterpreter. The program ends with jr zero: a fetch at pc 0 is the exit handoff,
        // not an instruction, so it is not counted.
        ulong interpreterFetches = 0;
        using var interpreter = new InterpreterTitleExecutionEngine(
            words, Entry, (reader, writer) => new BiosHleRuntime(new NullSink(), reader, writer));
        interpreter.FetchObserver = pc =>
        {
            if (pc != 0u) interpreterFetches++;
        };
        new ExecutionOrchestrator().Execute(interpreter, new ExitAtZeroHandoff(), Request(1000));
        join.ComparableUpToRetired.Should().Be(interpreterFetches,
            "the joined native + fallback retirement count must equal the interpreter's instruction count");
    }

    [Fact]
    public void UnitTrace_EndToEnd_AMismatchedOrMissingFallbackRecord_IsRejected_AndAnUntracedRunWritesNothing()
    {
        var (words, _) = RoundTripProgram();
#pragma warning disable AARC003
        using var dir = new TempDirectory();
        var tracePath = Path.Combine(dir.FullPath, "unit.jsonl");
        using (UnitTraceScope.Begin(tracePath))
        {
            RunArtifact(words, dir, On).Result.State.Should().Be(TitleExecutionState.Completed);
        }

        var native = File.ReadAllLines(tracePath);
        var fallback = File.ReadAllLines(UnitTraceScope.FallbackPathFor(tracePath));

        // The fallback trace of another run: the session id no longer matches the native trace.
        var otherSession = fallback.Select(line => line.Replace(
            "\"session\":\"", "\"session\":\"other-", StringComparison.Ordinal)).ToArray();
        UnitTraceJoiner.Join(native, otherSession).Status.Should().Be(UnitTraceJoinStatus.Rejected);

        // The fallback record for handoff 1 is removed although both traces ended normally. Removing a record leaves a
        // sequence gap, which the parser reports first; the join must refuse, not place the rest of the run.
        var missingRecord = fallback.Where(line => !line.Contains("\"handoff\":1,", StringComparison.Ordinal)).ToArray();
        var join = UnitTraceJoiner.Join(native, missingRecord);
        join.Status.Should().Be(UnitTraceJoinStatus.Rejected);
        join.Errors.Should().NotBeEmpty();
        join.Segments.Should().BeEmpty("a rejected join returns no segments");

        // Tracing off: the same program writes no trace and no fallback trace next to the artifact.
        using var untracedDir = new TempDirectory();
        RunArtifact(words, untracedDir, On).Result.State.Should().Be(TitleExecutionState.Completed);
        Directory.GetFiles(untracedDir.FullPath, "*.jsonl*", SearchOption.AllDirectories).Should().BeEmpty();
#pragma warning restore AARC003
    }

    /// <summary>The artifact writes RAM, the interpreter reads and rewrites it (two pages), and the artifact reads it back.</summary>
    private static (uint[] Words, uint ReturnSite) RoundTripProgram()
    {
        var main = new Block(Entry);
        main.Emit(Li(T0, Target), Li(T2, Data), Li(S1, 0x11111111u), Li(T3, 0xCAFEF00Du));
        main.Emit(Sw(T3, T2, 0), Jalr(T0), Nop);
        var returnSite = main.Here;
        main.Emit(Lw(S3, T2, 4), Nop, Lw(S4, T2, 0), Nop, Addu(S5, S0, S1));
        main.Emit(End());
        var target = new Block(Target);
        target.Emit(Li(S0, 0x1234u), [Lw(T4, T2, 0), Nop, Addiu(T4, T4, 1), Sw(T4, T2, 4), Sw(S0, T2, 0x1000), Jr(Ra), Nop]);
        return (Image(main, target), returnSite);
    }

    [Fact]
    public void Disabled_AnUncompiledInImageIndirectTarget_StopsAsBefore()
    {
        var (words, _) = RoundTripProgram();
        using var dir = new TempDirectory();

        var run = RunArtifact(words, dir, mixedFallback: null);

        run.Result.State.Should().Be(TitleExecutionState.UnsupportedTransfer);
        run.Result.DiagnosticCode.Should().Be("UNRESOLVED_TRANSFER");
        run.Result.FinalSnapshot!.PC.Should().Be(Target);
        run.Evidence.Should().BeNull("mixed execution was not enabled");
    }

    [Fact]
    public void Enabled_RunsTheTargetOnTheInterpreter_AndReturnsToTheArtifactAtTheReturnSite()
    {
        var (words, returnSite) = RoundTripProgram();
        using var dir = new TempDirectory();

        var run = RunArtifact(words, dir, On);

        run.Result.State.Should().Be(TitleExecutionState.Completed);
        run.Result.DiagnosticCode.Should().BeNull();
        Gpr(run.Result, S0).Should().Be(0x1234u, "the interpreter produced it");
        Gpr(run.Result, S1).Should().Be(0x11111111u, "an artifact register survives the round trip");
        Gpr(run.Result, S5).Should().Be(0x11112345u, "the artifact computed with the interpreter's result");
        run.Evidence!.Transitions.Should().Be(1);
        run.Evidence.Returns.Should().Be(1);
        run.Evidence.LastEntryPc.Should().Be(Target, "the accepted fallback entry was observed");
        run.Evidence.LastReturnPc.Should().Be(returnSite, "the state was committed before the artifact resumed");
        run.Evidence.Targets.Should().ContainSingle().Which.Should().Match<MixedFallbackTarget>(
            t => t.Target == Target && t.Entries == 1 && t.Instructions > 0 && t.LastReturnPc == returnSite);
        AssertSameCpuAsInterpreter(run.Result, RunInterpreter(words));
    }

    [Fact]
    public void RamRoundTrip_ArtifactWritesReachTheInterpreter_AndInterpreterWritesReachTheArtifact()
    {
        var (words, _) = RoundTripProgram();
        using var dir = new TempDirectory();

        var run = RunArtifact(words, dir, On);

        Gpr(run.Result, S4).Should().Be(0xCAFEF00Du, "the word the artifact stored before the handoff is intact");
        Gpr(run.Result, S3).Should().Be(0xCAFEF00Eu, "the interpreter read the artifact's word and wrote word + 1");
        run.Evidence!.PagesToArtifact.Should().Be(2, "the segment dirtied exactly the data page and the page at +0x1000");
        run.Evidence.PagesToInterpreter.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void GprHiLoRoundTrip_ArtifactStateReachesTheInterpreter_AndBack()
    {
        var main = new Block(Entry);
        main.Emit(Li(T0, Target), Li(T5, 0xAAAA0001u), Li(T6, 0xBBBB0002u), Li(T7, 3u), Li(S2, 0x22222222u), Li(S3, 0x33333333u));
        main.Emit(Mthi(T5), Mtlo(T6), Jalr(T0), Nop, Mfhi(T8), Mflo(T9), Nop);
        main.Emit(End());
        var target = new Block(Target);
        target.Emit(Mfhi(S6), Mflo(S7), Mult(T7, T7), Jr(Ra), Nop);
        var words = Image(main, target);
        using var dir = new TempDirectory();

        var run = RunArtifact(words, dir, On);

        Gpr(run.Result, S6).Should().Be(0xAAAA0001u, "the artifact's HI reached the interpreter");
        Gpr(run.Result, S7).Should().Be(0xBBBB0002u, "the artifact's LO reached the interpreter");
        Gpr(run.Result, T8).Should().Be(0u, "the interpreter's MULT left HI = 0, written back");
        Gpr(run.Result, T9).Should().Be(9u, "the interpreter's MULT left LO = 9, written back");
        Gpr(run.Result, S2).Should().Be(0x22222222u);
        Gpr(run.Result, S3).Should().Be(0x33333333u);
        AssertSameCpuAsInterpreter(run.Result, RunInterpreter(words));
    }

    [Fact]
    public void Cop0_TheArtifactsSrIsVisibleToTheInterpreter_AndASyscallInsideTheFallbackIsServiced()
    {
        // SYS(02h) leaves SR with the interrupt bits the kernel contract sets; the artifact owns that SR.
        var main = new Block(Entry);
        main.Emit(Li(T0, Target), [Ori(A0, Zero, 2), MipsEncoding.Syscall(), Jalr(T0), Nop]);
        main.Emit(End());
        var target = new Block(Target);
        target.Emit(Mfc0(S0, 12), Nop, Ori(A0, Zero, 2), MipsEncoding.Syscall(), Mfc0(S1, 12), Nop, Jr(Ra), Nop);
        var words = Image(main, target);
        using var dir = new TempDirectory();

        var run = RunArtifact(words, dir, On);

        run.Result.State.Should().Be(TitleExecutionState.Completed);
        Gpr(run.Result, S0).Should().NotBe(0u, "the artifact's SR was seeded into the interpreter's CP0");
        var reference = RunInterpreter(words);
        Gpr(run.Result, S0).Should().Be(Gpr(reference, S0));
        Gpr(run.Result, S1).Should().Be(Gpr(reference, S1), "a SYSCALL inside the fallback is serviced exactly as in the interpreter");
    }

    // Issue #789: a serviced SYSCALL completes (execution resumes at EPC + 4) and costs one unit of device time, so it is
    // a retired instruction. The fallback's count must match the instructions the interpreter executed: ori, syscall,
    // addiu, jr, and the delay-slot nop, five in all.
    [Fact]
    public void ASyscallServicedInsideTheFallback_IsCountedAsRetired()
    {
        var main = new Block(Entry);
        main.Emit(Li(T0, Target), [Jalr(T0), Nop]);
        main.Emit(End());
        var target = new Block(Target);
        target.Emit(Ori(A0, Zero, 2), MipsEncoding.Syscall(), Addiu(S2, S2, 1), Jr(Ra), Nop);
        var words = Image(main, target);
        using var dir = new TempDirectory();

        var run = RunArtifact(words, dir, On);

        run.Result.State.Should().Be(TitleExecutionState.Completed);
        run.Evidence!.Targets.Single().Instructions.Should().Be(5UL,
            "the serviced SYSCALL is one of the five instructions the fallback executes and retires");
    }

    [Fact]
    public void RepeatedLegitimateFallback_TheSameTargetIsEnteredEveryIteration_WithStateCarriedBothWays()
    {
        var main = new Block(Entry);
        main.Emit(Li(T0, Target), Li(T2, Data), [Addiu(S0, Zero, 0)]);
        var loop = main.Here;
        main.Emit(Jalr(T0), Nop, Addiu(S0, S0, 1), Sltiu(T1, S0, 5));
        main.Emit(MipsEncoding.Branch(0x05, (byte)T1, 0, main.Here, loop), Nop, Lw(S1, T2, 0), Nop);
        main.Emit(End());
        var target = new Block(Target);
        target.Emit(Lw(T4, T2, 0), Nop, Addiu(T4, T4, 1), Sw(T4, T2, 0), Jr(Ra), Nop);
        var words = Image(main, target);
        using var dir = new TempDirectory();

        var run = RunArtifact(words, dir, On);

        Gpr(run.Result, S0).Should().Be(5u);
        Gpr(run.Result, S1).Should().Be(5u, "the counter the interpreter increments persists across five handoffs");
        var entry = run.Evidence!.Targets.Should().ContainSingle().Subject;
        entry.Target.Should().Be(Target);
        entry.Entries.Should().Be(5, "a repeated target is legitimate: there is no per-PC limit");
        run.Evidence.Transitions.Should().Be(5);
        run.Evidence.Returns.Should().Be(5);
        AssertSameCpuAsInterpreter(run.Result, RunInterpreter(words));
    }

    // ---- fail-closed: ineligible targets ---------------------------------------------------------------------------

    private static (uint[] Words, uint Jump) JumpTo(uint destination, params uint[][] beforeJump)
    {
        var main = new Block(Entry);
        main.Emit(Li(T0, destination), Li(T2, Data));
        main.Emit(beforeJump);
        main.Emit(Jalr(T0), Nop);
        main.Emit(End());
        return (Image(main), destination);
    }

    [Theory]
    [InlineData(0x80001102u, "an unaligned PC", MixedFallbackDiagnostics.IneligibleUnaligned)]
    [InlineData(0x80003000u, "a PC outside the text image", MixedFallbackDiagnostics.IneligibleOutOfImage)]
    [InlineData(0x80000300u, "a PC below the text image", MixedFallbackDiagnostics.IneligibleOutOfImage)]
    public void IneligibleTargets_FailClosed_AsTheUnresolvedTransferTheyAlwaysWere(uint destination, string why, string expectedReason)
    {
        var (words, _) = JumpTo(destination);
        using var dir = new TempDirectory();

        var run = RunArtifact(words, dir, On);

        run.Result.State.Should().Be(TitleExecutionState.UnsupportedTransfer, why);
        run.Result.DiagnosticCode.Should().Be("UNRESOLVED_TRANSFER");
        run.Result.FinalSnapshot!.PC.Should().Be(destination);
        run.Evidence!.Transitions.Should().Be(0, "nothing was handed to the interpreter");
        // Issue #763: the host records why the transfer was declined, so the stop is identifiable.
        run.DeclinedTransfer.Should().NotBeNull();
        run.DeclinedTransfer!.TargetPc.Should().Be(destination);
        run.DeclinedTransfer.FallbackAttempted.Should().BeTrue();
        run.DeclinedTransfer.RejectReason.Should().Be(expectedReason);
    }

    [Fact]
    public void HleBiosReturn_ToUncompiledImageCode_IsRunByTheFallback_LikeTheInterpreter()
    {
        // Issue #763: A0:putchar returns to $ra through the host, and $ra lies in image code the static build never
        // reached. That return is the host's own `jr ra`, a runtime target, so the fallback must run it.
        const uint done = Target + 0x100u;
        var main = new Block(Entry);
        main.Emit(Li(Ra, Target), Li(T1, BiosHleRuntime.PutCharFunction), Li(A0, 0x41u), Li(T0, BiosJumpTables.A0VectorAddress));
        // A never-taken branch makes `done` a statically compiled block, the clean entry the fallback returns to.
        main.Emit(MipsEncoding.Branch(0x05, 0, 0, main.Here, done), Nop);
        main.Emit(Jr(T0), Nop);
        var tail = new Block(Target);
        tail.Emit(Ori(S1, Zero, 0x77), MipsEncoding.Jump(done), Nop);
        var doneBlock = new Block(done);
        doneBlock.Emit(End());
        var words = Image(main, tail, doneBlock);
        using var dir = new TempDirectory();

        var run = RunArtifact(words, dir, On);

        run.Result.State.Should().Be(TitleExecutionState.Completed, run.Result.DiagnosticMessage);
        Gpr(run.Result, S1).Should().Be(0x77u, "the uncompiled return point ran");
        run.Evidence!.Transitions.Should().Be(1, "the return point was handed to the interpreter once");
        run.DeclinedTransfer!.TargetPc.Should().Be(0u, "the only declined transfer is the program's own end marker");
        AssertSameCpuAsInterpreter(run.Result, RunInterpreter(words));
    }

    // Issue #773: the mismatch report only observes. Off and on, the same program ends in the same state; the on run records
    // the handoff at a RAM-placed PC that had one version the RAM did not match.
    [Fact]
    public void LoadedCodeMismatchDiagnostics_OffAndOn_EndInTheSameState()
    {
        const uint done = Target + 0x100u;
        // The program overwrites the RAM at Target with code no version holds, then jumps there: the artifact refuses every
        // version at that handoff, so the diagnostics have something to record.
        var main = new Block(Entry);
        main.Emit(Li(T0, Target), Li(T1, Ori(S1, Zero, 0x55)), [Sw(T1, T0, 0)], Li(T1, MipsEncoding.Jump(done)), [Sw(T1, T0, 4)]);
        // Register-indirect, so the static build does not reach Target and the image version stays a RAM-placed one.
        main.Emit(Li(T1, Nop), [Sw(T1, T0, 8)], [Jr(T0)], [Nop]);
        var tail = new Block(Target);
        tail.Emit(Ori(S1, Zero, 0x77), MipsEncoding.Jump(done), Nop);
        var doneBlock = new Block(done);
        doneBlock.Emit(End());
        var words = Image(main, tail, doneBlock);
        var tailStart = (int)((Target - Entry) / 4);
        var ramWords = words[tailStart..(tailStart + 3)];
        // Two RAM-placed images at Target. The program overwrites Target with other code, so neither version matches the RAM
        // at the handoff: both are refused and recorded.
        var otherWords = new[] { Ori(S1, Zero, 0x99), MipsEncoding.Jump(done), Nop };
        var loaded = new LoadedCodeTable([
            ReachableProgramBuilder.BuildLoadedImage(Target, otherWords, [Target], new HashSet<uint>()),
            ReachableProgramBuilder.BuildLoadedImage(Target, ramWords, [Target], new HashSet<uint>()),
        ]);
        using var offDir = new TempDirectory();
        using var onDir = new TempDirectory();

        var off = RunArtifact(words, offDir, On, loadedCode: loaded);
        var diagnostics = new LoadedCodeMismatchDiagnostics();
        var on = RunArtifact(words, onDir, On, loadedCode: loaded, loadedCodeMismatch: diagnostics);

        on.Result.State.Should().Be(off.Result.State);
        on.Result.FinalSnapshot!.Gpr.Should().Equal(off.Result.FinalSnapshot!.Gpr);
        Gpr(on.Result, S1).Should().Be(Gpr(off.Result, S1));
        on.Evidence!.Transitions.Should().Be(off.Evidence!.Transitions);
        Gpr(on.Result, S1).Should().Be(0x55u, "the code the program wrote at Target ran");
        diagnostics.TotalObservations.Should().BeGreaterThan(0UL, "the handoff at Target was observed");
        diagnostics.DistinctRecords.Should().Be(2, "both versions at Target were compared and refused");
    }

    [Fact]
    public void PatchedBiosTarget_ToUncompiledImageCode_StaysFailClosed()
    {
        // Issue #763: a guest-patched A0 jump-table entry is host-resolved but is not the host's `jr ra`; the artifact has no
        // block there and the fallback must not claim it, exactly as before the host-resume marker existed.
        const byte Unregistered = 0x10;
        var main = new Block(Entry);
        main.Emit(Li(T2, BiosJumpTables.EntryAddress(BiosCallFamily.A0, Unregistered)), Li(T3, Target));
        main.Emit(Sw(T3, T2, 0));
        main.Emit(Li(Ra, Entry), Li(T1, Unregistered), Li(T0, BiosJumpTables.A0VectorAddress));
        main.Emit(Jr(T0), Nop);
        var tail = new Block(Target);
        tail.Emit(Ori(S1, Zero, 0x77));
        tail.Emit(End());
        using var dir = new TempDirectory();

        var run = RunArtifact(Image(main, tail), dir, On);

        run.Result.State.Should().Be(TitleExecutionState.UnsupportedTransfer);
        run.Result.FinalSnapshot!.PC.Should().Be(Target);
        run.Evidence!.Transitions.Should().Be(0, "nothing was handed to the interpreter");
        run.DeclinedTransfer.Should().NotBeNull();
        run.DeclinedTransfer!.RejectReason.Should().Be(MixedFallbackDiagnostics.IneligibleNotIndirect);
    }

    [Fact]
    public void RamGeneratedCode_IsNotExecutedByTheFallback()
    {
        // The guest builds a "JR $ra" in RAM and jumps to it: arbitrary RAM execution is not a fallback case.
        var (words, _) = JumpTo(Data, Li(T3, Jr(Ra)), [Sw(T3, T2, 0), Sw(Zero, T2, 4)]);
        using var dir = new TempDirectory();

        var run = RunArtifact(words, dir, On);

        run.Result.State.Should().Be(TitleExecutionState.UnsupportedTransfer);
        run.Result.FinalSnapshot!.PC.Should().Be(Data);
        run.Evidence!.Transitions.Should().Be(0);
    }

    [Fact]
    public void ExceptionVector_IsNeverTakenByTheFallback()
    {
        var (words, _) = JumpTo(0x80000080u);
        using var dir = new TempDirectory();

        var run = RunArtifact(words, dir, On);

        run.Result.DiagnosticCode.Should().Be("ARTIFACT_EXCEPTION_VECTOR_UNHANDLED");
        run.Evidence!.Transitions.Should().Be(0);
    }

    [Fact]
    public void BiosVector_IsNeverTakenByTheFallback()
    {
        // B0:7F is no registered service: the Runtime's own diagnostic stands, not a fallback.
        var (words, _) = JumpTo(0xB0u, Li(T1, 0x7Fu));
        using var dir = new TempDirectory();

        var run = RunArtifact(words, dir, On);

        run.Result.DiagnosticCode.Should().Be("BIOS_HLE_UNSUPPORTED_CALL");
        run.Evidence!.Transitions.Should().Be(0);
    }

    [Fact]
    public void ReturnFromException_ThroughTheProductionGeneratedHost_ResumesAtTheSavedEpc_LikeTheInterpreter()
    {
        // The production generated-host engine (not the differential harness) drives the real
        // artifact: its CpuState replacement has to restore the machine and continue at the saved
        // EPC exactly as the interpreter's shared ApplyTo does. The program seeds the current TCB
        // into guest RAM the way the kernel's own save would, then reaches B0:17.
        const uint PcbAddress = BiosExceptionCompletion.ProcessControlBlockPointerAddress;
        const uint Pcb = 0x0000E100u;
        const uint Tcb = 0x0000E200u;
        const uint Epc = 0x80001100u;
        const uint TcbRegisters = 0x08u;
        const uint RegOffset = 4u;
        const uint TailMarker = 0x77u;
        const uint RaMarker = 0xAAu;
        const uint SavedSlot2 = 0x1022u;
        const uint SavedS0 = 0x00C0FFEEu;
        const uint SavedS1 = 0x1121u;
        const uint SavedSp = 0x0000FFFFu;
        const uint SavedRa = 0xDEAD0001u;
        const uint SavedHi = 0x11111111u;
        const uint SavedLo = 0x22222222u;
        const uint SavedSr = 0x40000404u;

        var main = new Block(Entry);
        // PCB pointer -> Pcb -> Tcb, then the saved context (Tcb layout: psx-spx control-blocks).
        main.Emit(Li(T2, PcbAddress));
        main.Emit(Li(T3, Pcb));
        main.Emit(Sw(T3, T2, 0));
        main.Emit(Li(T2, Pcb));
        main.Emit(Li(T3, Tcb));
        main.Emit(Sw(T3, T2, 0));
        main.Emit(Li(T2, Tcb));
        main.Emit(Li(T3, SavedSlot2));
        main.Emit(Sw(T3, T2, (short)(TcbRegisters + (uint)R3000aRegister.V0 * RegOffset)));
        main.Emit(Li(T3, SavedS0));
        main.Emit(Sw(T3, T2, (short)(TcbRegisters + (uint)R3000aRegister.S0 * RegOffset)));
        main.Emit(Li(T3, SavedS1));
        main.Emit(Sw(T3, T2, (short)(TcbRegisters + (uint)R3000aRegister.S1 * RegOffset)));
        main.Emit(Li(T3, 0u));
        main.Emit(Sw(T3, T2, (short)(TcbRegisters + (uint)R3000aRegister.S2 * RegOffset)));
        main.Emit(Li(T3, SavedSp));
        main.Emit(Sw(T3, T2, (short)(TcbRegisters + (uint)R3000aRegister.Sp * RegOffset)));
        main.Emit(Li(T3, SavedRa));
        main.Emit(Sw(T3, T2, (short)(TcbRegisters + (uint)R3000aRegister.Ra * RegOffset)));
        main.Emit(Li(T3, 0x0BAD0BADu));
        main.Emit(Sw(T3, T2, (short)(TcbRegisters + (uint)R3000aRegister.K0 * RegOffset)));
        main.Emit(Li(T3, Epc));
        main.Emit(Sw(T3, T2, 0x88));
        main.Emit(Li(T3, SavedHi));
        main.Emit(Sw(T3, T2, 0x8C));
        main.Emit(Li(T3, SavedLo));
        main.Emit(Sw(T3, T2, 0x90));
        main.Emit(Li(T3, SavedSr));
        main.Emit(Sw(T3, T2, 0x94));
        // Reach B0:17 the same way the differential tests do: a call to the B0 trampoline.
        main.Emit(Li(T1, BiosHleRuntime.ReturnFromExceptionFunction));
        main.Emit(Li(T0, BiosJumpTables.B0VectorAddress));
        main.Emit(Jalr(T0), Nop);
        // The $ra path marks a mistake: this call continues at the saved EPC, never at the call site.
        main.Emit(Ori(S2, Zero, (ushort)RaMarker));
        main.Emit(MipsEncoding.Jump(Epc), Nop);
        var epc = new Block(Epc);
        epc.Emit(Ori(S1, Zero, (ushort)TailMarker));
        epc.Emit(End());
        var words = Image(main, epc);
        using var dir = new TempDirectory();

        var run = RunArtifact(words, dir, On);

        run.Result.State.Should().Be(TitleExecutionState.Completed);
        Gpr(run.Result, S1).Should().Be(TailMarker, "the restored EPC's own instruction ran");
        Gpr(run.Result, S2).Should().Be(0u, "control resumed at the EPC, never at the call site's $ra");
        Gpr(run.Result, V0).Should().Be(SavedSlot2);
        Gpr(run.Result, S0).Should().Be(SavedS0);
        Gpr(run.Result, R3000aRegister.Sp).Should().Be(SavedSp);
        Gpr(run.Result, R3000aRegister.Ra).Should().Be(SavedRa);
        Gpr(run.Result, R3000aRegister.K0).Should().Be(0u, "a restore never overwrites the live $k0");
        AssertSameCpuAsInterpreter(run.Result, RunInterpreter(words));
    }

    // ---- budgets and unsupported states -----------------------------------------------------------------------------

    [Fact]
    public void SegmentBudget_AnEndlessFallbackStopsWithItsOwnDiagnostic()
    {
        var main = new Block(Entry);
        main.Emit(Li(T0, Target), [Jalr(T0), Nop]);
        main.Emit(End());
        var target = new Block(Target);
        target.Emit(MipsEncoding.Jump(Target), Nop);
        using var dir = new TempDirectory();

        var run = RunArtifact(Image(main, target), dir, new MixedFallbackOptions(SegmentInstructionBudget: 1000, MaxTransitions: 10));

        run.Result.State.Should().Be(TitleExecutionState.RuntimeFailure);
        run.Result.DiagnosticCode.Should().Be(MixedFallbackDiagnostics.SegmentBudgetExhausted);
        run.Result.DiagnosticMessage.Should().Contain("1000");
        run.Evidence!.LastEntryPc.Should().Be(Target, "the fallback entry is known even when the segment fails");
        run.Evidence.LastReturnPc.Should().BeNull("a segment that exhausted its budget never returned to the artifact");
    }

    [Fact]
    public void TransitionBudget_TooManyHandoffsStopWithTheirOwnDiagnostic()
    {
        var main = new Block(Entry);
        main.Emit(Li(T0, Target), [Addiu(S0, Zero, 0)]);
        var loop = main.Here;
        main.Emit(Jalr(T0), Nop, Addiu(S0, S0, 1), Sltiu(T1, S0, 5));
        main.Emit(MipsEncoding.Branch(0x05, (byte)T1, 0, main.Here, loop), Nop);
        main.Emit(End());
        var target = new Block(Target);
        target.Emit(Jr(Ra), Nop);
        using var dir = new TempDirectory();

        var run = RunArtifact(Image(main, target), dir, new MixedFallbackOptions(MaxTransitions: 3));

        run.Result.State.Should().Be(TitleExecutionState.RuntimeFailure);
        run.Result.DiagnosticCode.Should().Be(MixedFallbackDiagnostics.TransitionBudgetExhausted);
        run.Evidence!.Transitions.Should().Be(3);
    }

    [Fact]
    public void AnExceptionTheLoopDoesNotService_FailsClosedWithItsOwnDiagnostic()
    {
        var main = new Block(Entry);
        main.Emit(Li(T0, Target), [Jalr(T0), Nop]);
        main.Emit(End());
        var target = new Block(Target);
        target.Emit(MipsEncoding.Break(), Nop);
        using var dir = new TempDirectory();

        var run = RunArtifact(Image(main, target), dir, On);

        run.Result.State.Should().Be(TitleExecutionState.RuntimeFailure);
        run.Result.DiagnosticCode.Should().Be(MixedFallbackDiagnostics.ExceptionUnsupported);
        run.Evidence!.Returns.Should().Be(0, "nothing was written back to the artifact");
    }

    // ---- evidence survives a later failure ---------------------------------------------------------------------------

    [Fact]
    public void Evidence_SurvivesALaterFailureThatReturnsEarly()
    {
        // A handoff happens and returns; the artifact then reads the BIOS-ROM window, which the Runtime refuses
        // (ARTIFACT_MMIO_UNSUPPORTED, an early-return path of RunSegment). The evidence of the earlier handoff must not be lost.
        var main = new Block(Entry);
        main.Emit(Li(T0, Target), [Jalr(T0), Nop]);
        main.Emit(Li(T1, 0xBFC00000u), [Lw(T2, T1, 0), Nop]);
        main.Emit(End());
        var target = new Block(Target);
        target.Emit(Addiu(S0, Zero, 3), Jr(Ra), Nop);
        using var dir = new TempDirectory();

        var run = RunArtifact(Image(main, target), dir, On);


        run.Result.DiagnosticCode.Should().Be("ARTIFACT_MMIO_UNSUPPORTED", "the run ended on the refused MMIO access, an early return");
        run.Evidence.Should().NotBeNull("the handoff before the failure is still reported");
        run.Evidence!.Transitions.Should().Be(1);
        run.Evidence.Returns.Should().Be(1);
        run.Evidence.Targets.Should().ContainSingle().Which.Target.Should().Be(Target);
        run.Timings.Should().NotBeNull();
    }

    // ---- the clean-boundary rule, against the pure interpreter -------------------------------------------------------

    [Fact]
    public void CleanBoundary_ABlockEntryInABranchDelaySlot_IsNotAReturnPoint()
    {
        // 0x80001104 is a compiled block entry (a never-taken static branch targets it) AND the delay slot of the
        // uncompiled branch at Target. Returning there mid-branch would run the compiled block and skip the branch.
        var delaySlot = Target + 4;
        var branchTarget = 0x80001180u;
        var main = new Block(Entry);
        main.Emit(Li(T0, Target), [MipsEncoding.Branch(0x05, 0, 0, Entry + 8, delaySlot)]);
        main.Emit(Nop);
        main.Emit(Jalr(T0), Nop, Addiu(S6, Zero, 0x66));
        main.Emit(End());
        var branch = new Block(Target);
        branch.Emit(MipsEncoding.Branch(0x04, 0, 0, Target, branchTarget));
        var sharedBlock = new Block(delaySlot); // the delay-slot word is also the compiled block's first instruction
        sharedBlock.Emit(Addiu(S0, Zero, 1), Addiu(S1, Zero, 2), Jr(Zero), Nop);
        var landing = new Block(branchTarget);
        landing.Emit(Addiu(S2, Zero, 0x77), Jr(Ra), Nop);
        var words = Image(main, branch, sharedBlock, landing);
        using var dir = new TempDirectory();

        var run = RunArtifact(words, dir, On);

        Gpr(run.Result, S0).Should().Be(1u, "the delay-slot instruction ran");
        Gpr(run.Result, S2).Should().Be(0x77u, "the branch was taken after its delay slot");
        Gpr(run.Result, S1).Should().Be(0u, "the compiled block at the delay slot did not run");
        Gpr(run.Result, S6).Should().Be(0x66u, "the guest returned to the artifact at the return site");
        AssertSameCpuAsInterpreter(run.Result, RunInterpreter(words));
    }

    [Fact]
    public void CleanBoundary_ABlockEntryInALoadDelaySlot_IsNotAReturnPoint()
    {
        // The word after the uncompiled LW is a compiled block entry: it must read the old register (load delay), and
        // the load must still land. Returning there would drop the in-flight load.
        var loadAt = 0x80001200u;
        var main = new Block(Entry);
        main.Emit(Li(T0, loadAt), Li(T2, Data), Li(T3, 0x99u), Li(V0, 0x55u));
        main.Emit(Sw(T3, T2, 0), MipsEncoding.Branch(0x05, 0, 0, main.Here + 4, loadAt + 4), Nop);
        main.Emit(Jalr(T0), Nop, Addiu(S6, Zero, 0x66));
        main.Emit(End());
        var load = new Block(loadAt);
        load.Emit(Lw(V0, T2, 0));
        var sharedBlock = new Block(loadAt + 4);
        sharedBlock.Emit(Addu(S1, V0, Zero), Jr(Ra), Nop);
        var words = Image(main, load, sharedBlock);
        using var dir = new TempDirectory();

        var run = RunArtifact(words, dir, On);

        Gpr(run.Result, S1).Should().Be(0x55u, "the delay-slot instruction saw the old value");
        Gpr(run.Result, V0).Should().Be(0x99u, "the load was committed, not lost at the handoff");
        Gpr(run.Result, S6).Should().Be(0x66u);
        AssertSameCpuAsInterpreter(run.Result, RunInterpreter(words));
    }

    [Fact]
    public void CleanBoundary_ABranchRightAfterALoad_KeepsBothDelaySlotsIntact()
    {
        var at = 0x80001300u;
        var landing = 0x80001380u;
        var main = new Block(Entry);
        main.Emit(Li(T0, at), Li(T2, Data), Li(T3, 0x99u), Li(V0, 0x55u));
        main.Emit(Sw(T3, T2, 0), MipsEncoding.Branch(0x05, 0, 0, main.Here + 4, at + 8), Nop);
        main.Emit(Jalr(T0), Nop, Addiu(S6, Zero, 0x66));
        main.Emit(End());
        var load = new Block(at);
        load.Emit(Lw(V0, T2, 0), MipsEncoding.Branch(0x04, 0, 0, at + 4, landing));
        var sharedBlock = new Block(at + 8); // the branch's delay slot is also a compiled block entry
        sharedBlock.Emit(Addu(S2, V0, Zero), Jr(Zero), Nop);
        var land = new Block(landing);
        land.Emit(Jr(Ra), Nop);
        var words = Image(main, load, sharedBlock, land);
        using var dir = new TempDirectory();

        var run = RunArtifact(words, dir, On);

        Gpr(run.Result, S2).Should().Be(0x99u, "the delay slot of a branch that follows a load sees the loaded value");
        Gpr(run.Result, S6).Should().Be(0x66u, "the guest reached the landing pad and returned to the artifact");
        AssertSameCpuAsInterpreter(run.Result, RunInterpreter(words));
    }
}
