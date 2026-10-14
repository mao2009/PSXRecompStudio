using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure;

/// <summary>
/// Bounds how long the host-transfer pump is awaited after the artifact is told to stop
/// (Issue #762). A pump that is still running past the budget is reported as not ended so
/// the caller keeps the bridge's resources alive and lets <see cref="PumpOwnedResource"/>
/// release them when the pump finally ends; waiting forever would hang the launcher and
/// disposing eagerly would race the pump.
/// </summary>
[Infrastructure]
internal static class HostTransferPumpLifetime
{
    /// <summary>
    /// Returns true when <paramref name="pump"/> has ended within <paramref name="budget"/>
    /// (a faulted or cancelled pump has ended too); false when it is still running and the
    /// caller must not treat its shared resources as free.
    /// </summary>
    public static bool AwaitEnded(Task pump, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(pump);

        try
        {
            return pump.Wait(budget);
        }
        catch (AggregateException)
        {
            // A faulted or cancelled pump has already stopped: nothing is still using the
            // shared resources, so the caller may dispose them.
            return true;
        }
    }
}
