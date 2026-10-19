using System.IO;
using System.Text.Json;
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
    private readonly List<CapturedFrame> _frames = new();
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

            if (!CaptureAndSave(currentVblank))
            {
                _lastVblank = currentVblank; // unsaved target is dropped, not retried (not counted, not consumed)
                return false;
            }
            _capturedVblanks.Add(currentVblank);
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
                if (!CaptureAndSave(targetVblank))
                    continue; // nothing saved: this target is not counted or consumed
                _capturedVblanks.Add(targetVblank);
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
        if (CaptureAndSave(currentVblank, customName))
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

            if (!CaptureAndSave(vblank))
            {
                _lastVblank = vblank; // unsaved target is dropped, not retried (not counted, not consumed)
                return false;
            }
            _capturedVblanks.Add(vblank);
            _screenshotsTaken++;
            _lastVblank = vblank;
            return true;
        }

        // Also check if we've passed any target VBlanks since last check
        foreach (var targetVblank in _targetVblanks)
        {
            if (targetVblank <= vblank && targetVblank > _lastVblank && !_capturedVblanks.Contains(targetVblank))
            {
                if (!CaptureAndSave(targetVblank))
                    continue; // nothing saved: this target is not counted or consumed
                _capturedVblanks.Add(targetVblank);
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

    /// <summary>
    /// Saves one frame as PNG and records it in the manifest. Returns false (and writes nothing) when the frame has no
    /// displayable pixels, so callers count only frames that really exist on disk (Issue #751).
    /// </summary>
    private bool CaptureAndSave(ulong vblank, string? customName = null)
    {
        var frame = _gpuDevice.CaptureFrame();
        var frameHash = Convert.ToHexString(frame.ComputeStableHash()).ToLowerInvariant();
        var bgra = ConvertToBgra32(frame);

        if (bgra.Length == 0)
            return false; // Nothing to save

        var fileName = customName ?? $"persona_vblank_{vblank:D4}.png";
        var filePath = Path.Combine(_outputDirectory, fileName);

        // Write beside the target and rename so an interrupted run never leaves a truncated PNG (Issue #751).
        var tempPath = filePath + ".tmp";
        PngWriter.Save(tempPath, bgra, frame.Width, frame.Height);
        File.Move(tempPath, filePath, overwrite: true);

        var mode = _gpuDevice.GetState().DisplayMode;
        _frames.Add(new CapturedFrame(fileName, vblank, frameHash, frame.Width, frame.Height, mode, (mode & 0x10) != 0));
        return true;
    }

    /// <summary>Full paths of the PNG files this session actually wrote, in capture order (matches the manifest).</summary>
    public IReadOnlyList<string> SavedFiles =>
        _frames.Select(f => Path.GetFullPath(Path.Combine(_outputDirectory, f.File))).ToList();

    /// <summary>
    /// Writes <c>manifest.json</c> into the output directory: the stop reason, the final guest PC (when known) and, per
    /// saved PNG, the VBlank (guest frame) index, size, display mode, pixel hash and whether the 24-bit path was used
    /// (false means the frame was converted as legacy 15-bit). Written atomically; safe to call after a partial run.
    /// </summary>
    public void WriteManifest(string stopReason, ulong maxVblankReached, TimeSpan elapsed, uint? lastGuestPc)
    {
        var manifest = new
        {
            stopReason,
            screenshotsTaken = _frames.Count,
            maxVblankReached,
            elapsedMs = (long)elapsed.TotalMilliseconds,
            lastGuestPc = lastGuestPc is { } pc ? $"0x{pc:X8}" : null,
            frames = _frames,
        };
        var path = Path.Combine(_outputDirectory, "manifest.json");
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(manifest, ManifestOptions));
        File.Move(tempPath, path, overwrite: true);
    }

    private static readonly JsonSerializerOptions ManifestOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record CapturedFrame(string File, ulong Vblank, string Sha256, int Width, int Height, uint DisplayMode, bool Is24Bit);

    /// <summary>
    /// Converts a FrameSnapshot's VRAM pixels to BGRA32 format.
    /// Handles both 15-bit (5-5-5) and 24-bit (8-8-8) display modes.
    /// </summary>
    private byte[] ConvertToBgra32(FrameSnapshot frame) => ToBgra32(frame, _gpuDevice);

    /// <summary>
    /// Converts <paramref name="frame"/>, captured from <paramref name="gpuDevice"/>, to BGRA32 in whichever display colour
    /// depth the GPU is currently in. Shared by the headless screenshot service and the standalone Runtime host (Issue #762).
    /// </summary>
    public static byte[] ToBgra32(FrameSnapshot frame, GpuDevice gpuDevice)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(gpuDevice);
        if (frame.Width <= 0 || frame.Height <= 0)
            return [];

        var gpuState = gpuDevice.GetState();
        bool is24BitMode = (gpuState.DisplayMode & 0x10) != 0; // Bit 4: 24-bit color depth

        if (is24BitMode)
        {
            return Convert24BitToBgra32(frame, gpuState, gpuDevice);
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
    /// DisplayVramStart X is in 16-bit halfword units; byte offset = startX * 2. Each pixel = 3 bytes.
    /// </summary>
    private static byte[] Convert24BitToBgra32(FrameSnapshot frame, GpuState gpuState, GpuDevice gpuDevice)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            return [];

        // Get display region from GPU state
        int startX = (int)(gpuState.DisplayVramStart & 0x3FF);
        int startY = (int)((gpuState.DisplayVramStart >> 10) & 0x1FF);
        // FrameSnapshot already applies mode-aware scanline clipping (15-bit vs 24-bit).
        // Keep the PNG dimensions identical to the captured frame and never read
        // three-byte RGB pixels across the 2048-byte VRAM row boundary.
        int maxWidthInRow = ((GpuVram.Width - startX) * sizeof(ushort)) / 3;
        int width = Math.Min(frame.Width, maxWidthInRow);
        int height = Math.Min(frame.Height, GpuVram.Height - startY);

        return Convert24BitRegionToBgra32(gpuDevice.GetVramBytes(), startX, startY, width, height);
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