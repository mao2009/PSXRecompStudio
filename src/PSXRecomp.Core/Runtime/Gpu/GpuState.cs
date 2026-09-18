using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Gpu;

/// <summary>
/// Pure domain model of the GPU control state. No I/O, no side effects.
/// All fields are named registers; <see cref="GpuDevice.ReadGpustat"/> derives
/// every GPUSTAT bit from these fields (plus transient packet flags), rather
/// than from a magic-status word.
/// </summary>
[Domain]
public sealed class GpuState
{
    /// <summary>GP0(E1h) - Draw mode / Texpage.</summary>
    public uint TexturePage { get; set; }

    /// <summary>GP0(E2h) - Texture window. Returned by GP1(10h:02h).</summary>
    public uint TextureWindow { get; set; }

    /// <summary>GP0(E3h) - Drawing area top-left. Returned by GP1(10h:03h).</summary>
    public uint DrawAreaTopLeft { get; set; }

    /// <summary>GP0(E4h) - Drawing area bottom-right. Returned by GP1(10h:04h).</summary>
    public uint DrawAreaBottomRight { get; set; }

    /// <summary>GP0(E5h) - Drawing offset. Returned by GP1(10h:05h).</summary>
    public uint DrawOffset { get; set; }

    /// <summary>GP0(E6h) - Mask-bit setting (bit0 = set mask, bit1 = check mask).</summary>
    public uint MaskSetting { get; set; }

    /// <summary>GP1(03h).0 - Display on (0=On, 1=Off).</summary>
    public bool DisplayEnabled { get; set; }

    /// <summary>GP1(04h).0-1 - DMA direction / data request mode.</summary>
    public GpuDmaDirection DmaDirection { get; set; }

    /// <summary>GP1(05h) - Display area start address in VRAM (10-bit X, 9/10-bit Y).</summary>
    public uint DisplayVramStart { get; set; }

    /// <summary>GP1(06h) - Horizontal display range on screen (X1/X2 in video clock units).</summary>
    public uint HorizontalDisplayRange { get; set; }

    /// <summary>GP1(07h) - Vertical display range on screen (Y1/Y2 in scanlines).</summary>
    public uint VerticalDisplayRange { get; set; }

    /// <summary>GP1(08h) - Display mode (resolution/video mode/color depth/interlace).</summary>
    public uint DisplayMode { get; set; }

    /// <summary>GP1(09h).0 - Allow Y coordinates in 512-1023 (v2 GPU; not honored by the 1 MiB buffer).</summary>
    public uint VramSizeSetting { get; set; }

    /// <summary>GP0(1Fh)/GP1(02h) - Interrupt request (IRQ1) pending flag; GPUSTAT.24.</summary>
    public bool IrqRequested { get; set; }

    /// <summary>
    /// GPUSTAT.13 while vertical interlace is on. Not timing-driven (no scanline
    /// model in this issue); kept as named state, defaulting to false.
    /// </summary>
    public bool InterlaceField { get; set; }

    public void Reset()
    {
        TexturePage = 0;
        TextureWindow = 0;
        DrawAreaTopLeft = 0;
        DrawAreaBottomRight = 0;
        DrawOffset = 0;
        MaskSetting = 0;
        DisplayEnabled = false;
        DmaDirection = GpuDmaDirection.Off;
        DisplayVramStart = 0;
        HorizontalDisplayRange = 0;
        VerticalDisplayRange = 0;
        DisplayMode = 0;
        VramSizeSetting = 0;
        IrqRequested = false;
        InterlaceField = false;
    }
}

/// <summary>GP1(04h) DMA direction / data request mode.</summary>
[Domain]
public enum GpuDmaDirection
{
    Off = 0,
    Fifo = 1,
    CpuToGp0 = 2,
    GpureadToCpu = 3,
}

/// <summary>
/// Disposition of a GP0 command after decoding. The four states are part of the
/// GPU runtime contract so callers and tools can distinguish a command that was
/// executed from one merely recognized or explicitly unsupported.
/// </summary>
[Domain]
public enum GpuCommandResult
{
    /// <summary>Executed by the modeled GPU (state updated and/or VRAM transfer performed).</summary>
    Executed = 0,

    /// <summary>Decoded and accumulated; rasterization itself is deferred to Issue #441.</summary>
    DecodedPendingRasterization,

    /// <summary>Recognized hardware command with no modeled behavior yet; safely ignored.</summary>
    RecognizedNotImplemented,

    /// <summary>Unknown or undocumented command; explicitly surfaced as unsupported.</summary>
    Unsupported,
}

/// <summary>
/// Fully decoded GP0 command: classification plus the total number of 32-bit
/// words the packet occupies (command word + parameters). <see cref="HasDataPhase"/>
/// marks streaming transfers whose bodies (after <see cref="HeaderWords"/>) are
/// consumed one 32-bit word at a time rather than buffered.
/// <see cref="DiscardUntilTerminator"/> marks variable-length packets (polyline)
/// whose payload is consumed word-by-word until the
/// <see cref="Gp0CommandDecoder.IsPolylineTerminator"/> rule fires; no fixed
/// <see cref="TotalWords"/> exists for them.
/// </summary>
[Domain]
public readonly struct Gp0Command
{
    public byte Opcode { get; init; }
    public GpuCommandResult Result { get; init; }
    public int TotalWords { get; init; }
    public bool HasDataPhase { get; init; }
    public int HeaderWords { get; init; }
    public bool DiscardUntilTerminator { get; init; }
}

/// <summary>
/// A drawing primitive packet (polygon/line/rectangle) accumulated and decoded
/// but not rasterized. Kept for Issue #441, which will consume
/// <see cref="Command"/> and <see cref="Parameters"/> to render into VRAM.
/// </summary>
[Domain]
public readonly record struct GpuPrimitivePacket(Gp0Command Command, IReadOnlyList<uint> Parameters);