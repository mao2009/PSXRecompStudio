using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PSXRecomp.Architecture;
using PSXRecompStudio.Services;

namespace PSXRecompStudio.Views;

/// <summary>Copies a <see cref="PresentationFrame"/> into an Avalonia bitmap; null unless the frame is Ready.</summary>
[Application]
public sealed class FrameBitmapConverter : IValueConverter
{
    public static FrameBitmapConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not PresentationFrame { State: FramePresentationState.Ready } frame)
        {
            return null;
        }

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

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
