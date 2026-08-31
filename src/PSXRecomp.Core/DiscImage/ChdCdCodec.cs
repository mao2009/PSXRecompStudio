using System.IO.Compression;
using PSXRecomp.Architecture;
using SharpCompress.Compressors.LZMA;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Decompresses CHD V5 CD-ROM codec payloads (cdlz, cdzl) into raw CD frames.
/// Implements the shared chd_cd_decompressor framing: ECC bitmap header, then
/// separated compressed sector-data and zlib subcode regions.
/// </summary>
[Domain]
internal static class ChdCdCodec
{
    public const int CdSectorDataSize = 2352;
    public const int CdSubcodeDataSize = 96;
    public const int CdFrameSize = CdSectorDataSize + CdSubcodeDataSize; // 2448
    public const uint CodecLzma = 0x63646C7AU; // "cdlz" (big-endian tag)
    public const uint CodecZlib = 0x63647A6CU; // "cdzl" (big-endian tag)

    // Fixed LZMA properties for cdlz (lc=3, lp=0, pb=2)
    private static readonly byte[] CdlzProperties = [(byte)((2 * 5 + 0) * 9 + 3)]; // 0x5D

    /// <summary>
    /// Decompresses a compressed hunk into a full hunk of CD frames
    /// (each frame = 2448 bytes: 2352 sector data + 96 subcode).
    /// </summary>
    public static byte[] Decompress(uint codec, byte[] compressed, int framesExpected)
    {
        int destLen = framesExpected * CdFrameSize;
        int complenBytes = destLen < 65536 ? 2 : 3;
        int eccBytes = (framesExpected + 7) / 8;
        int headerBytes = eccBytes + complenBytes;

        if (compressed.Length < headerBytes)
        {
            throw new InvalidDataException(
                $"CHD CD codec: compressed hunk too small ({compressed.Length} < {headerBytes}).");
        }

        // ECC bitmap
        var eccBitmap = new byte[eccBytes];
        Buffer.BlockCopy(compressed, 0, eccBitmap, 0, eccBytes);

        // base compressed length (big-endian)
        int complenBase = ReadLength(compressed, eccBytes, complenBytes);
        if (complenBase < 0 || eccBytes + complenBytes + complenBase > compressed.Length)
        {
            throw new InvalidDataException(
                $"CHD CD codec: base length {complenBase} out of range.");
        }

        int sectorDataLen = framesExpected * CdSectorDataSize;
        int subcodeLen = framesExpected * CdSubcodeDataSize;

        // 1. Decompress sector data (codec-specific)
        byte[] sectorData;
        byte[] subcode;

        switch (codec)
        {
            case CodecLzma:
                sectorData = DecompressLzma(compressed, headerBytes, complenBase, sectorDataLen);
                break;
            case CodecZlib:
                sectorData = DecompressZlib(compressed, headerBytes, complenBase, sectorDataLen);
                break;
            default:
                throw new InvalidDataException($"CHD CD codec: unsupported codec tag 0x{codec:X8}.");
        }

        // 2. Decompress subcode (always zlib)
        int subcodeOffset = headerBytes + complenBase;
        subcode = DecompressZlib(compressed, subcodeOffset, compressed.Length - subcodeOffset, subcodeLen);

        // 3. Interleave into frames
        var result = new byte[destLen];
        for (int i = 0; i < framesExpected; i++)
        {
            Buffer.BlockCopy(sectorData, i * CdSectorDataSize, result, i * CdFrameSize, CdSectorDataSize);
            Buffer.BlockCopy(subcode, i * CdSubcodeDataSize, result, i * CdFrameSize + CdSectorDataSize, CdSubcodeDataSize);
        }

        // 4. Reconstitute sync + ECC for frames flagged in the bitmap
        for (int i = 0; i < framesExpected; i++)
        {
            if ((eccBitmap[i >> 3] & (1 << (i & 7))) == 0) continue;
            var frame = new byte[CdFrameSize];
            Buffer.BlockCopy(result, i * CdFrameSize, frame, 0, CdFrameSize);
            WriteCdSyncHeader(frame);
            EccGenerate(frame);
            Buffer.BlockCopy(frame, 0, result, i * CdFrameSize, CdFrameSize);
        }

        return result;
    }

    private static int ReadLength(byte[] data, int offset, int bytes)
    {
        int value = 0;
        for (int i = 0; i < bytes; i++)
        {
            value = (value << 8) | data[offset + i];
        }
        return value;
    }

    private static byte[] DecompressLzma(byte[] compressed, int offset, int length, int expectedSize)
    {
        var lzma = new Decoder();

        // cdlz uses raw LZMA (no props header). Provide fixed props + dict size.
        // For hunk-base that is typically small, use a minimal large-enough dict.
        int dictSize = GetLzmaDictSize(expectedSize);
        var props = new byte[5];
        props[0] = CdlzProperties[0];
        for (int i = 0; i < 4; i++)
        {
            props[1 + i] = (byte)(dictSize >> (i * 8));
        }
        lzma.SetDecoderProperties(props);

        using var inStream = new MemoryStream(compressed, offset, length, writable: false);
        using var outStream = new MemoryStream(expectedSize);
        try
        {
            lzma.Code(inStream, outStream, length, expectedSize, null);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"CHD cdlz LZMA decode failed: {ex.Message}", ex);
        }
        return outStream.ToArray();
    }

    private static int GetLzmaDictSize(int expectedSize)
    {
        // Mirrors MAME's logic for level 9, bounded to the destination size.
        int dictSize = 1 << 26;
        if (dictSize > expectedSize)
        {
            for (int i = 11; i < 31; i++)
            {
                if (expectedSize <= (2 << i))
                {
                    dictSize = 2 << i;
                    break;
                }
                if (expectedSize <= (3 << i))
                {
                    dictSize = 3 << i;
                    break;
                }
            }
        }
        return dictSize;
    }

    private static byte[] DecompressZlib(byte[] compressed, int offset, int length, int expectedSize)
    {
        // MAME uses inflateInit2(..., -MAX_WBITS) = raw deflate (no zlib header/checksum).
        // .NET's DeflateStream is the equivalent of raw deflate.
        using var inStream = new MemoryStream(compressed, offset, length, writable: false);
        using var deflate = new DeflateStream(inStream, CompressionMode.Decompress);
        var result = new byte[expectedSize];
        int totalRead = 0;
        while (totalRead < expectedSize)
        {
            int read = deflate.Read(result, totalRead, expectedSize - totalRead);
            if (read == 0) break;
            totalRead += read;
        }
        if (totalRead < expectedSize)
        {
            throw new InvalidDataException(
                $"CHD zlib decode: expected {expectedSize} bytes, got {totalRead}.");
        }
        return result;
    }

    private static void WriteCdSyncHeader(byte[] frame)
    {
        // 12-byte sync pattern: 00 FF FF FF FF FF FF FF FF FF FF 00
        Array.Clear(frame, 0, 12);
        for (int i = 1; i < 11; i++)
        {
            frame[i] = 0xFF;
        }
    }

    private static void EccGenerate(byte[] frame)
    {
        // Compute ECC bytes in the frame's header region.
        // PS1 CD sectors use the standard "raw" 2352-byte layout with P/Q ECC.
        // The sector data (offset 12..2352) already holds the 2048-byte data + EDC.
        //
        // ECC regeneration is intentionally a no-op here: the analysis pipeline
        // only reads the first 2048 bytes of mode-2 user data (offset 24..2072 of
        // the raw sector), which does not depend on the ECC parity bytes. Keeping
        // them zero keeps this independent of a full CD-ROM XA ECC table.
        // (These bytes are NOT validated by any consumer in the analysis path.)
    }
}
