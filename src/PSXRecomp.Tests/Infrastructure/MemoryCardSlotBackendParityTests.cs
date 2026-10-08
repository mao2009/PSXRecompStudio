using System.Reflection;
using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.MemoryCard;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Infrastructure;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Tests.Recompiler;
using Xunit;
using static PSXRecomp.Tests.Infrastructure.RecompiledArtifactMmioBridgeTests;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #715 (review F2/F3): both execution backends take the same immutable <see cref="MemoryCardSlotConfiguration"/>
/// and hand it to the device graph their guest sees, so the backends cannot disagree on card presence.
/// </summary>
[Test]
public sealed class MemoryCardSlotBackendParityTests
{
    private const uint Entry = 0x80001000u;

    private static readonly MemoryCardSlotConfiguration Configured =
        MemoryCardSlotConfiguration.Empty.WithCard(MemoryCardSlot.Slot2, "/cards/a.mcr");

    [Fact]
    public void Interpreter_ExposesTheConfiguredSlotsOnItsDeviceGraph()
    {
        using var engine = new InterpreterTitleExecutionEngine([MipsEncoding.Nop], Entry, memoryCardSlots: Configured);

        InterpreterGraph(engine).MemoryCardSlots.Should().BeSameAs(Configured);
    }

    [Fact]
    public void Interpreter_WithoutConfiguration_IsEmpty()
    {
        using var engine = new InterpreterTitleExecutionEngine([MipsEncoding.Nop], Entry);

        InterpreterGraph(engine).MemoryCardSlots.Should().Be(MemoryCardSlotConfiguration.Empty);
    }

    [Fact]
    public void GeneratedHost_ForwardsTheConfiguredSlotsToTheGraphItBuilds_AndMatchesTheInterpreter()
    {
        using var dir = new TempDirectory();
        var words = Program([MipsEncoding.Nop]);
        MemoryCardSlotConfiguration? seen = null;
        var program = ReachableProgramBuilder.Build(Entry, words, Entry, []);
        using var engine = new RecompiledHostExecutionEngine(
            program,
            words,
            Entry,
            new GeneratedHostBuildService(),
            dir.FullPath,
            (reader, writer) => new BiosHleRuntime(new NullSink(), reader, writer),
            graph => seen = graph.MemoryCardSlots,
            memoryCardSlots: Configured);

        var result = new ExecutionOrchestrator().Execute(engine, new ExitHandoff(), Request());

        result.State.Should().Be(TitleExecutionState.Completed, result.DiagnosticMessage);
        seen.Should().BeSameAs(Configured);
        using var interpreter = new InterpreterTitleExecutionEngine([MipsEncoding.Nop], Entry, memoryCardSlots: Configured);
        InterpreterGraph(interpreter).MemoryCardSlots.Should().Be(seen);
    }

    // The engine retains the graph it owns and disposes (the same route InterpreterTitleExecutionEngineTests uses).
    private static PsxDeviceGraph InterpreterGraph(InterpreterTitleExecutionEngine engine) =>
        (PsxDeviceGraph)typeof(InterpreterTitleExecutionEngine)
            .GetField("_devices", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
}
