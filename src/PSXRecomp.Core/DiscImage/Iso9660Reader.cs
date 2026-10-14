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

    /// <summary>
    /// Normalizes an ISO 9660 file/directory identifier by removing the version number suffix (;1, ;2, etc.).
    /// Per ISO 9660, the version number is a semicolon followed by digits. This method removes it for
    /// consistent path handling across ReadFile, ListDirectory, and EnumerateFiles.
    /// </summary>
    internal static string NormalizeIdentifier(string identifier)
    {
        // Special entries "." (0x00) and ".." (0x01) are encoded as single bytes and never have version numbers.
        if (identifier == "\0" || identifier == "..")
        {
            return identifier;
        }

        // Find the last semicolon that starts a version number (; followed by digits only until end).
        int semicolonIndex = identifier.LastIndexOf(';');
        if (semicolonIndex >= 0)
        {
            bool allDigits = true;
            for (int i = semicolonIndex + 1; i < identifier.Length; i++)
            {
                if (!char.IsDigit(identifier[i]))
                {
                    allDigits = false;
                    break;
                }
            }

            if (allDigits && semicolonIndex + 1 < identifier.Length)
            {
                return identifier[..semicolonIndex];
            }
        }

        return identifier;
    }

    /// <summary>
    /// Normalizes a path component (single path segment) by removing ISO 9660 version number suffixes.
    /// Public helper for callers that need to normalize paths before passing to ReadFile/ListDirectory.
    /// </summary>
    public static string NormalizeIdentifierForPath(string pathComponent) => NormalizeIdentifier(pathComponent);

    /// <summary>
    /// Upper bound for a single file's extent read through <see cref="ReadRawFile"/>. The
    /// largest medium a PlayStation disc image can come from is a 99-minute CD: 99 * 60 * 75
    /// = 445,500 sectors of 2048-byte user data, about 870 MiB — and no single file can
    /// exceed the whole disc's capacity. 1 GiB leaves ample headroom for any real PS1 file
    /// while still ruling out the near-2 GiB range the previous int.MaxValue addressability
    /// check alone let through as an unbounded resource, not merely an addressing limit.
    /// </summary>
    private const long MaxReadableExtentBytes = 1L * 1024 * 1024 * 1024;

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

            // Prefer an exact on-disc identifier (including ;1 or ;2) when specified.
            // An unversioned path remains compatible with versioned ISO file names.
            var part = pathParts[i];
            var entry = entries.FirstOrDefault(candidate =>
                string.Equals(candidate.FileName, part, StringComparison.OrdinalIgnoreCase))
                ?? entries.FirstOrDefault(candidate =>
                    string.Equals(NormalizeIdentifier(candidate.FileName), part, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                throw new FileNotFoundException($"ISO 9660: Path component '{part}' not found.");
            }

            if (i == pathParts.Length - 1)
            {
                return ReadRawFile(entry);
            }

            currentDirRecord = entry;
        }

        throw new FileNotFoundException($"ISO 9660: File '{isoPath}' not found.");
    }

    /// <summary>
    /// Reads an enumerated file by its exact directory record, avoiding ambiguity between
    /// distinct ISO versions that share the same normalized path.
    /// </summary>
    public byte[] ReadEnumeratedFile(Iso9660DirectoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsDirectory)
        {
            throw new InvalidDataException("ISO 9660: A directory cannot be read as an enumerated file.");
        }

        return ReadRawFile(entry);
    }

    /// <summary>
    /// Reads a file directly from its directory entry, preserving the exact version.
    /// Use this when the entry comes from <see cref="EnumerateFiles"/> or <see cref="ListDirectory"/>
    /// to avoid normalized path ambiguity with multiple file versions.
    /// </summary>
    public byte[] ReadFile(Iso9660DirectoryEntry entry) => ReadEnumeratedFile(entry);

    /// <summary>
    /// Represents an enumerated file with both its normalized path (for compatibility) and its full versioned path
    /// (for unique identification across versioned directories).
    /// </summary>
    public sealed record EnumeratedFile
    {
        /// <summary>Normalized path without version numbers (e.g., "DIR/GAME.EXE"). Compatible with ReadFile/ListDirectory.</summary>
        public required string NormalizedPath { get; init; }

        /// <summary>Full versioned path preserving all directory and file version numbers (e.g., "DIR;1/GAME.EXE;1").</summary>
        public required string VersionedPath { get; init; }

        /// <summary>The original directory entry with on-disc identifiers intact.</summary>
        public required Iso9660DirectoryEntry Entry { get; init; }
    }

    /// <summary>
    /// Every regular file of the volume, depth first, with both normalized and versioned paths, in ordinal
    /// path order so the result is deterministic. Each directory is entered by its unique on-disc location, so multiple
    /// directories with the same normalized name (e.g., DIR;1 and DIR;2) are all traversed.
    /// </summary>
    public IReadOnlyList<EnumeratedFile> EnumerateFiles()
    {
        var files = new List<EnumeratedFile>();
        var visited = new HashSet<uint>();

        void Walk(string normalizedDirectory, string versionedDirectory, Iso9660DirectoryEntry dirRecord, bool isRoot)
        {
            List<Iso9660DirectoryEntry> entries;
            try
            {
                entries = ReadDirectory(dirRecord);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidDataException or IndexOutOfRangeException)
            {
                if (isRoot)
                {
                    // Root directory failure must propagate - cannot treat a broken root as an empty disc.
                    throw;
                }
                // Skip invalid child directory; continue with siblings
                return;
            }

            foreach (var entry in entries)
            {
                var normalizedName = NormalizeIdentifier(entry.FileName);
                if (entry.FileName is "\0" or ".." || normalizedName.Length == 0)
                {
                    continue;
                }

                var normalizedPath = normalizedDirectory.Length == 0 ? normalizedName : normalizedDirectory + "/" + normalizedName;
                var versionedPath = versionedDirectory.Length == 0 ? entry.FileName : versionedDirectory + "/" + entry.FileName;

                if (!entry.IsDirectory)
                {
                    files.Add(new EnumeratedFile
                    {
                        NormalizedPath = normalizedPath,
                        VersionedPath = versionedPath,
                        Entry = entry
                    });
                }
                else if (visited.Add(entry.Location))
                {
                    Walk(normalizedPath, versionedPath, entry, false);
                }
            }
        }

        var rootRecord = new Iso9660DirectoryEntry
        {
            Location = RootDirectoryLocation,
            Size = RootDirectorySize,
            Flags = 0x02,
            FileNameLength = 1,
            FileName = "\0",
        };
        Walk("", "", rootRecord, true);
        files.Sort(static (a, b) => string.CompareOrdinal(a.NormalizedPath, b.NormalizedPath));
        return files;
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
            var directory = entries.FirstOrDefault(entry => entry.IsDirectory &&
                string.Equals(entry.FileName, part, StringComparison.OrdinalIgnoreCase))
                ?? entries.FirstOrDefault(entry => entry.IsDirectory &&
                    string.Equals(NormalizeIdentifier(entry.FileName), part, StringComparison.OrdinalIgnoreCase));

            if (directory is null)
            {
                throw new DirectoryNotFoundException($"ISO 9660: Directory '{isoPath}' not found.");
            }

            currentDirRecord = directory;
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
        // entry.Size and entry.Location come straight from an on-disc directory record
        // and are untrusted. Everything below is computed in long so nothing wraps, and
        // the extent is proven to exist in the container before a byte is allocated.
        long totalSectors = ((long)entry.Size + SectorSize - 1) / SectorSize;
        long totalBytes = totalSectors * SectorSize;
        long lastSector = (long)entry.Location + totalSectors - 1;

        // int.MaxValue alone is an addressability check, not a resource cap: an extent
        // just below it is still addressable but would allocate close to 2 GiB from a
        // container that, via a CHD's zero-cost CompressionParent/CompressionSelf hunks,
        // can be tiny on disk. MaxReadableExtentBytes bounds the allocation independently
        // of what the container claims to hold; it is tighter than int.MaxValue for every
        // real PS1 file, so it never rejects anything the addressability check would not.
        if (totalBytes > Math.Min(MaxReadableExtentBytes, int.MaxValue)
            || (totalSectors > 0 && lastSector > int.MaxValue))
        {
            throw new InvalidDataException(
                $"ISO 9660: directory entry declares a {entry.Size}-byte extent at sector {entry.Location}, which is not addressable.");
        }

        if (totalSectors > 0)
        {
            // Reading the last sector first proves the container really holds the whole
            // extent. It bounds the allocation by the real image rather than by an
            // arbitrary ceiling, and cannot reject anything the sector-by-sector read
            // below would have succeeded on.
            _sectorReader((int)lastSector);
        }

        var data = new byte[totalBytes];

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
