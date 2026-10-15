using System.Text.Json;
using FluentAssertions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Diagnostics;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Tests.Recompiler;
using Xunit;
using static PSXRecomp.Tests.Infrastructure.MixedFallbackTestSupport;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #774 (B-5): the production generated host, traced, run as a child process on synthetic programs. The joined
/// native + fallback sequence is checked against the pure interpreter one stretch at a time: each stretch's entry pc
/// must be the interpreter's fetch at its first instruction, and its next pc the fetch one past its last.
/// </summary>
/// <remarks>
/// Counting rule used throughout: the interpreter's fetches are the reference positions. A fetch that an interrupt
/// preempts does not retire, so it is counted as a fetch but not as a retirement. Known divergences are pinned by name
/// rather than hidden (see the SYSCALL test).
/// </remarks>
[Test]
public sealed class UnitTraceEndToEndTests
{
    private const uint Target2 = 0x80001200u;
    private const uint Vector = 0x80000080u;

    private sealed record TracedRun(string[] Native, string[] Fallback, MixedRun Run, IReadOnlyList<uint> InterpreterFetches);

    private static uint Pc(string text) => Convert.ToUInt32(text[2..], 16);

    /// <summary>The production host's trace on <paramref name="words"/>, and the interpreter's fetches on the same words.</summary>
    private static TracedRun RunTraced(uint[] words, TempDirectory dir)
    {
#pragma warning disable AARC003
        var tracePath = Path.Combine(dir.FullPath, "unit.jsonl");
        var program = ReachableProgramBuilder.Build(Entry, words, Entry, []);
        MixedRun run;
        using (UnitTraceScope.Begin(tracePath))
        {
            using var engine = new RecompiledHostExecutionEngine(
                program, words, Entry, new GeneratedHostBuildService(), dir.FullPath,
                (reader, writer) => new BiosHleRuntime(new NullSink(), reader, writer),
                mixedFallback: new MixedFallbackOptions());
            var result = new ExecutionOrchestrator().Execute(engine, new ExitAtZeroHandoff(), Request(1000));
            run = new MixedRun(result, engine.FallbackEvidence, engine.FallbackTimings, engine.LastDeclinedTransfer);
        }

        var fetches = new List<uint>();
        using (var interpreter = new InterpreterTitleExecutionEngine(
            words, Entry, (reader, writer) => new BiosHleRuntime(new NullSink(), reader, writer)))
        {
            interpreter.FetchObserver = pc =>
            {
                if (pc != 0u) fetches.Add(pc);
            };
            new ExecutionOrchestrator().Execute(interpreter, new ExitAtZeroHandoff(), Request(1000));
        }

        var traced = new TracedRun(
            File.ReadAllLines(tracePath),
            File.ReadAllLines(UnitTraceScope.FallbackPathFor(tracePath)),
            run,
            fetches);
#pragma warning restore AARC003
        return traced;
    }

    /// <summary>
    /// Every stretch with instructions must start and end at the interpreter's positions: the entry pc at its first
    /// fetch, the next pc at the fetch one past its last. Host-only transfers complete nothing and are not checked.
    /// </summary>
    private static void AssertStretchesMatchTheInterpreter(UnitTraceJoin join, IReadOnlyList<uint> fetches)
    {
        join.ComparableUpToRetired.Should().Be((ulong)fetches.Count,
            "the joined run must retire exactly the instructions the interpreter fetched");
        foreach (var stretch in join.Segments.Where(static s => s.GlobalEnd > s.GlobalStart))
        {
            fetches[(int)stretch.GlobalStart].Should().Be(Pc(stretch.EntryPc),
                $"the stretch at {stretch.GlobalStart} ({stretch.Source}, handoff {stretch.Handoff}) must start at the interpreter's fetch");
            if (stretch.GlobalEnd < (ulong)fetches.Count)
            {
                fetches[(int)stretch.GlobalEnd].Should().Be(Pc(stretch.NextPc),
                    $"the stretch ending at {stretch.GlobalEnd} ({stretch.Source}, handoff {stretch.Handoff}) must continue at the interpreter's fetch");
            }
        }
    }

    /// <summary>native -&gt; fallback -&gt; native -&gt; fallback -&gt; native, then the exit transfer served by the host.</summary>
    private static uint[] ThreeStretchProgram()
    {
        var main = new Block(Entry);
        main.Emit(Li(T0, Target), [Jalr(T0), Nop]);
        main.Emit(Addiu(S0, S0, 1));
        main.Emit(Li(T0, Target2), [Jalr(T0), Nop]);
        main.Emit(Addiu(S1, S1, 1));
        main.Emit(End());
        var first = new Block(Target);
        first.Emit(Addiu(S2, S2, 1), Jr(Ra), Nop);
        var second = new Block(Target2);
        second.Emit(Addiu(S3, S3, 1), Addiu(S3, S3, 1), Jr(Ra), Nop);
        return Image(main, first, second);
    }

    [Fact]
    public void MultipleHandoffs_JoinInExecutionOrder_AndEveryStretchMatchesTheInterpreter()
    {
        using var dir = new TempDirectory();
        var traced = RunTraced(ThreeStretchProgram(), dir);
        traced.Run.Result.State.Should().Be(TitleExecutionState.Completed);

        var join = UnitTraceJoiner.Join(traced.Native, traced.Fallback);
        join.Status.Should().Be(UnitTraceJoinStatus.Complete, join.Reason ?? string.Join("; ", join.Errors));

        // Handoff identity: the transfers carry handoffs 1, 2 and 3 in order.
        var transfers = join.Segments.Where(static s => s.Source != UnitTraceSegmentSource.Native).ToList();
        transfers.Select(static s => s.Handoff).Should().Equal(1U, 2U, 3U);
        transfers.Select(static s => s.Source).Should().Equal(
            UnitTraceSegmentSource.Fallback, UnitTraceSegmentSource.Fallback, UnitTraceSegmentSource.Host);
        transfers[0].Status.Should().Be("returned");
        transfers[1].Status.Should().Be("returned");

        // The host-only exit transfer retires nothing.
        transfers[2].GlobalEnd.Should().Be(transfers[2].GlobalStart, "the host served the exit with no guest instruction");

        // The interpreter, stretch by stretch: entry and next pc, and the total.
        AssertStretchesMatchTheInterpreter(join, traced.InterpreterFetches);

        // Native and fallback stretches meet with no gap and no overlap, in the order they ran.
        for (var i = 1; i < join.Segments.Count; i++)
        {
            join.Segments[i].GlobalStart.Should().Be(join.Segments[i - 1].GlobalEnd, $"segment {i} must follow segment {i - 1} with no gap");
        }
    }

    [Fact]
    public void MultipleHandoffs_TruncatingEitherTraceAtAnyLine_NeverJoinsAsComplete()
    {
        using var dir = new TempDirectory();
        var traced = RunTraced(ThreeStretchProgram(), dir);
        var full = UnitTraceJoiner.Join(traced.Native, traced.Fallback);
        full.Status.Should().Be(UnitTraceJoinStatus.Complete);

        // A child killed at any line, a parent cut at any line, and a torn last line must never look like a whole run.
        for (var cut = 0; cut < traced.Native.Length; cut++)
        {
            AssertCutIsNotComplete(UnitTraceJoiner.Join(traced.Native[..cut], traced.Fallback), full, $"native cut to {cut} lines");
        }

        for (var cut = 0; cut < traced.Fallback.Length; cut++)
        {
            AssertCutIsNotComplete(UnitTraceJoiner.Join(traced.Native, traced.Fallback[..cut]), full, $"fallback cut to {cut} lines");
        }

        var lastNative = traced.Native[^1];
        var torn = traced.Native[..^1].Append(lastNative[..(lastNative.Length / 2)]).ToArray();
        AssertCutIsNotComplete(UnitTraceJoiner.Join(torn, traced.Fallback), full, "native last line torn in half");
    }

    private static void AssertCutIsNotComplete(UnitTraceJoin cut, UnitTraceJoin full, string label)
    {
        cut.Status.Should().NotBe(UnitTraceJoinStatus.Complete, $"{label} must not be reported as a whole run");
        if (cut.Status != UnitTraceJoinStatus.Partial)
        {
            return;
        }

        // A partial result is a verified prefix of the whole run, and nothing past it may be compared. A cut that removes only
        // the end line keeps every record, so its prefix may reach the whole run's total without being complete.
        cut.ComparableUpToRetired.Should().BeLessThanOrEqualTo(full.ComparableUpToRetired, label);
        cut.Segments.Should().Equal(full.Segments.Take(cut.Segments.Count), label);
    }

    /// <summary>
    /// A SYSCALL in native code: the unit is credited with the instruction and resumes at the next one. The interpreter
    /// fetches the same instruction, so every stretch boundary still agrees.
    /// </summary>
    [Fact]
    public void ServicedSyscallInNativeCode_IsCreditedOnce_AndResumesAtTheNextInstruction()
    {
        var main = new Block(Entry);
        main.Emit(Ori(A0, Zero, 2), MipsEncoding.Syscall(), Addiu(S0, S0, 1));
        main.Emit(Li(T0, Target), [Jalr(T0), Nop]);
        main.Emit(Addiu(S1, S1, 1));
        main.Emit(End());
        var callee = new Block(Target);
        callee.Emit(Addiu(S2, S2, 1), Jr(Ra), Nop);
        using var dir = new TempDirectory();
        var traced = RunTraced(Image(main, callee), dir);
        traced.Run.Result.State.Should().Be(TitleExecutionState.Completed);

        var join = UnitTraceJoiner.Join(traced.Native, traced.Fallback);
        join.Status.Should().Be(UnitTraceJoinStatus.Complete, join.Reason ?? string.Join("; ", join.Errors));
        var syscall = traced.Native.Select(static line => JsonDocument.Parse(line).RootElement)
            .Single(static r => r.GetProperty("kind").GetString() == "unit" && r.GetProperty("exception").GetInt32() == 1);
        syscall.GetProperty("entry").GetString().Should().Be("0x80001004");
        syscall.GetProperty("next").GetString().Should().Be("0x80001008", "a serviced SYSCALL resumes at pc + 4");
        syscall.GetProperty("retired").GetInt32().Should().Be(1);
        AssertStretchesMatchTheInterpreter(join, traced.InterpreterFetches);
    }

    /// <summary>
    /// KNOWN DIVERGENCE, pinned and not fixed (B-6a decision: the serviced SYSCALL retires; the fix is #789). The same kind of serviced SYSCALL is credited when it runs in native
    /// code (previous test) and not credited when it runs in the fallback: the interpreter's retirement loop skips its count
    /// after a serviced SYSCALL, while the interpreter still fetches it. Changing either count changes device time
    /// (host_retired feeds the scheduler), so the reconciliation is a runtime decision for Phase B-6 / Phase C.
    /// </summary>
    [Fact]
    public void KnownDivergence_ServicedSyscallInTheFallback_IsFetchedButNotRetired()
    {
        var main = new Block(Entry);
        main.Emit(Li(T0, Target), [Jalr(T0), Nop]);
        main.Emit(Addiu(S1, S1, 1));
        main.Emit(End());
        var callee = new Block(Target);
        callee.Emit(Ori(A0, Zero, 2), MipsEncoding.Syscall(), Addiu(S2, S2, 1), Jr(Ra), Nop);
        using var dir = new TempDirectory();
        var traced = RunTraced(Image(main, callee), dir);
        traced.Run.Result.State.Should().Be(TitleExecutionState.Completed);

        var join = UnitTraceJoiner.Join(traced.Native, traced.Fallback);
        join.Status.Should().Be(UnitTraceJoinStatus.Complete, join.Reason ?? string.Join("; ", join.Errors));
        var fallback = join.Segments.Single(static s => s.Source == UnitTraceSegmentSource.Fallback);

        traced.InterpreterFetches.Should().Contain(0x80001104u, "the SYSCALL is fetched and serviced by the interpreter");
        (fallback.GlobalEnd - fallback.GlobalStart).Should().Be(4UL, "5 fetches, of which the serviced SYSCALL is not retired");
        join.ComparableUpToRetired.Should().Be((ulong)traced.InterpreterFetches.Count - 1,
            "the joined run is one retirement short of the interpreter's fetch count: exactly the serviced SYSCALL");
    }

    /// <summary>
    /// A timer IRQ taken at a boundary (deadline 2) or inside a fallback segment (deadline 4). The interpreter's fetch list
    /// ends with the instruction the interrupt preempted: fetched, not retired. The fallback stretch that the interrupt cut
    /// ends at the vector.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void TimerIrq_ThePreemptedFetchIsNotRetired_AndTheStretchEndsAtTheVector(ushort deadline)
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
        var (native, fallback) = RunTimerIrqTraced(words, dir);
        var join = UnitTraceJoiner.Join(native, fallback);
        join.Status.Should().Be(UnitTraceJoinStatus.Complete, join.Reason ?? string.Join("; ", join.Errors));

        var fetches = new List<uint>();
        using (var interpreter = new InterpreterTitleExecutionEngine(words, Entry, allowRuntimeRamExecution: true))
        {
            interpreter.FetchObserver = pc =>
            {
                if (pc == Vector)
                {
                    interpreter.StopRequested = true;
                    return;
                }

                if (pc != 0u) fetches.Add(pc);
            };
            new ExecutionOrchestrator().Execute(interpreter, null, Request(1000));
        }

        var retired = join.ComparableUpToRetired;
        retired.Should().Be((ulong)fetches.Count - 1, "exactly one fetch was preempted by the interrupt");
        fetches[(int)retired].Should().NotBe(Vector, "the preempted fetch is an instruction, not the vector");
        // The fallback stretch that the interrupt cut ends at the vector. The native stop record that follows is zero-length
        // and its next pc carries no meaning, so it is not checked.
        join.Segments.Single(static s => s.Source == UnitTraceSegmentSource.Fallback).NextPc.Should().Be($"0x{Vector:X8}",
            "the interrupted fallback stretch ends at the vector");
    }

    /// <summary>
    /// A load-heavy program whose timer edge can land between the two instructions of one fused block (deadline 4).
    /// </summary>
    private static uint[] LoadProgram(ushort deadline)
    {
        var main = new Block(Entry);
        main.Emit(Ori(T1, Zero, 0x401), 0x40896000u); // MTC0 t1,SR: guest-owned IRQ enable
        main.Emit(Li(T0, Target), [MipsEncoding.I(0x0F, (byte)T3, 0, 0x1F80)]);
        main.Emit(Ori(T1, Zero, 1 << 6), Sw(T1, T3, 0x1074));
        main.Emit(Ori(T1, Zero, deadline), Sw(T1, T3, 0x1128));
        main.Emit(Ori(T1, Zero, 0x10), Sw(T1, T3, 0x1124));
        main.Emit(Li(T2, Data), [Lw(T4, T2, 0), Addiu(T5, T4, 1), Lw(T6, T2, 4), Addu(T7, T5, T6), Sw(T7, T2, 8), Lw(T8, T2, 8), Addiu(T9, T8, 1), Nop]);
        main.Emit(End());
        return Image(main);
    }

    /// <summary>The interpreter's fetches on the same timer program, stopped at the vector (the vector is not fetched).</summary>
    private static List<uint> InterpreterFetchesUntilVector(uint[] words)
    {
        var fetches = new List<uint>();
        using var interpreter = new InterpreterTitleExecutionEngine(words, Entry, allowRuntimeRamExecution: true);
        interpreter.FetchObserver = pc =>
        {
            if (pc == Vector)
            {
                interpreter.StopRequested = true;
                return;
            }

            if (pc != 0u) fetches.Add(pc);
        };
        new ExecutionOrchestrator().Execute(interpreter, null, Request(1000));
        return fetches;
    }

    [Fact]
    public void InterruptInsideAFusedBlock_CreditsOnlyTheInstructionsThatCompleted()
    {
        var words = LoadProgram(4);
        using var dir = new TempDirectory();
        var (native, fallback) = RunTimerIrqTraced(words, dir);
        var join = UnitTraceJoiner.Join(native, fallback);
        join.Status.Should().Be(UnitTraceJoinStatus.Complete, join.Reason ?? string.Join("; ", join.Errors));

        // The native unit that was interrupted in its body: it completed one instruction, then the interrupt was taken
        // at the next instruction's boundary. Its next pc is the vector, not the program-order successor.
        var interrupted = native.Select(static line => JsonDocument.Parse(line).RootElement)
            .Single(static r => r.GetProperty("kind").GetString() == "unit" && r.GetProperty("interrupted").GetInt32() == 1);
        interrupted.GetProperty("entry").GetString().Should().Be("0x80001034");
        interrupted.GetProperty("retired").GetInt32().Should().Be(1);
        interrupted.GetProperty("next").GetString().Should().Be($"0x{Vector:X8}", "the in-body interrupt redirects next to the vector");

        var fetches = InterpreterFetchesUntilVector(words);
        var retired = join.ComparableUpToRetired;
        retired.Should().Be((ulong)fetches.Count - 1, "the preempted fetch is counted as a fetch but does not retire");
        fetches[(int)retired - 1].Should().Be(0x80001034u, "the last completed instruction is the unit's entry");
        fetches[(int)retired].Should().Be(0x80001038u, "the preempted instruction is the program-order successor");
    }

    [Fact]
    public void DelaySlotSyscall_IsRefusedByBothEngines_AndNeitherCompletes()
    {
        var main = new Block(Entry);
        main.Emit(Li(T0, Target), [Jalr(T0), MipsEncoding.Syscall()]);
        main.Emit(Addiu(S1, S1, 1));
        main.Emit(End());
        var callee = new Block(Target);
        callee.Emit(Jr(Ra), Nop);
        var words = Image(main, callee);
        using var dir = new TempDirectory();
        var traced = RunTraced(words, dir);
        var reference = RunInterpreter(words);

        traced.Run.Result.State.Should().Be(TitleExecutionState.RuntimeFailure, "a SYSCALL in a delay slot is not serviced");
        reference.State.Should().Be(traced.Run.Result.State);

        var faulting = traced.Native.Select(static line => JsonDocument.Parse(line).RootElement)
            .Single(static r => r.GetProperty("kind").GetString() == "unit" && r.GetProperty("exception").GetInt32() == 1);
        faulting.GetProperty("termination").GetInt32().Should().Be((int)RecompilerIrTerminationReason.Exception);
        faulting.GetProperty("entry").GetString().Should().Be("0x80001008", "the branch is the unit's entry");

        // The delay slot was fetched by both engines. The branch is credited once (both engines execute it), which is the
        // current convention; under the R3000 restart model it would be re-executed, so the convention is open (see docs).
        traced.InterpreterFetches.Should().Contain(0x8000100Cu);
        faulting.GetProperty("retired").GetInt32().Should().Be(1);
    }

    /// <summary>An exception the runtime does not service stops the run; it is not retired, and both engines agree.</summary>
    [Fact]
    public void UnserviceableException_IsNotRetired_AndBothEnginesStop()
    {
        var main = new Block(Entry);
        main.Emit(Addiu(S0, S0, 1), MipsEncoding.Break(), Addiu(S1, S1, 1));
        main.Emit(End());
        using var dir = new TempDirectory();
        var traced = RunTraced(Image(main), dir);
        var reference = RunInterpreter(Image(main));

        traced.Run.Result.State.Should().Be(TitleExecutionState.RuntimeFailure);
        reference.State.Should().Be(traced.Run.Result.State);
        var faulting = traced.Native.Select(static line => JsonDocument.Parse(line).RootElement)
            .Single(static r => r.GetProperty("kind").GetString() == "unit" && r.GetProperty("exception").GetInt32() == 1);
        faulting.GetProperty("entry").GetString().Should().Be("0x80001004", "the BREAK is the faulting unit");
        faulting.GetProperty("retired").GetInt32().Should().Be(0, "the faulting instruction does not retire");
        faulting.GetProperty("termination").GetInt32().Should().Be((int)RecompilerIrTerminationReason.Exception);
    }

    /// <summary>
    /// native SYSCALL -&gt; fallback (with its own SYSCALL) -&gt; native. The native part matches the interpreter boundary by
    /// boundary; the fallback part carries the pinned one-retirement difference, so the joined total is exactly one short.
    /// </summary>
    [Fact]
    public void NativeThenFallbackThenNative_WithASyscallInEachPart_MatchesExceptTheKnownSyscallDifference()
    {
        var main = new Block(Entry);
        main.Emit(Ori(A0, Zero, 2), MipsEncoding.Syscall(), Addiu(S0, S0, 1));
        main.Emit(Li(T0, Target), [Jalr(T0), Nop]);
        main.Emit(Addiu(S1, S1, 1));
        main.Emit(End());
        var callee = new Block(Target);
        callee.Emit(Ori(A0, Zero, 2), MipsEncoding.Syscall(), Addiu(S2, S2, 1), Jr(Ra), Nop);
        using var dir = new TempDirectory();
        var traced = RunTraced(Image(main, callee), dir);
        traced.Run.Result.State.Should().Be(TitleExecutionState.Completed);

        var join = UnitTraceJoiner.Join(traced.Native, traced.Fallback);
        join.Status.Should().Be(UnitTraceJoinStatus.Complete, join.Reason ?? string.Join("; ", join.Errors));
        var fallback = join.Segments.Single(static s => s.Source == UnitTraceSegmentSource.Fallback);
        (fallback.GlobalEnd - fallback.GlobalStart).Should().Be(4UL, "the fallback SYSCALL is not retired (pinned, #789)");
        join.ComparableUpToRetired.Should().Be((ulong)traced.InterpreterFetches.Count - 1);
        var nativeBefore = join.Segments.Where(s => s.Source == UnitTraceSegmentSource.Native && s.GlobalEnd <= fallback.GlobalStart).ToList();
        foreach (var stretch in nativeBefore.Where(static s => s.GlobalEnd > s.GlobalStart))
        {
            traced.InterpreterFetches[(int)stretch.GlobalStart].Should().Be(Pc(stretch.EntryPc));
        }
    }

    private static (string[] Native, string[] Fallback) RunTimerIrqTraced(uint[] words, TempDirectory dir)
    {
#pragma warning disable AARC003
        var tracePath = Path.Combine(dir.FullPath, "unit.jsonl");
        var program = ReachableProgramBuilder.Build(Entry, words, Entry, []);
        using (UnitTraceScope.Begin(tracePath))
        {
            using var host = new RecompiledHostExecutionEngine(
                program, words, Entry, new GeneratedHostBuildService(), dir.FullPath,
                mixedFallback: new MixedFallbackOptions(), guestFirmware: true);
            host.FallbackFetchObserver = (engine, pc) =>
            {
                if (pc == Vector) engine.StopRequested = true;
            };
            new ExecutionOrchestrator().Execute(host, new ExitAtZeroHandoff(), Request(1000));
        }

        var result = (File.ReadAllLines(tracePath), File.ReadAllLines(UnitTraceScope.FallbackPathFor(tracePath)));
#pragma warning restore AARC003
        return result;
    }
}
