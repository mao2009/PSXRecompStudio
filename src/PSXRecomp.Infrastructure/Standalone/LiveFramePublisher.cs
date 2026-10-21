using PSXRecomp.Architecture;
using PSXRecomp.Core.Runtime.Gpu;
using PSXRecomp.Infrastructure.Runtime.Gpu;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// Publishes the display the GPU shows now into a <see cref="LiveFrameSlot"/> (Issue #818). The capture reuses the
/// headless screenshot path: <see cref="GpuDevice.CaptureFrame"/> copies the pixels, and <see cref="ScreenshotCapture.ToBgra32"/>
/// converts 15-bit and 24-bit modes.
/// </summary>
/// <remarks>
/// Must run on the thread that owns the GPU/VRAM. The Runtime calls it from the VBlank point, which runs on the host-transfer
/// pump thread. The copy is taken there, so the slot's readers never touch the GPU.
/// </remarks>
[Infrastructure]
public static class LiveFramePublisher
{
    /// <summary>Publishes the current display; false when the guest has not drawn anything or nothing is displayable.</summary>
    public static bool TryPublish(GpuDevice gpu, LiveFrameSlot slot)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(slot);

        // Nothing is copied unless a reader is waiting: a run nobody displays does not read the VRAM at every VBlank.
        if (!slot.ReaderIsWaiting)
        {
            return false;
        }

        // Same gate as the run-end frame output: no evidence means the guest drew nothing, so nothing is published.
        if (!gpu.HasFrameEvidence)
        {
            return false;
        }

        var frame = gpu.CaptureFrame();
        var bgra = ScreenshotCapture.ToBgra32(frame, gpu);
        if (bgra.Length == 0)
        {
            return false;
        }

        slot.Publish(frame.Width, frame.Height, bgra);
        return true;
    }
}
