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
    private const int RootDirectoryRecordOffset = 156;
    private const int PathTableSize = 10; // 8-byte header + 1-byte name + 1 pad byte

    // Single-byte on-disc names for the implicit "." (0x00) and ".." (0x01) records.
    private static readonly byte[] SelfName = [0];
    private static readonly byte[] ParentName = [1];

    private readonly record struct Child(string Name, bool IsDirectory, byte[]? Content, string Path);

    private readonly List<(string Path, byte[] Content)> _files = [];
    private readonly Dictionary<string, (uint Location, uint Size)> _corruptDirectories = new(StringComparer.Ordinal);
    private (uint Location, uint Size)? _corruptRoot;
    private string _volumeIdentifier = "PSXRECOMP_TEST";
    private bool _emitPrimaryVolumeDescriptor = true;

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
        var cursor = (uint)FirstFileSector;
        foreach (var directory in directories)
        {
            directorySector[directory] = cursor++;
        }
        foreach (var (path, content) in _files)
        {
            fileSector[path] = cursor;
            cursor += (uint)Math.Max(1, (content.Length + SectorSize - 1) / SectorSize);
        }

        var image = new byte[cursor * SectorSize];

        WriteVolumeDescriptor(image, cursor);

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

    private void WriteVolumeDescriptor(byte[] image, uint volumeSpaceSize)
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
        BitConverter.GetBytes((uint)PathTableSize).CopyTo(image, offset + 132);
        BitConverter.GetBytes((uint)PathTableSector).CopyTo(image, offset + 140);
        var terminator = TerminatorSector * SectorSize;
        image[terminator] = 255;
        Encoding.ASCII.GetBytes("CD001").CopyTo(image, terminator + 1);
        image[terminator + 6] = 1;

        // L path table: one entry, the root directory (name length 1, LBA, parent 1, name 0x00).
        var table = PathTableSector * SectorSize;
        image[table] = 1;
        BitConverter.GetBytes((uint)RootDirectorySector).CopyTo(image, table + 2);
        image[table + 6] = 1;

        var (rootLocation, rootSize) = _corruptRoot ?? ((uint)RootDirectorySector, (uint)SectorSize);
        WriteDirectoryRecord(image, offset + RootDirectoryRecordOffset, rootLocation, rootSize, flags: 0x02, SelfName);
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

        foreach (var child in children)
        {
            offset += WriteChildRecord(image, offset, child, directorySector, fileSector);
        }
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
            return WriteDirectoryRecord(image, offset, location, size, flags: 0x02, Encoding.ASCII.GetBytes(child.Name));
        }

        return WriteDirectoryRecord(image, offset, fileSector[child.Path],
            (uint)child.Content!.Length, flags: 0x00, Encoding.ASCII.GetBytes(child.Name));
    }

    private static int WriteDirectoryRecord(byte[] buffer, int offset, uint location, uint size, byte flags, byte[] nameBytes)
    {
        var recordLength = 33 + nameBytes.Length;
        if (recordLength % 2 != 0)
        {
            recordLength++;
        }

        buffer[offset] = (byte)recordLength;
        buffer[offset + 1] = 0; // extended attribute length
        BitConverter.GetBytes(location).CopyTo(buffer, offset + 2);
        BitConverter.GetBytes(size).CopyTo(buffer, offset + 10);
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

    /// <summary>A bootable Mode 2 Form 1 disc whose SYSTEM.CNF runs <see cref="ExeIsoName"/>.</summary>
    public static byte[] Bootable(byte[] exeBytes) => Mode2Form1(
        new SyntheticIsoImageBuilder().AddSystemCnf(BootPath).AddFile(ExeIsoName, exeBytes).Build());

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

    /// <summary>Wraps 2048-byte ISO sectors into raw 2352-byte Mode 2 Form 1 sectors.</summary>
    public static byte[] Mode2Form1(byte[] iso)
    {
        ArgumentNullException.ThrowIfNull(iso);
        var sectors = iso.Length / Iso9660Reader.SectorSize;
        var image = new byte[sectors * ICdSectorSource.RawSectorSize];
        for (var lba = 0; lba < sectors; lba++)
        {
            var sector = image.AsSpan(lba * ICdSectorSource.RawSectorSize, ICdSectorSource.RawSectorSize);
            sector.Slice(1, 10).Fill(0xFF);
            var absolute = lba + 150;
            sector[12] = Bcd(absolute / 4500);
            sector[13] = Bcd(absolute / 75 % 60);
            sector[14] = Bcd(absolute % 75);
            sector[15] = 2;
            sector[18] = sector[22] = 0x08; // submode: data
            iso.AsSpan(lba * Iso9660Reader.SectorSize, Iso9660Reader.SectorSize).CopyTo(sector[24..]);
        }

        return image;
    }

    private static byte Bcd(int value) => (byte)((value / 10 << 4) | (value % 10));
}
