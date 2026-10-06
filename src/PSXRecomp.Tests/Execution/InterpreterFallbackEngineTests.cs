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

    private static (PsxDeviceGraph Graph, DeviceScheduler Scheduler, InterpreterTitleExecutionEngine Engine) Attached(uint[] words)
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
            words, Entry, graph, scheduler, (reader, writer) => new BiosHleRuntime(new NullSink(), reader, writer));
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
}
