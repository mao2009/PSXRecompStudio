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
    public void A_Throwing_Observer_Leaves_The_Guest_Result_And_Scheduler_State_Unchanged()
    {
        // Advance by several VBlanks plus a remainder, twice, so the phase accounting is exercised across calls.
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
        var cleanState = OpenBiosProbeBoundaryLog.Describe("x", clean, cleanScheduler.VblankCount, cleanScheduler.ElapsedCycles);
        var throwingState = OpenBiosProbeBoundaryLog.Describe("x", throwing, throwingScheduler.VblankCount, throwingScheduler.ElapsedCycles);
        throwingState.ToJsonString().Should().Be(cleanState.ToJsonString());
        throwingScheduler.VblankObserverFailure.Should().NotBeNull();
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
        using var graph = new PsxDeviceGraph();
        var scheduler = graph.CreateScheduler();
        var removed = new List<ulong>();
        var kept = new List<ulong>();
        var handle = scheduler.AddVblankCallback(removed.Add);
        _ = scheduler.AddVblankCallback(kept.Add);

        handle.Dispose();
        handle.Dispose();
        scheduler.Advance(DeviceScheduler.VblankIntervalCycles);

        removed.Should().BeEmpty();
        kept.Should().Equal(1UL);
    }

    [Fact]
    public void A_Subscriber_Does_Not_Leak_Into_A_Later_Scheduler()
    {
        var leaked = new List<ulong>();
        using (var first = new PsxDeviceGraph())
        {
            _ = first.CreateScheduler().AddVblankCallback(leaked.Add);
        }

        using var second = new PsxDeviceGraph();
        var secondScheduler = second.CreateScheduler();
        secondScheduler.Advance(DeviceScheduler.VblankIntervalCycles);

        leaked.Should().BeEmpty("a registration belongs to the scheduler it was made on");
    }
}
