using System.Text;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage;

/// <summary>
/// Minimal ISO 9660 reader for PS1 disc images.
/// Reads volume descriptors, traverses directories, and extracts files.
/// Only supports Primary Volume Descriptor (Type 255 = terminator).
/// </summary>
[Domain]
public sealed class Iso9660Reader
{
    public const int SectorSize = 2048;
    private const int VolumeDescriptorSector = 16;
    private const byte PrimaryVolumeDescriptorType = 1;
    private const byte DirectoryRecordTerminator = 0;

    private readonly Func<int, byte[]> _sectorReader;
    private bool _initialized;

    public Iso9660Reader(Func<int, byte[]> sectorReader)
    {
        _sectorReader = sectorReader;
    }

    public uint RootDirectoryLocation { get; private set; }
    public uint RootDirectorySize { get; private set; }

    /// <summary>Volume identifier (32-byte ASCII field from the Primary Volume Descriptor).</summary>
    public string? VolumeIdentifier { get; private set; }

    /// <summary>Volume space size in sectors (from the Primary Volume Descriptor).</summary>
    public uint VolumeSpaceSize { get; private set; }

    public void Initialize()
    {
        int sectorIndex = VolumeDescriptorSector;
        while (true)
        {
            var sector = _sectorReader(sectorIndex);
            byte type = sector[0];

            if (type == PrimaryVolumeDescriptorType)
            {
                ParsePrimaryVolumeDescriptor(sector);
                return;
            }

            if (type == 255)
            {
                throw new InvalidDataException("ISO 9660: No Primary Volume Descriptor found.");
            }

            sectorIndex++;
        }
    }

    private void ParsePrimaryVolumeDescriptor(byte[] sector)
    {
        // Volume space size (LE uint32) at offset 80
        VolumeSpaceSize = BitConverter.ToUInt32(sector, 80);

        // Volume identifier (32 ASCII bytes) at offset 40
        var volIdBytes = sector.AsSpan(40, 32).ToArray();
        int volLen = 0;
        while (volLen < volIdBytes.Length && volIdBytes[volLen] != 0 && volIdBytes[volLen] != ' ') volLen++;
        VolumeIdentifier = Encoding.ASCII.GetString(volIdBytes, 0, volLen);

        // Root directory record is at offset 156, 34 bytes long
        int rootOffset = 156;
        RootDirectoryLocation = BitConverter.ToUInt32(sector, rootOffset + 2);
        RootDirectorySize = BitConverter.ToUInt32(sector, rootOffset + 10);
        _initialized = true;
    }

    public byte[] ReadFile(string isoPath)
    {
        var pathParts = isoPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentDirRecord = new Iso9660DirectoryEntry
        {
            Location = RootDirectoryLocation,
            Size = RootDirectorySize,
            Flags = 0x02,
            FileNameLength = 1,
            FileName = "\0",
        };

        for (int i = 0; i < pathParts.Length; i++)
        {
            var entries = ReadDirectory(currentDirRecord);
            var found = false;
            foreach (var entry in entries)
            {
                if (string.Equals(entry.FileName, pathParts[i], StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry.FileName, pathParts[i] + ";1", StringComparison.OrdinalIgnoreCase))
                {
                    if (i == pathParts.Length - 1)
                    {
                        return ReadRawFile(entry);
                    }

                    currentDirRecord = entry;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                // Try case-insensitive partial match for directories
                bool matched = false;
                foreach (var entry in entries)
                {
                    if (entry.IsDirectory &&
                        string.Equals(entry.FileName.TrimEnd(';'), pathParts[i], StringComparison.OrdinalIgnoreCase))
                    {
                        currentDirRecord = entry;
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                {
                    throw new FileNotFoundException($"ISO 9660: Path component '{pathParts[i]}' not found.");
                }
            }
        }

        throw new FileNotFoundException($"ISO 9660: File '{isoPath}' not found.");
    }

    public bool FileExists(string isoPath)
    {
        try
        {
            var _ = ReadFile(isoPath);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    public List<Iso9660DirectoryEntry> ListDirectory(string isoPath)
    {
        var pathParts = isoPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentDirRecord = new Iso9660DirectoryEntry
        {
            Location = RootDirectoryLocation,
            Size = RootDirectorySize,
            Flags = 0x02,
            FileNameLength = 1,
            FileName = "\0",
        };

        foreach (var part in pathParts)
        {
            var entries = ReadDirectory(currentDirRecord);
            bool found = false;
            foreach (var entry in entries)
            {
                if (entry.IsDirectory &&
                    string.Equals(entry.FileName.TrimEnd(';'), part, StringComparison.OrdinalIgnoreCase))
                {
                    currentDirRecord = entry;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                throw new DirectoryNotFoundException($"ISO 9660: Directory '{isoPath}' not found.");
            }
        }

        return ReadDirectory(currentDirRecord);
    }

    /// <summary>
    /// Collects the deterministic volume statistics for this disc: identity fields from
    /// the Primary Volume Descriptor plus a full recursive entry count from the root.
    /// <see cref="Initialize"/> must have been called first.
    ///
    /// This is the filesystem-layer counterpart of <c>ChdReader.ComputeMapStatistics</c>;
    /// it performs a full directory traversal, so callers should compute it once and
    /// reuse the result rather than calling it per field.
    /// </summary>
    public IsoVolumeStatistics ComputeVolumeStatistics()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException(
                "ISO 9660: Initialize() must be called before ComputeVolumeStatistics().");
        }

        var root = new Iso9660DirectoryEntry
        {
            Location = RootDirectoryLocation,
            Size = RootDirectorySize,
            Flags = 0x02,
            FileNameLength = 1,
            FileName = "\0",
        };

        CountEntries(root, out int fileCount, out int directoryCount);

        return new IsoVolumeStatistics
        {
            VolumeIdentifier = VolumeIdentifier,
            VolumeSpaceSize = VolumeSpaceSize,
            RootDirectoryLocation = RootDirectoryLocation,
            RootDirectorySize = RootDirectorySize,
            SystemCnfPresent = FileExists("SYSTEM.CNF"),
            FileCount = fileCount,
            DirectoryCount = directoryCount,
        };
    }

    /// <summary>
    /// Recursively counts files and directories beneath a directory record.
    /// Excludes the implicit "." and ".." entries.
    /// </summary>
    public void CountEntries(Iso9660DirectoryEntry root, out int fileCount, out int directoryCount)
    {
        fileCount = 0;
        directoryCount = 0;
        CountEntriesCore(root, ref fileCount, ref directoryCount);
    }

    private void CountEntriesCore(Iso9660DirectoryEntry dirRecord, ref int fileCount, ref int directoryCount)
    {
        // Some disc images contain directory records whose location points outside
        // the disc (e.g. stray XA / overlaid entries). Guard so enumeration never
        // throws; such subtrees are ignored rather than counted.
        List<Iso9660DirectoryEntry> entries;
        try
        {
            entries = ReadDirectory(dirRecord);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidDataException or IndexOutOfRangeException)
        {
            return;
        }

        directoryCount++;

        foreach (var entry in entries)
        {
            if (entry.FileName == "\0" || entry.FileName == "..") continue;

            if (entry.IsDirectory)
            {
                CountEntriesCore(entry, ref fileCount, ref directoryCount);
            }
            else
            {
                fileCount++;
            }
        }
    }

    private List<Iso9660DirectoryEntry> ReadDirectory(Iso9660DirectoryEntry dirRecord)
    {
        var entries = new List<Iso9660DirectoryEntry>();
        var data = ReadRawFile(dirRecord, padToSectorSize: true);
        int offset = 0;

        while (offset < data.Length)
        {
            byte recordLength = data[offset];
            if (recordLength == 0)
            {
                // Move to next sector boundary
                offset = ((offset / SectorSize) + 1) * SectorSize;
                if (offset >= data.Length) break;
                recordLength = data[offset];
                if (recordLength == 0) break;
            }

            if (offset + recordLength > data.Length) break;

            byte fileNameLength = data[offset + 32];
            byte flags = data[offset + 25];
            uint location = BitConverter.ToUInt32(data, offset + 2);
            uint size = BitConverter.ToUInt32(data, offset + 10);
            byte extraAttrLength = data[offset + 1];

            int nameOffset = 33;
            string fileName;
            if (fileNameLength == 1)
            {
                byte nameByte = data[offset + nameOffset];
                fileName = nameByte switch
                {
                    0 => "\0",
                    1 => "..",
                    _ => ((char)nameByte).ToString(),
                };
            }
            else
            {
                fileName = Encoding.ASCII.GetString(data, offset + nameOffset, fileNameLength);
            }

            entries.Add(new Iso9660DirectoryEntry
            {
                Location = location,
                Size = size,
                Flags = flags,
                FileNameLength = fileNameLength,
                FileName = fileName,
            });

            offset += recordLength;
        }

        return entries;
    }

    private byte[] ReadRawFile(Iso9660DirectoryEntry entry, bool padToSectorSize = false)
    {
        var totalSectors = (int)((entry.Size + SectorSize - 1) / SectorSize);
        var data = new byte[totalSectors * SectorSize];

        for (int i = 0; i < totalSectors; i++)
        {
            var sector = _sectorReader((int)entry.Location + i);
            Buffer.BlockCopy(sector, 0, data, i * SectorSize, SectorSize);
        }

        if (padToSectorSize || data.Length == entry.Size)
        {
            return data;
        }

        // File data is exactly entry.Size bytes; trim the sector-rounded padding so
        // hash/size and parsed content match the bytes stored on the disc.
        Array.Resize(ref data, (int)entry.Size);
        return data;
    }
}
