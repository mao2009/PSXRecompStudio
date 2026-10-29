using System.Reflection;
using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Tests.Recompiler;

namespace PSXRecomp.Tests.Execution;

// Issue #774 (W03): the interpreter engine's diagnostic VBlank observer is attached to its scheduler and removed on dispose,
// and it never removes any other subscriber. The attached form (Attach) owns no devices, so the test's graph outlives Dispose.
[Test]
public sealed class InterpreterVBlankObserverAttachTests
{
    private const uint Entry = 0x80010000u;

    private static void AttachVBlankObserver(InterpreterTitleExecutionEngine engine) =>
        typeof(InterpreterTitleExecutionEngine).GetMethod("AttachVBlankObserver", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(engine, null);

    [Fact]
    public void Load_Attaches_The_Observer_And_Its_Throw_Is_Recorded_As_The_Engine_Failure()
    {
        using var engine = new InterpreterTitleExecutionEngine([MipsEncoding.Nop], Entry);
        engine.VBlankObserver = (vblank, _) => { if (vblank == 1) throw new InvalidOperationException("observer defect"); };
        engine.Load(new TitleExecutionRequest(
            Entry, new uint[TitleExecutionRequest.GprCount], initialHi: 0, initialLo: 0,
            initialMemory: Array.Empty<PSXRecomp.Core.Recompiler.RecompilerInitialMemoryItem>(), outerBudget: 1, segmentBudget: 64));
        engine.VBlankObserverFailure.Should().BeNull("no VBlank has run yet");

        engine.Scheduler!.Advance(DeviceScheduler.VblankIntervalCycles);

        engine.VBlankObserverFailure.Should().Be(new VblankObserverFailure(1, nameof(InvalidOperationException), "observer defect"));
    }

    [Fact]
    public void Re_Attach_Replaces_Only_The_Engines_Own_Registration_And_Keeps_Another_Subscriber()
    {
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        using var engine = InterpreterTitleExecutionEngine.Attach([MipsEncoding.Nop], Entry, graph, scheduler);
        var observerRuns = new List<ulong>();
        engine.VBlankObserver = (vblank, _) => observerRuns.Add(vblank);
        var otherRuns = new List<ulong>();
        _ = scheduler.AddVblankCallback(otherRuns.Add);

        AttachVBlankObserver(engine);
        AttachVBlankObserver(engine);
        scheduler.Advance(DeviceScheduler.VblankIntervalCycles);

        // A second attach replaces the engine's registration rather than adding one; the other subscriber is untouched.
        observerRuns.Should().Equal(1UL);
        otherRuns.Should().Equal(1UL);
    }

    [Fact]
    public void Dispose_Removes_Only_The_Engines_Own_Registration()
    {
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        var engine = InterpreterTitleExecutionEngine.Attach([MipsEncoding.Nop], Entry, graph, scheduler);
        var observerRuns = new List<ulong>();
        engine.VBlankObserver = (vblank, _) => observerRuns.Add(vblank);
        AttachVBlankObserver(engine);
        var otherRuns = new List<ulong>();
        _ = scheduler.AddVblankCallback(otherRuns.Add);

        // Before dispose, both run at VBlank 1: this proves the observer is attached, so the later check is not vacuous.
        scheduler.Advance(DeviceScheduler.VblankIntervalCycles);
        observerRuns.Should().Equal(1UL);

        engine.Dispose();
        scheduler.Advance(DeviceScheduler.VblankIntervalCycles);

        // A disposed engine must not stay subscribed to a scheduler that outlives it; dispose does not remove another subscriber.
        observerRuns.Should().Equal(1UL);
        otherRuns.Should().Equal(1UL, 2UL);
    }

    [Fact]
    public void Setting_VBlankObserver_On_An_Attached_Engine_Attaches_It_At_Once_Through_The_Public_Property()
    {
        // No reflection: the public setter is the path a caller uses on an engine built with Attach (which never calls Load).
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        using var engine = InterpreterTitleExecutionEngine.Attach([MipsEncoding.Nop], Entry, graph, scheduler);
        var runs = new List<ulong>();

        engine.VBlankObserver = (vblank, _) => runs.Add(vblank);
        scheduler.Advance(DeviceScheduler.VblankIntervalCycles);

        runs.Should().Equal(1UL);
    }

    [Fact]
    public void Changing_Or_Clearing_VBlankObserver_Replaces_Or_Removes_Only_The_Engines_Registration()
    {
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        using var engine = InterpreterTitleExecutionEngine.Attach([MipsEncoding.Nop], Entry, graph, scheduler);
        var first = new List<ulong>();
        var second = new List<ulong>();
        var other = new List<ulong>();
        _ = scheduler.AddVblankCallback(other.Add);

        engine.VBlankObserver = (vblank, _) => first.Add(vblank);
        engine.VBlankObserver = (vblank, _) => second.Add(vblank);
        scheduler.Advance(DeviceScheduler.VblankIntervalCycles);
        engine.VBlankObserver = null;
        scheduler.Advance(DeviceScheduler.VblankIntervalCycles);

        first.Should().BeEmpty("a replaced observer no longer runs");
        // The replacement ran once, at VBlank 1, and was removed at VBlank 2.
        second.Should().Equal(1UL);
        // Another subscriber is untouched by the engine's changes.
        other.Should().Equal(1UL, 2UL);
    }
}
