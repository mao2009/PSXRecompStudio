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

    // CodeRabbit PR #491: the constructor built DmaMmioAdapter/TimerMmioAdapter/
    // InterruptControllerMmioAdapter as locals and attached them to the bus
    // without keeping a reference, so Dispose() never disposed them and their
    // registered callbacks could outlive the native core. Reached through the
    // engine's own fields (no product-code test hook added); each adapter
    // throws ObjectDisposedException once disposed, which is enough to prove
    // the engine's Dispose() actually reached them.
    [Fact]
    public void Dispose_DisposesOwnedMmioAdapters()
    {
        var engine = new InterpreterTitleExecutionEngine(new[] { MipsEncoding.Nop }, Entry);

        var dmaAdapter = GetAdapter<DmaMmioAdapter>(engine, "_dmaAdapter");
        var timerAdapter = GetAdapter<TimerMmioAdapter>(engine, "_timerAdapter");
        var interruptAdapter = GetAdapter<InterruptControllerMmioAdapter>(engine, "_interruptControllerAdapter");

        engine.Dispose();

        var dmaRead = () => dmaAdapter.ReadRegister(0x1F801080u);
        var timerRead = () => timerAdapter.ReadRegister(0x1F801100u);
        var interruptRead = () => interruptAdapter.ReadRegister(0x1F801070u);

        dmaRead.Should().Throw<ObjectDisposedException>();
        timerRead.Should().Throw<ObjectDisposedException>();
        interruptRead.Should().Throw<ObjectDisposedException>();
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
        var field = typeof(InterpreterTitleExecutionEngine).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        field.Should().NotBeNull($"the engine must retain its {fieldName} field to dispose it");
        return (T)field!.GetValue(engine)!;
    }
}
