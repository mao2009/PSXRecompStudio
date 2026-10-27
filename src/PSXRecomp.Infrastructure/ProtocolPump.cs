using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// Starts the output pump of a child process that speaks a line protocol (Issue #815). The pump blocks in ReadLine for the
/// child's whole life. On the thread pool it would hold a worker for that time: on a small pool other work then starts late
/// (measured: a trivial Task.Run waited about 4.3 s with 8 workers held on a 4-processor pool), and time budgets are
/// exceeded. A long-running task takes its own thread instead.
/// </summary>
[Infrastructure]
internal static class ProtocolPump
{
    internal static Task Start(Action pumpBody) =>
        Task.Factory.StartNew(pumpBody, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    // Same dedicated thread for a blocking read that produces a result: the drain of a child's whole stream (ReadToEnd).
    internal static Task<T> StartFunc<T>(Func<T> body) =>
        Task.Factory.StartNew(body, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
