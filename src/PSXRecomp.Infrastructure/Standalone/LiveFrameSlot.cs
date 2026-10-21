using System.Diagnostics.CodeAnalysis;
using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// One displayable frame published by the Runtime (Issue #630). The array is not copied on publish: the publisher must not
/// write to <see cref="Bgra32"/> after <see cref="LiveFrameSlot.Publish"/>, and readers must not write to it either.
/// </summary>
[Infrastructure]
public sealed record LiveFrame(long Sequence, int Width, int Height, byte[] Bgra32);

/// <summary>
/// Latest-only handoff of Runtime frames from the device-owning thread to a presentation thread (Issue #630).
/// It keeps exactly one frame: a publish replaces the previous one, so a slow reader never queues frames and never
/// blocks the publisher. A reader detects skipped frames from the sequence gap.
/// </summary>
/// <remarks>
/// The publisher calls <see cref="Publish"/> from the thread that owns the GPU/VRAM (the VBlank point). The slot itself
/// never touches the GPU, so it is safe to read from any thread.
/// </remarks>
[Infrastructure]
public sealed class LiveFrameSlot
{
    private readonly object _gate = new();
    private LiveFrame? _latest;
    private bool _readerPolled;

    /// <summary>
    /// True when a reader has asked for a frame since the last publish. The publisher checks this first, so a run with no
    /// reader does not copy the display at every VBlank.
    /// </summary>
    public bool ReaderIsWaiting
    {
        get
        {
            lock (_gate)
            {
                return _readerPolled;
            }
        }
    }

    /// <summary>Publishes a frame, replacing any unread one. <paramref name="bgra32"/> is taken over, not copied.</summary>
    public void Publish(int width, int height, byte[] bgra32)
    {
        ArgumentNullException.ThrowIfNull(bgra32);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        // Area is checked first so the byte count below cannot overflow, whatever the dimensions.
        var area = (long)width * height;
        if (area > Array.MaxLength / 4)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The frame is too large to hold as BGRA32.");
        }

        if (bgra32.LongLength != area * 4)
        {
            throw new ArgumentException("BGRA32 length must equal width * height * 4.", nameof(bgra32));
        }

        lock (_gate)
        {
            _latest = new LiveFrame((_latest?.Sequence ?? 0) + 1, width, height, bgra32);
            _readerPolled = false;
        }
    }

    /// <summary>
    /// Returns the latest frame when it is newer than <paramref name="lastSeenSequence"/>; otherwise false, with
    /// <paramref name="frame"/> set to null. Pass 0 before the first frame has been seen.
    /// </summary>
    public bool TryGetNewer(long lastSeenSequence, [NotNullWhen(true)] out LiveFrame? frame)
    {
        lock (_gate)
        {
            _readerPolled = true;
            if (_latest is { } latest && latest.Sequence > lastSeenSequence)
            {
                frame = latest;
                return true;
            }

            frame = null;
            return false;
        }
    }
}
