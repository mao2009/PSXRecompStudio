using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime;

/// <summary>
/// The one blocking BIOS HLE call (<see cref="BiosServiceStatus.Pending"/>) an execution path may have
/// outstanding, and its fail-closed bound (Issue #717).
/// </summary>
/// <remarks>
/// <para>
/// The continuation itself is not stored here: it is the guest's own state. The CPU stays at the
/// trampoline vector with <c>$t1</c>, <c>$a0</c>-<c>$a3</c> and <c>$ra</c> untouched, so an interrupt
/// taken during the wait saves EPC = the vector and its return re-enters the call. This type only
/// counts polls, so a call whose condition never changes stops the run instead of spinning.
/// </para>
/// <para>
/// Each poll advances the devices by <see cref="PollCycles"/>, so the bound is guest time, never wall
/// clock: <see cref="MaxPolls"/> polls are <see cref="MaxWaitVblanks"/> VBlank intervals of device time.
/// </para>
/// </remarks>
[Domain]
public sealed class BiosBlockingCallWait
{
    /// <summary>Device cycles one poll of a pending call advances: 1/64 of a VBlank interval.</summary>
    /// <remarks>
    /// ponytail: fixed quantum. A device edge that fires more than once inside one quantum (a fast repeat
    /// timer) latches once, exactly as a long <see cref="DeviceScheduler.Advance"/> already does; shrink the
    /// quantum if a blocking service ever depends on such a source.
    /// </remarks>
    public const uint PollCycles = DeviceScheduler.VblankIntervalCycles / 64;

    /// <summary>Guest time, in VBlank intervals, a single blocking call may wait before the run fails closed (10 s at 60 Hz).</summary>
    public const uint MaxWaitVblanks = 600;

    /// <summary>The poll count that equals <see cref="MaxWaitVblanks"/> of device time.</summary>
    public const uint MaxPolls = MaxWaitVblanks * (DeviceScheduler.VblankIntervalCycles / PollCycles);

    /// <summary>Reported by an execution path that cannot advance devices while a call waits.</summary>
    public const string UnsupportedDiagnosticCode = "BIOS_HLE_WAIT_UNSUPPORTED";

    /// <summary>Reported when a pending call did not complete within <see cref="MaxPolls"/> polls.</summary>
    public const string TimeoutDiagnosticCode = "BIOS_HLE_WAIT_TIMEOUT";

    /// <summary>Reported when a second, different blocking call starts while one is still pending.</summary>
    public const string NestedDiagnosticCode = "BIOS_HLE_WAIT_NESTED";

    private (BiosCallFamily Family, byte Function, uint Ra)? _call;
    private uint _polls;

    /// <summary>Counts one poll of a pending call; null to keep waiting, else the diagnostic the run stops with.</summary>
    internal (string Code, string Message)? Poll(BiosCallIdentity identity, uint ra)
    {
        var call = (identity.Family, identity.FunctionNumber, ra);
        if (_call is { } outstanding && outstanding != call)
        {
            return (NestedDiagnosticCode,
                $"{identity.StableKey} (ra=0x{ra:X8}) is pending while {outstanding.Family}:{outstanding.Function:X2} " +
                $"(ra=0x{outstanding.Ra:X8}) has not completed; nested blocking calls are not modelled.");
        }

        _call = call;
        if (++_polls > MaxPolls)
        {
            return (TimeoutDiagnosticCode,
                $"{identity.StableKey} (ra=0x{ra:X8}) did not complete within {MaxWaitVblanks} VBlank intervals " +
                $"({MaxPolls} polls of {PollCycles} cycles).");
        }

        return null;
    }

    /// <summary>Ends the outstanding wait when this is the call that was pending; any other call leaves it alone.</summary>
    internal void Complete(BiosCallIdentity identity, uint ra)
    {
        if (_call == (identity.Family, identity.FunctionNumber, ra))
        {
            _call = null;
            _polls = 0;
        }
    }
}
