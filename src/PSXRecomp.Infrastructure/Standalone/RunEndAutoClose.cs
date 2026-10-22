using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// The grace period of the game window after its run has ended (Issue #830). The window shows the run's result, then closes by itself
/// once the grace period has passed, so a finished program does not leave a window and its processes open. The clock is supplied by the
/// caller, so the timing is testable without a window.
/// </summary>
[Infrastructure]
public sealed class RunEndAutoClose
{
    /// <summary>The grace period the game window uses: the result stays visible for 30 seconds before the window closes.</summary>
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _grace;
    private TimeSpan? _dueAt;

    /// <summary>Creates a timer that closes after <paramref name="grace"/>; the grace period must not be negative.</summary>
    public RunEndAutoClose(TimeSpan grace)
    {
        if (grace < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(grace), "The grace period must not be negative.");
        }

        _grace = grace;
    }

    /// <summary>The grace period.</summary>
    public TimeSpan Grace => _grace;

    /// <summary>Whether the grace period has started.</summary>
    public bool IsStarted => _dueAt is not null;

    /// <summary>Starts the grace period at <paramref name="now"/>. A second start keeps the first deadline.</summary>
    public void Start(TimeSpan now)
    {
        _dueAt ??= now + _grace;
    }

    /// <summary>Whether the window is due to close at <paramref name="now"/>: the grace period has started and has passed.</summary>
    public bool IsDue(TimeSpan now) => _dueAt is { } due && now >= due;
}
