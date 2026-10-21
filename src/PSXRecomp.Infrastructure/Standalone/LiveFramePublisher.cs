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

/// <summary>
/// The live publish of one run (Issue #818): called at each VBlank on the pump thread. It is observation only. The first
/// failure, of any kind, turns publishing off for the rest of the run and is reported once, so a persistent failure cannot
/// copy the VRAM and throw at every VBlank. It never throws, so the guest's time and result are unaffected.
/// </summary>
[Infrastructure]
public sealed class LiveFrameVBlankPublisher
{
    private readonly LiveFrameSlot _slot;
    private readonly Func<GpuDevice> _gpu;
    private readonly Action<Exception> _report;
    private bool _stopped;

    public LiveFrameVBlankPublisher(LiveFrameSlot slot, Func<GpuDevice> gpu, Action<Exception> report)
    {
        _slot = slot ?? throw new ArgumentNullException(nameof(slot));
        _gpu = gpu ?? throw new ArgumentNullException(nameof(gpu));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>Publishes the display for this VBlank, unless publishing has stopped for the run.</summary>
    public void OnVBlank()
    {
        if (_stopped)
        {
            return;
        }

        try
        {
            LiveFramePublisher.TryPublish(_gpu(), _slot);
        }
        catch (Exception exception)
        {
            _stopped = true;
            try
            {
                _report(exception);
            }
            catch (Exception)
            {
                // The report itself failed. Dropping it keeps the guest's VBlank path unaffected.
            }
        }
    }
}
