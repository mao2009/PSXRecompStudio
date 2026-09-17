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

    // This reader is used by the PS1 analysis pipeline, not as a general ISO
    // extraction utility. Bounding one materialized file prevents a hostile
    // directory record from turning an untrusted uint length into a process-sized
    // byte[] allocation. Legitimate analysis inputs (SYSTEM.CNF / PS-X EXE) are
    // far below this ceiling.
    internal const int MaxSingleFileBytes = 256 * 1024 * 1024;
    internal const ulong MaxVolumeBytes = 1UL * 1024UL * 1024UL * 1024UL;

    private readonly Func<int, byte[]> _sectorReader;
    private bool _initialized;

    public Iso9660Reader(Func<int, byte[]> sectorReader)
    {
        ArgumentNullException.ThrowIfNull(sectorReader);
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
            var sector = ReadSectorChecked(sectorIndex, "volume descriptor");
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

            sectorIndex = checked(sectorIndex + 1);
        }
    }

    private void ParsePrimaryVolumeDescriptor(byte[] sector)
    {
        if (sector.Length < SectorSize)
        {
            throw new InvalidDataException(
                $"ISO 9660: Primary Volume Descriptor sector is {sector.Length} bytes; expected at least {SectorSize}.");
        }

        VolumeSpaceSize = BitConverter.ToUInt32(sector, 80);
        if (VolumeSpaceSize == 0)
        {
            throw new InvalidDataException("ISO 9660: VolumeSpaceSize must be positive.");
        }

        ulong volumeBytes = (ulong)VolumeSpaceSize * SectorSize;
        if (volumeBytes > MaxVolumeBytes)
        {
            throw new InvalidDataException(
                $"ISO 9660: declared volume size {volumeBytes} bytes exceeds the {MaxVolumeBytes}-byte PS1 analysis ceiling.");
        }

        var volIdBytes = sector.AsSpan(40, 32).ToArray();
        int volLen = 0;
        while (volLen < volIdBytes.Length && volIdBytes[volLen] != 0 && volIdBytes[volLen] != ' ') volLen++;
        VolumeIdentifier = Encoding.ASCII.GetString(volIdBytes, 0, volLen);

        int rootOffset = 156;
        RootDirectoryLocation = BitConverter.ToUInt32(sector, rootOffset + 2);
        RootDirectorySize = BitConverter.ToUInt32(sector, rootOffset + 10);
        ValidateExtent(RootDirectoryLocation, RootDirectorySize, "root directory", allowLargeAllocation: false);
        _initialized = true;
    }

    public byte[] ReadFile(string isoPath)
    {
        EnsureInitialized();
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
        EnsureInitialized();
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
    /// </summary>
    public IsoVolumeStatistics ComputeVolumeStatistics()
    {
        EnsureInitialized();

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
        EnsureInitialized();
        fileCount = 0;
        directoryCount = 0;
        CountEntriesCore(root, ref fileCount, ref directoryCount);
    }

    private void CountEntriesCore(Iso9660DirectoryEntry dirRecord, ref int fileCount, ref int directoryCount)
    {
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
            if (recordLength == DirectoryRecordTerminator)
            {
                offset = checked(((offset / SectorSize) + 1) * SectorSize);
                if (offset >= data.Length) break;
                recordLength = data[offset];
                if (recordLength == DirectoryRecordTerminator) break;
            }

            if (recordLength < 34 || checked(offset + recordLength) > data.Length)
            {
                break;
            }

            byte fileNameLength = data[offset + 32];
            if (fileNameLength > recordLength - 33)
            {
                throw new InvalidDataException(
                    $"ISO 9660: directory record at offset {offset} declares filename length {fileNameLength} beyond record length {recordLength}.");
            }

            byte flags = data[offset + 25];
            uint location = BitConverter.ToUInt32(data, offset + 2);
            uint size = BitConverter.ToUInt32(data, offset + 10);

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

            // Validate extent metadata while it is still cheap. This rejects a
            // hostile size/location during directory enumeration instead of only
            // when a later consumer happens to open the entry.
            ValidateExtent(location, size, $"directory entry '{fileName}'", allowLargeAllocation: true);

            entries.Add(new Iso9660DirectoryEntry
            {
                Location = location,
                Size = size,
                Flags = flags,
                FileNameLength = fileNameLength,
                FileName = fileName,
            });

            offset = checked(offset + recordLength);
        }

        return entries;
    }

    private byte[] ReadRawFile(Iso9660DirectoryEntry entry, bool padToSectorSize = false)
    {
        EnsureInitialized();
        var extent = ValidateExtent(entry.Location, entry.Size, $"entry '{entry.FileName}'", allowLargeAllocation: false);

        var data = new byte[extent.AllocationBytes];
        for (int i = 0; i < extent.TotalSectors; i++)
        {
            int sectorIndex = checked((int)((ulong)entry.Location + (uint)i));
            var sector = ReadSectorChecked(sectorIndex, $"entry '{entry.FileName}'");
            Buffer.BlockCopy(sector, 0, data, checked(i * SectorSize), SectorSize);
        }

        if (padToSectorSize || data.Length == entry.Size)
        {
            return data;
        }

        Array.Resize(ref data, checked((int)entry.Size));
        return data;
    }

    private (int TotalSectors, int AllocationBytes) ValidateExtent(
        uint location,
        uint size,
        string description,
        bool allowLargeAllocation)
    {
        if (!_initialized && description != "root directory")
        {
            // During PVD parsing, VolumeSpaceSize is already populated before the
            // root extent is validated. All normal callers require initialization.
            EnsureInitialized();
        }

        ulong totalSectors = ((ulong)size + SectorSize - 1UL) / SectorSize;
        ulong endSector;
        try
        {
            endSector = checked((ulong)location + totalSectors);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException($"ISO 9660: {description} sector extent overflows.", ex);
        }

        if ((ulong)location > VolumeSpaceSize || endSector > VolumeSpaceSize)
        {
            throw new InvalidDataException(
                $"ISO 9660: {description} extent [{location}, {endSector}) exceeds declared volume size {VolumeSpaceSize} sectors.");
        }

        ulong allocationBytes;
        try
        {
            allocationBytes = checked(totalSectors * SectorSize);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException($"ISO 9660: {description} allocation size overflows.", ex);
        }

        if (allocationBytes > int.MaxValue || totalSectors > int.MaxValue)
        {
            throw new InvalidDataException(
                $"ISO 9660: {description} is too large for the in-memory reader ({allocationBytes} bytes).");
        }

        if (!allowLargeAllocation && allocationBytes > MaxSingleFileBytes)
        {
            throw new InvalidDataException(
                $"ISO 9660: {description} would allocate {allocationBytes} bytes, above the {MaxSingleFileBytes}-byte safety ceiling.");
        }

        return (checked((int)totalSectors), checked((int)allocationBytes));
    }

    private byte[] ReadSectorChecked(int sectorIndex, string description)
    {
        if (sectorIndex < 0)
        {
            throw new InvalidDataException($"ISO 9660: {description} requested negative sector {sectorIndex}.");
        }

        var sector = _sectorReader(sectorIndex);
        if (sector is null || sector.Length < SectorSize)
        {
            throw new InvalidDataException(
                $"ISO 9660: {description} sector {sectorIndex} returned {sector?.Length ?? 0} bytes; expected at least {SectorSize}.");
        }
        return sector;
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("ISO 9660: Initialize() must be called before reading the volume.");
        }
    }
}
