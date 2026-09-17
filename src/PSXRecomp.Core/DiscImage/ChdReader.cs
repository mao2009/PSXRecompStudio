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

    // PS1 images are substantially smaller than these limits. The ceilings are
    // deliberately generous enough for valid PS1 media while preventing hostile
    // length fields from becoming process-sized allocations before validation.
    internal const ulong MaxLogicalBytes = 2UL * 1024UL * 1024UL * 1024UL;
    internal const uint MaxHunkBytes = 16U * 1024U * 1024U;
    internal const uint MaxCompressedMapBytes = 16U * 1024U * 1024U;
    internal const int MaxHunkCount = 500_000;

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
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("CHD input stream must be readable and seekable.", nameof(stream));
        }

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

    private int TotalUnits => checked((int)(_header.LogicalBytes / _header.UnitBytes));
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
        Buffer.BlockCopy(hunkData, checked(frameOffsetInHunk * CdFrameSize), frame, 0, CdFrameSize);

        var sector = new byte[ChdCdCodec.CdSectorDataSize];
        Buffer.BlockCopy(frame, 0, sector, 0, ChdCdCodec.CdSectorDataSize);
        return sector;
    }

    /// <summary>
    /// Reads a sequence of raw CD sectors into a contiguous buffer.
    /// </summary>
    public byte[] ReadSectors(int startSector, int count)
    {
        if (startSector < 0) throw new ArgumentOutOfRangeException(nameof(startSector));
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));

        int byteCount;
        try
        {
            byteCount = checked(count * ChdCdCodec.CdSectorDataSize);
        }
        catch (OverflowException ex)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Requested sector range is too large.", ex);
        }

        var result = new byte[byteCount];
        for (int i = 0; i < count; i++)
        {
            var sector = ReadSector(checked(startSector + i));
            Buffer.BlockCopy(sector, 0, result, checked(i * ChdCdCodec.CdSectorDataSize), ChdCdCodec.CdSectorDataSize);
        }
        return result;
    }

    private byte[] GetDecompressedHunk(int hunkIndex)
    {
        if ((uint)hunkIndex >= (uint)_map.Length)
        {
            throw new InvalidDataException($"CHD hunk index {hunkIndex} is outside map length {_map.Length}.");
        }

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

        if (entry.CompressionType == CompressionSelf)
        {
            if (entry.FileOffset > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"CHD hunk {hunkIndex}: self reference {entry.FileOffset} cannot be represented as a hunk index.");
            }

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

        int hunkOutputBytes = GetExpectedHunkOutputBytes();

        if (entry.CompressionType == CompressionParent)
        {
            if (_header.HasParent)
            {
                throw new InvalidDataException(
                    $"CHD hunk {hunkIndex}: parent-reference resolution requires a parent CHD, which is not supported.");
            }

            return new byte[hunkOutputBytes];
        }

        if (entry.CompressionType >= CompressionNone)
        {
            throw new InvalidDataException(
                $"CHD hunk {hunkIndex}: unexpected compression type {entry.CompressionType}.");
        }

        if (entry.CompressedLength > MaxHunkBytes)
        {
            throw new InvalidDataException(
                $"CHD hunk {hunkIndex}: compressed length {entry.CompressedLength} exceeds the {MaxHunkBytes}-byte safety ceiling.");
        }
        if (entry.FileOffset > long.MaxValue)
        {
            throw new InvalidDataException(
                $"CHD hunk {hunkIndex}: file offset {entry.FileOffset} exceeds the supported stream range.");
        }

        int frames = FramesPerHunk;
        if (entry.CompressedLength == 0)
        {
            return new byte[hunkOutputBytes];
        }

        var compressed = ReadBytesAt((long)entry.FileOffset, checked((int)entry.CompressedLength));
        var codec = _header.Compressors[entry.CompressionType];

        if (codec == 0)
        {
            return ComposeRawFrames(compressed, frames);
        }

        return ChdCdCodec.Decompress(codec, compressed, frames);
    }

    private int GetExpectedHunkOutputBytes()
    {
        try
        {
            int result = checked(FramesPerHunk * CdFrameSize);
            if ((uint)result > MaxHunkBytes)
            {
                throw new InvalidDataException(
                    $"CHD hunk expands to {result} bytes, above the {MaxHunkBytes}-byte safety ceiling.");
            }
            return result;
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("CHD hunk output size overflows the supported allocation range.", ex);
        }
    }

    private byte[] ComposeRawFrames(byte[] compressed, int frames)
    {
        int outputBytes;
        try
        {
            outputBytes = checked(frames * CdFrameSize);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("CHD raw hunk output size overflows the supported allocation range.", ex);
        }

        if ((uint)outputBytes > MaxHunkBytes)
        {
            throw new InvalidDataException(
                $"CHD raw hunk expands to {outputBytes} bytes, above the {MaxHunkBytes}-byte safety ceiling.");
        }

        var result = new byte[outputBytes];
        Buffer.BlockCopy(compressed, 0, result, 0, Math.Min(compressed.Length, result.Length));
        return result;
    }

    private byte[] ReadBytesAt(long offset, int length)
    {
        if (length < 0 || (uint)length > MaxHunkBytes)
        {
            throw new InvalidDataException(
                $"CHD read length {length} exceeds the supported hunk allocation range.");
        }

        EnsureStreamRange(_stream, offset, length, "CHD hunk data");
        var buffer = new byte[length];
        _stream.Seek(offset, SeekOrigin.Begin);
        _stream.ReadExactly(buffer, 0, length);
        return buffer;
    }

    internal static ChdHeader ReadHeader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("CHD input stream must be readable and seekable.", nameof(stream));
        }

        EnsureStreamRange(stream, 0, ChdHeader.V5HeaderSize, "CHD v5 header");
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

        if (hunkBytes == 0)
        {
            throw new InvalidDataException("CHD header declares hunkBytes=0; hunks must have a positive size.");
        }
        if (hunkBytes > MaxHunkBytes)
        {
            throw new InvalidDataException(
                $"CHD header declares hunkBytes={hunkBytes}, above the {MaxHunkBytes}-byte safety ceiling.");
        }
        if (unitBytes == 0)
        {
            throw new InvalidDataException("CHD header declares unitBytes=0; units must have a positive size.");
        }
        if (unitBytes > hunkBytes)
        {
            throw new InvalidDataException(
                $"CHD header declares unitBytes={unitBytes} larger than hunkBytes={hunkBytes}.");
        }
        if (logicalBytes > MaxLogicalBytes)
        {
            throw new InvalidDataException(
                $"CHD header declares logicalBytes={logicalBytes}, above the {MaxLogicalBytes}-byte PS1 analysis ceiling.");
        }
        if (logicalBytes / unitBytes > int.MaxValue)
        {
            throw new InvalidDataException(
                $"CHD header declares too many logical units ({logicalBytes / unitBytes}).");
        }
        if (mapOffset > long.MaxValue || mapOffset > (ulong)stream.Length)
        {
            throw new InvalidDataException(
                $"CHD map offset {mapOffset} lies outside the {stream.Length}-byte container.");
        }
        if (metaOffset > long.MaxValue)
        {
            throw new InvalidDataException(
                $"CHD metadata offset {metaOffset} exceeds the supported stream range.");
        }

        var rawSha1 = headerBytes[64..84].ToArray();
        var sha1 = headerBytes[84..104].ToArray();
        var parentSha1 = headerBytes[104..124].ToArray();

        var header = new ChdHeader
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

        int totalHunks;
        try
        {
            totalHunks = header.TotalHunks;
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("CHD header declares more hunks than the reader can represent.", ex);
        }

        if (totalHunks > MaxHunkCount)
        {
            throw new InvalidDataException(
                $"CHD header declares {totalHunks} hunks, above the {MaxHunkCount} hunk safety ceiling.");
        }

        return header;
    }

    internal static ChdMapEntry[] ReadMap(Stream stream, ChdHeader header)
    {
        int hunkCount;
        try
        {
            hunkCount = header.TotalHunks;
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("CHD hunk count exceeds the supported range.", ex);
        }

        if (hunkCount < 0 || hunkCount > MaxHunkCount)
        {
            throw new InvalidDataException(
                $"CHD declares {hunkCount} hunks, outside the supported range 0..{MaxHunkCount}.");
        }

        if (!header.IsCompressed)
        {
            int rawLength;
            try
            {
                rawLength = checked(4 * hunkCount);
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException("CHD uncompressed map size overflows the supported allocation range.", ex);
            }

            EnsureStreamRange(stream, CheckedStreamOffset(header.MapOffset, "CHD map offset"), rawLength, "CHD uncompressed map");
            var entries = new ChdMapEntry[hunkCount];
            stream.Seek((long)header.MapOffset, SeekOrigin.Begin);
            var raw = new byte[rawLength];
            stream.ReadExactly(raw, 0, raw.Length);
            for (int i = 0; i < hunkCount; i++)
            {
                uint offset = ChdHeader.ReadUInt32BE(raw, i * 4);
                ulong fileOffset = (ulong)offset * header.HunkBytes;
                if (offset != 0)
                {
                    ValidateDataRange(stream, fileOffset, header.HunkBytes, $"CHD hunk {i}");
                }

                entries[i] = new ChdMapEntry
                {
                    CompressionType = (byte)(offset == 0 ? CompressionParent : 0),
                    CompressedLength = header.HunkBytes,
                    FileOffset = fileOffset,
                    Crc16 = 0,
                };
            }
            return entries;
        }

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

            if (entry.CompressionType < 4)
            {
                uint tag = _header.Compressors[entry.CompressionType];
                if (tag == ChdCdCodec.CodecLzma) cdlz++;
                else if (tag == ChdCdCodec.CodecZlib) cdzl++;
            }

            if (entry.CompressionType <= CompressionNone)
            {
                dataRegion = checked(dataRegion + entry.CompressedLength);
            }
        }

        long mapConsumed = 4L * _map.Length;
        if (_header.IsCompressed)
        {
            long original = _stream.Position;
            long mapOffset = CheckedStreamOffset(_header.MapOffset, "CHD map offset");
            EnsureStreamRange(_stream, mapOffset, 16, "CHD compressed map header");
            _stream.Seek(mapOffset, SeekOrigin.Begin);
            var mapHeader = new byte[16];
            _stream.ReadExactly(mapHeader, 0, 16);
            uint mapBytes = ChdHeader.ReadUInt32BE(mapHeader, 0);
            if (mapBytes > MaxCompressedMapBytes)
            {
                throw new InvalidDataException(
                    $"CHD compressed map length {mapBytes} exceeds the {MaxCompressedMapBytes}-byte safety ceiling.");
            }
            EnsureStreamRange(_stream, checked(mapOffset + 16), checked((int)mapBytes), "CHD compressed map data");
            mapConsumed = checked(16L + mapBytes);
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
        if (hunkCount < 0 || hunkCount > MaxHunkCount)
        {
            throw new InvalidDataException(
                $"CHD hunk count {hunkCount} is outside the supported range.");
        }

        int expandedMapBytes;
        try
        {
            expandedMapBytes = checked(hunkCount * ChdHeader.MapEntrySize);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("CHD expanded map size overflows the supported allocation range.", ex);
        }

        var rawMap = new byte[expandedMapBytes];

        long mapOffset = CheckedStreamOffset(header.MapOffset, "CHD map offset");
        EnsureStreamRange(stream, mapOffset, 16, "CHD compressed map header");
        stream.Seek(mapOffset, SeekOrigin.Begin);
        var mapHeader = new byte[16];
        stream.ReadExactly(mapHeader, 0, 16);

        uint mapBytes = ChdHeader.ReadUInt32BE(mapHeader, 0);
        if (mapBytes > MaxCompressedMapBytes)
        {
            throw new InvalidDataException(
                $"CHD compressed map length {mapBytes} exceeds the {MaxCompressedMapBytes}-byte safety ceiling.");
        }

        int mapByteCount = checked((int)mapBytes);
        long mapDataOffset = checked(mapOffset + 16);
        EnsureStreamRange(stream, mapDataOffset, mapByteCount, "CHD compressed map data");

        ulong firstOffs = ReadUInt48BE(mapHeader, 4);
        ushort mapCrc = (ushort)((mapHeader[10] << 8) | mapHeader[11]);
        _ = mapCrc;
        byte lengthBits = mapHeader[12];
        byte selfBits = mapHeader[13];
        byte parentBits = mapHeader[14];

        if (lengthBits > 24)
        {
            throw new InvalidDataException($"CHD compressed map lengthBits={lengthBits} exceeds the 24-bit map-entry length field.");
        }
        if (selfBits > 32 || parentBits > 32)
        {
            throw new InvalidDataException(
                $"CHD compressed map reference widths are invalid (selfBits={selfBits}, parentBits={parentBits}).");
        }

        var compressed = new byte[mapByteCount];
        stream.ReadExactly(compressed, 0, compressed.Length);

        var bitbuf = new ChdBitstream(compressed);
        var decoder = new ChdHuffmanDecoder();
        decoder.ImportTreeRle(bitbuf);

        byte lastComp = 0;
        int repCount = 0;
        var types = new byte[hunkCount];
        for (uint hunkNum = 0; hunkNum < (uint)hunkCount; hunkNum++)
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
                    if (val < 0 || val > CompressionParent1)
                    {
                        throw new InvalidDataException($"CHD map contains unsupported compression type {val}.");
                    }
                    types[hunkNum] = lastComp = (byte)val;
                }
            }
        }

        var entries = new ChdMapEntry[hunkCount];
        ulong curOffset = firstOffs;
        uint lastSelf = 0;
        ulong lastParent = 0;

        for (uint hunkNum = 0; hunkNum < (uint)hunkCount; hunkNum++)
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
                    if (length > MaxHunkBytes)
                    {
                        throw new InvalidDataException(
                            $"CHD hunk {hunkNum}: compressed length {length} exceeds the {MaxHunkBytes}-byte safety ceiling.");
                    }
                    crc = (ushort)bitbuf.Read(16);
                    ValidateDataRange(stream, offset, length, $"CHD hunk {hunkNum}");
                    try
                    {
                        curOffset = checked(curOffset + length);
                    }
                    catch (OverflowException ex)
                    {
                        throw new InvalidDataException($"CHD hunk {hunkNum}: data offset overflow.", ex);
                    }
                    break;

                case CompressionNone:
                    length = header.HunkBytes;
                    crc = (ushort)bitbuf.Read(16);
                    ValidateDataRange(stream, offset, length, $"CHD raw hunk {hunkNum}");
                    try
                    {
                        curOffset = checked(curOffset + length);
                    }
                    catch (OverflowException ex)
                    {
                        throw new InvalidDataException($"CHD raw hunk {hunkNum}: data offset overflow.", ex);
                    }
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
                    lastSelf = checked(lastSelf + 1);
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
                    try
                    {
                        lastParent = checked(lastParent + ((ulong)header.HunkBytes / header.UnitBytes));
                    }
                    catch (OverflowException ex)
                    {
                        throw new InvalidDataException($"CHD hunk {hunkNum}: parent reference overflow.", ex);
                    }
                    type = CompressionParent;
                    offset = lastParent;
                    break;

                default:
                    throw new InvalidDataException($"CHD hunk {hunkNum}: unsupported map compression type {type}.");
            }

            int e = checked((int)hunkNum * ChdHeader.MapEntrySize);
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

        return entries;
    }

    private static long CheckedStreamOffset(ulong offset, string description)
    {
        if (offset > long.MaxValue)
        {
            throw new InvalidDataException($"{description} {offset} exceeds the supported stream range.");
        }
        return (long)offset;
    }

    private static void ValidateDataRange(Stream stream, ulong offset, uint length, string description)
    {
        if (length > MaxHunkBytes)
        {
            throw new InvalidDataException(
                $"{description} length {length} exceeds the {MaxHunkBytes}-byte safety ceiling.");
        }

        long signedOffset = CheckedStreamOffset(offset, $"{description} offset");
        EnsureStreamRange(stream, signedOffset, checked((int)length), description);
    }

    private static void EnsureStreamRange(Stream stream, long offset, int length, string description)
    {
        if (offset < 0 || length < 0)
        {
            throw new InvalidDataException($"{description} has a negative offset or length.");
        }

        long end;
        try
        {
            end = checked(offset + length);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException($"{description} range overflows the supported stream address space.", ex);
        }

        if (offset > stream.Length || end > stream.Length)
        {
            throw new InvalidDataException(
                $"{description} range [{offset}, {end}) lies outside the {stream.Length}-byte container.");
        }
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
