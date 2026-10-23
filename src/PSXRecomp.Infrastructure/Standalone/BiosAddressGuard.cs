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

    /// <summary>A copy of BIOS code is recognised by a run of this many words that the BIOS holds too (256 bytes).</summary>
    public const int CopyWindowWords = 64;

    /// <summary>
    /// The manifest stage (Issue #B2, MEDIUM-3): an AOT input must come from an allowed source kind, a <c>rom</c> image (BIOS bytes
    /// copied to RAM to be translated) is refused, an image overlapping the BIOS range is refused, and an image holding a copy of a
    /// BIOS run of <see cref="CopyWindowWords"/> words is refused. A shorter overlap is not a copy and is allowed: disc code that
    /// happens to share bytes with the BIOS is not evidence of its origin.
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

            if (ContainsBiosCopy(image.Words, firmware))
            {
                throw new InvalidDataException(
                    $"image '{image.Name}' holds a copy of BIOS code ({CopyWindowWords} words or more); BIOS-derived code is never translated (Issue #B2).");
            }
        }
    }

    /// <summary>
    /// True when <paramref name="words"/> holds a run of <see cref="CopyWindowWords"/> words that the BIOS holds at some offset. Uniform
    /// runs (padding such as all-zero words) are not evidence and are skipped.
    /// </summary>
    public static bool ContainsBiosCopy(IReadOnlyList<uint> words, OpenBiosFirmware firmware)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(firmware);
        if (words.Count < CopyWindowWords || firmware.Words.Count < CopyWindowWords)
        {
            return false;
        }

        var biosKeys = new HashSet<ulong>();
        for (var j = 0; j + CopyWindowWords <= firmware.Words.Count; j++)
        {
            if (!IsUniform(firmware.Words, j))
            {
                biosKeys.Add(WindowKey(firmware.Words, j));
            }
        }

        for (var i = 0; i + CopyWindowWords <= words.Count; i++)
        {
            if (!IsUniform(words, i) && biosKeys.Contains(WindowKey(words, i)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUniform(IReadOnlyList<uint> words, int start)
    {
        for (var k = 1; k < CopyWindowWords; k++)
        {
            if (words[start + k] != words[start])
            {
                return false;
            }
        }

        return true;
    }

    private static ulong WindowKey(IReadOnlyList<uint> words, int start)
    {
        var bytes = new byte[CopyWindowWords * 4];
        for (var k = 0; k < CopyWindowWords; k++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(k * 4), words[start + k]);
        }

        return BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(bytes));
    }

    /// <summary>The plan and dispatch stage: no static or loaded block may start inside the BIOS range.</summary>
    public static void RequireNoBiosCode(RecompilerIrProgram program, LoadedCodeTable loaded)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(loaded);
        foreach (var block in program.Blocks)
        {
            if (IsBiosAddress(block.EntryPc))
            {
                throw new InvalidOperationException($"a compiled block at 0x{block.EntryPc:X8} lies in the BIOS (Issue #B2); the BIOS is never translated.");
            }
        }

        foreach (var version in loaded.Blocks)
        {
            var block = version.Block;
            if (OverlapsBios(block.EntryPc, (ulong)Math.Max(1, block.RetiredInstructionCount) * 4))
            {
                throw new InvalidOperationException($"a loaded block at 0x{block.EntryPc:X8} overlaps the BIOS (Issue #B2); the BIOS is never translated.");
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
