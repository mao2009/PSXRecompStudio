using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Gpu;

/// <summary>
/// Decodes a single GP0 command word into its opcode, outcome, and packet word
/// count, per the GPU command model (psx-spx "GPU Command Summary"). The top 3
/// bits select the command family; environment commands (top bits 111) are
/// identified by their full 8-bit opcode.
/// </summary>
/// <remarks>
/// Word counts (from psx-spx):
///   polygon  = 1 + vertices x (1 + textured) + (vertices-1 if gouraud),
///              vertices = 4 if bit27 else 3; the command word carries the
///              first vertex's color, so only the remaining Gouraud colors
///              are separate words
///   line     = 3 (+1 if gouraud); polyline is variable -> discard until terminator
///   rectangle= 2 + textured + variable-size(word when size bits 28-27 == 0)
///   transfer = 3 header words then a streaming data phase (CPU-to-VRAM),
///              or queued for GPUREAD (VRAM-to-CPU)
/// </remarks>
[Domain]
public static class Gp0CommandDecoder
{
    public static Gp0Command Decode(uint word)
    {
        byte _op = (byte)(word >> 24);
        switch (word >> 29)
        {
            case 0: // Misc commands (GP0 00h-1Fh)
                return _op switch
                {
                    0x00 => Executed(_op, 1), // NOP (takes no FIFO space)
                    0x01 => new Gp0Command { Opcode = _op, Result = GpuCommandResult.RecognizedNotImplemented, TotalWords = 1 }, // Clear Cache
                    0x02 => Executed(_op, 3), // Quick Rectangle Fill
                    0x1F => Executed(_op, 1), // Interrupt Request (IRQ1)
                    >= 0x04 and <= 0x1E => Executed(_op, 1), // Mirrors of GP0(00h) - NOP
                    _ => Unsupported(_op, 1), // GP0(03h) - unknown, still occupies FIFO
                };
            case 1: // Polygon primitive (GP0 20h-3Fh)
            {
                bool _gouraud = ((word >> 28) & 1) != 0;
                bool _quad = ((word >> 27) & 1) != 0;
                bool _textured = ((word >> 26) & 1) != 0;
                int _vertices = _quad ? 4 : 3;
                int _vertexWords = _vertices * (1 + (_textured ? 1 : 0));
                int _colorWords = _gouraud ? _vertices - 1 : 0;
                return Rendering(_op, 1 + _vertexWords + _colorWords);
            }
            case 2: // Line primitive (GP0 40h-5Fh; bit28 = Gouraud, bit27 = polyline)
            {
                bool _gouraud = ((word >> 28) & 1) != 0;
                bool _polyline = ((word >> 27) & 1) != 0;
                if (_polyline)
                    return new Gp0Command { Opcode = _op, Result = GpuCommandResult.Unsupported, TotalWords = 1, DiscardUntilTerminator = true };
                return Rendering(_op, 3 + (_gouraud ? 1 : 0));
            }
            case 3: // Rectangle primitive (GP0 60h-7Fh)
            {
                int _size = (int)((word >> 27) & 3);
                bool _textured = ((word >> 26) & 1) != 0;
                int _wordCount = 2 + (_textured ? 1 : 0) + (_size == 0 ? 1 : 0);
                return Rendering(_op, _wordCount);
            }
            case 4: // VRAM-to-VRAM blit (GP0 80h-9Fh; 81h-9Fh mirror GP0(80h))
                return new Gp0Command { Opcode = _op, Result = GpuCommandResult.RecognizedNotImplemented, TotalWords = 4 };
            case 5: // CPU-to-VRAM blit (GP0 A0h-BFh; A1h-BFh mirror GP0(A0h))
                return new Gp0Command { Opcode = _op, Result = GpuCommandResult.Executed, TotalWords = 3, HasDataPhase = true, HeaderWords = 3 };
            case 6: // VRAM-to-CPU blit (GP0 C0h-DFh; C1h-DFh mirror GP0(C0h))
                return new Gp0Command { Opcode = _op, Result = GpuCommandResult.Executed, TotalWords = 3, HasDataPhase = true, HeaderWords = 3 };
            default: // Environment commands (GP0 E0h-FFh)
                return _op switch
                {
                    0xE1 or 0xE2 or 0xE3 or 0xE4 or 0xE5 or 0xE6 => Executed(_op, 1),
                    0xE0 or (>= 0xE7 and <= 0xEF) => Executed(_op, 1), // Mirrors of GP0(00h) - NOP
                    _ => Unsupported(_op, 1), // GP0(F0h-FFh) - undocumented
                };
        }
    }

    /// <summary>
    /// PS1 polyline terminator rule: bits 12-15 and 28-31 both equal 5, i.e.
    /// <c>(word &amp; 0xF000F000) == 0x50005000</c>. Covers the usual
    /// <c>0x55555555h</c> and the Wild Arms 2 <c>0x50005000h</c> forms. The
    /// terminator may appear in the Gouraud color word, so it must be checked
    /// against every polyline word.
    /// </summary>
    public static bool IsPolylineTerminator(uint word) => (word & 0xF000F000) == 0x50005000;

    private static Gp0Command Executed(byte op, int totalWords) =>
        new() { Opcode = op, Result = GpuCommandResult.Executed, TotalWords = totalWords };

    private static Gp0Command Rendering(byte op, int totalWords) =>
        new() { Opcode = op, Result = GpuCommandResult.DecodedPendingRasterization, TotalWords = totalWords };

    private static Gp0Command Unsupported(byte op, int totalWords) =>
        new() { Opcode = op, Result = GpuCommandResult.Unsupported, TotalWords = totalWords };
}