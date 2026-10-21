using PSXRecomp.Core.Recompiler;
using System.Buffers.Binary;
using FluentAssertions;
using PSXRecomp.Core;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Runtime;
using Xunit;
using static PSXRecomp.Tests.Infrastructure.MixedFallbackTestSupport;

namespace PSXRecomp.Tests.Execution;

/// <summary>
/// Issue #693: the interpreter half of mixed execution, without an artifact. An engine attached to a host-owned device
/// graph seeds its CPU from the artifact's state, runs until a clean compiled block entry (or a fail-closed stop), and
/// reports exactly the state to write back. The graph's core is stepped, so devices are shared, never copied.
/// </summary>
[Test]
public sealed class InterpreterFallbackEngineTests
{
    private const uint ReturnPc = 0x80001800u;

    private static (PsxDeviceGraph Graph, DeviceScheduler Scheduler, InterpreterTitleExecutionEngine Engine) Attached(uint[] words, Action? beforeFirstStep = null)
    {
        var graph = new PsxDeviceGraph();
        var scheduler = new DeviceScheduler(graph.Core, graph.InterruptControllerAdapter, graph.GpuAdapter, graph.CdRomDevice, graph.CdRomDmaTransfer);
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
        }

        graph.Core.CopyRamFrom(Entry & 0x1FFFFFFFu, bytes);
        var engine = InterpreterTitleExecutionEngine.Attach(
            words, Entry, graph, scheduler, (reader, writer) =>
            {
                beforeFirstStep?.Invoke(); // runs inside each segment, before its first step
                return new BiosHleRuntime(new NullSink(), reader, writer);
            });
        return (graph, scheduler, engine);
    }

    private static FallbackCpuState Entering(uint pc, uint sr = 0, uint cause = 0, uint epc = 0, uint ra = ReturnPc, uint[]? gpr = null)
    {
        var registers = gpr ?? new uint[TitleExecutionRequest.GprCount];
        registers[(int)Ra] = ra;
        return new FallbackCpuState(registers, 0, 0, pc, sr, cause, epc);
    }

    private static readonly IReadOnlySet<uint> Returns = new HashSet<uint> { ReturnPc };

    [Fact]
    public void Returned_StopsBeforeTheCompiledEntry_AndReportsTheStateToWriteBack()
    {
        var code = new Block(Entry).Emit(Addiu(S0, Zero, 7), Addiu(S1, S0, 1), Jr(Ra), Nop);
        var (graph, _, engine) = Attached(Image(code));
        using var _ = graph;
        using var __ = engine;

        var outcome = engine.RunFallbackSegment(Entering(Entry), Returns, instructionBudget: 100);

        outcome.Status.Should().Be(FallbackSegmentStatus.Returned);
        outcome.State.Pc.Should().Be(ReturnPc);
        outcome.State.Gpr[(int)S0].Should().Be(7u);
        outcome.State.Gpr[(int)S1].Should().Be(8u);
        outcome.RetiredInstructions.Should().Be(4, "two ALU instructions, the JR and its delay slot");
        outcome.DiagnosticCode.Should().BeNull();
    }

    [Fact]
    public void Cop0_SrCauseAndEpc_AreSeededAndReturned()
    {
        var code = new Block(Entry);
        code.Emit(Mfc0(S0, 12), Nop, Mfc0(S1, 14), Nop);
        code.Emit(Li(T0, 0xAAAA0000u));
        code.Emit(Mtc0(T0, 14), Jr(Ra), Nop);
        var (graph, _, engine) = Attached(Image(code));
        using var _ = graph;
        using var __ = engine;

        var outcome = engine.RunFallbackSegment(Entering(Entry, sr: 0x401, cause: 0, epc: 0x12345678), Returns, 100);

        outcome.Status.Should().Be(FallbackSegmentStatus.Returned);
        outcome.State.Gpr[(int)S0].Should().Be(0x401u, "SR was seeded");
        outcome.State.Gpr[(int)S1].Should().Be(0x12345678u, "EPC was seeded");
        outcome.State.Epc.Should().Be(0xAAAA0000u, "a COP0 write by the fallback is reported");
        outcome.State.Sr.Should().Be(0x401u);
    }

    [Fact]
    public void Devices_AreTheAttachedGraphs_AndTimeAdvancesOneCyclePerRetiredInstruction()
    {
        var code = new Block(Entry).Emit(Addiu(S0, Zero, 1), Addiu(S0, S0, 1), Addiu(S0, S0, 1), Jr(Ra), Nop);
        var (graph, _, engine) = Attached(Image(code));
        using var _ = graph;
        using var __ = engine;
        const uint timer0Counter = 0x1F801100u;
        var before = graph.Core.ReadTimerRegister(timer0Counter);

        var outcome = engine.RunFallbackSegment(Entering(Entry), Returns, 100);

        var after = graph.Core.ReadTimerRegister(timer0Counter);
        (after - before).Should().Be((uint)outcome.RetiredInstructions, "the shared scheduler advanced the shared timers per retired instruction");
    }

    [Fact]
    public void BudgetExhausted_AnEndlessLoopStopsWithoutAClaimOfSuccess()
    {
        var code = new Block(Entry).Emit(PSXRecomp.Tests.Recompiler.MipsEncoding.Jump(Entry), Nop);
        var (graph, _, engine) = Attached(Image(code));
        using var _ = graph;
        using var __ = engine;

        var outcome = engine.RunFallbackSegment(Entering(Entry), Returns, instructionBudget: 50);

        outcome.Status.Should().Be(FallbackSegmentStatus.BudgetExhausted);
        outcome.DiagnosticCode.Should().Be(MixedFallbackDiagnostics.SegmentBudgetExhausted);
        outcome.RetiredInstructions.Should().Be(50);
    }

    [Fact]
    public void AStopRequestedEnd_IsReportedAsAStop_AndNeverClaimsAnExhaustedBudget()
    {
        // A probe's --stop-at sets StopRequested from a FetchObserver; the step loop then ends exactly as budget
        // exhaustion does, and the outcome must not claim an instruction budget was retired (Issue #774).
        var code = new Block(Entry).Emit(Addiu(S0, Zero, 1), PSXRecomp.Tests.Recompiler.MipsEncoding.Jump(Entry), Nop);
        var (graph, _, engine) = Attached(Image(code));
        using var _ = graph;
        using var __ = engine;
        engine.FetchObserver = _ => { engine.StopRequested = true; };

        var outcome = engine.RunFallbackSegment(Entering(Entry), Returns, instructionBudget: 50);

        outcome.Status.Should().Be(FallbackSegmentStatus.Stopped);
        outcome.DiagnosticCode.Should().Be(MixedFallbackDiagnostics.StoppedOnRequest);
        outcome.RetiredInstructions.Should().Be(0, "the loop ends before the fetched instruction executes");
        outcome.DiagnosticMessage.Should().NotContain("retired its", "no budget was retired");
    }

    [Fact]
    public void AStopRequestedEnd_AfterInstructionsRetired_KeepsTheirExactCount_AndStillClaimsNoExhaustion()
    {
        // The real --stop-at case stops after millions of retired instructions, not at the first fetch: the outcome
        // must keep the exact retired count, name the stop PC, and still never claim an exhausted budget (Issue #774).
        var code = new Block(Entry).Emit(
            Addiu(S0, Zero, 1), Addiu(S0, S0, 1), Addiu(S0, S0, 1), PSXRecomp.Tests.Recompiler.MipsEncoding.Jump(Entry), Nop);
        var (graph, _, engine) = Attached(Image(code));
        using var _ = graph;
        using var __ = engine;
        var fetches = 0;
        engine.FetchObserver = _ => { if (++fetches == 3) { engine.StopRequested = true; } };

        var outcome = engine.RunFallbackSegment(Entering(Entry), Returns, instructionBudget: 50);

        outcome.Status.Should().Be(FallbackSegmentStatus.Stopped);
        outcome.DiagnosticCode.Should().Be(MixedFallbackDiagnostics.StoppedOnRequest);
        outcome.RetiredInstructions.Should().Be(2, "two instructions retired before the observer requested the stop");
        outcome.State.Gpr[(int)S0].Should().Be(2u, "the retired instructions' writes are reported");
        outcome.DiagnosticMessage.Should().Contain($"0x{Entry + 8:X8}")
            .And.Contain("2 instructions retired", "the message names the stop PC and the exact retired count");
        outcome.DiagnosticMessage.Should().NotContain("retired its", "the configured budget was not exhausted");
    }

    [Fact]
    public void FallbackProgress_IsPublishedWhileTheSegmentRuns_AndClearedAfterIt()
    {
        // Issue #806: a host that is stuck inside a segment reads these scalars (never the graph) to say where it is.
        // An endless loop that the observer stops after five fetches: each fetch sees the PC it is about to execute and
        // the instructions retired before it.
        var code = new Block(Entry).Emit(Addiu(S0, S0, 1), Addiu(S0, S0, 1), PSXRecomp.Tests.Recompiler.MipsEncoding.Jump(Entry), Nop);
        var (graph, _, engine) = Attached(Image(code));
        using var _ = graph;
        using var __ = engine;
        engine.FallbackProgress.Should().BeNull("no segment is running");
        var fetches = 0;
        var seen = new List<(uint Pc, ulong Retired, uint EntryPc, uint Budget)>();
        engine.FetchObserver = pc =>
        {
            fetches++;
            var progress = engine.FallbackProgress;
            progress.Should().NotBeNull("a fetch inside a segment sees the segment in flight");
            seen.Add((progress!.Pc, progress.RetiredInstructions, progress.EntryPc, progress.InstructionBudget));
            pc.Should().Be(progress.Pc, "the published PC is the one about to be fetched");
            if (fetches == 5) engine.StopRequested = true;
        };

        var outcome = engine.RunFallbackSegment(Entering(Entry), Returns, instructionBudget: 50);

        outcome.Status.Should().Be(FallbackSegmentStatus.Stopped);
        seen.Should().HaveCount(5);
        seen.Select(s => s.Retired).Should().Equal(new ulong[] { 0, 1, 2, 3, 4 }, "one instruction retires per fetch in this loop");
        seen.Should().OnlyContain(s => s.EntryPc == Entry && s.Budget == 50u);
        engine.FallbackProgress.Should().BeNull("the segment ended");
    }

    [Fact]
    public void ASecondSegment_BeforeItsFirstStep_DoesNotShowThePreviousSegmentsProgress()
    {
        // Issue #806: the previous segment's last published (PC, retired) must not show through at the next segment's start.
        // The BIOS factory runs inside each segment before its first step, so it reads what a host would read at that moment.
        InterpreterTitleExecutionEngine? engine = null;
        var atStart = new List<FallbackSegmentProgress?>();
        var code = new Block(Entry).Emit(Addiu(S0, S0, 1), Addiu(S0, S0, 1), Jr(Ra), Nop);
        var (graph, _, attached) = Attached(Image(code), () => atStart.Add(engine!.FallbackProgress));
        engine = attached;
        using var _ = graph;
        using var __ = engine;

        engine.RunFallbackSegment(Entering(Entry), Returns, instructionBudget: 100).Status.Should().Be(FallbackSegmentStatus.Returned);
        engine.RunFallbackSegment(Entering(Entry), Returns, instructionBudget: 100).Status.Should().Be(FallbackSegmentStatus.Returned);

        atStart.Should().HaveCount(2);
        atStart[1].Should().Be(new FallbackSegmentProgress(Entry, Entry, 0, 100),
            "the second segment starts at its own entry with nothing retired, not at the first segment's last published step");
    }

    [Fact]
    public void AnUnservicedException_StopsFailClosed()
    {
        var code = new Block(Entry).Emit(PSXRecomp.Tests.Recompiler.MipsEncoding.Break(), Nop);
        var (graph, _, engine) = Attached(Image(code));
        using var _ = graph;
        using var __ = engine;

        var outcome = engine.RunFallbackSegment(Entering(Entry), Returns, 100);

        outcome.Status.Should().Be(FallbackSegmentStatus.Stopped);
        outcome.DiagnosticCode.Should().Be(MixedFallbackDiagnostics.ExceptionUnsupported);
    }

    [Fact]
    public void LeavingTheImage_StopsFailClosed()
    {
        var code = new Block(Entry).Emit(Li(T0, 0x80003000u), [Jr(T0), Nop]);
        var (graph, _, engine) = Attached(Image(code));
        using var _ = graph;
        using var __ = engine;

        var outcome = engine.RunFallbackSegment(Entering(Entry), Returns, 100);

        outcome.Status.Should().Be(FallbackSegmentStatus.Stopped);
        outcome.DiagnosticCode.Should().Be(MixedFallbackDiagnostics.UnsupportedState);
        outcome.State.Pc.Should().Be(0x80003000u);
    }

    [Fact]
    public void ABiosBoundary_KeepsTheRuntimesOwnDiagnostic()
    {
        var code = new Block(Entry).Emit(Li(T1, 0x7Fu), Li(T0, 0xB0u), [Jalr(T0), Nop]);
        var (graph, _, engine) = Attached(Image(code));
        using var _ = graph;
        using var __ = engine;

        var outcome = engine.RunFallbackSegment(Entering(Entry), Returns, 100);

        outcome.Status.Should().Be(FallbackSegmentStatus.Stopped);
        outcome.DiagnosticCode.Should().Be("BIOS_HLE_UNSUPPORTED_CALL");
    }

    [Fact]
    public void AReturnPc_InABranchDelaySlot_IsNotACleanBoundary()
    {
        // The word at Entry + 4 is the delay slot of the BEQ at Entry, and it is a "compiled" entry.
        var code = new Block(Entry);
        code.Emit(PSXRecomp.Tests.Recompiler.MipsEncoding.Branch(0x04, 0, 0, Entry, ReturnPc), Addiu(S0, Zero, 5));
        var (graph, _, engine) = Attached(Image(code));
        using var _ = graph;
        using var __ = engine;

        var outcome = engine.RunFallbackSegment(Entering(Entry), new HashSet<uint> { Entry + 4, ReturnPc }, 100);

        outcome.Status.Should().Be(FallbackSegmentStatus.Returned);
        outcome.State.Pc.Should().Be(ReturnPc, "it did not stop at the delay slot");
        outcome.State.Gpr[(int)S0].Should().Be(5u, "the delay-slot instruction executed");
    }

    [Fact]
    public void AnAttachedEngine_NeverLoadsAnImage_AndAnOwningEngineCannotRunAFallback()
    {
        var words = Image(new Block(Entry).Emit(Nop));
        var (graph, _, attached) = Attached(words);
        using var _ = graph;
        using var __ = attached;

        var load = () => attached.Load(Request());
        load.Should().Throw<InvalidOperationException>("Load would reset the host-owned graph's core");

        using var owning = new InterpreterTitleExecutionEngine(words, Entry);
        var run = () => owning.RunFallbackSegment(Entering(Entry), Returns, 10);
        run.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Disposing_AnAttachedEngine_LeavesTheSharedGraphUsable()
    {
        var (graph, _, engine) = Attached(Image(new Block(Entry).Emit(Nop)));
        using var _ = graph;

        engine.Dispose();

        graph.Core.ReadMemory32(Entry & 0x1FFFFFFFu).Should().Be(0u, "the graph was not disposed by the attached engine");
    }

    // Issue #806 (return classification): every refusal of a return check is counted in the bucket its condition names.

    [Fact]
    public void ReturnCheck_ABlockEntry_IsAnAotEntryHit_AndAccepted()
    {
        var verdict = FallbackReturnCheck.Classify(Entry, new HashSet<uint> { Entry }, LoadedCodeTable.Empty, _ => 0);

        verdict.Should().Be(FallbackReturnVerdict.AcceptedAotEntry);
    }

    [Fact]
    public void ReturnCheck_NoBlockEntryAndNoRamVersion_IsRefusedAsNoAotEntry()
    {
        var verdict = FallbackReturnCheck.Classify(Entry + 8, new HashSet<uint>(), LoadedCodeTable.Empty, _ => 0);

        verdict.Should().Be(FallbackReturnVerdict.RefusedNoAotEntry);
    }

    [Fact]
    public void ReturnCheck_RamVersionWhoseWordsMatchRam_IsAcceptedAsARamVersion()
    {
        var loaded = new LoadedCodeTable([ReachableProgramBuilder.BuildLoadedImage(Target, [Nop, Nop], [Target], new HashSet<uint>())]);

        var verdict = FallbackReturnCheck.Classify(Target, new HashSet<uint>(), loaded, _ => Nop);

        verdict.Should().Be(FallbackReturnVerdict.AcceptedRamVersion);
    }

    [Fact]
    public void ReturnCheck_RamVersionWhoseWordsDifferFromRam_IsRefusedAsARamWordMismatch()
    {
        var loaded = new LoadedCodeTable([ReachableProgramBuilder.BuildLoadedImage(Target, [Nop, Nop], [Target], new HashSet<uint>())]);

        var verdict = FallbackReturnCheck.Classify(Target, new HashSet<uint>(), loaded, _ => 0xDEADBEEFu);

        verdict.Should().Be(FallbackReturnVerdict.RefusedRamWordMismatch);
    }

    [Fact]
    public void ReturnCheck_RamVersionAtAPcOutsideRam_IsRefusedAsPcNotRamMapped()
    {
        var loaded = new LoadedCodeTable([ReachableProgramBuilder.BuildLoadedImage(0xBFC00000u, [Nop, Nop], [0xBFC00000u], new HashSet<uint>())]);

        var verdict = FallbackReturnCheck.Classify(0xBFC00000u, new HashSet<uint>(), loaded, _ => Nop);

        verdict.Should().Be(FallbackReturnVerdict.RefusedPcNotRamMapped, "no RAM word can be compared, so no version can be shown to own the PC");
    }

    [Fact]
    public void ReturnCheck_RamVersionThatDoesNotFitInRam_IsRefusedAsNoComparableVersion()
    {
        // Two words at the last RAM word: the version is longer than the RAM left at that PC, so none is compared.
        // The version is written as two words explicitly: the builder may cut a unit shorter, and the fixture must not fit.
        var block = new RecompilerIrBlock(0x801FFFFCu, [], new RecompilerIrExit(RecompilerIrTerminationReason.Success, 0x80200004u));
        var loaded = new LoadedCodeTable([new GuardedImageProgram([new GuardedBlock(block, [Nop, Nop])], [])]);

        var verdict = FallbackReturnCheck.Classify(0x801FFFFCu, new HashSet<uint>(), loaded, _ => Nop);

        verdict.Should().Be(FallbackReturnVerdict.RefusedNoComparableVersion);
    }

    [Fact]
    public void ReturnCheck_AcceptedExactlyForTheTwoAcceptedVerdicts()
    {
        FallbackReturnCheck.IsAccepted(FallbackReturnVerdict.AcceptedAotEntry).Should().BeTrue();
        FallbackReturnCheck.IsAccepted(FallbackReturnVerdict.AcceptedRamVersion).Should().BeTrue();
        FallbackReturnCheck.IsAccepted(FallbackReturnVerdict.RefusedRamWordMismatch).Should().BeFalse();
        FallbackReturnCheck.IsAccepted(FallbackReturnVerdict.RefusedNoAotEntry).Should().BeFalse();
    }

    [Fact]
    public void ReturnCheck_PipelineNotClean_IsCountedAsARefusal_AtTheDelaySlot()
    {
        // The delay slot of the BEQ is a return point, but the pipeline holds a pending branch: a refused check.
        var code = new Block(Entry);
        code.Emit(PSXRecomp.Tests.Recompiler.MipsEncoding.Branch(0x04, 0, 0, Entry, ReturnPc), Addiu(S0, Zero, 5));
        var (graph, _, engine) = Attached(Image(code));
        using var _ = graph;
        using var __ = engine;
        var diagnostics = new FallbackReturnDiagnostics();
        engine.ReturnDiagnostics = diagnostics;

        var outcome = engine.RunFallbackSegment(Entering(Entry), new HashSet<uint> { Entry + 4, ReturnPc }, 100);

        outcome.Status.Should().Be(FallbackSegmentStatus.Returned);
        diagnostics.Snapshot().RefusedPipelineNotClean.Should().Be(1UL);
        diagnostics.Snapshot().TopRefusedPcs.Should().ContainSingle(p => p.Pc == Entry + 4 && p.Count == 1UL);
    }

    [Fact]
    public void ReturnCheck_WhileAnInterruptHandlerRuns_IsCountedAsSuppressed()
    {
        // A hardware IRQ 0 is pending and unmasked (I_MASK, I_STAT) with SR IEc|IM2, so the CPU takes it into the general
        // exception vector, outside the image. The vector holds a guest handler (non-zero words; an empty vector would be
        // the kernel's), and the step loop suppresses every return check while that handler runs. (A software interrupt
        // ends the segment instead: see SoftwareInterruptBackendDivergenceTests.)
        var (graph, _, engine) = Attached(Image(new Block(Entry).Emit(Nop, Nop, Nop, Nop)));
        using var _ = graph;
        using var __ = engine;
        // The handler loops inside the vector (addiu; j vector; nop), so only the budget ends it and it never reaches the A0 table.
        var handler = new byte[3 * 4];
        var handlerWords = new[] { Addiu(S0, S0, 1), PSXRecomp.Tests.Recompiler.MipsEncoding.Jump(0x80000080u), Nop };
        for (var i = 0; i < handlerWords.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(handler.AsSpan(i * 4), handlerWords[i]);
        }

        graph.Core.CopyRamFrom(0x80000080u & 0x1FFFFFFFu, handler);
        graph.Core.WriteInterruptControllerRegister(0x1F801074u, 1u);
        graph.Core.RaiseInterrupt(0);
        var diagnostics = new FallbackReturnDiagnostics();
        engine.ReturnDiagnostics = diagnostics;

        var outcome = engine.RunFallbackSegment(Entering(Entry, sr: 0x401), new HashSet<uint> { 0x80000080u }, 40);

        outcome.Status.Should().Be(FallbackSegmentStatus.BudgetExhausted, "the guest handler runs to the budget");
        outcome.RetiredInstructions.Should().BeGreaterThan(0UL, "the handler's addiu instructions retired");
        var snapshot = diagnostics.Snapshot();
        snapshot.StepsInInterruptHandler.Should().BeGreaterThan(0UL, "the handler stepped while the check was suppressed");
        snapshot.RefusedPipelineNotClean.Should().Be(0UL);
        snapshot.ReturnChecks.Should().Be(0UL, "no check reached the predicate: every step was inside the handler");
    }

    [Fact]
    public void FallbackReturns_CountsTheSegmentsAndTheLongestOne_AndTheBudgetExhaustion()
    {
        var stats = new FallbackReturnDiagnostics();

        stats.NoteSegment(entryPc: 0x80001000u, exitPc: 0x80001010u, retired: 10, FallbackSegmentStatus.Returned);
        stats.NoteSegment(entryPc: 0x80002000u, exitPc: 0x80002004u, retired: 500, FallbackSegmentStatus.BudgetExhausted);
        stats.NoteSegment(entryPc: 0x80003000u, exitPc: 0x80003008u, retired: 3, FallbackSegmentStatus.Stopped);

        var snapshot = stats.Snapshot();
        snapshot.Segments.Should().Be(3UL);
        snapshot.SuccessfulReturns.Should().Be(1UL);
        snapshot.BudgetExhaustedSegments.Should().Be(1UL);
        snapshot.LongestSegmentRetired.Should().Be(500UL);
        snapshot.LongestSegmentEntryPc.Should().Be(0x80002000u);
        snapshot.LongestSegmentExitPc.Should().Be(0x80002004u);
        snapshot.LongestSegmentStatus.Should().Be("BudgetExhausted");
    }

    [Fact]
    public void FallbackReturns_TopRefusedPcs_IsBounded_AndKeepsTheHottestPc()
    {
        var stats = new FallbackReturnDiagnostics();
        const uint hot = 0x80001234u;
        for (var i = 0; i < 100; i++)
        {
            stats.NoteVerdict(hot, FallbackReturnVerdict.RefusedNoAotEntry);
            stats.NoteVerdict(0x80010000u + (uint)i * 4u, FallbackReturnVerdict.RefusedRamWordMismatch);
        }

        var snapshot = stats.Snapshot();
        snapshot.TopRefusedPcs.Count.Should().BeLessThanOrEqualTo(FallbackReturnDiagnostics.TopN, "the table is bounded");
        snapshot.TopRefusedPcs[0].Pc.Should().Be(hot);
        snapshot.TopRefusedPcs[0].Count.Should().BeGreaterThanOrEqualTo(100UL, "a space-saving count never undercounts the hottest PC");
        snapshot.ReturnChecks.Should().Be(200UL);
    }

    [Fact]
    public void FallbackReturns_EveryVerdictIsCountedInExactlyItsOwnBucket()
    {
        var stats = new FallbackReturnDiagnostics();
        stats.NoteVerdict(Entry, FallbackReturnVerdict.AcceptedAotEntry);
        stats.NoteVerdict(Entry, FallbackReturnVerdict.AcceptedRamVersion);
        stats.NoteVerdict(Entry, FallbackReturnVerdict.RefusedNoAotEntry);
        stats.NoteVerdict(Entry, FallbackReturnVerdict.RefusedPcNotRamMapped);
        stats.NoteVerdict(Entry, FallbackReturnVerdict.RefusedRamWordMismatch);
        stats.NoteVerdict(Entry, FallbackReturnVerdict.RefusedNoComparableVersion);

        var s = stats.Snapshot();
        s.AotEntryHits.Should().Be(1UL);
        s.RamVersionHits.Should().Be(1UL);
        s.RefusedNoAotEntry.Should().Be(1UL);
        s.RefusedPcNotRamMapped.Should().Be(1UL);
        s.RefusedRamWordMismatch.Should().Be(1UL);
        s.RefusedNoComparableVersion.Should().Be(1UL);
        s.ReturnChecks.Should().Be(6UL);
    }

    [Fact]
    public void TopRefusedPcs_SamePcOnly_IsExactWithZeroError()
    {
        var stats = new FallbackReturnDiagnostics();
        for (var i = 0; i < 25; i++) stats.NoteVerdict(0x80001234u, FallbackReturnVerdict.RefusedNoAotEntry);

        var top = stats.Snapshot().TopRefusedPcs;
        top.Should().ContainSingle();
        top[0].Pc.Should().Be(0x80001234u);
        top[0].Count.Should().Be(25UL, "the estimate is the exact count while no eviction happened");
        top[0].Error.Should().Be(0UL);
        top[0].LowerBound.Should().Be(25UL);
    }

    [Fact]
    public void TopRefusedPcs_AtMostTopNDistinctPcs_AreAllExactWithZeroError()
    {
        var stats = new FallbackReturnDiagnostics();
        const uint basePc = 0x80010000u;
        for (var k = 0; k < FallbackReturnDiagnostics.TopN; k++)
        {
            for (var i = 0; i <= k; i++) stats.NoteVerdict(basePc + (uint)k * 4u, FallbackReturnVerdict.RefusedRamWordMismatch);
        }

        var top = stats.Snapshot().TopRefusedPcs;
        top.Count.Should().Be(FallbackReturnDiagnostics.TopN);
        foreach (var entry in top)
        {
            var k = (entry.Pc - basePc) / 4u;
            entry.Count.Should().Be(k + 1UL);
            entry.Error.Should().Be(0UL);
            entry.LowerBound.Should().Be(k + 1UL);
        }
    }

    [Fact]
    public void TopRefusedPcs_NinePlusDistinctPcs_ErrorBoundsHold()
    {
        var stats = new FallbackReturnDiagnostics();
        var truth = new Dictionary<uint, ulong>();
        for (var k = 0; k < 9; k++)
        {
            var pc = 0x80020000u + (uint)k * 4u;
            stats.NoteVerdict(pc, FallbackReturnVerdict.RefusedNoAotEntry);
            truth[pc] = 1UL;
        }

        var top = stats.Snapshot().TopRefusedPcs;
        top.Should().HaveCount(FallbackReturnDiagnostics.TopN);
        AssertSpaceSavingBounds(top, truth);
    }

    [Fact]
    public void TopRefusedPcs_EightyPcsTenTimesEach_ReportsBoundedEstimates()
    {
        var stats = new FallbackReturnDiagnostics();
        var truth = new Dictionary<uint, ulong>();
        for (var round = 0; round < 10; round++)
        {
            for (var k = 0; k < 80; k++)
            {
                var pc = 0x80030000u + (uint)k * 4u;
                stats.NoteVerdict(pc, FallbackReturnVerdict.RefusedRamWordMismatch);
                truth[pc] = truth.GetValueOrDefault(pc) + 1UL;
            }
        }

        var top = stats.Snapshot().TopRefusedPcs;
        top.Should().HaveCount(FallbackReturnDiagnostics.TopN);
        foreach (var entry in top)
        {
            entry.Count.Should().BeGreaterThanOrEqualTo(10UL, "every PC occurred 10 times, so no estimate may undercount");
            entry.LowerBound.Should().BeLessThanOrEqualTo(10UL);
        }

        AssertSpaceSavingBounds(top, truth);
    }

    [Fact]
    public void TopRefusedPcs_HotPcAmongColdPcs_KeepsItsTrueCountInsideItsBounds()
    {
        var stats = new FallbackReturnDiagnostics();
        var truth = new Dictionary<uint, ulong>();
        const uint hot = 0x80001234u;
        for (var i = 0; i < 300; i++)
        {
            stats.NoteVerdict(hot, FallbackReturnVerdict.RefusedNoAotEntry);
            truth[hot] = truth.GetValueOrDefault(hot) + 1UL;
            var cold = 0x80040000u + (uint)i * 4u;
            stats.NoteVerdict(cold, FallbackReturnVerdict.RefusedNoAotEntry);
            truth[cold] = 1UL;
        }

        var top = stats.Snapshot().TopRefusedPcs;
        var hotEntry = top.Should().ContainSingle(p => p.Pc == hot).Subject;
        hotEntry.LowerBound.Should().BeLessThanOrEqualTo(300UL);
        hotEntry.Count.Should().BeGreaterThanOrEqualTo(300UL);
        AssertSpaceSavingBounds(top, truth);
    }

    [Fact]
    public void TopRefusedPcs_Eviction_InheritsTheMinimumCountAsError()
    {
        var stats = new FallbackReturnDiagnostics();
        const uint pcA = 0x80050000u;
        for (var i = 0; i < 3; i++) stats.NoteVerdict(pcA, FallbackReturnVerdict.RefusedNoAotEntry);
        for (var k = 1; k < FallbackReturnDiagnostics.TopN; k++)
        {
            var pc = 0x80050000u + (uint)k * 4u;
            stats.NoteVerdict(pc, FallbackReturnVerdict.RefusedNoAotEntry);
            stats.NoteVerdict(pc, FallbackReturnVerdict.RefusedNoAotEntry);
        }

        // The table is full: A=3 and seven PCs at 2. A new PC replaces the first minimum (count 2) and inherits it.
        const uint pcNew = 0x80060000u;
        stats.NoteVerdict(pcNew, FallbackReturnVerdict.RefusedNoAotEntry);

        var top = stats.Snapshot().TopRefusedPcs;
        var evicted = top.Should().ContainSingle(p => p.Pc == pcNew).Subject;
        evicted.Error.Should().Be(2UL, "the inherited minimum count is the error bound");
        evicted.Count.Should().Be(3UL, "the estimate is the inherited minimum plus one");
        evicted.LowerBound.Should().Be(1UL, "the new PC occurred once, so the lower bound is exact here");
        top.Aggregate(0UL, (sum, p) => sum + p.Count).Should().Be(18UL, "every counted refusal stays in the table's estimates");
    }

    [Fact]
    public void TopRefusedPcs_Invariants_HoldForEveryEntryOnAMixedSequence()
    {
        var stats = new FallbackReturnDiagnostics();
        var truth = new Dictionary<uint, ulong>();
        var state = 12345u;
        for (var n = 0; n < 2000; n++)
        {
            state = unchecked(state * 1103515245u + 12345u);
            var pc = 0x80070000u + ((state >> 16) % 50u) * 4u;
            stats.NoteVerdict(pc, FallbackReturnVerdict.RefusedNoAotEntry);
            truth[pc] = truth.GetValueOrDefault(pc) + 1UL;
        }

        var top = stats.Snapshot().TopRefusedPcs;
        AssertSpaceSavingBounds(top, truth);
        top.Aggregate(0UL, (sum, p) => sum + p.Count).Should().Be(2000UL, "the table's estimates sum to the refusals it counted");
    }

    [Fact]
    public void TopRefusedPcs_ExactTotalsPerReasonAreUnchangedByTheTable()
    {
        var stats = new FallbackReturnDiagnostics();
        for (var i = 0; i < 40; i++) stats.NoteVerdict(0x80080000u + (uint)(i % 20) * 4u, FallbackReturnVerdict.RefusedRamWordMismatch);
        stats.NotePipelineRefusal(0x80090000u);

        var s = stats.Snapshot();
        s.RefusedRamWordMismatch.Should().Be(40UL);
        s.RefusedPipelineNotClean.Should().Be(1UL);
    }

    private static void AssertSpaceSavingBounds(IReadOnlyList<FallbackPcCount> top, IReadOnlyDictionary<uint, ulong> truth)
    {
        foreach (var entry in top)
        {
            var actual = truth[entry.Pc];
            entry.Error.Should().BeLessThanOrEqualTo(entry.Count, "the error never exceeds the estimate");
            entry.LowerBound.Should().BeLessThanOrEqualTo(actual, "the lower bound never exceeds the true count");
            actual.Should().BeLessThanOrEqualTo(entry.Count, "the estimate never undercounts the true count");
        }
    }
}
