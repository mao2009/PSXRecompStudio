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
/// disc-exes &lt;prefix&gt;                                                                 # every PS-X EXE file on --disc
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

        foreach (var (line, fields) in Directives(path))
        {
            switch (fields[0])
            {
                case "image":
                    images.Add(ParseRomImage(fields, firmware, baseDirectory));
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
                case "disc-exes":
                    if (fields.Length != 2)
                    {
                        throw new InvalidDataException($"manifest: 'disc-exes' takes exactly one name prefix; got '{line}'.");
                    }
                    if (disc is null)
                    {
                        throw new InvalidDataException($"manifest: 'disc-exes {fields[1]}' needs a --disc image.");
                    }
                    iso ??= OpenIso(disc);
                    images.AddRange(DiscExecutables(iso, fields[1]));
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

    /// <summary>
    /// The file paths a manifest's directives reference, exactly as written (Issue #732 P1-3): <c>image</c>'s
    /// optional roots file and both the executable and optional roots file of <c>exe</c>. <c>boot-exe</c>,
    /// <c>disc-exes</c> and <c>interpret</c> reference no file of their own (the disc is hashed separately). Malformed or unknown
    /// directives are rejected exactly as <see cref="Read"/> rejects them. Packaging resolves, contains and hashes
    /// these; the run re-verifies them.
    /// </summary>
    public static IReadOnlyList<string> ReadReferencedPaths(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var referenced = new List<string>();
        foreach (var (line, fields) in Directives(path))
        {
            switch (fields[0])
            {
                case "image":
                    if (fields.Length is < 6 or > 7 || fields[3] != "rom")
                    {
                        throw new InvalidDataException("manifest: 'image' is 'image <name> <dest> rom <addr> <size> [roots-file]'.");
                    }
                    if (fields.Length == 7)
                    {
                        referenced.Add(fields[6]);
                    }
                    break;
                case "exe":
                    if (fields.Length is < 3 or > 4)
                    {
                        throw new InvalidDataException("manifest: 'exe' is 'exe <name> <path> [roots-file]'.");
                    }
                    referenced.Add(fields[2]);
                    if (fields.Length == 4)
                    {
                        referenced.Add(fields[3]);
                    }
                    break;
                case "boot-exe":
                    if (fields.Length != 2)
                    {
                        throw new InvalidDataException($"manifest: 'boot-exe' takes exactly one name; got '{line}'.");
                    }
                    break;
                case "disc-exes":
                    if (fields.Length != 2)
                    {
                        throw new InvalidDataException($"manifest: 'disc-exes' takes exactly one name prefix; got '{line}'.");
                    }
                    break;
                case "interpret":
                    if (fields.Length != 2)
                    {
                        throw new InvalidDataException($"manifest: 'interpret' takes exactly one address; got '{line}'.");
                    }
                    break;
                default:
                    throw new InvalidDataException($"manifest: unknown directive '{fields[0]}'.");
            }
        }

        return referenced;
    }

    /// <summary>Non-blank, comment-stripped directive lines with their whitespace-separated fields.</summary>
    private static IEnumerable<(string Line, string[] Fields)> Directives(string path)
    {
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            yield return (line, line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
    }

    private static LoadedImageSpec ParseRomImage(IReadOnlyList<string> fields, OpenBiosFirmware firmware, string? baseDirectory)
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
        var roots = fields.Count == 7 ? ReadRoots(fields[6], baseDirectory) : [loadAddress];

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

    /// <summary>The largest file a PS-X EXE can be: the 2 MiB of main RAM plus its 2 KiB header.</summary>
    private const int MaxExecutableFileBytes = 0x200000 + 0x800;

    /// <summary>
    /// Every PS-X EXE file on the disc (a file whose header carries the <c>PS-X EXE</c> magic), as one image each named
    /// <c>prefix:ISO/PATH</c>. A game's later stages are such files, staged into RAM by its boot program; overlays may share a
    /// load address, which the loaded-code guard resolves by comparing the code actually in RAM. Files too large to be an
    /// executable (movies, archives) are not read.
    ///
    /// The image name is the normalized ISO path, which keeps the names of collision-free images stable. Only when several
    /// files share a normalized path (for example <c>DIR;1/GAME.EXE;1</c> and <c>DIR;2/GAME.EXE;1</c>, or two file versions
    /// in one directory) does each keep its full versioned path, so those names stay unique. Distinct files are always read
    /// from their own directory records. Nothing is title-specific.
    /// </summary>
    private static IEnumerable<LoadedImageSpec> DiscExecutables(Iso9660Reader iso, string prefix)
    {
        var files = iso.EnumerateFiles();

        // Grouping is case-insensitive because ISO 9660 is: two entries that differ only by case would otherwise
        // both keep a normalized name that collides when compared case-insensitively.
        var ambiguousPaths = files
            .GroupBy(static file => file.NormalizedPath, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            if (file.Entry.Size is < 0x800 or > MaxExecutableFileBytes)
            {
                continue;
            }

            // A normalized path can refer to several file versions; read the exact extent.
            var bytes = iso.ReadEnumeratedFile(file.Entry);
            if (bytes.Length >= 8 && System.Text.Encoding.ASCII.GetString(bytes, 0, 8) == "PS-X EXE")
            {
                var imagePath = ambiguousPaths.Contains(file.NormalizedPath) ? file.VersionedPath : file.NormalizedPath;
                yield return FromExeBytes($"{prefix}:{imagePath}", bytes, []);
            }
        }
    }

    private static string NormalizeBootPath(Iso9660Reader iso) =>
        RomAnalysisPipeline.NormalizeBootPath(SystemCnfParser.Parse(iso.ReadFile("SYSTEM.CNF")).BootPath);

    /// <summary>Opens the ISO 9660 filesystem of a disc's first data track (Mode 1 or Mode 2 Form 1 user data).</summary>
    public static Iso9660Reader OpenIso(ICdSectorSource disc)
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

    /// <summary>
    /// Reads a roots file: one hexadecimal PC per line, with blank lines and <c>#</c> comments ignored, an
    /// optional <c>0x</c> prefix accepted and invalid lines rejected with <see cref="InvalidDataException"/>.
    /// Shared by the manifest's <c>image</c>/<c>exe</c> roots files and the firmware AOT plan's <c>rom.roots</c>,
    /// so the two readers accept exactly the same syntax (Issue #732 review: no <see cref="FormatException"/> may
    /// escape a malformed roots file).
    /// </summary>
    public static uint[] ReadRootsFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ParseRoots(File.ReadAllLines(path));
    }

    private static uint[] ReadRoots(string path, string? baseDirectory) =>
        ParseRoots(File.ReadAllLines(Resolve(path, baseDirectory)));

    private static uint[] ParseRoots(IEnumerable<string> lines) =>
        lines.Select(static line => line.Split('#')[0].Trim())
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
