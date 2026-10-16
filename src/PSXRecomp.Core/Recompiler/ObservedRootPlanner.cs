using System.Globalization;
using System.Text.Json;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Recompiler;

/// <summary>
/// The deterministic limits on observed roots (Issue #773). Each limit bounds a cost the next build pays: roots per image,
/// roots in total, and the code they add, measured as reachable instructions. The code allowance is the larger of
/// <see cref="MinExtraReachable"/> and <see cref="MaxExtraReachableFraction"/> of the entry-driven reachable set, so a tiny
/// image still admits a real callee while a large one is bounded by its size.
/// </summary>
[Domain]
internal sealed record ObservedRootBudget(int MaxRootsPerImage, int MaxRootsTotal, double MaxExtraReachableFraction, int MinExtraReachable)
{
    public static ObservedRootBudget Default { get; } = new(MaxRootsPerImage: 64, MaxRootsTotal: 256, MaxExtraReachableFraction: 0.20, MinExtraReachable: 1024);
}

/// <summary>One distinct fallback target reported by a probe run (Issue #773).</summary>
/// <param name="Pc">The guest PC the artifact handed to the interpreter.</param>
/// <param name="Cause">The probe's classification of the target; only <c>unknown-code</c> and <c>aot-coverage-gap:loaded-image</c> may become a root.</param>
/// <param name="Entries">How many times control was handed to the interpreter at the PC.</param>
/// <param name="Instructions">Guest instructions the interpreter retired from the PC.</param>
[Domain]
internal sealed record ObservedFallbackTarget(uint Pc, string Cause, ulong Entries, ulong Instructions);

/// <summary>One image of the run that produced a fallback-target export, as the run's manifest described it.</summary>
/// <param name="SourceSha256">The SHA-256 of the file an EXE image was read from; null for a ROM image.</param>
/// <param name="Roots">The image's roots as the run built them (sorted).</param>
[Domain]
internal sealed record ObservedRunImage(string Name, uint LoadAddress, string WordsSha256, string? SourceSha256, IReadOnlyList<uint> Roots);

/// <summary>
/// The identity of the run behind a fallback-target export (Issue #773): the firmware it ran, the interpret points and the images
/// (with their source and code-word hashes and roots) its manifest produced. It proves which inputs the trace was taken over; it does
/// not prove the disc image, the ROM roots/code-bytes options or the run's inputs, none of which the export carries a hash of.
/// </summary>
[Domain]
internal sealed record ObservedRunIdentity(string FirmwareSha256, IReadOnlyList<uint> Interpreted, IReadOnlyList<ObservedRunImage> Images);

/// <summary>A parsed fallback-target export: the identity of its run, and its targets.</summary>
[Domain]
internal sealed record ObservedFallbackExport(ObservedRunIdentity Run, IReadOnlyList<ObservedFallbackTarget> Targets);

/// <summary>One accepted observed root with the run evidence that selected it.</summary>
[Domain]
internal sealed record ObservedRoot(uint Pc, ulong Instructions, ulong Entries);

/// <summary>The observed roots accepted for one image.</summary>
[Domain]
internal sealed record ObservedImageRoots(int ImageIndex, string Name, IReadOnlyList<ObservedRoot> Roots);

/// <summary>How the candidates were disposed of. Every candidate is counted exactly once, as accepted or under one reason.</summary>
/// <param name="Candidates">Targets offered to the planner.</param>
/// <param name="Accepted">Targets accepted as roots.</param>
/// <param name="BaseReachable">Reachable instructions from the entry-driven roots, summed over the images.</param>
/// <param name="ExtraReachable">Reachable instructions the accepted roots add.</param>
/// <param name="Rejected">Rejected candidates per reason, in reason order.</param>
[Domain]
internal sealed record ObservedRootCounts(
    int Candidates,
    int Accepted,
    int BaseReachable,
    int ExtraReachable,
    IReadOnlyDictionary<string, int> Rejected);

/// <summary>The planned observed roots, per image in manifest order.</summary>
[Domain]
internal sealed record ObservedRootPlan(IReadOnlyList<ObservedImageRoots> Images, ObservedRootCounts Counts);

/// <summary>
/// Turns observed fallback targets into per-image roots (Issue #773). Only <c>unknown-code</c> and loaded-image coverage-gap
/// targets that lie inside exactly one EXE image with a source identity become candidates; a candidate is accepted only when it is structurally safe
/// and within the budget. A PC is never assumed to be a function entry: its shape is not checked, only its structure.
/// </summary>
[Domain]
internal static class ObservedRootPlanner
{
    /// <summary>The fallback-target export format written by <c>openbios-probe --fallback-targets</c>.</summary>
    internal const string TargetFormat = "psxrecomp.fallback-targets/v2";

    internal const string UnknownCodeCause = "unknown-code";

    /// <summary>A PC inside a loaded EXE image's words that the build did not compile (a coverage gap, not unknown memory).</summary>
    internal const string LoadedImageGapCause = "aot-coverage-gap:loaded-image";

    /// <summary>
    /// Plans the observed roots. Candidates are ordered by observed instructions (then entries, then PC) so the same inputs
    /// always select the same roots and reject the same candidates.
    /// </summary>
    public static ObservedRootPlan Plan(
        IReadOnlyList<LoadedImageSpec> images,
        IReadOnlyList<ObservedFallbackTarget> targets,
        ObservedRootBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(targets);
        budget ??= ObservedRootBudget.Default;

        var roots = images.Select(static image => new List<uint>(image.Roots)).ToArray();
        var reachable = images.Select(static image => ReachableProgramBuilder.ReachablePcs(image.LoadAddress, image.Words, image.Roots)).ToArray();
        var baseReachable = reachable.Sum(static set => set.Count);
        var accepted = images.Select(static _ => new List<ObservedRoot>()).ToArray();
        var rejected = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var extra = 0;
        var total = 0;

        void Reject(string reason) => rejected[reason] = rejected.GetValueOrDefault(reason) + 1;

        foreach (var target in targets
            .OrderByDescending(static t => t.Instructions)
            .ThenByDescending(static t => t.Entries)
            .ThenBy(static t => t.Pc))
        {
            if (target.Cause is not (UnknownCodeCause or LoadedImageGapCause))
            {
                Reject("cause-not-candidate");
                continue;
            }

            if ((target.Pc & 3) != 0)
            {
                Reject("unaligned");
                continue;
            }

            var owners = Enumerable.Range(0, images.Count).Where(i => ContainsPc(images[i], target.Pc)).ToArray();
            if (owners.Length == 0)
            {
                Reject("outside-images");
                continue;
            }

            if (owners.Length > 1)
            {
                Reject("ambiguous-image");
                continue;
            }

            var index = owners[0];
            var image = images[index];
            if (image.SourceSha256 is null)
            {
                Reject("image-without-source-identity");
                continue;
            }

            if (!IsRepresentableName(image.Name))
            {
                Reject("unrepresentable-image-name");
                continue;
            }

            if (reachable[index].Contains(target.Pc))
            {
                Reject("already-reachable");
                continue;
            }

            if (accepted[index].Count >= budget.MaxRootsPerImage)
            {
                Reject("per-image-budget");
                continue;
            }

            if (total >= budget.MaxRootsTotal)
            {
                Reject("total-budget");
                continue;
            }

            if (!ReachableProgramBuilder.IsDiscoverableRoot(image.LoadAddress, image.Words, target.Pc))
            {
                Reject("not-structurally-safe");
                continue;
            }

            var grown = ReachableProgramBuilder.ReachablePcs(image.LoadAddress, image.Words, roots[index].Append(target.Pc));
            var growth = grown.Count - reachable[index].Count;
            var allowance = Math.Max(budget.MinExtraReachable, budget.MaxExtraReachableFraction * baseReachable);
            if (extra + growth > allowance)
            {
                Reject("code-budget");
                continue;
            }

            roots[index].Add(target.Pc);
            reachable[index] = grown;
            extra += growth;
            total++;
            accepted[index].Add(new ObservedRoot(target.Pc, target.Instructions, target.Entries));
        }

        var perImage = Enumerable.Range(0, images.Count)
            .Where(i => accepted[i].Count > 0)
            .Select(i => new ObservedImageRoots(i, images[i].Name, accepted[i]))
            .ToArray();
        return new ObservedRootPlan(perImage, new ObservedRootCounts(targets.Count, total, baseReachable, extra, rejected));
    }

    /// <summary>The run identity as the probe records it and as <see cref="Verify"/> recomputes it from the current inputs.</summary>
    public static ObservedRunIdentity DescribeRun(string firmwareSha256, LoadImageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(firmwareSha256);
        ArgumentNullException.ThrowIfNull(manifest);
        return new ObservedRunIdentity(
            firmwareSha256.ToLowerInvariant(),
            manifest.Interpreted.Order().ToArray(),
            manifest.Images.Select(static i => new ObservedRunImage(
                i.Name, i.LoadAddress, LoadImageManifest.CodeWordsSha256(i.Words), i.SourceSha256, i.Roots.Order().ToArray())).ToArray());
    }

    /// <summary>The JSON shape of <see cref="ObservedRunIdentity"/> in an export (the inverse of the <c>run</c> parse).</summary>
    public static object RunDocument(ObservedRunIdentity run) => new
    {
        firmwareSha256 = run.FirmwareSha256,
        interpret = run.Interpreted.Select(static pc => $"0x{pc:X8}").ToArray(),
        images = run.Images.Select(static i => new
        {
            name = i.Name,
            loadAddress = $"0x{i.LoadAddress:X8}",
            wordsSha256 = i.WordsSha256,
            sourceSha256 = i.SourceSha256,
            roots = i.Roots.Select(static pc => $"0x{pc:X8}").ToArray(),
        }).ToArray(),
    };

    /// <summary>Fail closed unless the export's run is exactly the run the current inputs describe.</summary>
    public static void Verify(ObservedRunIdentity recorded, ObservedRunIdentity current)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(current);
        static InvalidDataException Mismatch(string what) =>
            new($"fallback targets: the export was not taken over these inputs ({what} differs); re-run openbios-probe --fallback-targets with this firmware and manifest.");

        if (recorded.FirmwareSha256 != current.FirmwareSha256) throw Mismatch("firmware SHA-256");
        if (!recorded.Interpreted.SequenceEqual(current.Interpreted)) throw Mismatch("interpret points");
        if (recorded.Images.Count != current.Images.Count) throw Mismatch("image count");
        for (var i = 0; i < current.Images.Count; i++)
        {
            var (r, c) = (recorded.Images[i], current.Images[i]);
            if (r.Name != c.Name) throw Mismatch($"image {i} name");
            if (r.LoadAddress != c.LoadAddress) throw Mismatch($"image '{c.Name}' load address");
            if (r.WordsSha256 != c.WordsSha256) throw Mismatch($"image '{c.Name}' code words");
            if (r.SourceSha256 != c.SourceSha256) throw Mismatch($"image '{c.Name}' source SHA-256");
            if (!r.Roots.SequenceEqual(c.Roots)) throw Mismatch($"image '{c.Name}' roots");
        }
    }

    /// <summary>Reads the targets of an export; the run identity is parsed but not checked here (see <see cref="Verify"/>).</summary>
    public static IReadOnlyList<ObservedFallbackTarget> ReadTargets(string json) => ReadExport(json).Targets;

    /// <summary>
    /// Reads a fallback-target export. Fail closed: the format must match, the run identity must be present and well formed
    /// (its firmware hash equal to the top-level <c>sha256</c>), every target must carry a valid PC, cause, entries and
    /// instructions, and a PC may appear only once.
    /// </summary>
    public static ObservedFallbackExport ReadExport(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("format").GetString() != TargetFormat)
            {
                throw new InvalidDataException($"fallback targets: the format is not '{TargetFormat}'.");
            }

            var runElement = root.GetProperty("run");
            var firmware = Sha(runElement.GetProperty("firmwareSha256").GetString(), "firmwareSha256");
            if (root.GetProperty("sha256").GetString() != firmware)
            {
                throw new InvalidDataException("fallback targets: 'sha256' and 'run.firmwareSha256' disagree.");
            }

            var run = new ObservedRunIdentity(
                firmware,
                runElement.GetProperty("interpret").EnumerateArray().Select(static e => ParsePc(e.GetString())).ToArray(),
                runElement.GetProperty("images").EnumerateArray().Select(static e => new ObservedRunImage(
                    e.GetProperty("name").GetString() ?? throw new InvalidDataException("fallback targets: an image name is null."),
                    ParsePc(e.GetProperty("loadAddress").GetString()),
                    Sha(e.GetProperty("wordsSha256").GetString(), "wordsSha256"),
                    e.GetProperty("sourceSha256") is { ValueKind: not JsonValueKind.Null } s ? Sha(s.GetString(), "sourceSha256") : null,
                    e.GetProperty("roots").EnumerateArray().Select(static r => ParsePc(r.GetString())).ToArray())).ToArray());

            var seen = new HashSet<uint>();
            var targets = new List<ObservedFallbackTarget>();
            foreach (var element in root.GetProperty("targets").EnumerateArray())
            {
                var pc = ParsePc(element.GetProperty("pc").GetString());
                var cause = element.GetProperty("cause").GetString() ?? throw new InvalidDataException("fallback targets: a cause is null.");
                var target = new ObservedFallbackTarget(pc, cause, element.GetProperty("entries").GetUInt64(), element.GetProperty("instructions").GetUInt64());
                if (!seen.Add(pc))
                {
                    throw new InvalidDataException($"fallback targets: 0x{pc:X8} is listed more than once.");
                }

                targets.Add(target);
            }

            return new ObservedFallbackExport(run, targets);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or FormatException or InvalidOperationException or JsonException)
        {
            throw new InvalidDataException($"fallback targets: malformed document ({ex.Message}).", ex);
        }
    }

    private static string Sha(string? text, string field) =>
        text is { Length: 64 } && text.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
            ? text
            : throw new InvalidDataException($"fallback targets: '{field}' is not a lower-case SHA-256.");

    private static uint ParsePc(string? text)
    {
        var digits = text is not null && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        if (digits is null || !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pc))
        {
            throw new InvalidDataException($"fallback targets: '{text}' is not a hexadecimal PC.");
        }

        return pc;
    }

    private static bool ContainsPc(LoadedImageSpec image, uint pc) =>
        pc >= image.LoadAddress && (ulong)pc < (ulong)image.LoadAddress + (ulong)image.Words.Length * 4;

    // A name that a manifest directive can carry as one field, so the observed roots written for it read back intact.
    private static bool IsRepresentableName(string name) =>
        name.Length > 0 && !name.Any(static c => char.IsWhiteSpace(c) || c == '#');
}
