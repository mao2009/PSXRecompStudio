using System.Buffers.Binary;
using System.Text;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Runtime.CdRom;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Builds minimal, in-memory ISO 9660 images (2048-byte user-data sectors) so the
/// post-filesystem stages of <see cref="RomAnalysisPipeline"/> can be exercised
/// without a copyrighted disc image.
///
/// Only what <see cref="Iso9660Reader"/> actually reads is emitted: a Primary Volume
/// Descriptor, a single-sector directory per directory, and file extents. A path with
/// separators (for example <c>DIR;1/GAME.EXE;1</c>) creates the intervening directories,
/// each named with its on-disc version suffix. Images are built in memory and never committed.
/// </summary>
[Test]
public sealed class SyntheticIsoImageBuilder
{
    private const int SectorSize = Iso9660Reader.SectorSize;
    private const int VolumeDescriptorSector = 16;
    private const int TerminatorSector = 17;
    private const int RootDirectorySector = 18;
    private const int PathTableSector = 19;
    private const int FirstFileSector = 20;
    private const int BothEndianFirstFileSector = 21; // the Type M path table takes sector 20 in that layout
    private const int MPathTableSector = 20;
    private const int RootDirectoryRecordOffset = 156;
    private const int PathTableSize = 10; // 8-byte header + 1-byte name + 1 pad byte
    private const int FileStructureVersionOffset = 881; // PVD BP 882, File Structure Version (ECMA-119 table of PVD fields), value 1

    // Single-byte on-disc names for the implicit "." (0x00) and ".." (0x01) records.
    private static readonly byte[] SelfName = [0];
    private static readonly byte[] ParentName = [1];

    private readonly record struct Child(string Name, bool IsDirectory, byte[]? Content, string Path);

    private readonly List<(string Path, byte[] Content)> _files = [];
    private readonly Dictionary<string, (uint Location, uint Size)> _corruptDirectories = new(StringComparer.Ordinal);
    private (uint Location, uint Size)? _corruptRoot;
    private string _volumeIdentifier = "PSXRECOMP_TEST";
    private bool _emitPrimaryVolumeDescriptor = true;
    private bool _bothEndianNumericFields;

    /// <summary>
    /// Corrected volume layout (ECMA-119): fills the big-endian halves of the both-endian PVD fields and of each
    /// directory record's location and data length; sets Volume Set Size, Volume Sequence Number, Logical Block
    /// Size and File Structure Version; sets each directory record's Volume Sequence Number; writes the root's
    /// ".." record; and writes complete L and M path tables (one record per directory, ordered by 6.9.1) in
    /// sectors 19 and 20, which moves the first file extent to sector 21. Directory identifiers carry no version
    /// number. Path tables must fit one sector; larger hierarchies throw.
    /// Off by default: the legacy layout stays byte-identical so the checked baseline pin does not move.
    /// </summary>
    public SyntheticIsoImageBuilder WithBothEndianNumericFields()
    {
        _bothEndianNumericFields = true;
        return this;
    }

    /// <summary>Sets the 32-byte volume identifier written into the PVD.</summary>
    public SyntheticIsoImageBuilder WithVolumeIdentifier(string volumeIdentifier)
    {
        _volumeIdentifier = volumeIdentifier;
        return this;
    }

    /// <summary>
    /// Writes a volume-descriptor-set terminator instead of a PVD, so
    /// <see cref="Iso9660Reader.Initialize"/> fails and the FILESYSTEM stage fails.
    /// </summary>
    public SyntheticIsoImageBuilder WithoutPrimaryVolumeDescriptor()
    {
        _emitPrimaryVolumeDescriptor = false;
        return this;
    }

    /// <summary>
    /// Adds a file at <paramref name="versionedPath"/>. A path with no separator lands in
    /// the root directory; separators (for example <c>DIR;1/GAME.EXE;1</c>) create the
    /// intervening directories. Each component keeps its on-disc version suffix.
    /// </summary>
    public SyntheticIsoImageBuilder AddFile(string versionedPath, byte[] content)
    {
        _files.Add((versionedPath, content));
        return this;
    }

    /// <summary>Adds a SYSTEM.CNF whose BOOT entry points at <paramref name="bootValue"/>.</summary>
    public SyntheticIsoImageBuilder AddSystemCnf(string bootValue) =>
        AddFile("SYSTEM.CNF;1", Encoding.ASCII.GetBytes($"BOOT = {bootValue}\r\nTCB = 4\r\nEVENT = 10\r\nSTACK = 801FFFF0\r\n"));

    /// <summary>Adds a SYSTEM.CNF with no BOOT entry, so parsing fails.</summary>
    public SyntheticIsoImageBuilder AddSystemCnfWithoutBoot() =>
        AddFile("SYSTEM.CNF;1", Encoding.ASCII.GetBytes("TCB = 4\r\nEVENT = 10\r\nSTACK = 801FFFF0\r\n"));

    /// <summary>
    /// Overrides the directory record the parent writes for <paramref name="versionedDirectoryPath"/>
    /// (for example <c>DIR;1</c>), so tests can model a corrupt child extent.
    /// </summary>
    public SyntheticIsoImageBuilder CorruptDirectoryExtent(string versionedDirectoryPath, uint location, uint size)
    {
        _corruptDirectories[versionedDirectoryPath] = (location, size);
        return this;
    }

    /// <summary>Overrides the root directory record in the PVD, so tests can model a corrupt root.</summary>
    public SyntheticIsoImageBuilder CorruptRootDirectoryExtent(uint location, uint size)
    {
        _corruptRoot = (location, size);
        return this;
    }

    public byte[] Build()
    {
        // Resolve the directory tree implied by the file paths, in first-seen order.
        var directories = new List<string>();
        var seenDirectories = new HashSet<string>(StringComparer.Ordinal);
        var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var childrenOf = new Dictionary<string, List<Child>>(StringComparer.Ordinal);
        var rootChildren = new List<Child>();

        List<Child> ChildrenOf(string directory)
        {
            if (!childrenOf.TryGetValue(directory, out var list))
            {
                list = [];
                childrenOf[directory] = list;
            }
            return list;
        }

        void AddChild(string parent, Child child) =>
            (parent.Length == 0 ? rootChildren : ChildrenOf(parent)).Add(child);

        foreach (var (path, content) in _files)
        {
            var parts = path.Split('/');
            var parent = "";
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var directory = parent.Length == 0 ? parts[i] : parent + "/" + parts[i];
                if (seenDirectories.Add(directory))
                {
                    directories.Add(directory);
                    parentOf[directory] = parent;
                    AddChild(parent, new Child(parts[i], IsDirectory: true, Content: null, directory));
                }
                parent = directory;
            }
            AddChild(parent, new Child(parts[^1], IsDirectory: false, content, path));
        }

        // One sector per directory after the path table, then the file extents.
        var directorySector = new Dictionary<string, uint>(StringComparer.Ordinal);
        var fileSector = new Dictionary<string, uint>(StringComparer.Ordinal);
        var cursor = (uint)(_bothEndianNumericFields ? BothEndianFirstFileSector : FirstFileSector);
        foreach (var directory in directories)
        {
            directorySector[directory] = cursor++;
        }
        foreach (var (path, content) in _files)
        {
            fileSector[path] = cursor;
            cursor += (uint)Math.Max(1, (content.Length + SectorSize - 1) / SectorSize);
        }

        // Corrected layout: check every directory's record area and identifiers before the first byte is written, so a
        // refused build returns no image and cannot spill into the path tables or neighbouring sectors.
        if (_bothEndianNumericFields)
        {
            ValidateCorrectedDirectories(rootChildren, childrenOf, directories);
        }

        var image = new byte[cursor * SectorSize];

        WriteVolumeDescriptor(image, cursor,
            _bothEndianNumericFields ? BuildPathTables(directories, parentOf, directorySector) : null);

        var (rootLocation, rootSize) = _corruptRoot ?? ((uint)RootDirectorySector, (uint)SectorSize);
        WriteDirectory(image, RootDirectorySector, rootLocation, rootSize, rootChildren,
            parentSector: null, directorySector, fileSector);
        foreach (var directory in directories)
        {
            var parent = parentOf[directory];
            var parentSector = parent.Length == 0 ? (uint)RootDirectorySector : directorySector[parent];
            WriteDirectory(image, (int)directorySector[directory], directorySector[directory], SectorSize,
                ChildrenOf(directory), parentSector, directorySector, fileSector);
        }

        foreach (var (path, content) in _files)
        {
            Buffer.BlockCopy(content, 0, image, (int)fileSector[path] * SectorSize, content.Length);
        }

        return image;
    }

    private void WriteVolumeDescriptor(byte[] image, uint volumeSpaceSize, (byte[] L, byte[] M)? pathTables)
    {
        var offset = VolumeDescriptorSector * SectorSize;

        if (!_emitPrimaryVolumeDescriptor)
        {
            image[offset] = 255; // volume descriptor set terminator
            return;
        }

        image[offset] = 1; // primary volume descriptor
        Encoding.ASCII.GetBytes("CD001").CopyTo(image, offset + 1);
        image[offset + 6] = 1; // version

        var volumeId = _volumeIdentifier.PadRight(32).Substring(0, 32);
        Encoding.ASCII.GetBytes(volumeId).CopyTo(image, offset + 40);

        BitConverter.GetBytes(volumeSpaceSize).CopyTo(image, offset + 80);

        // OpenBIOS's own CD driver resolves files through the L path table (size @132, LBA @140), not the PVD's
        // root record alone, and expects the descriptor set to end with a terminator.
        var pathTableSize = (uint)(pathTables?.L.Length ?? PathTableSize);
        BitConverter.GetBytes(pathTableSize).CopyTo(image, offset + 132);
        BitConverter.GetBytes((uint)PathTableSector).CopyTo(image, offset + 140);
        var terminator = TerminatorSector * SectorSize;
        image[terminator] = 255;
        Encoding.ASCII.GetBytes("CD001").CopyTo(image, terminator + 1);
        image[terminator + 6] = 1;

        if (pathTables is { } tables)
        {
            tables.L.CopyTo(image, PathTableSector * SectorSize);
            tables.M.CopyTo(image, MPathTableSector * SectorSize);
            WriteBothEndianVolumeFields(image, offset, volumeSpaceSize, pathTableSize);
            image[offset + FileStructureVersionOffset] = 1;
        }
        else
        {
            // L path table: one entry, the root directory (name length 1, LBA, parent 1, name 0x00).
            var table = PathTableSector * SectorSize;
            image[table] = 1;
            BitConverter.GetBytes((uint)RootDirectorySector).CopyTo(image, table + 2);
            image[table + 6] = 1;
        }

        var (rootLocation, rootSize) = _corruptRoot ?? ((uint)RootDirectorySector, (uint)SectorSize);
        WriteDirectoryRecord(image, offset + RootDirectoryRecordOffset, rootLocation, rootSize, flags: 0x02, SelfName);
    }

    // ECMA-119 8.4: the big-endian halves of the PVD numeric fields, plus the single-volume set/sequence/block values.
    // Offsets are 0-based from the start of the PVD. The little-endian halves are written in WriteVolumeDescriptor.
    private void WriteBothEndianVolumeFields(byte[] image, int offset, uint volumeSpaceSize, uint pathTableSize)
    {
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(offset + 84), volumeSpaceSize);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset + 120), 1); // volume set size
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(offset + 122), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset + 124), 1); // volume sequence number
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(offset + 126), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset + 128), SectorSize); // logical block size
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(offset + 130), SectorSize);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(offset + 136), pathTableSize);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(offset + 148), MPathTableSector);
    }

    // ECMA-119 6.9 and 9.4: one Path Table Record per directory, numbered from the root (1) and ordered by level,
    // then parent directory number, then identifier (6.9.1). The L table is little-endian and the M table is
    // big-endian; both describe the same hierarchy. Each table must fit the one sector reserved for it.
    private (byte[] L, byte[] M) BuildPathTables(
        List<string> directories, Dictionary<string, string> parentOf, Dictionary<string, uint> directorySector)
    {
        var childrenOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var directory in directories)
        {
            var parent = parentOf[directory];
            if (!childrenOf.TryGetValue(parent, out var list))
            {
                childrenOf[parent] = list = [];
            }
            list.Add(directory);
        }

        // Breadth-first from the root, so a directory's queue position plus one is its directory number.
        var order = new List<string> { "" };
        for (var i = 0; i < order.Count; i++)
        {
            if (childrenOf.TryGetValue(order[i], out var children))
            {
                children.Sort((a, b) => CompareIdentifiers(IdentifierOf(a), IdentifierOf(b)));
                order.AddRange(children);
            }
        }

        var number = new Dictionary<string, ushort>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++)
        {
            number[order[i]] = (ushort)(i + 1);
        }

        var lTable = new List<byte>();
        var mTable = new List<byte>();
        foreach (var directory in order)
        {
            var identifier = IdentifierOf(directory);
            var extent = directory.Length == 0 ? (uint)RootDirectorySector : directorySector[directory];
            var parentNumber = number[directory.Length == 0 ? "" : parentOf[directory]];
            AppendPathTableRecord(lTable, identifier, extent, parentNumber, bigEndian: false);
            AppendPathTableRecord(mTable, identifier, extent, parentNumber, bigEndian: true);
        }

        if (lTable.Count > SectorSize)
        {
            throw new NotSupportedException("Path table exceeds one sector; the synthetic builder supports at most one sector of directories.");
        }
        return (lTable.ToArray(), mTable.ToArray());
    }

    private void AppendPathTableRecord(List<byte> table, byte[] identifier, uint extent, ushort parent, bool bigEndian)
    {
        var extentBytes = new byte[4];
        var parentBytes = new byte[2];
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(extentBytes, extent);
            BinaryPrimitives.WriteUInt16BigEndian(parentBytes, parent);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(extentBytes, extent);
            BinaryPrimitives.WriteUInt16LittleEndian(parentBytes, parent);
        }

        table.Add((byte)identifier.Length);
        table.Add(0); // extended attribute record length
        table.AddRange(extentBytes);
        table.AddRange(parentBytes);
        table.AddRange(identifier);
        if (identifier.Length % 2 != 0)
        {
            table.Add(0); // padding field
        }
    }

    // Directory identifiers carry no version number in the corrected layout (ECMA-119 7.6.1: d-characters only).
    // The legacy layout keeps the versioned names that the existing path contract depends on.
    private byte[] DirectoryName(string name) =>
        Encoding.ASCII.GetBytes(_bothEndianNumericFields ? StripVersion(name) : name);

    // Identifier of a directory path; the root is the single (00) byte.
    private byte[] IdentifierOf(string path) =>
        path.Length == 0 ? SelfName : DirectoryName(path[(path.LastIndexOf('/') + 1)..]);

    private static string StripVersion(string name)
    {
        var semicolon = name.LastIndexOf(';');
        return semicolon > 0 ? name[..semicolon] : name;
    }

    // ECMA-119 6.9.1: shorter identifiers are padded on the right with spaces (0x20) before comparison.
    private static int CompareIdentifiers(byte[] a, byte[] b)
    {
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : (byte)0x20;
            var y = i < b.Length ? b[i] : (byte)0x20;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }
        return 0;
    }

    private void WriteDirectory(
        byte[] image,
        int sector,
        uint selfLocation,
        uint selfSize,
        List<Child> children,
        uint? parentSector,
        Dictionary<string, uint> directorySector,
        Dictionary<string, uint> fileSector)
    {
        var offset = sector * SectorSize;

        // "." — the directory's own record.
        offset += WriteDirectoryRecord(image, offset, selfLocation, selfSize, flags: 0x02, SelfName);
        if (parentSector is { } parent)
        {
            offset += WriteDirectoryRecord(image, offset, parent, SectorSize, flags: 0x02, ParentName);
        }
        else if (_bothEndianNumericFields)
        {
            // The root's ".." refers to the root itself.
            offset += WriteDirectoryRecord(image, offset, selfLocation, selfSize, flags: 0x02, ParentName);
        }

        // ECMA-119 6.8.1.2 and 9.3: records follow the File Identifier order in the corrected layout.
        var ordered = _bothEndianNumericFields
            ? children.OrderBy(c => c.IsDirectory ? StripVersion(c.Name) : c.Name, Comparer<string>.Create(CompareRecordNames)).ToList()
            : children;
        foreach (var child in ordered)
        {
            offset += WriteChildRecord(image, offset, child, directorySector, fileSector);
        }
    }

    // ECMA-119 9.3: by File Name, then File Extension (both space-padded, 6.9.1 style), then File Version descending.
    private static int CompareRecordNames(string a, string b)
    {
        var (nameA, extensionA, versionA) = SplitRecordName(a);
        var (nameB, extensionB, versionB) = SplitRecordName(b);
        var order = ComparePadded(nameA, nameB);
        if (order == 0)
        {
            order = ComparePadded(extensionA, extensionB);
        }
        return order != 0 ? order : versionB.CompareTo(versionA);
    }

    private static (string Name, string Extension, int Version) SplitRecordName(string identifier)
    {
        if (identifier is "\0" or "\x01")
        {
            return (identifier, "", 0); // "." and ".." sort first
        }

        var semicolon = identifier.LastIndexOf(';');
        var body = semicolon >= 0 ? identifier[..semicolon] : identifier;
        var version = semicolon >= 0 && int.TryParse(identifier.AsSpan(semicolon + 1), out var parsed) ? parsed : 0;
        var dot = body.IndexOf('.');
        return dot >= 0 ? (body[..dot], body[(dot + 1)..], version) : (body, "", version);
    }

    private static int ComparePadded(string a, string b)
    {
        var width = Math.Max(a.Length, b.Length);
        return string.CompareOrdinal(a.PadRight(width), b.PadRight(width));
    }

    private int WriteChildRecord(
        byte[] image,
        int offset,
        Child child,
        Dictionary<string, uint> directorySector,
        Dictionary<string, uint> fileSector)
    {
        if (child.IsDirectory)
        {
            var (location, size) = _corruptDirectories.TryGetValue(child.Path, out var corrupt)
                ? corrupt
                : (directorySector[child.Path], (uint)SectorSize);
            return WriteDirectoryRecord(image, offset, location, size, flags: 0x02, DirectoryName(child.Name));
        }

        return WriteDirectoryRecord(image, offset, fileSector[child.Path],
            (uint)child.Content!.Length, flags: 0x00, Encoding.ASCII.GetBytes(child.Name));
    }

    // ECMA-119 9.1.1: 33 fixed bytes plus the identifier, padded to an even length.
    private static int RecordLength(int identifierLength)
    {
        var length = 33 + identifierLength;
        return length % 2 == 0 ? length : length + 1;
    }

    // ECMA-119 6.8.1: a directory is one sector, so its "." and ".." records plus every child record must fit in it.
    // The corrected layout writes directory identifiers without a version, so two children can normalise to one name.
    private void ValidateCorrectedDirectories(
        List<Child> rootChildren, Dictionary<string, List<Child>> childrenOf, List<string> directories)
    {
        ValidateCorrectedDirectory("", rootChildren);
        foreach (var directory in directories)
        {
            ValidateCorrectedDirectory(directory, childrenOf.TryGetValue(directory, out var children) ? children : []);
        }
    }

    private void ValidateCorrectedDirectory(string directory, List<Child> children)
    {
        var label = directory.Length == 0 ? "(root)" : directory;
        var total = 2 * RecordLength(1); // "." and ".." (identifiers 0x00 and 0x01)
        var directoryKeys = new Dictionary<string, string>(StringComparer.Ordinal); // normalised name -> path
        var fileKeys = new Dictionary<string, string>(StringComparer.Ordinal);      // file identifier -> path
        foreach (var child in children)
        {
            var identifier = child.IsDirectory ? DirectoryName(child.Name) : Encoding.ASCII.GetBytes(child.Name);
            var recordLength = RecordLength(identifier.Length);
            if (recordLength > byte.MaxValue)
            {
                // LEN_DR is one byte; WriteDirectoryRecord would truncate the value and end the directory early.
                throw new NotSupportedException(
                    $"the record for '{child.Path}' in '{label}' is {recordLength} bytes; LEN_DR is one byte (at most 255).");
            }
            total += recordLength;

            var key = Encoding.ASCII.GetString(identifier);
            var keys = child.IsDirectory ? directoryKeys : fileKeys;
            if (keys.TryGetValue(key, out var first))
            {
                throw new NotSupportedException(child.IsDirectory
                    ? $"directory '{label}' has two children whose identifiers both normalise to '{key}' " +
                      $"once the version is removed: '{first}' and '{child.Path}'."
                    : $"directory '{label}' has two files with the identifier '{key}': '{first}' and '{child.Path}'. " +
                      "The second would overwrite the first's content.");
            }
            keys[key] = child.Path;
        }

        // Directory identifiers have no version, so a directory and a file whose names agree once the version is removed
        // cannot be told apart by name. Rejected conservatively; files that differ only in version remain allowed.
        foreach (var (file, filePath) in fileKeys)
        {
            var stripped = StripVersion(file);
            if (directoryKeys.TryGetValue(stripped, out var directoryPath))
            {
                throw new NotSupportedException(
                    $"directory '{label}' has the directory '{directoryPath}' and the file '{filePath}', " +
                    $"which both normalise to '{stripped}'.");
            }
        }

        if (total > SectorSize)
        {
            throw new NotSupportedException(
                $"directory record area of '{label}' needs {total} bytes, more than one {SectorSize}-byte sector; " +
                "split the directory or shorten its identifiers.");
        }
    }

    private int WriteDirectoryRecord(byte[] buffer, int offset, uint location, uint size, byte flags, byte[] nameBytes)
    {
        var recordLength = RecordLength(nameBytes.Length);

        buffer[offset] = (byte)recordLength;
        buffer[offset + 1] = 0; // extended attribute length
        BitConverter.GetBytes(location).CopyTo(buffer, offset + 2);
        BitConverter.GetBytes(size).CopyTo(buffer, offset + 10);
        if (_bothEndianNumericFields)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 28), 1); // volume sequence number
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset + 30), 1);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 6), location);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 14), size);
        }
        buffer[offset + 25] = flags;
        buffer[offset + 32] = (byte)nameBytes.Length;
        nameBytes.CopyTo(buffer, offset + 33);

        return recordLength;
    }
}

/// <summary>
/// Builds minimal PS-X EXE byte arrays for pipeline stage tests, including
/// deliberately malformed variants (bad magic, truncated header, inconsistent
/// text region) used to drive the failure classification.
/// </summary>
[Test]
public static class SyntheticPsxExeBuilder
{
    /// <summary>Conventional PS1 load address, used as the default text start.</summary>
    public const uint DefaultTextStart = 0x80010000;

    /// <summary>
    /// Builds a PS-X EXE. <paramref name="storedTextBytes"/> is what the file actually
    /// contains after the header, which may be shorter than <paramref name="textSize"/>
    /// to model a truncated or inconsistent executable.
    /// </summary>
    public static byte[] Build(
        uint entryPoint,
        uint textStart,
        uint textSize,
        byte[] storedTextBytes,
        ulong magic = PsxExeHeader.Magic,
        uint spInitial = 0x801FFF00,
        uint gpInitial = 0)
    {
        ArgumentNullException.ThrowIfNull(storedTextBytes);

        var file = new byte[PsxExeHeader.HeaderSize + storedTextBytes.Length];

        BitConverter.GetBytes(magic).CopyTo(file, 0);
        BitConverter.GetBytes(entryPoint).CopyTo(file, 0x10);
        BitConverter.GetBytes(textStart).CopyTo(file, 0x18);
        BitConverter.GetBytes(textSize).CopyTo(file, 0x1C);
        BitConverter.GetBytes(spInitial).CopyTo(file, 0x30);
        BitConverter.GetBytes(gpInitial).CopyTo(file, 0x34);

        storedTextBytes.CopyTo(file, PsxExeHeader.HeaderSize);
        return file;
    }

    /// <summary>
    /// Builds a well-formed executable whose entry point is the start of a text region
    /// of <paramref name="instructionCount"/> decodable instructions.
    /// </summary>
    public static byte[] BuildValid(int instructionCount = 16, uint textStart = DefaultTextStart)
    {
        var text = BuildText(instructionCount);
        return Build(textStart, textStart, (uint)text.Length, text);
    }

    /// <summary>
    /// Produces a decodable instruction stream: <c>addiu</c>/<c>nop</c> pairs terminated
    /// by <c>jr $ra</c> plus its delay slot, so basic-block construction has real input.
    /// </summary>
    public static byte[] BuildText(int instructionCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(instructionCount, 2);

        var words = new uint[instructionCount];
        for (int i = 0; i < instructionCount - 2; i++)
        {
            // addiu $t0, $zero, i  — opcode 0x09, rs=0, rt=8
            words[i] = 0x24080000u | (uint)(i & 0xFFFF);
        }

        words[instructionCount - 2] = 0x03E00008u; // jr $ra
        words[instructionCount - 1] = 0x00000000u; // nop (delay slot)

        var bytes = new byte[instructionCount * 4];
        for (int i = 0; i < instructionCount; i++)
        {
            BitConverter.GetBytes(words[i]).CopyTo(bytes, i * 4);
        }
        return bytes;
    }
}

/// <summary>
/// Builds a deterministic synthetic CD image from the in-memory ISO 9660 volume
/// (<see cref="SyntheticIsoImageBuilder"/>) by wrapping every 2048-byte user-data sector in a
/// raw 2352-byte Mode 2 Form 1 frame (sync, BCD MSF header, data subheader). The bytes are a
/// pure function of the inputs, so the same disc is reproduced on every machine; the mirror in
/// <c>scripts/demo/synthetic-disc.ps1</c> pins the same SHA-256. No commercial data.
/// </summary>
[Test]
public static class SyntheticDiscBuilder
{
    /// <summary>On-disc BOOT value and the file name of the synthetic boot executable.</summary>
    public const string BootPath = @"cdrom:\PSXRECOMP.EXE;1";
    public const string ExeIsoName = "PSXRECOMP.EXE;1";

    /// <summary>
    /// A bootable Mode 2 Form 1 disc whose SYSTEM.CNF runs <see cref="ExeIsoName"/>. Pass
    /// <paramref name="iso9660BothEndian"/> for the corrected volume (see
    /// <see cref="SyntheticIsoImageBuilder.WithBothEndianNumericFields"/>); the default is the legacy baseline.
    /// </summary>
    public static byte[] Bootable(byte[] exeBytes, bool iso9660BothEndian = false)
    {
        var iso = new SyntheticIsoImageBuilder();
        if (iso9660BothEndian)
        {
            iso.WithBothEndianNumericFields();
        }
        return Mode2Form1(iso.AddSystemCnf(BootPath).AddFile(ExeIsoName, exeBytes).Build());
    }

    /// <summary>Sector-aligns a synthetic EXE payload for OpenBIOS CD reads and ends it with a stable loop.</summary>
    public static byte[] OpenBiosCompatibleExe(byte[] exeBytes)
    {
        var size = checked((int)BitConverter.ToUInt32(exeBytes, 0x1C));
        var padded = checked((size + 8 + 2047) / 2048 * 2048);
        var result = new byte[checked(0x800 + padded)];
        exeBytes.AsSpan(0, 0x800 + size).CopyTo(result);
        BitConverter.GetBytes((uint)padded).CopyTo(result, 0x1C);
        BitConverter.GetBytes(0x1000FFFFu).CopyTo(result, 0x800 + size);
        return result;
    }

    /// <summary>
    /// Wraps 2048-byte ISO sectors into a raw 2352-byte Mode 2 Form 1 disc image from file index 0 (MSF 00:00:00): 150
    /// lead-in sectors with zero user data, then ISO sector n at file index n + 150 (LBA n). Each sector's header MSF is
    /// its file index, the absolute frame count of a dump that starts at 00:00:00.
    /// </summary>
    public static byte[] Mode2Form1(byte[] iso)
    {
        ArgumentNullException.ThrowIfNull(iso);
        var sectors = iso.Length / Iso9660Reader.SectorSize;
        var leadIn = CdSectorAddress.LeadInFrames;
        var image = new byte[(leadIn + sectors) * ICdSectorSource.RawSectorSize];
        for (var fileIndex = 0; fileIndex < leadIn + sectors; fileIndex++)
        {
            var sector = image.AsSpan(fileIndex * ICdSectorSource.RawSectorSize, ICdSectorSource.RawSectorSize);
            sector.Slice(1, 10).Fill(0xFF);
            var absolute = fileIndex;
            sector[12] = Bcd(absolute / 4500);
            sector[13] = Bcd(absolute / 75 % 60);
            sector[14] = Bcd(absolute % 75);
            sector[15] = 2;
            sector[18] = sector[22] = 0x08; // submode: data
            if (fileIndex >= leadIn)
            {
                iso.AsSpan((fileIndex - leadIn) * Iso9660Reader.SectorSize, Iso9660Reader.SectorSize).CopyTo(sector[24..]);
            }
        }

        return image;
    }

    private static byte Bcd(int value) => (byte)((value / 10 << 4) | (value % 10));
}
