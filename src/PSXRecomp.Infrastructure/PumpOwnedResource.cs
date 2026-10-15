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
/// release can be requested. While the pump is active the resource is deliberately retained:
/// there is no way to free it safely without risking use-after-free inside the pump. The
/// caller bounds the pump's own liveness separately (<c>RunProcess</c> kills the child and
/// releases its stdout pipe, then waits a bounded drain), so a deferred release normally
/// follows shortly after; a pump wedged in device or fallback work, which killing the child
/// cannot interrupt, holds its resource until that work returns or the process exits. The
/// resource is never released twice, and a faulted pump is observed rather than leaking an
/// unobserved task exception.
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
                // Reading completed.Exception observes a fault of a pump that later failed after
                // the bounded drain gave up, so it does not surface as an unobserved exception.
                _ = pump.ContinueWith(
                    static (completed, state) =>
                    {
                        _ = completed.Exception;
                        ((PumpOwnedResource)state!).Release();
                    },
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
