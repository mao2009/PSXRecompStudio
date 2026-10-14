using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// Owns the device graph, fallback session and BIOS Runtime a host-transfer pump uses on its
/// own thread, and releases them only once that pump has ended (Issue #762). Disposal is
/// idempotent: if the pump is still running when disposal is requested, the release is
/// deferred to the pump's completion, so the pump can never observe a disposed device graph,
/// scheduler or fallback session — the race CodeRabbit flagged in PR #771.
/// </summary>
/// <remarks>
/// The production call order is fixed: <see cref="Track"/> is called as soon as the pump is
/// started and disposal only when the run ends, so the tracked pump is always known before a
/// release can be requested. A pump that never ends keeps the resource alive only until it is
/// stopped, which <c>RunProcess</c> guarantees by killing the child and releasing its stdout
/// pipe; a single deferred release is bounded by that pump's own termination, not unbounded
/// retention.
/// </remarks>
[Infrastructure]
internal sealed class PumpOwnedResource : IDisposable
{
    private readonly Action _release;
    private readonly object _gate = new();
    private Task? _pump;
    private bool _disposeRequested;
    private bool _released;

    /// <summary>Creates an owner whose resource is released by <paramref name="release"/>, exactly once.</summary>
    public PumpOwnedResource(Action release) =>
        _release = release ?? throw new ArgumentNullException(nameof(release));

    /// <summary>Whether a tracked pump is still running and therefore still using the resource.</summary>
    public bool IsPumpActive
    {
        get
        {
            lock (_gate)
            {
                return _pump is { IsCompleted: false };
            }
        }
    }

    /// <summary>
    /// Makes <paramref name="pump"/> the owner of the resource until it ends. Must be called
    /// before the first <see cref="Dispose"/>; tracking a pump after disposal is a programming
    /// error (the resource is already released).
    /// </summary>
    public void Track(Task pump)
    {
        ArgumentNullException.ThrowIfNull(pump);
        lock (_gate)
        {
            if (_disposeRequested)
            {
                throw new InvalidOperationException("A pump must be tracked before the resource is disposed.");
            }

            if (_pump is not null)
            {
                throw new InvalidOperationException("A pump is already tracked.");
            }

            _pump = pump;
        }
    }

    public void Dispose()
    {
        Task? pump;
        lock (_gate)
        {
            if (_disposeRequested)
            {
                return;
            }

            _disposeRequested = true;
            pump = _pump;
            if (pump is { IsCompleted: false })
            {
                // The pump outlived the run and still owns the resource: release it only when
                // the pump ends, on whatever thread ends it. ExecuteSynchronously also closes
                // the window where the pump ends between the check above and this registration.
                _ = pump.ContinueWith(
                    static (_, state) => ((PumpOwnedResource)state!).Release(),
                    state: this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return;
            }
        }

        Release();
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_released)
            {
                return;
            }

            _released = true;
        }

        _release();
    }
}
