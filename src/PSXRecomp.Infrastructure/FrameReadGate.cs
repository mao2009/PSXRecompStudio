using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// The single gate for every read of a run's GPU/VRAM (Issue #827, owner requirement): the run-end frame and the live VBlank frames alike.
/// A read runs only while no stop has been requested. The request flag is set under the same lock, so a read's check and its start form one
/// section: a stop cannot land between them, and no read starts after the flag is visible.
/// </summary>
/// <remarks>
/// The lock covers one read at a time, and only the flag check and the read. A stop request waits for a read in progress, which is bounded by
/// one frame read (a copy and a conversion of the displayed frame, at most a few milliseconds for a PS-X frame). Nothing else is done under the lock.
/// </remarks>
[Infrastructure]
internal sealed class FrameReadGate
{
    private readonly object _lock = new();
    private bool _stopRequested;
    private int _reads;
    private int _readsAtStop;

    /// <summary>Sets the stop request flag. Waits for a read in progress; no read starts after this returns.</summary>
    internal void RequestStop()
    {
        lock (_lock)
        {
            if (!_stopRequested)
            {
                _stopRequested = true;
                _readsAtStop = _reads;
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="read"/> only while no stop has been requested. <paramref name="stopRecorded"/> is checked too (the stop record and
    /// the stop token cover a stop that began on another path). Returns false when the read was not run.
    /// </summary>
    internal bool TryRead(Func<bool> stopRecorded, Action read)
    {
        ArgumentNullException.ThrowIfNull(stopRecorded);
        ArgumentNullException.ThrowIfNull(read);
        lock (_lock)
        {
            if (_stopRequested || stopRecorded())
            {
                return false;
            }

            _reads++;
            read();
            return true;
        }
    }

    /// <summary>Whether the request flag is set.</summary>
    internal bool StopRequested
    {
        get
        {
            lock (_lock)
            {
                return _stopRequested;
            }
        }
    }

    /// <summary>The number of reads so far, and the number that had been done when the stop was requested (zero when none was).</summary>
    internal (int Reads, int ReadsAtStop) Stats
    {
        get
        {
            lock (_lock)
            {
                return (_reads, _stopRequested ? _readsAtStop : 0);
            }
        }
    }
}
