using System.Text;
using PSXRecomp.Core.DiscImage;

namespace PSXRecomp.Tests.RealRomAnalysis;

/// <summary>
/// Builds minimal, in-memory ISO 9660 images (2048-byte user-data sectors) so the
/// post-filesystem stages of <see cref="RomAnalysisPipeline"/> can be exercised
/// without a copyrighted disc image.
///
/// Only what <see cref="Iso9660Reader"/> actually reads is emitted: a Primary Volume
/// Descriptor, a single-sector root directory, and file extents. Images are built in
/// memory and never committed.
/// </summary>
[Test]
public sealed class SyntheticIsoImageBuilder
{
    private const int SectorSize = Iso9660Reader.SectorSize;
    private const int VolumeDescriptorSector = 16;
    private const int RootDirectorySector = 18;
    private const int FirstFileSector = 20;
    private const int RootDirectoryRecordOffset = 156;

    private readonly List<(string Name, byte[] Content)> _files = [];
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
    /// Adds a file to the root directory. <paramref name="isoName"/> is the on-disc
    /// name including the ISO 9660 version suffix (for example <c>SYSTEM.CNF;1</c>).
    /// </summary>
    public SyntheticIsoImageBuilder AddFile(string isoName, byte[] content)
    {
        _files.Add((isoName, content));
        return this;
    }

    /// <summary>Adds a SYSTEM.CNF whose BOOT entry points at <paramref name="bootValue"/>.</summary>
    public SyntheticIsoImageBuilder AddSystemCnf(string bootValue) =>
        AddFile("SYSTEM.CNF;1", Encoding.ASCII.GetBytes($"BOOT = {bootValue}\r\nTCB = 4\r\nEVENT = 10\r\nSTACK = 801FFFF0\r\n"));

    /// <summary>Adds a SYSTEM.CNF with no BOOT entry, so parsing fails.</summary>
    public SyntheticIsoImageBuilder AddSystemCnfWithoutBoot() =>
        AddFile("SYSTEM.CNF;1", Encoding.ASCII.GetBytes("TCB = 4\r\nEVENT = 10\r\nSTACK = 801FFFF0\r\n"));

    public byte[] Build()
    {
        var locations = new uint[_files.Count];
        var cursor = FirstFileSector;
        for (int i = 0; i < _files.Count; i++)
        {
            locations[i] = (uint)cursor;
            var sectors = Math.Max(1, (_files[i].Content.Length + SectorSize - 1) / SectorSize);
            cursor += sectors;
        }

        var image = new byte[cursor * SectorSize];

        WriteVolumeDescriptor(image, (uint)cursor);
        WriteRootDirectory(image, locations);

        for (int i = 0; i < _files.Count; i++)
        {
            var content = _files[i].Content;
            Buffer.BlockCopy(content, 0, image, (int)locations[i] * SectorSize, content.Length);
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

        WriteDirectoryRecord(image, offset + RootDirectoryRecordOffset,
            RootDirectorySector, SectorSize, flags: 0x02, name: "\0");
    }

    private void WriteRootDirectory(byte[] image, uint[] locations)
    {
        var offset = RootDirectorySector * SectorSize;

        // "." — the directory's own record.
        offset += WriteDirectoryRecord(image, offset, RootDirectorySector, SectorSize, flags: 0x02, name: "\0");

        for (int i = 0; i < _files.Count; i++)
        {
            offset += WriteDirectoryRecord(image, offset, locations[i],
                (uint)_files[i].Content.Length, flags: 0x00, name: _files[i].Name);
        }
    }

    private static int WriteDirectoryRecord(byte[] buffer, int offset, uint location, uint size, byte flags, string name)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
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
