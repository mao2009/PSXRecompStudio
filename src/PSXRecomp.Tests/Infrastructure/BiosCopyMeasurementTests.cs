using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2: the measurement the copy rule (<see cref="BiosAddressGuard.MinRunWords"/>, <see cref="BiosAddressGuard.MinDistinctWords"/>,
/// <see cref="BiosAddressGuard.CopyThresholdBytes"/>, <see cref="BiosAddressGuard.LowVarietyThresholdBytes"/>) was chosen from. It is
/// skipped unless its inputs are given:
/// <list type="bullet">
/// <item><c>PSXRECOMP_OPENBIOS_ROM</c>: the BIOS, read in place.</item>
/// <item><c>PSXRECOMP_MEASURE_DIR</c>: a directory of non-BIOS binaries (the first 400 by name are measured). The label of its lines
/// is <c>PSXRECOMP_MEASURE_LABEL</c> (default <c>x86</c>).</item>
/// <item><c>PSXRECOMP_MEASURE_MIPS_DIR</c> (optional): a directory of non-BIOS MIPS binaries, measured the same way when given.</item>
/// </list>
/// It prints lines that start with <c>b2-measure</c>, and the docs record them. It measures (1) the coincidence of ordinary inputs,
/// singly (continuous) and in groups whose BIOS coverage is united (cumulative), for several (minimum run, minimum distinct) schemes;
/// (2) the detection of BIOS copies of several lengths, split into several pieces with non-BIOS words between them, for high-variety
/// and low-variety fragments.
/// </summary>
[Test]
public sealed class BiosCopyMeasurementTests
{
    private const int FileLimit = 400;
    private const int GroupsPerSize = 200;
    private const int Trials = 40;
    private const int HostWords = 8;

    /// <summary>(minimum run words, minimum distinct values): the low-variety schemes count matches of two or more values.</summary>
    private static readonly (int MinRun, int MinDistinct)[] Schemes = [(4, 2), (8, 2), (16, 2), (4, 4), (8, 4), (16, 4)];
    private static readonly int[] GroupSizes = [2, 4, 8, 16, 32];
    private static readonly int[] DetectionWords = [8, 16, 24, 31, 32, 64];
    private static readonly int[] DetectionPieces = [1, 2, 4];

    [SkippableFact]
    public void CopyRule_IsMeasured_OnNormalInputsAndOnInsertedBiosCopies()
    {
#pragma warning disable AARC003 // Test-layer measurement: the BIOS and the corpora are read from the environment
        var biosPath = Environment.GetEnvironmentVariable("PSXRECOMP_OPENBIOS_ROM");
        var dir = Environment.GetEnvironmentVariable("PSXRECOMP_MEASURE_DIR");
        var mipsDir = Environment.GetEnvironmentVariable("PSXRECOMP_MEASURE_MIPS_DIR");
        var label = Environment.GetEnvironmentVariable("PSXRECOMP_MEASURE_LABEL");
        Skip.If(string.IsNullOrWhiteSpace(biosPath) || string.IsNullOrWhiteSpace(dir),
            "measurement skipped: set PSXRECOMP_OPENBIOS_ROM and PSXRECOMP_MEASURE_DIR");

        var (_, _) = OpenBiosRomInput.Read(biosPath!);
        var bios = OpenBiosFirmware.FromBytes(File.ReadAllBytes(biosPath!));
        var x86 = Directory.EnumerateFiles(dir!, "*", SearchOption.TopDirectoryOnly)
            .Where(static f => Path.GetExtension(f) is ".exe" or ".dll")
            .OrderBy(static f => Path.GetFileName(f), StringComparer.Ordinal)
            .Take(FileLimit)
            .ToArray();
#pragma warning restore AARC003

        MeasureCorpus(string.IsNullOrWhiteSpace(label) ? "x86" : label, x86, bios, assertNoFalsePositive: true);
        Emit($"b2-measure production thresholds: high-variety T={BiosAddressGuard.CopyThresholdBytes} " +
             $"(minRun={BiosAddressGuard.MinRunWords}, minDistinct={BiosAddressGuard.MinDistinctWords}); " +
             $"low-variety T={BiosAddressGuard.LowVarietyThresholdBytes} (minRun={BiosAddressGuard.MinRunWords}, minDistinct={BiosAddressGuard.MinLowVarietyDistinctWords})");

        if (string.IsNullOrWhiteSpace(mipsDir))
        {
            Emit("b2-measure corpus=mips not measured: PSXRECOMP_MEASURE_MIPS_DIR is not set");
        }
        else
        {
#pragma warning disable AARC003 // Test-layer measurement of the MIPS corpus
            var mips = Directory.EnumerateFiles(mipsDir, "*", SearchOption.TopDirectoryOnly).OrderBy(static f => Path.GetFileName(f), StringComparer.Ordinal)
                .Take(FileLimit).ToArray();
#pragma warning restore AARC003
            MeasureCorpus("mips", mips, bios, assertNoFalsePositive: true);
        }

        MeasureDetection(bios, x86);
        x86.Should().NotBeEmpty("the x86 corpus must hold binaries");

        // The MIPS corpus of Issue #B2 (owner ruling): the PS-X executables of a disc (PSXRECOMP_MEASURE_CHD), extracted to a temporary
        // directory that is removed after the run. Nothing extracted is committed or printed; only the measured numbers are.
#pragma warning disable AARC003 // Test-layer measurement: the disc path is read from the environment
        var chdPath = Environment.GetEnvironmentVariable("PSXRECOMP_MEASURE_CHD");
#pragma warning restore AARC003
        if (!string.IsNullOrWhiteSpace(chdPath))
        {
            using var temp = new TempDirectory();
            var psx = ExtractPsxExecutables(chdPath!, temp.FullPath);
            Emit($"b2-measure corpus=disc-mips psx-executables={psx.Length} bytes={psx.Sum(static f => new FileInfo(f).Length)}");
            psx.Should().NotBeEmpty("the disc must hold PS-X executables");
            MeasureCorpus("disc-mips", psx, bios, assertNoFalsePositive: true);
            MeasureDetection(bios, psx);
            MeasureBoundaries(bios, psx);
        }
    }

    /// <summary>The threshold a scheme is judged against: high-variety schemes against the high threshold, the others the low one.</summary>
    private static int ThresholdFor(int minDistinct) =>
        minDistinct >= BiosAddressGuard.MinDistinctWords ? BiosAddressGuard.CopyThresholdBytes : BiosAddressGuard.LowVarietyThresholdBytes;

    /// <summary>
    /// Per scheme: the BIOS bytes each input covers, singly (continuous) and united over random groups (cumulative). The production
    /// schemes must not refuse an ordinary input or group; the assertion says so when <paramref name="assertNoFalsePositive"/> is set.
    /// </summary>
    private static void MeasureCorpus(string name, string[] files, OpenBiosFirmware bios, bool assertNoFalsePositive)
    {
        var perScheme = Schemes.ToDictionary(static s => s, _ => new List<HashSet<int>>());
        foreach (var file in files)
        {
#pragma warning disable AARC003 // Test-layer measurement of one corpus file
            var words = ToWords(File.ReadAllBytes(file));
#pragma warning restore AARC003
            if (assertNoFalsePositive)
            {
                // The provenance rule (Issue #B2, option 1) never refuses by content: an ordinary input must pass the manifest stage.
                var manifest = new LoadImageManifest([new LoadedImageSpec(Path.GetFileName(file), 0x80010000u, words, [], "exe-file") { SourceSha256 = "measurement-evidence" }], new HashSet<uint>());
                ((Action)(() => BiosAddressGuard.RequireNoBiosImage(manifest, bios))).Should().NotThrow(
                    $"the ordinary {name} input {Path.GetFileName(file)} must not be refused");
            }

            foreach (var scheme in Schemes)
            {
                perScheme[scheme].Add(new HashSet<int>(BiosAddressGuard.BiosCoveredWords([words], bios, scheme.MinRun, scheme.MinDistinct)));
            }
        }

        foreach (var scheme in Schemes)
        {
            var threshold = ThresholdFor(scheme.MinDistinct);
            var sets = perScheme[scheme];
            var bytes = sets.Select(static s => s.Count * 4).OrderBy(static b => b).ToArray();
            var overSingle = bytes.Count(b => b > threshold);
            var overSingleAt96 = bytes.Count(b => b > BiosAddressGuard.CopyThresholdBytes);
            Emit($"b2-measure singles corpus={name} minRun={scheme.MinRun} minDistinct={scheme.MinDistinct} T={threshold} files={bytes.Length} " +
                 $"p50={bytes[bytes.Length / 2]} p90={bytes[(int)(bytes.Length * 0.9)]} max={bytes[^1]} " +
                 $"anyMatch={bytes.Count(static b => b > 0)} overThreshold={overSingle} overAt96={overSingleAt96}");

            var overGroups = new List<int>();
            foreach (var size in GroupSizes.Where(k => k <= sets.Count))
            {
                var random = new Lcg(20261023UL + (ulong)size);
                var over = 0;
                var over96 = 0;
                var maxUnion = 0;
                for (var g = 0; g < GroupsPerSize; g++)
                {
                    var order = Enumerable.Range(0, sets.Count).ToArray();
                    for (var i = order.Length - 1; i > 0; i--)
                    {
                        var j = random.Next(i + 1);
                        (order[i], order[j]) = (order[j], order[i]);
                    }

                    var union = new HashSet<int>();
                    foreach (var index in order.Take(size))
                    {
                        union.UnionWith(sets[index]);
                    }

                    var covered = union.Count * 4;
                    maxUnion = Math.Max(maxUnion, covered);
                    if (covered > threshold)
                    {
                        over++;
                    }

                    if (covered > BiosAddressGuard.CopyThresholdBytes)
                    {
                        over96++;
                    }
                }

                overGroups.Add(over);
                Emit($"b2-measure groups corpus={name} minRun={scheme.MinRun} minDistinct={scheme.MinDistinct} T={threshold} k={size} " +
                     $"groups={GroupsPerSize} overThreshold={over} overAt96={over96} maxBytes={maxUnion}");
            }

        }
    }

    /// <summary>The PS-X executables of a disc image, written to <paramref name="directory"/>; the disc is read in place, never committed.</summary>
    private static string[] ExtractPsxExecutables(string chdPath, string directory)
    {
#pragma warning disable AARC003 // Test-layer measurement: the disc is read from the environment and the executables are written to a temporary directory
        using var chd = ChdReader.Open(chdPath);
        var iso = DiscImageAnalyzer.CreateIsoReader(chd);
        iso.Initialize();
        var written = new List<string>();
        foreach (var file in iso.EnumerateFiles())
        {
            var bytes = iso.ReadFile(file.Entry);
            if (bytes.Length >= 8 && Encoding.ASCII.GetString(bytes, 0, 8) == "PS-X EXE")
            {
                var path = Path.Combine(directory, $"psx{written.Count:D3}.exe");
                File.WriteAllBytes(path, bytes);
                written.Add(path);
            }
        }

        return written.ToArray();
#pragma warning restore AARC003
    }

    /// <summary>
    /// The byte-size boundaries of Issue #B2 (1, 7, 9, 32, 96, 127, 256): a BIOS copy of that many bytes, of three kinds, inserted
    /// into a non-BIOS host. Whole words are counted; a copy shorter than a match (8 words) cannot be refused by content.
    /// The rule refuses a copy when its high-variety coverage is over 96 bytes or its all-match coverage is over 256 bytes.
    /// </summary>
    private static void MeasureBoundaries(OpenBiosFirmware bios, string[] hostFiles)
    {
        var hosts = LoadHosts(hostFiles);
        foreach (var bytes in new[] { 1, 7, 9, 32, 96, 127, 256 })
        {
            var words = bytes / 4;
            if (words == 0)
            {
                Emit($"b2-measure boundary bytes={bytes} words=0 (no whole word: cannot be a match)");
                continue;
            }

            var lowOffsets = LowVarietyOffsets(bios.Words, words);
            var uniformOffsets = UniformOffsets(bios.Words, words);
            var kinds = new (string Kind, Func<Lcg, int> Offset, int Candidates)[]
            {
                ("high", r => CodeOffset(bios, words, r), -1),
                ("low", r => lowOffsets[r.Next(lowOffsets.Length)], lowOffsets.Length),
                ("uniform", r => uniformOffsets[r.Next(uniformOffsets.Length)], uniformOffsets.Length),
            };
            foreach (var (kind, offset, candidates) in kinds)
            {
                if (candidates == 0)
                {
                    Emit($"b2-measure boundary bytes={bytes} words={words} variety={kind} candidates=0 (the BIOS holds no such fragment)");
                    continue;
                }

                var random = new Lcg(20261026UL + (ulong)(bytes * 7));
                var refused = 0;
                var minCovered = int.MaxValue;
                for (var t = 0; t < Trials; t++)
                {
                    var start = offset(random);
                    var host = hosts[random.Next(hosts.Length)];
                    var hostStart = random.Next(host.Length - HostWords);
                    var image = bios.Words.Skip(start).Take(words).Concat(host.Skip(hostStart).Take(HostWords)).ToArray();
                    var high = BiosAddressGuard.BiosCoveredWords([image], bios).Count * 4;
                    var all = BiosAddressGuard.BiosCoveredWords([image], bios, BiosAddressGuard.MinRunWords, BiosAddressGuard.MinLowVarietyDistinctWords).Count * 4;
                    if (high > BiosAddressGuard.CopyThresholdBytes || all > BiosAddressGuard.LowVarietyThresholdBytes)
                    {
                        refused++;
                    }

                    minCovered = Math.Min(minCovered, Math.Max(high, all));
                }

                Emit($"b2-measure boundary bytes={bytes} words={words} variety={kind} candidates={candidates} refused={refused}/{Trials} minCoveredBytes={minCovered}");
            }
        }
    }

    /// <summary>The BIOS offsets of windows of <paramref name="count"/> words that are one repeated word (a uniform run).</summary>
    private static int[] UniformOffsets(IReadOnlyList<uint> bios, int count)
    {
        var offsets = new List<int>();
        for (var start = 0; start + count <= bios.Count; start++)
        {
            var uniform = true;
            for (var k = 1; k < count && uniform; k++)
            {
                uniform = bios[start + k] == bios[start];
            }

            if (uniform)
            {
                offsets.Add(start);
            }
        }

        return offsets.ToArray();
    }

    private static uint[][] LoadHosts(string[] hostFiles) => hostFiles.Take(8).Select(static f =>
    {
#pragma warning disable AARC003 // Test-layer measurement: the separators are real words of a corpus binary
        var words = ToWords(File.ReadAllBytes(f));
#pragma warning restore AARC003
        return words.Take(16384).ToArray();
    }).Where(static h => h.Length > HostWords * 2).ToArray();

    /// <summary>
    /// BIOS copies of <see cref="DetectionWords"/> words, split into <see cref="DetectionPieces"/> pieces, each piece separated by
    /// non-BIOS words of a corpus binary. The BIOS offsets are seeded and drawn from code (padding is excluded by design). Low-variety
    /// fragments (two or three distinct values, no uniform window) are measured separately, where the BIOS holds any.
    /// </summary>
    private static void MeasureDetection(OpenBiosFirmware bios, string[] hostFiles)
    {
        var hosts = hostFiles.Take(8).Select(static f =>
        {
#pragma warning disable AARC003 // Test-layer measurement: the separators are real words of a corpus binary
            var words = ToWords(File.ReadAllBytes(f));
#pragma warning restore AARC003
            return words.Take(16384).ToArray();
        }).Where(static h => h.Length > HostWords * 2).ToArray();
        var schemes = new[] { (BiosAddressGuard.MinRunWords, BiosAddressGuard.MinDistinctWords), (4, 4), (8, 2) };

        foreach (var words in DetectionWords)
        {
            var lowOffsets = LowVarietyOffsets(bios.Words, words);
            foreach (var pieces in DetectionPieces)
            {
                foreach (var scheme in schemes)
                {
                    var random = new Lcg(20261024UL + (ulong)(words * 10 + pieces));
                    var refused = RunDetection(bios, hosts, random, words, pieces, scheme, () => CodeOffset(bios, words, random), out var minCovered);
                    Emit($"b2-measure detect copyBytes={words * 4} pieces={pieces} minRun={scheme.Item1} minDistinct={scheme.Item2} " +
                         $"T={ThresholdFor(scheme.Item2)} refused={refused}/{Trials} minCoveredBytes={minCovered}");
                }

                if (lowOffsets.Length == 0)
                {
                    Emit($"b2-measure detect-low copyBytes={words * 4} pieces={pieces} candidates=0 (the BIOS holds no such fragment)");
                    continue;
                }

                foreach (var scheme in schemes.Where(static s => s.Item2 <= 2))
                {
                    var random = new Lcg(20261025UL + (ulong)(words * 10 + pieces));
                    var refused = RunDetection(bios, hosts, random, words, pieces, scheme, () => lowOffsets[random.Next(lowOffsets.Length)], out var minCovered);
                    Emit($"b2-measure detect-low copyBytes={words * 4} pieces={pieces} minRun={scheme.Item1} minDistinct={scheme.Item2} " +
                         $"T={ThresholdFor(scheme.Item2)} candidates={lowOffsets.Length} refused={refused}/{Trials} minCoveredBytes={minCovered}");
                }
            }
        }
    }

    private static int RunDetection(
        OpenBiosFirmware bios,
        uint[][] hosts,
        Lcg random,
        int words,
        int pieces,
        (int Item1, int Item2) scheme,
        Func<int> nextOffset,
        out int minCovered)
    {
        var refused = 0;
        minCovered = int.MaxValue;
        for (var t = 0; t < Trials; t++)
        {
            var offset = nextOffset();
            var host = hosts[random.Next(hosts.Length)];
            var start = random.Next(host.Length - HostWords);
            var image = new List<uint>();
            var position = 0;
            for (var piece = 0; piece < pieces; piece++)
            {
                var length = words / pieces + (piece < words % pieces ? 1 : 0);
                image.AddRange(bios.Words.Skip(offset + position).Take(length));
                position += length;
                if (piece < pieces - 1)
                {
                    image.AddRange(host.Skip(start).Take(HostWords));
                }
            }

            var covered = BiosAddressGuard.BiosCoveredWords([image.ToArray()], bios, scheme.Item1, scheme.Item2).Count * 4;
            if (covered > ThresholdFor(scheme.Item2))
            {
                refused++;
            }

            minCovered = Math.Min(minCovered, covered);
        }

        return refused;
    }

    /// <summary>A seeded BIOS offset for a code fragment: padding (a uniform window) is redrawn, because it is never evidence by design.</summary>
    private static int CodeOffset(OpenBiosFirmware bios, int count, Lcg random)
    {
        var offset = random.Next(bios.Words.Count - count);
        while (HasUniformWindow(bios.Words, offset, count))
        {
            offset = random.Next(bios.Words.Count - count);
        }

        return offset;
    }

    /// <summary>
    /// The BIOS offsets of windows of <paramref name="count"/> words that hold two or three distinct values and no uniform 4-word window:
    /// the low-variety fragments the rule counts at its low threshold.
    /// </summary>
    private static int[] LowVarietyOffsets(IReadOnlyList<uint> bios, int count)
    {
        var offsets = new List<int>();
        for (var start = 0; start + count <= bios.Count; start++)
        {
            var distinct = new HashSet<uint>();
            for (var k = 0; k < count; k++)
            {
                distinct.Add(bios[start + k]);
            }

            if (distinct.Count is >= 2 and <= 3 && !HasUniformWindow(bios, start, count))
            {
                offsets.Add(start);
            }
        }

        return offsets.ToArray();
    }

    /// <summary>True when the BIOS fragment holds a uniform 4-word window (padding). Padding is never evidence by design.</summary>
    private static bool HasUniformWindow(IReadOnlyList<uint> bios, int start, int count)
    {
        for (var k = start; k + BiosAddressGuard.KeyWords <= start + count; k++)
        {
            if (bios[k] == bios[k + 1] && bios[k] == bios[k + 2] && bios[k] == bios[k + 3])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A seeded linear congruential generator: the sampling is reproducible from its seed.</summary>
    private sealed class Lcg(ulong seed)
    {
        private ulong _state = seed;

        public int Next(int exclusiveMax)
        {
            _state = unchecked(_state * 6364136223846793005UL + 1442695040888963407UL);
            return (int)((_state >> 33) % (ulong)exclusiveMax);
        }
    }

    /// <summary>The measurement's output: one line per result, starting with <c>b2-measure</c>.</summary>
    private static void Emit(string line)
    {
#pragma warning disable AARC003 // Test-layer measurement: its output is the record of the measurement
        Console.WriteLine(line);
#pragma warning restore AARC003
    }

    private static uint[] ToWords(byte[] bytes)
    {
        var words = new uint[bytes.Length / 4];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4));
        }

        return words;
    }
}
