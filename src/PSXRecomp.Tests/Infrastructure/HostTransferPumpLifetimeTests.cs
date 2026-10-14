using FluentAssertions;
using PSXRecomp.Infrastructure;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #762: after a run budget is exhausted the host-transfer pump is given a bounded drain
/// rather than being awaited forever. <see cref="HostTransferPumpLifetime.AwaitEnded"/> reports
/// whether the pump actually stopped so the caller keeps the bridge's devices alive when it did not.
/// </summary>
[Test]
public sealed class HostTransferPumpLifetimeTests
{
    [Fact]
    public void ACompletedPump_HasEnded()
    {
        HostTransferPumpLifetime.AwaitEnded(Task.CompletedTask, TimeSpan.Zero).Should().BeTrue();
    }

    [Fact]
    public void APumpThatEndsWithinTheBudget_HasEnded()
    {
        var pump = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            await Task.Yield();
            pump.SetResult();
        });

        HostTransferPumpLifetime.AwaitEnded(pump.Task, TimeSpan.FromSeconds(5)).Should().BeTrue();
    }

    [Fact]
    public void AFaultedPump_HasEnded()
    {
        var pump = Task.FromException(new InvalidOperationException("pump boom"));

        HostTransferPumpLifetime.AwaitEnded(pump, TimeSpan.Zero).Should().BeTrue();
    }

    [Fact]
    public void ACancelledPump_HasEnded()
    {
        var pump = Task.FromCanceled(new CancellationToken(canceled: true));

        HostTransferPumpLifetime.AwaitEnded(pump, TimeSpan.Zero).Should().BeTrue();
    }

    [Fact]
    public void APumpStillRunningAtTheBudget_HasNotEnded()
    {
        var pump = new TaskCompletionSource();

        HostTransferPumpLifetime.AwaitEnded(pump.Task, TimeSpan.FromMilliseconds(10)).Should().BeFalse();
    }

    [Fact]
    public void NullPump_IsRejected()
    {
        var awaitEnded = () => HostTransferPumpLifetime.AwaitEnded(null!, TimeSpan.Zero);

        awaitEnded.Should().Throw<ArgumentNullException>();
    }
}
