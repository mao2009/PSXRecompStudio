using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PSXRecomp.Architecture;
using PSXRecompStudio.Services;

namespace PSXRecompStudio.Views;

/// <summary>
/// Sole owner of the presentation resource (Issue #634) built from a <see cref="PresentationFrame"/>.
/// Holds at most one live resource: <see cref="Present"/> publishes the new one to the consumer first,
/// then disposes the previous one, so the consumer never draws a disposed resource. UI-thread only; the
/// guest/Runtime never touches this, so guest timing does not depend on allocation or disposal here.
/// </summary>
[Application]
public sealed class FrameResourceOwner<T> : IDisposable where T : class, IDisposable
{
    private readonly Func<PresentationFrame, T> _create;
    private readonly Action<T?> _publish;

    public FrameResourceOwner(Func<PresentationFrame, T> create, Action<T?> publish)
    {
        _create = create;
        _publish = publish;
    }

    public T? Current { get; private set; }

    /// <summary>Replaces the current resource: a Ready frame yields a new one; NoFrame/Unsupported clears.</summary>
    public void Present(PresentationFrame frame)
    {
        var next = frame.State == FramePresentationState.Ready ? _create(frame) : null;
        var previous = Current;
        Current = next;
        _publish(next);
        previous?.Dispose();
    }

    /// <summary>Releases the current resource (window/view teardown).</summary>
    public void Dispose()
    {
        var previous = Current;
        Current = null;
        _publish(null);
        previous?.Dispose();
    }
}

/// <summary>Copies a Ready <see cref="PresentationFrame"/> into an Avalonia bitmap.</summary>
[Application]
public static class FrameBitmapFactory
{
    public static Bitmap Create(PresentationFrame frame)
    {
        var bytes = frame.Bgra32.ToArray();
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            // The Bitmap constructor copies the pixels, so the pin is released right after.
            return new Bitmap(
                PixelFormat.Bgra8888, AlphaFormat.Opaque, handle.AddrOfPinnedObject(),
                new PixelSize(frame.Width, frame.Height), new Vector(96, 96), frame.Width * 4);
        }
        finally
        {
            handle.Free();
        }
    }
}
