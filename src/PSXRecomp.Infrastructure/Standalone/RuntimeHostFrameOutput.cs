using PSXRecomp.Architecture;
using PSXRecomp.Infrastructure.Runtime.Gpu;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// Writes the display the packaged runtime host's guest left in the shared GPU/VRAM state as a PNG (Issue #762;
/// <c>PSXRecomp.RuntimeHost --frame-out &lt;path&gt;</c>). It reuses the existing <c>FrameSnapshot</c> capture and BGRA
/// conversion of the headless screenshot path; there is no second GPU or rasterizer here.
/// </summary>
/// <remarks>
/// Observation only, at the end of the run: the run's exit code and output never depend on it. When the guest drew
/// nothing the file is not written and the absence is reported, never a blank or fabricated image. A live window is
/// out of scope (Issue #630).
/// </remarks>
[Infrastructure]
public static class RuntimeHostFrameOutput
{
    /// <summary>Writes the engine's last displayed frame to <paramref name="path"/>; returns whether a frame was written.</summary>
    public static bool TryWrite(RecompiledHostExecutionEngine engine, string path, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            if (engine.LastFrameBgra32 is not { } frame)
            {
                error.WriteLine("runtime host: no frame (the guest drew nothing to the display); nothing written.");
                return false;
            }

            PngWriter.Save(path, frame.Bgra, frame.Width, frame.Height);
            error.WriteLine($"runtime host: frame {frame.Width}x{frame.Height} written to {path}");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Best-effort observation: a frame-write failure must not change the run's classified result.
            error.WriteLine($"runtime host: could not write the frame to {path}: {exception.Message}");
            return false;
        }
    }
}
