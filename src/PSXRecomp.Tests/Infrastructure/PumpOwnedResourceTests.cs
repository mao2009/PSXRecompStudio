using FluentAssertions;
using PSXRecomp.Infrastructure;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #762: the host-transfer bridge releases its device graph, fallback session and BIOS
/// Runtime through <see cref="PumpOwnedResource"/> so a still-running pump never observes a
/// disposed resource (the race CodeRabbit flagged in PR #771).
/// </summary>
[Test]
public sealed class PumpOwnedResourceTests
{
    [Fact]
    public void DisposeWithoutAPump_ReleasesImmediatelyAndOnlyOnce()
    {
        var releases = 0;
        var resource = new PumpOwnedResource(() => releases++);

        resource.IsPumpActive.Should().BeFalse();
        resource.Dispose();
        releases.Should().Be(1);

        resource.Dispose();
        releases.Should().Be(1);
    }

    [Fact]
    public void DisposeAfterThePumpEnded_ReleasesImmediately()
    {
        var completed = new TaskCompletionSource();
        completed.SetResult();
        var releases = 0;
        var resource = new PumpOwnedResource(() => releases++);
        resource.Track(completed.Task);

        resource.IsPumpActive.Should().BeFalse();
        resource.Dispose();

        releases.Should().Be(1);
    }

    [Fact]
    public async Task DisposeWhileThePumpIsRunning_DefersReleaseUntilThePumpEnds()
    {
        var pump = new TaskCompletionSource();
        var released = new TaskCompletionSource();
        var resource = new PumpOwnedResource(released.SetResult);
        resource.Track(pump.Task);

        resource.IsPumpActive.Should().BeTrue();
        resource.Dispose();

        released.Task.IsCompleted.Should().BeFalse("the pump still owns the resource");
        resource.IsPumpActive.Should().BeTrue();

        pump.SetResult();

        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        resource.IsPumpActive.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeWhileThePumpIsRunning_ReleasesExactlyOnceWhenThePumpEnds()
    {
        var pump = new TaskCompletionSource();
        var released = new TaskCompletionSource();
        var releases = 0;
        var resource = new PumpOwnedResource(() =>
        {
            Interlocked.Increment(ref releases);
            released.TrySetResult();
        });
        resource.Track(pump.Task);

        resource.Dispose();
        resource.Dispose();

        pump.SetResult();

        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releases.Should().Be(1);
    }

    [Fact]
    public async Task PumpEndingConcurrentlyWithDispose_ReleasesExactlyOnce()
    {
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var pump = new TaskCompletionSource();
            var released = new TaskCompletionSource();
            var releases = 0;
            var resource = new PumpOwnedResource(() =>
            {
                Interlocked.Increment(ref releases);
                released.TrySetResult();
            });
            resource.Track(pump.Task);

            using var barrier = new Barrier(2);
            var disposer = Task.Run(() =>
            {
                barrier.SignalAndWait();
                resource.Dispose();
            });
            var completer = Task.Run(() =>
            {
                barrier.SignalAndWait();
                pump.SetResult();
            });
            await Task.WhenAll(disposer, completer);

            await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
            releases.Should().Be(1, "release must run exactly once regardless of the race outcome");
        }
    }

    [Fact]
    public void TrackAfterDispose_IsAProgrammingError()
    {
        var resource = new PumpOwnedResource(() => { });
        resource.Dispose();

        var tracking = () => resource.Track(Task.CompletedTask);

        tracking.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TrackingTwoPumps_IsAProgrammingError()
    {
        var resource = new PumpOwnedResource(() => { });
        resource.Track(Task.CompletedTask);

        var tracking = () => resource.Track(Task.CompletedTask);

        tracking.Should().Throw<InvalidOperationException>();
    }
}
