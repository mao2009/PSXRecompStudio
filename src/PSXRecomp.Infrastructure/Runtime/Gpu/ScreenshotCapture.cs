using System.IO;
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
    private ulong _lastVblank = 0;
    private bool _disposed;

    /// <summary>The output directory for screenshots.</summary>
    public string OutputDirectory => _outputDirectory;

    /// <summary>The GPU device for diagnostic access.</summary>
    internal GpuDevice GpuDevice => _gpuDevice;

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
        
        // Check for exact match first
        var targetIndex = _targetVblanks.BinarySearch(currentVblank);
        if (targetIndex >= 0)
        {
            if (_capturedVblanks.Contains(currentVblank))
                return false; // Already captured this VBlank (duplicate notification)

            _capturedVblanks.Add(currentVblank);
            CaptureAndSave(currentVblank);
            _screenshotsTaken++;
            _lastVblank = currentVblank;
            return true;
        }

        // Also check if we've passed any target VBlanks since last check
        // This handles cases where VBlank counter jumps over target values
        foreach (var targetVblank in _targetVblanks)
        {
            if (targetVblank <= currentVblank && targetVblank > _lastVblank && !_capturedVblanks.Contains(targetVblank))
            {
                _capturedVblanks.Add(targetVblank);
                CaptureAndSave(targetVblank);
                _screenshotsTaken++;
                _lastVblank = targetVblank;
                return true;
            }
        }

        _lastVblank = currentVblank;
        return false;
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
        _screenshotsTaken++;
    }

    /// <summary>
    /// Attempts to capture a screenshot at a specific VBlank count (from VBlank callback).
    /// </summary>
    /// <param name="vblank">The VBlank count at which the interrupt occurred.</param>
    /// <returns>True if a screenshot was captured, false otherwise.</returns>
    public bool TryCaptureAtVBlank(ulong vblank)
    {
        if (_disposed || _screenshotsTaken >= _maxScreenshots)
            return false;

        // Check for exact match first
        var targetIndex = _targetVblanks.BinarySearch(vblank);
        if (targetIndex >= 0)
        {
            if (_capturedVblanks.Contains(vblank))
                return false; // Already captured this VBlank

            _capturedVblanks.Add(vblank);
            CaptureAndSave(vblank);
            _screenshotsTaken++;
            _lastVblank = vblank;
            return true;
        }

        // Also check if we've passed any target VBlanks since last check
        foreach (var targetVblank in _targetVblanks)
        {
            if (targetVblank <= vblank && targetVblank > _lastVblank && !_capturedVblanks.Contains(targetVblank))
            {
                _capturedVblanks.Add(targetVblank);
                CaptureAndSave(targetVblank);
                _screenshotsTaken++;
                _lastVblank = targetVblank;
                return true;
            }
        }

        _lastVblank = vblank;
        return false;
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
        var frameHash = frame.ComputeStableHash();
        var frameHashStr = Convert.ToHexString(frameHash)[..16];
        var bgra = ConvertToBgra32(frame);

        if (bgra.Length == 0)
            return; // Nothing to save

        var fileName = customName ?? $"persona_vblank_{vblank:D4}.png";
        var filePath = Path.Combine(_outputDirectory, fileName);

        PngWriter.Save(filePath, bgra, frame.Width, frame.Height);
    }

    /// <summary>
    /// Converts a FrameSnapshot's VRAM pixels to BGRA32 format.
    /// Handles both 15-bit (5-5-5) and 24-bit (8-8-8) display modes.
    /// </summary>
    private byte[] ConvertToBgra32(FrameSnapshot frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            return [];

        var gpuState = _gpuDevice.GetState();
        bool is24BitMode = (gpuState.DisplayMode & 0x10) != 0; // Bit 4: 24-bit color depth

        if (is24BitMode)
        {
            return Convert24BitToBgra32(frame, gpuState);
        }
        else
        {
            return Convert15BitToBgra32(frame);
        }
    }

    /// <summary>
    /// Converts 15-bit (5-5-5) VRAM pixels to BGRA32 format.
    /// </summary>
    private static byte[] Convert15BitToBgra32(FrameSnapshot frame)
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

    /// <summary>
    /// Converts 24-bit (8-8-8) VRAM pixels to BGRA32 format.
    /// In 24-bit mode, VRAM stores 24-bit pixels as 3 bytes per pixel (R, G, B).
    /// VRAM is byte-addressed in 24-bit mode with stride of 1024 * 2 = 2048 bytes (same halfword layout, read as bytes).
    /// </summary>
    private byte[] Convert24BitToBgra32(FrameSnapshot frame, GpuState gpuState)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            return [];

        // Get display region from GPU state
        int startX = (int)(gpuState.DisplayVramStart & 0x3FF);
        int startY = (int)((gpuState.DisplayVramStart >> 10) & 0x1FF);
        var resolution = _gpuDevice.GetDisplayResolution();
        int width = Math.Max(0, Math.Min((int)resolution.Width, GpuVram.Width - startX));
        int height = Math.Max(0, Math.Min((int)resolution.Height, GpuVram.Height - startY));

        return Convert24BitRegionToBgra32(_gpuDevice.GetVramBytes(), startX, startY, width, height);
    }

    /// <summary>
    /// Converts a 24-bit (8-8-8) VRAM region to BGRA32. VRAM stores 3 bytes per pixel (R, G, B),
    /// and each row is byte-addressed with a fixed <see cref="GpuVram.Width"/> * 2 byte stride; Y wraps
    /// at the 512-line VRAM bound. <paramref name="startX"/> is the GP1(05h) display origin, a halfword
    /// address, so its byte offset is <c>startX * 2</c> and each pixel then advances 3 bytes.
    /// Internal so the display conversion is testable directly.
    /// </summary>
    internal static byte[] Convert24BitRegionToBgra32(ReadOnlySpan<byte> vramBytes, int startX, int startY, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return [];

        var bgra = new byte[width * height * 4];

        // VRAM stride = 1024 halfwords * 2 bytes per row.
        const int VramRowStride = GpuVram.Width * 2;

        for (int y = 0; y < height; y++)
        {
            int vramRowOffset = ((startY + y) & 0x1FF) * VramRowStride;

            for (int x = 0; x < width; x++)
            {
                // startX is a halfword address; the 24-bit pixel stream is 3 bytes per pixel.
                int vramByteOffset = vramRowOffset + startX * 2 + x * 3;

                // Bounds check
                if (vramByteOffset + 2 >= vramBytes.Length)
                    break;

                // VRAM stores RGB (R, G, B) in 24-bit mode
                byte r = vramBytes[vramByteOffset];
                byte g = vramBytes[vramByteOffset + 1];
                byte b = vramBytes[vramByteOffset + 2];

                int dstIdx = (y * width + x) * 4;
                bgra[dstIdx + 0] = b;  // B
                bgra[dstIdx + 1] = g;  // G
                bgra[dstIdx + 2] = r;  // R
                bgra[dstIdx + 3] = 0xFF; // A
            }
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