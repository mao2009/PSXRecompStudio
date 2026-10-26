using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// The wiring of the generation-stage guards. Production never disables a stage. A mutation test disables one stage in its own
/// async flow (<see cref="DisableStages"/>) to prove that the stage is load-bearing (Issue #B2, MEDIUM-2).
/// </summary>
[Infrastructure]
public static class BiosGuardWiring
{
    /// <summary>The stage names: <c>manifest</c>, <c>plan</c>, <c>dispatch</c>, <c>source</c>.</summary>
    public const string Manifest = "manifest";

    /// <summary>The plan stage (<see cref="BiosAddressGuard.RequireNoBiosCode"/> on the firmware plan).</summary>
    public const string Plan = "plan";

    /// <summary>The dispatch stage (the program and loaded blocks the artifact is generated from).</summary>
    public const string Dispatch = "dispatch";

    /// <summary>The generated-source stage (the generated C).</summary>
    public const string Source = "source";

    private static readonly AsyncLocal<ImmutableHashSet<string>> Disabled = new();

    /// <summary>Runs <paramref name="check"/> unless <paramref name="stage"/> is disabled in the current async flow.</summary>
    public static void Run(string stage, Action check)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (!(Disabled.Value?.Contains(stage) ?? false))
        {
            check();
        }
    }

    /// <summary>Disables the named stages in the current async flow until the returned scope is disposed. Tests only.</summary>
    public static IDisposable DisableStages(params string[] stages)
    {
        var previous = Disabled.Value;
        Disabled.Value = (previous ?? ImmutableHashSet<string>.Empty).Union(stages);
        return new Restore(previous);
    }

    private sealed class Restore(ImmutableHashSet<string>? previous) : IDisposable
    {
        public void Dispose() => Disabled.Value = previous;
    }
}

/// <summary>
/// The BIOS is never an input of ahead-of-time code generation (owner ruling, Issue #B2, option 3). This guard is the
/// generation-stage enforcement: it refuses any program, loaded image, block or generated source that would translate
/// BIOS code, and it is called at each stage the code passes through (manifest, plan, dispatch, generated C).
/// </summary>
/// <remarks>
/// The BIOS occupies 512 KiB at physical <c>0x1FC00000</c>. Its CPU aliases (KUSEG <c>0x1FC00000</c>, KSEG0 <c>0x9FC00000</c>,
/// KSEG1 <c>0xBFC00000</c>) all reduce to that physical range once the segment bits are masked off, so the mask is applied
/// before the range test. Masking is conservative: an unmapped alias such as <c>0x3FC00000</c> is treated as the BIOS too.
/// </remarks>
[Infrastructure]
public static class BiosAddressGuard
{
    /// <summary>The physical base of the BIOS ROM.</summary>
    public const uint PhysicalBase = 0x1FC00000u;

    /// <summary>One past the last byte of the BIOS ROM (512 KiB).</summary>
    public const uint PhysicalEnd = PhysicalBase + (uint)OpenBiosFirmware.ImageSize;

    private const uint PhysicalMask = 0x1FFFFFFFu;

    private static readonly Regex BlockName = new(@"recompiler_block_0x([0-9A-Fa-f]{8})", RegexOptions.CultureInvariant);

    /// <summary>True when a guest address, through any alias, lies in the BIOS ROM.</summary>
    public static bool IsBiosAddress(uint address)
    {
        var physical = address & PhysicalMask;
        return physical >= PhysicalBase && physical < PhysicalEnd;
    }

    /// <summary>True when the guest range <c>[start, start + length)</c> touches the BIOS ROM through any alias.</summary>
    public static bool OverlapsBios(uint start, ulong length)
    {
        if (length == 0)
        {
            return false;
        }

        var physical = (ulong)(start & PhysicalMask);
        return physical < PhysicalEnd && physical + length > PhysicalBase;
    }

    /// <summary>The image kinds whose bytes come from the disc or from a named PS-X EXE file (the source is recorded per image).</summary>
    public static IReadOnlySet<string> AllowedAotSourceKinds { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "boot-exe", "disc-exe", "exe-file" };

    /// <summary>
    /// The manifest stage (Issue #B2, MEDIUM-3): an AOT input must come from an allowed source kind, a <c>rom</c> image (BIOS bytes
    /// copied to RAM to be translated) is refused, an image overlapping the BIOS range is refused, and the AOT inputs of one
    /// packaging together may not hold more BIOS code than <see cref="CopyThresholdBytes"/> (see <see cref="BiosCoveredWords"/>).
    /// </summary>
    public static void RequireNoBiosImage(LoadImageManifest manifest, OpenBiosFirmware firmware)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(firmware);
        foreach (var image in manifest.Images)
        {
            if (image.Kind == "rom-image")
            {
                throw new InvalidDataException(
                    $"image '{image.Name}' copies BIOS bytes into RAM to be translated; BIOS-derived code is never ahead-of-time translated (Issue #B2). " +
                    "Such code runs in the interpreter fallback instead.");
            }

            if (!AllowedAotSourceKinds.Contains(image.Kind))
            {
                throw new InvalidDataException(
                    $"image '{image.Name}' has the source kind '{image.Kind}', which is not an allowed AOT source (Issue #B2).");
            }

            if (OverlapsBios(image.LoadAddress, (ulong)image.Words.Length * 4))
            {
                throw new InvalidDataException($"image '{image.Name}' loads into the BIOS range; no code there is translated (Issue #B2).");
            }

        }

        // Cumulative (Issue #B2, owner ruling): the BIOS coverage of all AOT inputs together, each BIOS word counted once. A copy of
        // high-variety BIOS code is refused at CopyThresholdBytes; a copy of low-variety code (two or three distinct values, such as
        // mostly 0x00 and 0xFF) is refused at LowVarietyThresholdBytes, so it is not a hole in the rule.
        var inputs = manifest.Images.Select(static image => image.Words).ToArray();
        var (varied, any) = CoveredByVariety(inputs, firmware);
        var variedBytes = varied.Count * 4;
        if (variedBytes > CopyThresholdBytes)
        {
            throw new InvalidDataException(
                $"the AOT inputs together hold {variedBytes} bytes of BIOS code (threshold {CopyThresholdBytes} bytes); " +
                "BIOS-derived code is never translated (Issue #B2).");
        }

        var anyBytes = any.Count * 4;
        if (anyBytes > LowVarietyThresholdBytes)
        {
            throw new InvalidDataException(
                $"the AOT inputs together hold {anyBytes} bytes of low-variety BIOS code (threshold {LowVarietyThresholdBytes} bytes); " +
                "BIOS-derived code is never translated (Issue #B2).");
        }
    }

    /// <summary>The words a match is indexed by: a match is found by its first <see cref="KeyWords"/> words.</summary>
    public const int KeyWords = 4;

    /// <summary>
    /// The shortest match (in words) that counts as a copy (Issue #B2). A match of 8 words is 32 bytes: a copy of at least that
    /// length is evidence; a shorter match is a coincidence (measured: <c>docs/runtime/openbios-firmware-backend.md</c>).
    /// </summary>
    public const int MinRunWords = 8;

    /// <summary>
    /// The fewest distinct word values a counted match must hold (Issue #B2). A match of low-entropy words (padding, repeated
    /// small constants) is not evidence of a copy: without this filter, runs of 8 and 16 words coincide with the BIOS in
    /// ordinary binaries (measured in the docs). Real BIOS code holds at least this many distinct words in 8 words.
    /// </summary>
    public const int MinDistinctWords = 4;

    /// <summary>
    /// The fewest distinct values of a low-variety match (Issue #B2). A match of two or three values is still a match: it is
    /// counted against <see cref="LowVarietyThresholdBytes"/>, so that a BIOS fragment of mostly 0x00 and 0xFF is not a hole.
    /// </summary>
    public const int MinLowVarietyDistinctWords = 2;

    /// <summary>
    /// The cumulative threshold for all matches (high and low variety), in bytes (Issue #B2). It is set from the measured
    /// coincidence of ordinary inputs at this filter (docs), and is not below <see cref="CopyThresholdBytes"/>.
    /// </summary>
    public const int LowVarietyThresholdBytes = 256;

    /// <summary>
    /// The cumulative threshold: the BIOS code the AOT inputs hold, all together, may not exceed this many bytes (Issue #B2).
    /// 96 bytes is 24 words, three matches of the minimum length: one coincidental match cannot refuse a packaging, and a copy of
    /// 25 words or more (100 bytes), in matches of at least <see cref="MinRunWords"/> words, is refused. The measured coincidence
    /// of ordinary binaries is zero at this filter (docs); the x86 corpus is the only measured corpus (no MIPS corpus was available).
    /// </summary>
    public const int CopyThresholdBytes = 96;

    /// <summary>
    /// The BIOS word offsets covered by matches of at least <paramref name="minRunWords"/> words, holding at least
    /// <paramref name="minDistinctWords"/> distinct values, that the candidate images hold. A BIOS word covered by several matches,
    /// in one image or in several, is counted once: the measure is a union, not a sum. A run of one repeated word is never a match
    /// (its 4-word windows are never indexed): such a copy carries no identity that could show its origin.
    /// Defaults are the production values; the measurement varies them to reproduce its table.
    /// </summary>
    public static IReadOnlySet<int> BiosCoveredWords(
        IEnumerable<IReadOnlyList<uint>> images,
        OpenBiosFirmware firmware,
        int minRunWords = MinRunWords,
        int minDistinctWords = MinDistinctWords) =>
        Scan(images, firmware, minRunWords, [minDistinctWords])[0];

    /// <summary>
    /// The BIOS word offsets covered by high-variety matches (at least <see cref="MinDistinctWords"/> distinct values) and by all
    /// matches (at least <see cref="MinLowVarietyDistinctWords"/> distinct values), in one pass over the inputs.
    /// </summary>
    internal static (HashSet<int> Varied, HashSet<int> Any) CoveredByVariety(IEnumerable<IReadOnlyList<uint>> images, OpenBiosFirmware firmware)
    {
        var sets = Scan(images, firmware, MinRunWords, [MinDistinctWords, MinLowVarietyDistinctWords]);
        return (sets[0], sets[1]);
    }

    /// <summary>
    /// One pass that finds each match once. A match on a diagonal (BIOS offset minus image offset) that already covers the current
    /// image offset is skipped: it is the same maximal run, and a suffix of a run holds no more distinct values than the run.
    /// </summary>
    private static HashSet<int>[] Scan(IEnumerable<IReadOnlyList<uint>> images, OpenBiosFirmware firmware, int minRunWords, int[] minDistinct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minRunWords, KeyWords);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(firmware);
        var bios = firmware.Words;
        var index = new Dictionary<(uint, uint, uint, uint), List<int>>();
        for (var j = 0; j + KeyWords <= bios.Count; j++)
        {
            if (!IsUniformKey(bios, j))
            {
                var key = Key(bios, j);
                if (!index.TryGetValue(key, out var starts))
                {
                    index[key] = starts = [];
                }

                starts.Add(j);
            }
        }

        var covered = minDistinct.Select(static _ => new HashSet<int>()).ToArray();
        var diagonalEnd = new Dictionary<long, int>();
        foreach (var image in images)
        {
            diagonalEnd.Clear();
            for (var i = 0; i + KeyWords <= image.Count; i++)
            {
                if (IsUniformKey(image, i) || !index.TryGetValue(Key(image, i), out var starts))
                {
                    continue;
                }

                foreach (var j in starts)
                {
                    var diagonal = (long)j - i;
                    if (diagonalEnd.TryGetValue(diagonal, out var end) && end > i)
                    {
                        continue;
                    }

                    var run = 0;
                    while (i + run < image.Count && j + run < bios.Count && image[i + run] == bios[j + run])
                    {
                        run++;
                    }

                    diagonalEnd[diagonal] = i + run;
                    if (run < minRunWords)
                    {
                        continue;
                    }

                    var distinct = DistinctCount(image, i, run);
                    for (var k = 0; k < minDistinct.Length; k++)
                    {
                        if (distinct >= minDistinct[k])
                        {
                            for (var w = 0; w < run; w++)
                            {
                                covered[k].Add(j + w);
                            }
                        }
                    }
                }
            }
        }

        return covered;
    }

    /// <summary>The BIOS code the candidate images hold together, in bytes, each BIOS word counted once.</summary>
    public static int BiosCopyBytes(IEnumerable<IReadOnlyList<uint>> images, OpenBiosFirmware firmware) =>
        BiosCoveredWords(images, firmware).Count * 4;

    /// <summary>True when the candidate images together hold more BIOS code than <see cref="CopyThresholdBytes"/>.</summary>
    public static bool ExceedsCopyThreshold(IEnumerable<IReadOnlyList<uint>> images, OpenBiosFirmware firmware) =>
        BiosCopyBytes(images, firmware) > CopyThresholdBytes;

    /// <summary>A single image's verdict, for a single input.</summary>
    public static bool ContainsBiosCopy(IReadOnlyList<uint> words, OpenBiosFirmware firmware) =>
        ExceedsCopyThreshold([words], firmware);

    private static bool IsUniformKey(IReadOnlyList<uint> words, int start)
    {
        for (var k = 1; k < KeyWords; k++)
        {
            if (words[start + k] != words[start])
            {
                return false;
            }
        }

        return true;
    }

    private static int DistinctCount(IReadOnlyList<uint> words, int start, int count)
    {
        var distinct = new HashSet<uint>();
        for (var k = 0; k < count; k++)
        {
            distinct.Add(words[start + k]);
        }

        return distinct.Count;
    }

    private static (uint, uint, uint, uint) Key(IReadOnlyList<uint> words, int start) =>
        (words[start], words[start + 1], words[start + 2], words[start + 3]);

    /// <summary>The plan and dispatch stage: no static or loaded block may start inside the BIOS range.</summary>
    public static void RequireNoBiosCode(RecompilerIrProgram program, LoadedCodeTable loaded, string stage)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        foreach (var block in program.Blocks)
        {
            if (IsBiosAddress(block.EntryPc))
            {
                throw new InvalidOperationException(
                    $"a compiled block at 0x{block.EntryPc:X8} lies in the BIOS (Issue #B2); the BIOS is never translated. [stage: {stage}]");
            }
        }

        foreach (var version in loaded.Blocks)
        {
            var block = version.Block;
            if (OverlapsBios(block.EntryPc, (ulong)Math.Max(1, block.RetiredInstructionCount) * 4))
            {
                throw new InvalidOperationException(
                    $"a loaded block at 0x{block.EntryPc:X8} overlaps the BIOS (Issue #B2); the BIOS is never translated. [stage: {stage}]");
            }
        }
    }

    /// <summary>The generated-source stage: no block function named for a BIOS address may appear in the generated C.</summary>
    public static void RequireNoBiosSource(string generatedSource)
    {
        ArgumentNullException.ThrowIfNull(generatedSource);
        foreach (Match match in BlockName.Matches(generatedSource))
        {
            var address = Convert.ToUInt32(match.Groups[1].Value, 16);
            if (IsBiosAddress(address))
            {
                throw new InvalidOperationException(
                    $"the generated source names a block at 0x{address:X8}, a BIOS address (Issue #B2); generation is refused.");
            }
        }
    }

    /// <summary>The number of compiled ROM blocks in a plan's program, for the run's audit line.</summary>
    public static int BiosBlockCount(RecompilerIrProgram program) =>
        program.Blocks.Count(static block => IsBiosAddress(block.EntryPc));
}
