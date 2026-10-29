using System.Buffers.Binary;
using FluentAssertions;
using PSXRecomp.Core;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;
using static PSXRecomp.Tests.Infrastructure.MixedFallbackTestSupport;

namespace PSXRecomp.Tests.Execution;

/// <summary>
/// Issue #728: a fallback segment that returns to the artifact with an enabled software interrupt (CAUSE.IP0/IP1 with
/// SR.IM0/IM1 and SR.IEc) pending must stop, not resume: the artifact's INT boundary accepts only IP2 (ADR-025). The
/// interpreter-alone and the mixed/artifact paths report <see cref="MixedFallbackDiagnostics.SoftwareInterruptUnsupported"/>
/// for it. Inside a BIOS blocking wait the wait code is kept (<c>BiosBlockingCallWaitTests</c>).
/// </summary>
[Test]
public sealed class SoftwareInterruptBackendDivergenceTests
{
    private const uint ReturnPc = Entry + 0x20u; // the Nop right after the JR and its delay slot, inside the image
    private const uint SrIec_Im0 = 0x0000_0101u;
    private const uint CauseIp0 = 0x0000_0100u;
    private const uint HostReturnPc = Entry + 0x10u; // FallbackProgram: the Nop after the JALR (the program's return point)

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

    /// <summary>
    /// The compiled program calls an uncompiled target through JALR, so the target runs as a fallback segment. The target
    /// sets CAUSE and SR, then returns with <c>jr ra</c> to <see cref="HostReturnPc"/>, which ends the program.
    /// </summary>
    private static uint[] FallbackProgram(uint cause, uint sr)
    {
        var main = new Block(Entry);
        main.Emit(Li(T0, Target), [Jalr(T0)], [Nop]);
        main.Emit([Nop], [Jr(Zero)], [Nop]); // HostReturnPc is the first Nop; the jump to zero is the program's end
        var target = new Block(Target);
        target.Emit(Li(T1, cause), [Mtc0(T1, 13)], [Nop]);
        target.Emit(Li(T1, sr), [Jr(Ra)], [Mtc0(T1, 12)]);
        return Image(main, target);
    }

    private static FallbackCpuState Entering()
    {
        var gpr = new uint[TitleExecutionRequest.GprCount];
        gpr[(int)Ra] = ReturnPc;
        return new FallbackCpuState(gpr, 0, 0, Entry, 0, 0, 0);
    }

    private static FallbackCpuState EnteringTarget()
    {
        var gpr = new uint[TitleExecutionRequest.GprCount];
        gpr[(int)Ra] = HostReturnPc;
        return new FallbackCpuState(gpr, 0, 0, Target, 0, 0, 0);
    }

    [Fact]
    public void Mixed_DoesNotReturnWithAPendingEnabledSoftwareInterrupt_ItStopsAsTheInterpreterAloneDoes()
    {
        var (graph, engine) = Attached(Program(CauseIp0, SrIec_Im0));
        using var _ = graph;
        using var __ = engine;

        var outcome = engine.RunFallbackSegment(Entering(), new HashSet<uint> { ReturnPc }, 100);

        outcome.Status.Should().Be(FallbackSegmentStatus.Stopped, "a takeable software interrupt is a Runtime failure, not a return");
        outcome.DiagnosticCode.Should().Be(MixedFallbackDiagnostics.SoftwareInterruptUnsupported, outcome.DiagnosticMessage);
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
    public void InterpreterAlone_TakesTheSameInterruptAtTheReturnPc_AndStopsWithTheSoftwareInterruptCode()
    {
        var (graph, engine) = Attached(Program(CauseIp0, SrIec_Im0));
        using var _ = graph;
        using var __ = engine;

        // No return points: the interpreter keeps going, and the next Step takes the INT at ReturnPc.
        var outcome = engine.RunFallbackSegment(Entering(), new HashSet<uint>(), 100);

        outcome.Status.Should().Be(FallbackSegmentStatus.Stopped);
        outcome.DiagnosticCode.Should().Be(MixedFallbackDiagnostics.SoftwareInterruptUnsupported, outcome.DiagnosticMessage);
        outcome.State.Pc.Should().Be(0x80000080u, "the INT entered the general exception vector (BEV=0)");
        outcome.State.Epc.Should().Be(ReturnPc, "EPC is the instruction the INT preempted: the same PC mixed execution resumes at");
        outcome.RetiredInstructions.Should().Be(8, "li(2), mtc0, nop, li(2), JR and its slot all retire, then the INT preempts the fetch at ReturnPc");
    }

    [Theory]
    [InlineData(0x0000_0100u, 0x0000_0101u)] // IP0 takeable
    [InlineData(0x0000_0200u, 0x0000_0201u)] // IP1 takeable
    public void InterpreterAloneAndMixed_ReportTheSameDiagnosticForAnUnsupportedSoftwareInterrupt(uint cause, uint sr)
    {
        var words = FallbackProgram(cause, sr);
        using var dir = new TempDirectory();

        var (graph, engine) = Attached(words);
        using var _ = graph;
        using var __ = engine;
        var interpreter = engine.RunFallbackSegment(EnteringTarget(), new HashSet<uint>(), 100);

        var mixed = RunArtifact(words, dir, new MixedFallbackOptions()).Result;

        interpreter.Status.Should().Be(FallbackSegmentStatus.Stopped, interpreter.DiagnosticMessage);
        mixed.State.Should().NotBe(TitleExecutionState.Completed, mixed.DiagnosticMessage);
        mixed.DiagnosticCode.Should().Be(interpreter.DiagnosticCode, $"interpreter: {interpreter.DiagnosticMessage} / mixed: {mixed.DiagnosticMessage}");
        mixed.DiagnosticCode.Should().Be(MixedFallbackDiagnostics.SoftwareInterruptUnsupported);
    }

    [Theory]
    [InlineData(0x0000_0100u, 0x0000_0101u, true)]  // IP0 takeable: stop
    [InlineData(0x0000_0200u, 0x0000_0201u, true)]  // IP1 takeable: stop
    [InlineData(0x0000_0100u, 0x0000_0100u, false)] // IP0 pending, IEc clear: returns
    [InlineData(0x0000_0100u, 0x0000_0001u, false)] // IP0 pending, IEc set, IM0 clear: masked
    [InlineData(0x0000_0200u, 0x0000_0401u, false)] // IP1 pending, IM1 clear (IM2 set): masked
    [InlineData(0x0000_0400u, 0x0000_0401u, false)] // IP2 only (hardware line, not a software interrupt): returns
    [InlineData(0u, 0x0000_0101u, false)]           // nothing pending: normal return
    public void FallbackReturn_StopsOnlyForATakeableSoftwareInterrupt_OnBothBackends(uint cause, uint sr, bool stops)
    {
        var words = FallbackProgram(cause, sr);
        using var dir = new TempDirectory();

        var (graph, engine) = Attached(words);
        using var _ = graph;
        using var __ = engine;
        var interpreter = engine.RunFallbackSegment(EnteringTarget(), new HashSet<uint> { HostReturnPc }, 100);

        var mixed = RunArtifact(words, dir, new MixedFallbackOptions()).Result;

        if (stops)
        {
            interpreter.Status.Should().Be(FallbackSegmentStatus.Stopped, interpreter.DiagnosticMessage);
            interpreter.DiagnosticCode.Should().Be(MixedFallbackDiagnostics.SoftwareInterruptUnsupported, interpreter.DiagnosticMessage);
            mixed.DiagnosticCode.Should().Be(MixedFallbackDiagnostics.SoftwareInterruptUnsupported, mixed.DiagnosticMessage);
            mixed.State.Should().NotBe(TitleExecutionState.Completed);
        }
        else
        {
            interpreter.Status.Should().Be(FallbackSegmentStatus.Returned, interpreter.DiagnosticMessage);
            interpreter.State.Pc.Should().Be(HostReturnPc);
            mixed.State.Should().Be(TitleExecutionState.Completed, mixed.DiagnosticMessage);
        }
    }
}
