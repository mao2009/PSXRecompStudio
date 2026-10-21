using System.Diagnostics;
using FluentAssertions;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

/// <summary>
/// A protocol pump must start promptly even when the thread pool is occupied by blocked work (Issue #815). A pump that
/// ran on a pool worker would wait for the pool to inject threads: on a 4-processor runner that took seconds, past the
/// 5 s execution budget, and the child was reported as timed out.
/// </summary>
[Test]
public sealed class ProtocolPumpThreadTests
{
    [Fact]
    public void AProtocolPump_StartsPromptly_WhileTheThreadPoolWorkersAreHeld()
    {
        ThreadPool.GetMinThreads(out var minWorkers, out _);
        var holders = minWorkers * 4;
        var release = new ManualResetEventSlim(false);
        using var allStarted = new CountdownEvent(holders);
        var tasks = new Task[holders];
        for (var i = 0; i < holders; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                allStarted.Signal();
                release.Wait();
            });
        }

        try
        {
            // Start the pump only once the blocked work holds its workers.
            allStarted.Wait(TimeSpan.FromSeconds(60)).Should().BeTrue("the blocked work must occupy the pool first");

            var pumpRan = false;
            var sw = Stopwatch.StartNew();
            var pump = PSXRecomp.Infrastructure.ProtocolPump.Start(() => Volatile.Write(ref pumpRan, true));
            var completed = pump.Wait(TimeSpan.FromMilliseconds(500));

            completed.Should().BeTrue($"the pump must not wait for a pool worker (not complete after {sw.ElapsedMilliseconds} ms)");
            pumpRan.Should().BeTrue();
        }
        finally
        {
            release.Set();
            Task.WaitAll(tasks);
        }
    }

    [Fact]
    public void TheStandalonePump_IsStartedThroughProtocolPump_NotOnThePool()
    {
        // The production pump must go through ProtocolPump.Start (Issue #815). A revert to Task.Run would not show in the
        // helper test above, so this pins the call site as well.
#pragma warning disable AARC003 // Test-only: pins the call site of the production pump in its source file.
        var source = File.ReadAllText(Path.Combine(RealRomFixtures.RepositoryRoot,
            "src", "PSXRecomp.Infrastructure", "RecompiledHostExecutionEngine.cs"));
#pragma warning restore AARC003
        source.Should().Contain("ProtocolPump.Start(", "the standalone pump must not run on a thread-pool worker");
        source.Should().NotContain("var pump = Task.Run(", "a Task.Run pump holds a pool worker for the child's whole life");
    }
}
