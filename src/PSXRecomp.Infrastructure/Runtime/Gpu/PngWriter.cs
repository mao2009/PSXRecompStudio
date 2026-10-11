using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure.Runtime.Gpu;

/// <summary>Minimal 8-bit RGBA PNG writer for GPU screenshots. Uses only .NET libraries.</summary>
[Infrastructure]
internal static class PngWriter
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    internal static void Save(string path, ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "PNG dimensions must be positive.");
        int expected = checked(checked(width * height) * 4);
        if (bgra.Length != expected)
            throw new ArgumentException("BGRA buffer length does not match image dimensions.", nameof(bgra));

        using var output = File.Create(path);
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)height);
        header[8] = 8; // 8 bits per channel
        header[9] = 6; // RGBA
        WriteChunk(output, "IHDR", header);

        // Each scanline starts with PNG filter 0. PNG stores channels as RGBA.
        using var compressed = new MemoryStream();
        using (var deflater = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var scanline = new byte[checked(width * 4 + 1)];
            for (int y = 0; y < height; y++)
            {
                scanline[0] = 0;
                var start = checked(y * width * 4);
                for (int x = 0; x < width; x++)
                {
                    int src = start + x * 4;
                    int dst = 1 + x * 4;
                    scanline[dst] = bgra[src + 2];
                    scanline[dst + 1] = bgra[src + 1];
                    scanline[dst + 2] = bgra[src];
                    scanline[dst + 3] = bgra[src + 3];
                }
                deflater.Write(scanline);
            }
        }
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> field = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(field, checked((uint)data.Length));
        output.Write(field);
        Span<byte> kind = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, kind);
        output.Write(kind);
        output.Write(data);

        uint crc = 0xffffffff;
        foreach (byte b in kind)
            crc = CrcStep(crc, b);
        foreach (byte b in data)
            crc = CrcStep(crc, b);
        BinaryPrimitives.WriteUInt32BigEndian(field, ~crc);
        output.Write(field);
    }

    private static uint CrcStep(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        return crc;
    }
}
