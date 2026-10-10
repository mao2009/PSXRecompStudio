using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Runtime;
using PSXRecomp.Core.Runtime.Gpu;

namespace PSXRecomp.Infrastructure.Runtime.Gpu;

/// <summary>
/// Captures GPU frames as PNG images at specified VBlank intervals.
/// Infrastructure layer: handles file I/O for headless screenshot capture.
/// </summary>
[Infrastructure]
public sealed class ScreenshotCapture : IDisposable
{
    private readonly GpuDevice _gpuDevice;
    private readonly DeviceScheduler _scheduler;
    private readonly string _outputDirectory;
    private readonly uint _interval;
    private readonly int _maxScreenshots;
    private readonly List<ulong> _targetVblanks;
    private readonly HashSet<ulong> _capturedVblanks = new();
    private int _screenshotsTaken;
    private bool _disposed;

    /// <summary>The output directory for screenshots.</summary>
    public string OutputDirectory => _outputDirectory;

    /// <summary>
    /// Creates a screenshot capture service.
    /// </summary>
    /// <param name="gpuDevice">The GPU device to capture frames from.</param>
    /// <param name="scheduler">The device scheduler to read VBlank count from.</param>
    /// <param name="outputDirectory">Directory to save PNG files.</param>
    /// <param name="interval">VBlank interval between captures (default 300).</param>
    /// <param name="maxScreenshots">Maximum number of screenshots to capture (default 10).</param>
    /// <param name="startVblank">First VBlank to capture at (default 300).</param>
    public ScreenshotCapture(
        GpuDevice gpuDevice,
        DeviceScheduler scheduler,
        string outputDirectory,
        uint interval = 300,
        int maxScreenshots = 10,
        ulong startVblank = 300)
    {
        ArgumentNullException.ThrowIfNull(gpuDevice);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        ArgumentOutOfRangeException.ThrowIfZero(interval);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxScreenshots);

        _gpuDevice = gpuDevice;
        _scheduler = scheduler;
        _outputDirectory = outputDirectory;
        _interval = interval;
        _maxScreenshots = maxScreenshots;

        _targetVblanks = new List<ulong>(maxScreenshots);
        for (int i = 1; i <= maxScreenshots; i++)
        {
            _targetVblanks.Add(startVblank + (ulong)(i - 1) * interval);
        }

        Directory.CreateDirectory(_outputDirectory);
    }

    /// <summary>
    /// Checks if a screenshot should be captured at the current VBlank count.
    /// If so, captures and saves the frame as a PNG file.
    /// </summary>
    /// <returns>True if a screenshot was captured, false otherwise.</returns>
    public bool TryCapture()
    {
        if (_disposed || _screenshotsTaken >= _maxScreenshots)
            return false;

        var currentVblank = _scheduler.VblankCount;
        var targetIndex = _targetVblanks.BinarySearch(currentVblank);
        if (targetIndex < 0)
            return false; // Current VBlank is not a target

        if (_capturedVblanks.Contains(currentVblank))
            return false; // Already captured this VBlank (duplicate notification)

        _capturedVblanks.Add(currentVblank);
        CaptureAndSave(currentVblank);
        _screenshotsTaken++;
        return true;
    }

    /// <summary>
    /// Forces a capture at the current VBlank count, regardless of interval.
    /// </summary>
    public void ForceCapture(string? customName = null)
    {
        if (_disposed)
            return;

        var currentVblank = _scheduler.VblankCount;
        CaptureAndSave(currentVblank, customName);
    }

    /// <summary>
    /// Gets the number of screenshots captured so far.
    /// </summary>
    public int ScreenshotsTaken => _screenshotsTaken;

    /// <summary>
    /// Gets whether the maximum number of screenshots has been reached.
    /// </summary>
    public bool IsComplete => _screenshotsTaken >= _maxScreenshots;

    /// <summary>
    /// Gets the next target VBlank, or null if all targets have been passed.
    /// </summary>
    public ulong? NextTargetVblank
    {
        get
        {
            var current = _scheduler.VblankCount;
            foreach (var target in _targetVblanks)
            {
                if (target > current && !_capturedVblanks.Contains(target))
                    return target;
            }
            return null;
        }
    }

    private void CaptureAndSave(ulong vblank, string? customName = null)
    {
        var frame = _gpuDevice.CaptureFrame();
        var bgra = ConvertToBgra32(frame);

        if (bgra.Length == 0)
            return; // Nothing to save

        var fileName = customName ?? $"persona_vblank_{vblank:D4}.png";
        var filePath = Path.Combine(_outputDirectory, fileName);

        // Save as PNG using ImageSharp
        using var image = Image.LoadPixelData<Bgra32>(bgra, frame.Width, frame.Height);
        image.SaveAsPng(filePath);
    }

    /// <summary>
    /// Converts a FrameSnapshot's native 15-bit VRAM pixels to BGRA32 format.
    /// </summary>
    private static byte[] ConvertToBgra32(FrameSnapshot frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            return [];

        var bgra = new byte[frame.Width * frame.Height * 4];
        var pixels = frame.Pixels;
        for (int i = 0; i < pixels.Count; i++)
        {
            int v = pixels[i];
            // 5-bit -> 8-bit by bit replication; bit 15 (mask/STP) is not display alpha.
            bgra[(i * 4) + 0] = Expand5To8((v >> 10) & 0x1F); // B
            bgra[(i * 4) + 1] = Expand5To8((v >> 5) & 0x1F);  // G
            bgra[(i * 4) + 2] = Expand5To8(v & 0x1F);         // R
            bgra[(i * 4) + 3] = 0xFF;                          // A
        }
        return bgra;
    }

    private static byte Expand5To8(int c5) => (byte)((c5 << 3) | (c5 >> 2));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
    }
}

/// <summary>
/// Result of a screenshot capture session.
/// </summary>
[Infrastructure]
public sealed record ScreenshotSessionResult(
    int ScreenshotsTaken,
    ulong MaxVblankReached,
    IReadOnlyList<string> SavedFiles,
    TimeSpan ElapsedTime,
    string StopReason);