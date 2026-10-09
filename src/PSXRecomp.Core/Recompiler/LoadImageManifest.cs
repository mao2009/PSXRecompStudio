using System.Globalization;
using PSXRecomp.Architecture;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Runtime.CdRom;

namespace PSXRecomp.Core.Recompiler;

/// <summary>
/// One explicit code image a <see cref="LoadImageManifest"/> compiles ahead of time (Issue #732): the code the guest
/// itself places at <see cref="LoadAddress"/> at run time, given here as bytes at that destination. <see cref="Words"/>
/// are the image words in guest order; <see cref="Roots"/> are the entry PCs inside it (an ELF symbol set, an EXE entry,
/// or the load address itself). Nothing here is derived at run time.
/// </summary>
[Domain]
public sealed record LoadedImageSpec(string Name, uint LoadAddress, uint[] Words, IReadOnlyList<uint> Roots);

/// <summary>
/// The explicit list of RAM-placed code images the AOT build compiles, plus the observation points that must stay
/// interpreted (Issue #732). A manifest is a small text file; paths are relative to it:
/// <code>
/// image    &lt;name&gt; &lt;dest-hex&gt; rom &lt;rom-addr-hex&gt; &lt;size-hex&gt; [roots-file]   # ROM bytes copied into RAM
/// exe      &lt;name&gt; &lt;exe-path&gt; [roots-file]                                        # a PS-X EXE on disk
/// boot-exe &lt;name&gt;                                                                    # the SYSTEM.CNF boot EXE on --disc
/// interpret &lt;addr-hex&gt;                                                                # an entry that stays interpreted
/// </code>
/// Blank lines, leading/trailing whitespace and <c>#</c> comments are ignored; numbers are hexadecimal (an optional
/// <c>0x</c> prefix is accepted). The image compile itself is <see cref="ReachableProgramBuilder.BuildLoadedImage"/>.
/// </summary>
[Domain]
public sealed record LoadImageManifest(IReadOnlyList<LoadedImageSpec> Images, IReadOnlySet<uint> Interpreted)
{
    /// <summary>No RAM-placed code: the build compiles the ROM only.</summary>
    public static LoadImageManifest None { get; } = new([], new HashSet<uint>());

    /// <summary>
    /// Reads and validates a manifest. <paramref name="firmware"/> supplies the ROM bytes a <c>rom</c> image copies;
    /// <paramref name="disc"/> supplies the SYSTEM.CNF boot executable a <c>boot-exe</c> image needs.
    /// </summary>
    public static LoadImageManifest Read(string path, OpenBiosFirmware firmware, ICdSectorSource? disc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(firmware);

        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
        var images = new List<LoadedImageSpec>();
        var interpreted = new HashSet<uint>();
        Iso9660Reader? iso = null;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (fields[0])
            {
                case "image":
                    images.Add(ParseRomImage(fields, firmware));
                    break;
                case "exe":
                    images.Add(ParseExeImage(fields, baseDirectory));
                    break;
                case "boot-exe":
                    if (fields.Length != 2)
                    {
                        throw new InvalidDataException($"manifest: 'boot-exe' takes exactly one name; got '{line}'.");
                    }
                    if (disc is null)
                    {
                        throw new InvalidDataException($"manifest: 'boot-exe {fields[1]}' needs a --disc image.");
                    }
                    iso ??= OpenIso(disc);
                    images.Add(FromExeBytes(fields[1], iso.ReadFile(NormalizeBootPath(iso)), []));
                    break;
                case "interpret":
                    if (fields.Length != 2)
                    {
                        throw new InvalidDataException($"manifest: 'interpret' takes exactly one address; got '{line}'.");
                    }
                    interpreted.Add(ParseUint(fields[1]));
                    break;
                default:
                    throw new InvalidDataException($"manifest: unknown directive '{fields[0]}'.");
            }
        }

        return new LoadImageManifest(images, interpreted);
    }

    private static LoadedImageSpec ParseRomImage(IReadOnlyList<string> fields, OpenBiosFirmware firmware)
    {
        // image <name> <dest> rom <rom-addr> <size> [roots-file]
        if (fields.Count is < 6 or > 7 || fields[3] != "rom")
        {
            throw new InvalidDataException("manifest: 'image' is 'image <name> <dest> rom <addr> <size> [roots-file]'.");
        }

        var name = fields[1];
        var loadAddress = ParseUint(fields[2]);
        var romAddress = ParseUint(fields[4]);
        var size = ParseUint(fields[5]);
        var roots = fields.Count == 7 ? ReadRoots(fields[6], null) : [loadAddress];

        if ((size & 3u) != 0 || (loadAddress & 3u) != 0 || (romAddress & 3u) != 0)
        {
            throw new InvalidDataException($"manifest: image '{name}' size and addresses must be 4-byte aligned.");
        }

        var offset = (long)romAddress - OpenBiosFirmware.ResetVector;
        if (romAddress < OpenBiosFirmware.ResetVector || (ulong)offset + size > (ulong)firmware.Words.Count * 4)
        {
            throw new InvalidDataException($"manifest: image '{name}' ROM range 0x{romAddress:X8}+0x{size:X} is outside the firmware.");
        }

        var words = new uint[size / 4];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = firmware.Words[(int)(offset / 4) + i];
        }

        return new LoadedImageSpec(name, loadAddress, words, roots);
    }

    private static LoadedImageSpec ParseExeImage(IReadOnlyList<string> fields, string? baseDirectory)
    {
        // exe <name> <path> [roots-file]
        if (fields.Count is < 3 or > 4)
        {
            throw new InvalidDataException("manifest: 'exe' is 'exe <name> <path> [roots-file]'.");
        }

        var roots = fields.Count == 4 ? ReadRoots(fields[3], baseDirectory) : [];
        return FromExeBytes(fields[1], File.ReadAllBytes(Resolve(fields[2], baseDirectory)), roots);
    }

    private static LoadedImageSpec FromExeBytes(string name, byte[] bytes, IReadOnlyList<uint> roots)
    {
        var exe = PsxExe.Load(bytes, name);
        var words = new uint[exe.TextSegment.Length / 4];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = BitConverter.ToUInt32(exe.TextSegment, i * 4);
        }

        var entries = roots.Append(exe.Header.EntryPoint).Distinct().Order().ToArray();
        return new LoadedImageSpec(name, exe.Header.TextStart, words, entries);
    }

    private static string NormalizeBootPath(Iso9660Reader iso) =>
        RomAnalysisPipeline.NormalizeBootPath(SystemCnfParser.Parse(iso.ReadFile("SYSTEM.CNF")).BootPath);

    private static Iso9660Reader OpenIso(ICdSectorSource disc)
    {
        var reader = new Iso9660Reader(lba =>
        {
            var raw = new byte[ICdSectorSource.RawSectorSize];
            if (!disc.TryReadSector(lba, raw))
            {
                throw new FileNotFoundException($"the disc holds no sector {lba}.");
            }

            var userOffset = raw[15] switch
            {
                1 => 16,
                2 => 24,
                var mode => throw new InvalidDataException($"unsupported CD sector mode {mode} at sector {lba}."),
            };
            var isoData = new byte[Iso9660Reader.SectorSize];
            Buffer.BlockCopy(raw, userOffset, isoData, 0, Iso9660Reader.SectorSize);
            return isoData;
        });
        reader.Initialize();
        return reader;
    }

    private static string Resolve(string path, string? baseDirectory) =>
        Path.IsPathRooted(path) || baseDirectory is null ? path : Path.Combine(baseDirectory, path);

    private static uint[] ReadRoots(string path, string? baseDirectory) =>
        File.ReadAllLines(Resolve(path, baseDirectory))
            .Select(static line => line.Split('#')[0].Trim())
            .Where(static line => line.Length != 0)
            .Select(ParseUint)
            .Distinct()
            .Order()
            .ToArray();

    private static uint ParseUint(string value)
    {
        var text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidDataException($"manifest: '{value}' is not a hexadecimal number.");
        }

        return parsed;
    }
}
