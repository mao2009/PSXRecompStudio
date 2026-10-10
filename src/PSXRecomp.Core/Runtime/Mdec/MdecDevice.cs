using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Runtime.Mdec;

/// <summary>
/// PS1 Motion Decoder (Issue #732): command/parameter port 0x1F801820, status/control 0x1F801824, fed by DMA0 and
/// drained by DMA1 (see <see cref="Dma.MdecDmaTransfer"/>).
/// </summary>
/// <remarks>
/// Commands (psx-spx "MDEC"): 1 decode macroblocks (run-length → dequantise → IDCT → YUV→RGB, 4/8/15/24-bit
/// output), 2 set the quantisation tables, 3 set the IDCT scale table. The arithmetic follows DuckStation's reference
/// MDEC (rl_decode_block, IDCT_Old, YUVToRGB_Old). ponytail: a command is processed at once when its last parameter
/// word arrives, so the input and output FIFOs are unbounded and there is no decode time; add FIFO limits and
/// timing when a title depends on them.
/// </remarks>
[Domain]
public sealed class MdecDevice : IMdec
{
    /// <summary>Status after reset: output FIFO empty, current block 4, no parameters remaining.</summary>
    public const uint ResetStatus = 0x80040000u;

    private static readonly byte[] ZagZig = BuildZagZig();

    private readonly Queue<uint> _output = new();
    private readonly List<uint> _parameters = new();
    private readonly byte[] _lumaQuant = new byte[64];
    private readonly byte[] _chromaQuant = new byte[64];
    private readonly short[] _scale = new short[64];
    private uint _command;
    private int _remaining;
    private uint _control;

    public bool IsBusy => _remaining > 0;

    /// <summary>Words waiting in the output FIFO.</summary>
    public int FifoWordCount => _output.Count;

    /// <summary>DMA0 request: input enabled (control bit 30) and a command is collecting parameters.</summary>
    public bool DataInRequest => (_control & (1u << 30)) != 0 && _remaining > 0;

    /// <summary>DMA1 request: output enabled (control bit 29) and the output FIFO holds data.</summary>
    public bool DataOutRequest => (_control & (1u << 29)) != 0 && _output.Count > 0;

    public uint ReadRegister(uint offset) => (offset & 4) == 0 ? ReadData() : ReadStatus();

    public void WriteRegister(uint offset, uint value)
    {
        if ((offset & 4) == 0)
        {
            WriteData(value);
            return;
        }

        if ((value & 0x80000000u) != 0)
        {
            Reset();
        }

        _control = value & 0x60000000u;
    }

    public void Reset()
    {
        _output.Clear();
        _parameters.Clear();
        _command = 0;
        _remaining = 0;
        _control = 0;
    }

    /// <summary>The next output word, or 0 when the FIFO is empty.</summary>
    public uint ReadData() => _output.TryDequeue(out var word) ? word : 0;

    /// <summary>A command word, or the next parameter of the current command.</summary>
    public void WriteData(uint word)
    {
        if (_remaining == 0)
        {
            _command = word;
            _remaining = (word >> 29) switch
            {
                2 => (word & 1) != 0 ? 32 : 16,
                3 => 32,
                _ => (int)(word & 0xFFFF),
            };
            if (_remaining == 0)
            {
                Execute();
            }

            return;
        }

        _parameters.Add(word);
        if (--_remaining == 0)
        {
            Execute();
        }
    }

    /// <summary>
    /// psx-spx status: 31 output FIFO empty, 30 input FIFO full (never), 29 busy, 28 DMA0 request, 27 DMA1 request,
    /// 26-23 the current command's output format, 18-16 current block, 15-0 parameter words remaining minus one.
    /// </summary>
    public uint ReadStatus()
    {
        // psx-spx: reset sets status 80040000h; once a command has started, bits 15-0 count down to FFFFh.
        var remaining = _command == 0 && _remaining == 0 ? 0 : (uint)(_remaining - 1) & 0xFFFF;
        var status = ((_command >> 2) & 0x07800000u) | (4u << 16) | remaining;
        if (_output.Count == 0) status |= 1u << 31;
        if (_remaining > 0) status |= 1u << 29;
        if (DataInRequest) status |= 1u << 28;
        if (DataOutRequest) status |= 1u << 27;
        return status;
    }

    private void Execute()
    {
        var words = _parameters.ToArray();
        _parameters.Clear();
        switch (_command >> 29)
        {
            case 1:
                Decode(words);
                break;
            case 2:
                for (var i = 0; i < 64; i++)
                {
                    _lumaQuant[i] = (byte)(words[i / 4] >> (8 * (i % 4)));
                    if (words.Length > 16)
                    {
                        _chromaQuant[i] = (byte)(words[16 + i / 4] >> (8 * (i % 4)));
                    }
                }

                break;
            case 3:
                for (var i = 0; i < 64; i++)
                {
                    _scale[i] = (short)(words[i / 2] >> (16 * (i % 2)));
                }

                break;
        }
    }

    private void Decode(uint[] words)
    {
        var input = new ushort[words.Length * 2];
        for (var i = 0; i < words.Length; i++)
        {
            input[2 * i] = (ushort)words[i];
            input[2 * i + 1] = (ushort)(words[i] >> 16);
        }

        var depth = (_command >> 27) & 3;
        var position = 0;
        var rgb = new uint[256];
        var blocks = new short[6][];
        for (var b = 0; b < 6; b++) blocks[b] = new short[64];
        while (true)
        {
            if (depth < 2)
            {
                if (!DecodeBlock(input, ref position, blocks[0], _lumaQuant)) return;
                for (var i = 0; i < 64; i++)
                {
                    rgb[i] = (byte)(Math.Clamp((int)blocks[0][i], -128, 127) + AddValue);
                }

                EmitMono(rgb, depth);
                continue;
            }

            for (var b = 0; b < 6; b++)
            {
                if (!DecodeBlock(input, ref position, blocks[b], b < 2 ? _chromaQuant : _lumaQuant)) return;
            }

            YuvToRgb(rgb, 0, 0, blocks[0], blocks[1], blocks[2]);
            YuvToRgb(rgb, 8, 0, blocks[0], blocks[1], blocks[3]);
            YuvToRgb(rgb, 0, 8, blocks[0], blocks[1], blocks[4]);
            YuvToRgb(rgb, 8, 8, blocks[0], blocks[1], blocks[5]);
            EmitColor(rgb, depth);
        }
    }

    private int AddValue => (_command & (1u << 26)) != 0 ? 0 : 0x80;

    /// <summary>One run-length coded 8x8 block, dequantised and inverse transformed. False when input runs out.</summary>
    private bool DecodeBlock(ushort[] input, ref int position, short[] block, byte[] quant)
    {
        Array.Clear(block);
        ushort n;
        do
        {
            if (position >= input.Length) return false;
            n = input[position++];
        }
        while (n == 0xFE00);

        var qScale = (n >> 10) & 0x3F;
        var k = 0;
        var value = SignExtend10(n) * quant[0];
        while (true)
        {
            if (qScale == 0) value = SignExtend10(n) * 2;
            value = Math.Clamp(value, -0x400, 0x3FF);
            block[qScale > 0 ? ZagZig[k] : k] = (short)value;
            if (position >= input.Length) return false;
            n = input[position++];
            k += ((n >> 10) & 0x3F) + 1;
            if (k > 63) break;
            value = (SignExtend10(n) * quant[k] * qScale + 4) / 8;
        }

        Idct(block);
        return true;
    }

    private void Idct(short[] block)
    {
        var temp = new long[64];
        for (var x = 0; x < 8; x++)
        for (var y = 0; y < 8; y++)
        {
            long sum = 0;
            for (var u = 0; u < 8; u++) sum += block[u * 8 + x] * _scale[u * 8 + y];
            temp[x + y * 8] = sum;
        }

        for (var x = 0; x < 8; x++)
        for (var y = 0; y < 8; y++)
        {
            long sum = 0;
            for (var u = 0; u < 8; u++) sum += temp[u + y * 8] * _scale[u * 8 + x];
            var rounded = (int)((sum >> 32) + ((sum >> 31) & 1));
            block[x + y * 8] = (short)Math.Clamp((rounded << 23) >> 23, -128, 127);
        }
    }

    private void YuvToRgb(uint[] rgb, int xx, int yy, short[] cr, short[] cb, short[] luma)
    {
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
        {
            float r = cr[(x + xx) / 2 + (y + yy) / 2 * 8];
            float b = cb[(x + xx) / 2 + (y + yy) / 2 * 8];
            var g = (int)(-0.3437f * b + -0.7143f * r);
            var lum = luma[x + y * 8];
            var red = (byte)(Math.Clamp(lum + (int)(1.402f * r), -128, 127) + AddValue);
            var green = (byte)(Math.Clamp(lum + g, -128, 127) + AddValue);
            var blue = (byte)(Math.Clamp(lum + (int)(1.772f * b), -128, 127) + AddValue);
            rgb[x + xx + (y + yy) * 16] = red | (uint)green << 8 | (uint)blue << 16;
        }
    }

    private void EmitColor(uint[] rgb, uint depth)
    {
        if (depth == 3)
        {
            var bit15 = (_command & (1u << 25)) != 0 ? 0x8000u : 0;
            for (var i = 0; i < 256; i += 2)
            {
                _output.Enqueue(To15(rgb[i]) | bit15 | (To15(rgb[i + 1]) | bit15) << 16);
            }

            return;
        }

        var bytes = new byte[768];
        for (var i = 0; i < 256; i++)
        {
            bytes[3 * i] = (byte)rgb[i];
            bytes[3 * i + 1] = (byte)(rgb[i] >> 8);
            bytes[3 * i + 2] = (byte)(rgb[i] >> 16);
        }

        EnqueueBytes(bytes);
    }

    private void EmitMono(uint[] luma, uint depth)
    {
        if (depth == 1)
        {
            EnqueueBytes(luma.Take(64).Select(v => (byte)v).ToArray());
            return;
        }

        var nibbles = new byte[32];
        for (var i = 0; i < 64; i += 2)
        {
            nibbles[i / 2] = (byte)((luma[i] >> 4) | ((luma[i + 1] >> 4) << 4));
        }

        EnqueueBytes(nibbles);
    }

    private void EnqueueBytes(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i += 4)
        {
            _output.Enqueue(BitConverter.ToUInt32(bytes, i));
        }
    }

    private static uint To15(uint rgb) => ((rgb >> 3) & 0x1F) | ((rgb >> 11) & 0x1F) << 5 | ((rgb >> 19) & 0x1F) << 10;

    private static int SignExtend10(ushort n) => ((n & 0x3FF) << 22) >> 22;

    /// <summary>psx-spx: zagzig[zigzag[i]] = i, with zigzag the JPEG scan order.</summary>
    private static byte[] BuildZagZig()
    {
        byte[] zigzag =
        [
            0, 1, 5, 6, 14, 15, 27, 28, 2, 4, 7, 13, 16, 26, 29, 42,
            3, 8, 12, 17, 25, 30, 41, 43, 9, 11, 18, 24, 31, 40, 44, 53,
            10, 19, 23, 32, 39, 45, 52, 54, 20, 22, 33, 38, 46, 51, 55, 60,
            21, 34, 37, 47, 50, 56, 59, 61, 35, 36, 48, 49, 57, 58, 62, 63,
        ];
        var zagzig = new byte[64];
        for (var i = 0; i < 64; i++) zagzig[zigzag[i]] = (byte)i;
        return zagzig;
    }
}
