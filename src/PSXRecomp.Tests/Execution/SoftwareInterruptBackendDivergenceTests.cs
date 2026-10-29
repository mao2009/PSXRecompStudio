using System.Buffers.Binary;
using FluentAssertions;
using PSXRecomp.Core;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Runtime;
using Xunit;
using static PSXRecomp.Tests.Infrastructure.MixedFallbackTestSupport;

namespace PSXRecomp.Tests.Execution;

/// <summary>
/// Issue #728: the software-interrupt (CAUSE.IP0/IP1) divergence between the interpreter and the artifact. A fallback
/// segment whose return JR carries <c>MTC0 SR</c> in its delay slot leaves IP0 pending and enabled at the return PC. Run
/// alone the interpreter takes that INT on its next step and stops with <c>Exception</c>. The artifact's INT boundary
/// accepts INT only for CAUSE.IP2 and SR.IM2 (<c>artifact_interrupt_boundary</c>), so a segment that returned Clean and
/// resumed the artifact would drop the interrupt silently. The mixed segment now stops with the same Runtime-failure
/// classification the interpreter alone uses (docs/cpu/exceptions.md).
/// </summary>
[Test]
public sealed class SoftwareInterruptBackendDivergenceTests
{
    private const uint ReturnPc = Entry + 0x20u; // the Nop right after the JR and its delay slot, inside the image
    private const uint SrIec_Im0 = 0x0000_0101u;
    private const uint CauseIp0 = 0x0000_0100u;

    private static (PsxDeviceGraph Graph, InterpreterTitleExecutionEngine Engine) Attached(uint[] words)
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
        return (graph, engine);
    }

    private static uint[] Program(uint cause, uint sr)
    {
        var code = new Block(Entry);
        code.Emit(Li(T0, cause));
        code.Emit(Mtc0(T0, 13), Nop);
        code.Emit(Li(T0, sr));
        code.Emit(Jr(Ra), Mtc0(T0, 12)); // the delay slot enables the interrupt after the return decision's inputs are set
        code.Emit(Nop, Nop, Nop, Nop);
        return Image(code);
    }

    private static FallbackCpuState Entering()
    {
        var gpr = new uint[TitleExecutionRequest.GprCount];
        gpr[(int)Ra] = ReturnPc;
        return new FallbackCpuState(gpr, 0, 0, Entry, 0, 0, 0);
    }

    [Fact]
    public void Mixed_DoesNotReturnWithAPendingEnabledSoftwareInterrupt_ItStopsAsTheInterpreterAloneDoes()
    {
        var (graph, engine) = Attached(Program(CauseIp0, SrIec_Im0));
        using var _ = graph;
        using var __ = engine;

        var outcome = engine.RunFallbackSegment(Entering(), new HashSet<uint> { ReturnPc }, 100);

        // The interpreter alone takes this INT at ReturnPc (see the test below). Resuming the artifact there would drop it.
        outcome.Status.Should().Be(FallbackSegmentStatus.Stopped, "a takeable software interrupt is a Runtime failure, not a return");
        outcome.DiagnosticCode.Should().Be(MixedFallbackDiagnostics.ExceptionUnsupported, outcome.DiagnosticMessage);
        outcome.State.Pc.Should().Be(ReturnPc);
        (outcome.State.Cause & outcome.State.Sr & 0x300u).Should().NotBe(0u, "IP0 is pending and enabled in the stopped state");
    }

    [Theory]
    [InlineData(0x0000_0100u, 0x0000_0100u)] // IP0 pending, IM0 set, IEc clear: not takeable
    [InlineData(0x0000_0100u, 0x0000_0001u)] // IP0 pending, IEc set, IM0 clear: masked
    [InlineData(0u, 0x0000_0101u)]           // nothing pending
    public void Mixed_ReturnsCleanlyWhenNoSoftwareInterruptIsTakeableAtTheReturnPc(uint cause, uint sr)
    {
        var (graph, engine) = Attached(Program(cause, sr));
        using var _ = graph;
        using var __ = engine;

        var outcome = engine.RunFallbackSegment(Entering(), new HashSet<uint> { ReturnPc }, 100);

        outcome.Status.Should().Be(FallbackSegmentStatus.Returned, outcome.DiagnosticMessage);
        outcome.State.Pc.Should().Be(ReturnPc);
    }

    [Fact]
    public void InterpreterAlone_TakesTheSameInterruptAtTheReturnPc_AndStopsWithException()
    {
        var (graph, engine) = Attached(Program(CauseIp0, SrIec_Im0));
        using var _ = graph;
        using var __ = engine;

        // No return points: the interpreter keeps going, and the next Step takes the INT at ReturnPc.
        var outcome = engine.RunFallbackSegment(Entering(), new HashSet<uint>(), 100);

        outcome.Status.Should().Be(FallbackSegmentStatus.Stopped);
        outcome.DiagnosticCode.Should().Be(MixedFallbackDiagnostics.ExceptionUnsupported, outcome.DiagnosticMessage);
        outcome.State.Pc.Should().Be(0x80000080u, "the INT entered the general exception vector (BEV=0)");
        outcome.State.Epc.Should().Be(ReturnPc, "EPC is the instruction the INT preempted: the same PC mixed execution resumes at");
        outcome.RetiredInstructions.Should().Be(8, "li(2), mtc0, nop, li(2), JR and its slot all retire, then the INT preempts the fetch at ReturnPc");
    }
}
