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

    public Iso9660Reader(Func<int, byte[]> sectorReader)
    {
        _sectorReader = sectorReader;
    }

    public uint RootDirectoryLocation { get; private set; }
    public uint RootDirectorySize { get; private set; }

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
        // Root directory record is at offset 156, 34 bytes long
        int rootOffset = 156;
        RootDirectoryLocation = BitConverter.ToUInt32(sector, rootOffset + 2);
        RootDirectorySize = BitConverter.ToUInt32(sector, rootOffset + 10);
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

    private List<Iso9660DirectoryEntry> ReadDirectory(Iso9660DirectoryEntry dirRecord)
    {
        var entries = new List<Iso9660DirectoryEntry>();
        var data = ReadRawFile(dirRecord);
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

    private byte[] ReadRawFile(Iso9660DirectoryEntry entry)
    {
        var totalSectors = (int)((entry.Size + SectorSize - 1) / SectorSize);
        var data = new byte[totalSectors * SectorSize];

        for (int i = 0; i < totalSectors; i++)
        {
            var sector = _sectorReader((int)entry.Location + i);
            Buffer.BlockCopy(sector, 0, data, i * SectorSize, SectorSize);
        }

        return data;
    }
}
