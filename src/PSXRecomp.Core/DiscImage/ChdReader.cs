using System.IO.Compression;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Reader for CHD (Compressed Hunks of Data) v5 disc images.
/// Parses the big-endian V5 header, decompresses the Huffman-compressed map,
/// and decompresses hunks via the CD-ROM codecs to provide raw sector access.
/// </summary>
[Domain]
public sealed class ChdReader : IDisposable
{
    private const int CdFrameSize = ChdCdCodec.CdFrameSize; // 2448

    private const byte CompressionNone = 4;
    private const byte CompressionSelf = 5;
    private const byte CompressionParent = 6;
    private const byte CompressionRleSmall = 7;
    private const byte CompressionRleLarge = 8;
    private const byte CompressionSelf0 = 9;
    private const byte CompressionSelf1 = 10;
    private const byte CompressionParentSelf = 11;
    private const byte CompressionParent0 = 12;
    private const byte CompressionParent1 = 13;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly ChdHeader _header;
    private readonly ChdMapEntry[] _map;
    private readonly Dictionary<int, byte[]> _hunkCache;
    private readonly HashSet<int> _resolving;

    public ChdHeader Header => _header;
    public int MapEntryBytes => _header.IsCompressed ? 12 : 4;

    private ChdReader(Stream stream, bool ownsStream, ChdHeader header, ChdMapEntry[] map)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _header = header;
        _map = map;
        _hunkCache = new Dictionary<int, byte[]>();
        _resolving = new HashSet<int>();
    }

    public static ChdReader Open(string filePath)
    {
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var reader = Open(stream, ownsStream: false);
            return new ChdReader(stream, ownsStream: true, reader._header, reader._map);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static ChdReader Open(Stream stream)
    {
        return Open(stream, ownsStream: false);
    }

    private static ChdReader Open(Stream stream, bool ownsStream)
    {
        var header = ReadHeader(stream);
        var map = ReadMap(stream, header);
        return new ChdReader(stream, ownsStream, header, map);
    }

    public void Dispose()
    {
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }

    private int TotalUnits => _header.UnitBytes > 0
        ? (int)(_header.LogicalBytes / _header.UnitBytes)
        : 0;
    public int FramesPerHunk => _header.FramesPerHunk;

    /// <summary>
    /// Reads a single 2352-byte raw CD sector (frame data without subcode).
    /// </summary>
    public byte[] ReadSector(int sectorIndex)
    {
        if ((uint)sectorIndex >= (uint)TotalUnits)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorIndex),
                $"Sector index {sectorIndex} exceeds total frames {TotalUnits}.");
        }

        if (FramesPerHunk <= 0)
        {
            throw new InvalidDataException(
                $"CHD header declares hunkBytes={_header.HunkBytes}, unitBytes={_header.UnitBytes}; frames per hunk is 0.");
        }

        var hunkIndex = sectorIndex / FramesPerHunk;
        var frameOffsetInHunk = sectorIndex % FramesPerHunk;

        var hunkData = GetDecompressedHunk(hunkIndex);
        var frame = new byte[CdFrameSize];
        Buffer.BlockCopy(hunkData, frameOffsetInHunk * CdFrameSize, frame, 0, CdFrameSize);

        // Return just the 2352-byte sector data (drop subcode)
        var sector = new byte[ChdCdCodec.CdSectorDataSize];
        Buffer.BlockCopy(frame, 0, sector, 0, ChdCdCodec.CdSectorDataSize);
        return sector;
    }

    /// <summary>
    /// Reads a sequence of raw CD sectors into a contiguous buffer.
    /// </summary>
    public byte[] ReadSectors(int startSector, int count)
    {
        var result = new byte[count * ChdCdCodec.CdSectorDataSize];
        for (int i = 0; i < count; i++)
        {
            var sector = ReadSector(startSector + i);
            Buffer.BlockCopy(sector, 0, result, i * ChdCdCodec.CdSectorDataSize, ChdCdCodec.CdSectorDataSize);
        }
        return result;
    }

    private byte[] GetDecompressedHunk(int hunkIndex)
    {
        if (_hunkCache.TryGetValue(hunkIndex, out var cached))
        {
            return cached;
        }

        var hunkData = DecompressHunk(hunkIndex);
        _hunkCache[hunkIndex] = hunkData;
        return hunkData;
    }

    private byte[] DecompressHunk(int hunkIndex)
    {
        var entry = _map[hunkIndex];

        // Self references point to another hunk whose already-decompressed data
        // we can reuse (the stored FileOffset is that hunk's index).
        if (entry.CompressionType == CompressionSelf)
        {
            int refHunk = (int)entry.FileOffset;
            if ((uint)refHunk >= (uint)_map.Length)
            {
                throw new InvalidDataException(
                    $"CHD hunk {hunkIndex}: self reference to out-of-range hunk {refHunk}.");
            }
            if (!_resolving.Add(hunkIndex))
            {
                throw new InvalidDataException(
                    $"CHD hunk {hunkIndex}: cyclic self reference detected.");
            }
            try
            {
                return GetDecompressedHunk(refHunk);
            }
            finally
            {
                _resolving.Remove(hunkIndex);
            }
        }

        if (entry.CompressionType == CompressionParent)
        {
            if (_header.HasParent)
            {
                throw new InvalidDataException(
                    $"CHD hunk {hunkIndex}: parent-reference resolution requires a parent CHD, which is not supported.");
            }

            // With no parent CHD attached, a parent reference denotes an all-zero
            // hunk (the implicit "empty" parent). Returns zero-filled frames.
            return new byte[FramesPerHunk * CdFrameSize];
        }

        if (entry.CompressionType >= CompressionNone)
        {
            throw new InvalidDataException(
                $"CHD hunk {hunkIndex}: unexpected compression type {entry.CompressionType}.");
        }

        var codec = _header.Compressors[entry.CompressionType];
        var compressed = ReadBytesAt((long)entry.FileOffset, (int)entry.CompressedLength);

        int frames = FramesPerHunk;
        if (entry.CompressedLength == 0)
        {
            // Zero-length compressed hunk => all-zero frames
            return new byte[frames * CdFrameSize];
        }

        if (codec == 0)
        {
            // Raw/uncompressed hunk
            return ComposeRawFrames(compressed, frames);
        }

        return ChdCdCodec.Decompress(codec, compressed, frames);
    }

    private byte[] ComposeRawFrames(byte[] compressed, int frames)
    {
        // Raw hunk: the data is already frames of 2448 bytes (no subcode separation).
        // Pad/truncate to the expected hunk size.
        var result = new byte[frames * CdFrameSize];
        Buffer.BlockCopy(compressed, 0, result, 0, Math.Min(compressed.Length, result.Length));
        return result;
    }

    private byte[] ReadBytesAt(long offset, int length)
    {
        var buffer = new byte[length];
        _stream.Seek(offset, SeekOrigin.Begin);
        _stream.ReadExactly(buffer, 0, length);
        return buffer;
    }

    internal static ChdHeader ReadHeader(Stream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var headerBytes = new byte[ChdHeader.V5HeaderSize];
        stream.ReadExactly(headerBytes);

        uint magicLow = ChdHeader.ReadUInt32BE(headerBytes, 0);
        uint magicHigh = ChdHeader.ReadUInt32BE(headerBytes, 4);

        if (magicLow != ChdHeader.ExpectedMagicLow || magicHigh != ChdHeader.ExpectedMagicHigh)
        {
            throw new InvalidDataException(
                $"Invalid CHD magic: expected 'MComprHD', got 0x{magicLow:X8}{magicHigh:X8}.");
        }

        uint headerLength = ChdHeader.ReadUInt32BE(headerBytes, 8);
        uint version = ChdHeader.ReadUInt32BE(headerBytes, 12);

        var compressors = new uint[4];
        for (int i = 0; i < 4; i++)
        {
            compressors[i] = ChdHeader.ReadUInt32BE(headerBytes, 16 + i * 4);
        }

        ulong logicalBytes = ChdHeader.ReadUInt64BE(headerBytes, 32);
        ulong mapOffset = ChdHeader.ReadUInt64BE(headerBytes, 40);
        ulong metaOffset = ChdHeader.ReadUInt64BE(headerBytes, 48);
        uint hunkBytes = ChdHeader.ReadUInt32BE(headerBytes, 56);
        uint unitBytes = ChdHeader.ReadUInt32BE(headerBytes, 60);

        var rawSha1 = headerBytes[64..84].ToArray();
        var sha1 = headerBytes[84..104].ToArray();
        var parentSha1 = headerBytes[104..124].ToArray();

        return new ChdHeader
        {
            Version = version,
            HeaderLength = headerLength,
            Compressors = compressors,
            LogicalBytes = logicalBytes,
            MapOffset = mapOffset,
            MetaOffset = metaOffset,
            HunkBytes = hunkBytes,
            UnitBytes = unitBytes,
            RawSha1 = rawSha1,
            Sha1 = sha1,
            ParentSha1 = parentSha1,
        };
    }

    internal static ChdMapEntry[] ReadMap(Stream stream, ChdHeader header)
    {
        int hunkCount = header.TotalHunks;

        if (!header.IsCompressed)
        {
            // V5 uncompressed: 4-byte BE offset per hunk
            var entries = new ChdMapEntry[hunkCount];
            stream.Seek((long)header.MapOffset, SeekOrigin.Begin);
            var raw = new byte[4 * hunkCount];
            stream.ReadExactly(raw, 0, raw.Length);
            for (int i = 0; i < hunkCount; i++)
            {
                uint offset = ChdHeader.ReadUInt32BE(raw, i * 4);
                entries[i] = new ChdMapEntry
                {
                    CompressionType = (byte)(offset == 0 ? CompressionParent : 0),
                    CompressedLength = header.HunkBytes,
                    // The uncompressed V5 map stores a hunk-unit index; scale it to
                    // a byte offset, matching MAME's read_hunk (offset * hunkbytes).
                    FileOffset = (ulong)offset * header.HunkBytes,
                    Crc16 = 0,
                };
            }
            return entries;
        }

        // V5 compressed: decompress the Huffman-coded map.
        return DecompressV5Map(stream, header, hunkCount);
    }

    /// <summary>
    /// Computes deterministic statistics about this CHD's map and compressed data
    /// region. Does not decompress any hunks; it only inspects the parsed header
    /// and the decompressed map entries.
    /// </summary>
    public ChdMapStatistics ComputeMapStatistics()
    {
        int cdlz = 0;
        int cdzl = 0;
        long dataRegion = 0;

        for (int i = 0; i < _map.Length; i++)
        {
            var entry = _map[i];

            // Compression types 0..3 are the four codec slots; map them to their
            // compressor tags to identify cdlz / cdzl.
            if (entry.CompressionType < 4)
            {
                uint tag = _header.Compressors[entry.CompressionType];
                if (tag == ChdCdCodec.CodecLzma) cdlz++;
                else if (tag == ChdCdCodec.CodecZlib) cdzl++;
            }

            // Data-bearing entries are types 0..3 (codec-compressed) and type 4
            // (uncompressed/raw). Their compressed lengths describe the data region.
            if (entry.CompressionType <= CompressionNone)
            {
                dataRegion += entry.CompressedLength;
            }
        }

        long mapConsumed = 4L * _map.Length;
        if (_header.IsCompressed)
        {
            long original = _stream.Position;
            _stream.Seek((long)_header.MapOffset, SeekOrigin.Begin);
            var mapHeader = new byte[16];
            _stream.ReadExactly(mapHeader, 0, 16);
            uint mapBytes = ChdHeader.ReadUInt32BE(mapHeader, 0);
            mapConsumed = 16L + mapBytes;
            _stream.Seek(original, SeekOrigin.Begin);
        }

        return new ChdMapStatistics
        {
            Version = _header.Version,
            LogicalBytes = _header.LogicalBytes,
            HunkBytes = _header.HunkBytes,
            TotalHunks = _header.TotalHunks,
            CdlzCount = cdlz,
            CdzlCount = cdzl,
            MapBytesConsumed = mapConsumed,
            DataRegionSize = dataRegion,
        };
    }

    private static ChdMapEntry[] DecompressV5Map(Stream stream, ChdHeader header, int hunkCount)
    {
        var rawMap = new byte[hunkCount * 12];

        // Read the 16-byte map header
        stream.Seek((long)header.MapOffset, SeekOrigin.Begin);
        var mapHeader = new byte[16];
        stream.ReadExactly(mapHeader, 0, 16);

        uint mapBytes = ChdHeader.ReadUInt32BE(mapHeader, 0);
        ulong firstOffs = ReadUInt48BE(mapHeader, 4);
        ushort mapCrc = (ushort)((mapHeader[10] << 8) | mapHeader[11]);
        byte lengthBits = mapHeader[12];
        byte selfBits = mapHeader[13];
        byte parentBits = mapHeader[14];

        // Read compressed map data
        var compressed = new byte[mapBytes];
        stream.ReadExactly(compressed, 0, compressed.Length);

        var bitbuf = new ChdBitstream(compressed);

        // 1) Decode compression types
        var decoder = new ChdHuffmanDecoder();
        decoder.ImportTreeRle(bitbuf);

        byte lastComp = 0;
        int repCount = 0;
        var types = new byte[hunkCount];
        for (uint hunkNum = 0; hunkNum < hunkCount; hunkNum++)
        {
            if (repCount > 0)
            {
                types[hunkNum] = lastComp;
                repCount--;
            }
            else
            {
                int val = decoder.DecodeOne(bitbuf);
                if (val == CompressionRleSmall)
                {
                    types[hunkNum] = lastComp;
                    repCount = 2 + decoder.DecodeOne(bitbuf);
                }
                else if (val == CompressionRleLarge)
                {
                    types[hunkNum] = lastComp;
                    repCount = 2 + 16 + (decoder.DecodeOne(bitbuf) << 4) + decoder.DecodeOne(bitbuf);
                }
                else
                {
                    types[hunkNum] = lastComp = (byte)val;
                }
            }
        }

        // 2) Read the auxiliary per-hunk data
        var entries = new ChdMapEntry[hunkCount];
        ulong curOffset = firstOffs;
        uint lastSelf = 0;
        ulong lastParent = 0;

        for (uint hunkNum = 0; hunkNum < hunkCount; hunkNum++)
        {
            ulong offset = curOffset;
            uint length = 0;
            ushort crc = 0;
            byte type = types[hunkNum];

            switch (type)
            {
                case 0:
                case 1:
                case 2:
                case 3:
                    length = bitbuf.Read(lengthBits);
                    crc = (ushort)bitbuf.Read(16);
                    curOffset += length;
                    break;

                case CompressionNone:
                    length = header.HunkBytes;
                    crc = (ushort)bitbuf.Read(16);
                    curOffset += length;
                    break;

                case CompressionSelf:
                    lastSelf = bitbuf.Read(selfBits);
                    offset = lastSelf;
                    break;

                case CompressionParent:
                    offset = bitbuf.Read(parentBits);
                    lastParent = offset;
                    break;

                case CompressionSelf0:
                    type = CompressionSelf;
                    offset = lastSelf;
                    break;
                case CompressionSelf1:
                    lastSelf++;
                    offset = lastSelf;
                    type = CompressionSelf;
                    break;
                case CompressionParentSelf:
                    offset = (ulong)MulU32x32(hunkNum, header.HunkBytes) / header.UnitBytes;
                    type = CompressionParent;
                    lastParent = offset;
                    break;
                case CompressionParent0:
                    type = CompressionParent;
                    offset = lastParent;
                    break;
                case CompressionParent1:
                    lastParent += (ulong)header.HunkBytes / header.UnitBytes;
                    type = CompressionParent;
                    offset = lastParent;
                    break;
            }

            int e = (int)hunkNum * 12;
            rawMap[e] = type;
            PutUInt24BE(rawMap, e + 1, length);
            PutUInt48BE(rawMap, e + 4, offset);
            PutUInt16BE(rawMap, e + 10, crc);

            entries[hunkNum] = new ChdMapEntry
            {
                CompressionType = type,
                CompressedLength = length,
                FileOffset = offset,
                Crc16 = crc,
            };
        }

        // Optional CRC verification of the expanded raw map (MAME does this; low priority).
        return entries;
    }

    private static ulong ReadUInt48BE(byte[] data, int offset)
    {
        ulong value = 0;
        for (int i = 0; i < 6; i++)
        {
            value = (value << 8) | data[offset + i];
        }
        return value;
    }

    private static ulong MulU32x32(uint a, uint b) => (ulong)a * b;

    private static void PutUInt24BE(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 16);
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)value;
    }

    private static void PutUInt48BE(byte[] data, int offset, ulong value)
    {
        for (int i = 0; i < 6; i++)
        {
            data[offset + i] = (byte)(value >> (8 * (5 - i)));
        }
    }

    private static void PutUInt16BE(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }
}
