using System.Text.Json.Nodes;
using FluentAssertions;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Infrastructure;
using PSXRecomp.Infrastructure.Cli;

namespace PSXRecomp.Tests.Infrastructure;

// Issue #774 (W03): the standalone (generated-host) run reports each VBlank of its host scheduler to the same boundary log
// the interpreter writes, so the two can be compared on the same boundary. Synthetic only: no artifact or ROM runs here.
[Test]
public sealed class RecompiledHostVBlankObserverTests
{
    [Fact]
    public void The_Observer_Runs_At_Each_VBlank_With_The_Host_Devices()
    {
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        var seen = new List<(PsxDeviceGraph Devices, ulong Vblank)>();
        RecompiledHostExecutionEngine.WireVBlank(scheduler, graph, live: null, (devices, vblank, _) => seen.Add((devices, vblank)));

        scheduler.Advance(2 * DeviceScheduler.VblankIntervalCycles);

        seen.Select(static s => s.Vblank).Should().Equal(1UL, 2UL);
        seen.All(s => ReferenceEquals(s.Devices, graph)).Should().BeTrue();
    }

    [Fact]
    public void A_Standalone_Record_Has_The_Same_Keys_As_An_Interpreter_Record()
    {
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        var writer = new StringWriter();
        using var log = new OpenBiosProbeBoundaryLog(writer);
        RecompiledHostExecutionEngine.WireVBlank(scheduler, graph, live: null,
            (devices, vblank, cycles) => log.Write("standalone", devices, vblank, cycles));

        scheduler.Advance(DeviceScheduler.VblankIntervalCycles);

        var line = JsonNode.Parse(writer.ToString().Trim())!.AsObject();
        var interpreter = OpenBiosProbeBoundaryLog.Describe("interpreter", graph, 1, 0);
        line.Select(static p => p.Key).Should().Equal(interpreter.Select(static p => p.Key));
        line["engine"]!.GetValue<string>().Should().Be("standalone");
        log.FailureMessage.Should().BeNull();
    }

    [Fact]
    public void Live_Frames_And_The_Observer_Both_Run_At_Each_VBlank()
    {
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        var live = new List<ulong>();
        var observed = new List<ulong>();
        RecompiledHostExecutionEngine.WireVBlank(scheduler, graph, live: live.Add, (_, vblank, _) => observed.Add(vblank));

        scheduler.Advance(2 * DeviceScheduler.VblankIntervalCycles);

        live.Should().Equal(1UL, 2UL);
        observed.Should().Equal(1UL, 2UL);
    }

    // ----- Issue #774 (W03), owner ruling: the diagnostic observer is contained; game-execution callbacks are not. -----

    [Fact]
    public void A_Throwing_Observer_Leaves_Scheduler_Counters_Unchanged()
    {
        // Advance by several VBlanks plus a remainder, twice, so the phase accounting is exercised across calls. The
        // comparison covers the scheduler's own counters only; device state is not compared here.
        var cycles = 3 * DeviceScheduler.VblankIntervalCycles + 1234;

        using var clean = new PsxDeviceGraph();
        var cleanScheduler = clean.CreateScheduler();
        cleanScheduler.Advance(cycles);
        cleanScheduler.Advance(cycles);

        using var throwing = new PsxDeviceGraph();
        var throwingScheduler = throwing.CreateScheduler();
        _ = throwingScheduler.AddDiagnosticVblankObserver(vblank => { if (vblank == 2) throw new InvalidOperationException("observer defect"); });
        throwingScheduler.Advance(cycles);
        throwingScheduler.Advance(cycles);

        throwingScheduler.VblankCount.Should().Be(cleanScheduler.VblankCount);
        throwingScheduler.ElapsedCycles.Should().Be(cleanScheduler.ElapsedCycles);
        throwingScheduler.VblankObserverFailure.Should().NotBeNull();
    }

    [Fact]
    public void A_Subscriber_Added_During_A_VBlank_Does_Not_Run_In_That_VBlank_And_Runs_From_The_Next()
    {
        // The delivery iterates a snapshot taken for each VBlank, so a subscriber added while VBlank 1 is delivered first runs at VBlank 2.
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        var late = new List<ulong>();
        _ = scheduler.AddVblankCallback(vblank => { if (vblank == 1) _ = scheduler.AddVblankCallback(late.Add); });

        scheduler.Advance(3 * DeviceScheduler.VblankIntervalCycles);

        late.Should().Equal(2UL, 3UL);
    }

    [Fact]
    public void The_First_Observer_Failure_Is_Recorded_And_Later_VBlanks_Are_Still_Delivered()
    {
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        var delivered = new List<ulong>();
        _ = scheduler.AddDiagnosticVblankObserver(vblank =>
        {
            delivered.Add(vblank);
            if (vblank is 2 or 3) throw new InvalidOperationException($"boom at {vblank}");
        });

        scheduler.Advance(4 * DeviceScheduler.VblankIntervalCycles);

        delivered.Should().Equal(1UL, 2UL, 3UL, 4UL);
        scheduler.VblankObserverFailure.Should().Be(new VblankObserverFailure(2, nameof(InvalidOperationException), "boom at 2"));
    }

    [Fact]
    public void A_Game_Execution_Callback_Exception_Still_Propagates()
    {
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        _ = scheduler.AddVblankCallback(_ => throw new InvalidOperationException("game defect"));

        var act = () => scheduler.Advance(DeviceScheduler.VblankIntervalCycles);

        act.Should().Throw<InvalidOperationException>().WithMessage("game defect");
        scheduler.VblankObserverFailure.Should().BeNull("a game callback's failure is not an observer failure");
    }

    [Fact]
    public void A_Host_Observer_Failure_Is_Reported_To_The_Engine_Sink_And_Contained()
    {
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        var sink = new List<VblankObserverFailure>();
        RecompiledHostExecutionEngine.WireVBlank(scheduler, graph, live: null,
            (_, vblank, _) => throw new InvalidOperationException($"host {vblank}"), sink.Add);

        var act = () => scheduler.Advance(DeviceScheduler.VblankIntervalCycles);

        act.Should().NotThrow();
        sink.Should().ContainSingle().Which.Should().Be(new VblankObserverFailure(1, nameof(InvalidOperationException), "host 1"));
    }

    [Fact]
    public void Both_Callbacks_Run_In_Registration_Order_At_Each_VBlank()
    {
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        var order = new List<string>();
        _ = scheduler.AddVblankCallback(vblank => order.Add($"live{vblank}"));
        _ = scheduler.AddDiagnosticVblankObserver(vblank => order.Add($"observer{vblank}"));

        scheduler.Advance(2 * DeviceScheduler.VblankIntervalCycles);

        order.Should().Equal("live1", "observer1", "live2", "observer2");
    }

    [Fact]
    public void The_Same_Callback_Registered_Twice_Runs_Once()
    {
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        var seen = new List<ulong>();
        Action<ulong> callback = seen.Add;
        _ = scheduler.AddVblankCallback(callback);
        _ = scheduler.AddVblankCallback(callback);

        scheduler.Advance(DeviceScheduler.VblankIntervalCycles);

        seen.Should().Equal(1UL);
    }

    [Fact]
    public void Disposing_A_Handle_Removes_Only_That_Subscriber_And_Is_Idempotent()
    {
        // The kept subscriber is registered first and the removed one second, so an overwriting composition would drop the kept one.
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        var kept = new List<ulong>();
        var removed = new List<ulong>();
        _ = scheduler.AddVblankCallback(kept.Add);
        var handle = scheduler.AddVblankCallback(removed.Add);

        handle.Dispose();
        handle.Dispose();
        scheduler.Advance(2 * DeviceScheduler.VblankIntervalCycles);

        removed.Should().BeEmpty();
        kept.Should().Equal(1UL, 2UL);
    }

    [Fact]
    public void A_Subscriber_Disposed_By_An_Earlier_Subscriber_Never_Runs()
    {
        // A runs first and disposes B during the VBlank-1 delivery. B is never run, not in VBlank 1 and not later: its
        // snapshot entry is skipped because it was removed before its turn.
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        IDisposable? handleB = null;
        var bRuns = new List<ulong>();
        _ = scheduler.AddVblankCallback(vblank => { if (vblank == 1) handleB!.Dispose(); });
        handleB = scheduler.AddVblankCallback(bRuns.Add);

        scheduler.Advance(3 * DeviceScheduler.VblankIntervalCycles);

        bRuns.Should().BeEmpty("B was disposed before its turn in VBlank 1, so it never runs");
    }

    [Fact]
    public void A_Subscriber_Disposed_Mid_Run_Stops_After_The_VBlank_In_Which_It_Was_Disposed()
    {
        // B runs before A in each delivery, so B's VBlank 2 run is legitimate (it precedes the dispose). It must not run in VBlank 3.
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        var bRuns = new List<ulong>();
        IDisposable? handleB = null;
        handleB = scheduler.AddVblankCallback(bRuns.Add);
        _ = scheduler.AddVblankCallback(vblank => { if (vblank == 2) handleB!.Dispose(); });

        scheduler.Advance(3 * DeviceScheduler.VblankIntervalCycles);

        bRuns.Should().Equal(1UL, 2UL);
    }

    [Fact]
    public void A_Subscriber_Does_Not_Run_On_Another_Scheduler()
    {
        // A callback registered on scheduler 1 must not run for VBlanks delivered by scheduler 2.
        using var first = new PsxDeviceGraph();
        using var second = new PsxDeviceGraph();
        var firstScheduler = first.CreateScheduler();
        var secondScheduler = second.CreateScheduler();
        var seenOnFirst = new List<ulong>();
        _ = firstScheduler.AddVblankCallback(seenOnFirst.Add);

        secondScheduler.Advance(DeviceScheduler.VblankIntervalCycles);

        seenOnFirst.Should().BeEmpty("the registration belongs to scheduler 1; scheduler 2's VBlank must not reach it");
        firstScheduler.Advance(DeviceScheduler.VblankIntervalCycles);
        // Scheduler 1 still delivers its own VBlanks.
        seenOnFirst.Should().Equal(1UL);
    }

    [Fact]
    public void The_Same_Delegate_Registered_As_Diagnostic_Then_Propagating_Keeps_The_First_Kind_And_Runs_Once()
    {
        // Duplicate rule: the first registration's kind applies. As a diagnostic, its throw is contained (not propagated),
        // and it runs once per VBlank even though it was registered a second time.
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        var runs = new List<ulong>();
        Action<ulong> callback = vblank => { runs.Add(vblank); throw new InvalidOperationException("contained"); };
        _ = scheduler.AddDiagnosticVblankObserver(callback);
        _ = scheduler.AddVblankCallback(callback);

        var act = () => scheduler.Advance(DeviceScheduler.VblankIntervalCycles);

        act.Should().NotThrow();
        runs.Should().Equal(1UL);
        scheduler.VblankObserverFailure.Should().Be(new VblankObserverFailure(1, nameof(InvalidOperationException), "contained"));
    }

    [Fact]
    public void A_Duplicate_Handle_Is_Inert_And_The_First_Handle_Owns_The_Subscription()
    {
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        var runs = new List<ulong>();
        Action<ulong> callback = runs.Add;
        var first = scheduler.AddVblankCallback(callback);
        var duplicate = scheduler.AddVblankCallback(callback);

        duplicate.Dispose();
        scheduler.Advance(DeviceScheduler.VblankIntervalCycles);
        // Disposing the inert duplicate handle does nothing: the subscriber still runs.
        runs.Should().Equal(1UL);

        first.Dispose();
        scheduler.Advance(DeviceScheduler.VblankIntervalCycles);
        // Disposing the first handle removes the subscriber even though the duplicate handle is still held: no new run.
        runs.Should().Equal(1UL);
    }
}
