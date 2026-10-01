using System.Reflection;
using FluentAssertions;
using PSXRecomp.Core.Dma;
using PSXRecomp.Core.Execution;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Execution;

[Test]
public sealed class InterpreterTitleExecutionEngineTests
{
    private const uint Entry = 0x80001000u;

    // CodeRabbit PR #491: the constructor once built MMIO adapters
    // as locals and attached them to the bus without retaining/disposal ownership.
    // Issue #572 adds the managed GPU adapter to that same engine lifecycle.
    // Reached through the engine's own fields (no product-code test hook added);
    // each adapter throws ObjectDisposedException once disposed, which proves
    // the engine's Dispose() actually reached it.
    [Fact]
    public void Dispose_DisposesOwnedMmioAdapters()
    {
        var engine = new InterpreterTitleExecutionEngine(new[] { MipsEncoding.Nop }, Entry);

        var dmaAdapter = GetAdapter<DmaMmioAdapter>(engine, "DmaAdapter");
        var timerAdapter = GetAdapter<TimerMmioAdapter>(engine, "TimerAdapter");
        var interruptAdapter = GetAdapter<InterruptControllerMmioAdapter>(engine, "InterruptControllerAdapter");
        var gpuAdapter = GetAdapter<GpuMmioAdapter>(engine, "GpuAdapter");

        engine.Dispose();

        var dmaRead = () => dmaAdapter.ReadRegister(0x1F801080u);
        var timerRead = () => timerAdapter.ReadRegister(0x1F801100u);
        var interruptRead = () => interruptAdapter.ReadRegister(0x1F801070u);
        var gpuRead = () => gpuAdapter.ReadRegister(0x1F801814u);

        dmaRead.Should().Throw<ObjectDisposedException>();
        timerRead.Should().Throw<ObjectDisposedException>();
        interruptRead.Should().Throw<ObjectDisposedException>();
        gpuRead.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var engine = new InterpreterTitleExecutionEngine(new[] { MipsEncoding.Nop }, Entry);
        engine.Dispose();

        var act = () => engine.Dispose();

        act.Should().NotThrow();
    }

    private static T GetAdapter<T>(InterpreterTitleExecutionEngine engine, string fieldName)
    {
        // The adapters live on the shared Runtime device graph the engine owns and disposes.
        var graphField = typeof(InterpreterTitleExecutionEngine).GetField("_devices", BindingFlags.NonPublic | BindingFlags.Instance);
        graphField.Should().NotBeNull("the engine must retain its device graph to dispose it");
        var graph = graphField!.GetValue(engine)!;
        var property = graph.GetType().GetProperty(fieldName);
        property.Should().NotBeNull($"the device graph must expose {fieldName}");
        return (T)property!.GetValue(graph)!;
    }
}
