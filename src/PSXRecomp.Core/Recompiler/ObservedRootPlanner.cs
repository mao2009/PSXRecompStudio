using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;

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
/// <param name="RamWord">The instruction word the interpreter fetched from guest RAM at the PC, or null when the probe did not observe it.
/// A root is planned only when this equals the word the EXE file places there (see <see cref="ObservedRootPlanner.Plan"/>).</param>
[Domain]
internal sealed record ObservedFallbackTarget(uint Pc, string Cause, ulong Entries, ulong Instructions, uint? RamWord);

/// <summary>One image of the run that produced a fallback-target export, as the run's manifest described it.</summary>
/// <param name="SourceSha256">The SHA-256 of the file an EXE image was read from; null for a ROM image.</param>
/// <param name="Roots">The image's roots as the run built them (sorted).</param>
[Domain]
internal sealed record ObservedRunImage(string Name, uint LoadAddress, string WordsSha256, string? SourceSha256, IReadOnlyList<uint> Roots);

/// <summary>
/// The identity of the run behind a fallback-target export (Issue #773): the firmware (ROM) it ran, the interpret points, the images
/// (with their source and code-word hashes and roots) its manifest produced, the disc image's SHA-256 (null when the run had no disc)
/// and the SHA-256 of the probe options that change which PCs are observed (<c>--roots</c>, <c>--code-bytes</c>). Every field is a
/// content hash or a PC; no path is carried. The planner refuses an export whose identity differs from its own inputs. It does not
/// prove the trace matches guest RAM at run time.
/// </summary>
[Domain]
internal sealed record ObservedRunIdentity(
    string FirmwareSha256,
    IReadOnlyList<uint> Interpreted,
    IReadOnlyList<ObservedRunImage> Images,
    string? DiscSha256,
    string ProbeOptionsSha256);

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

    /// <summary>
    /// Whether a run that ended in <paramref name="state"/> reached a classified end, so its fallback set is the run's whole
    /// observation. Budget exhaustion, a failed or unsupported transfer, and a contract violation leave a partial set, which
    /// the planner refuses. A deliberate <c>StopAt</c> pause ends in <see cref="TitleExecutionState.RuntimeHandoff"/>, a complete end.
    /// </summary>
    public static bool IsCompleteOutcome(TitleExecutionState state) =>
        state is TitleExecutionState.Completed or TitleExecutionState.Returned or TitleExecutionState.RuntimeHandoff;

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
        ObservedRootBudget? budget = null,
        Func<uint, IReadOnlyList<uint>, IEnumerable<uint>, IReadOnlySet<uint>>? walk = null)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(targets);
        budget ??= ObservedRootBudget.Default;
        // The reachability walk is a parameter only so a test can make a candidate's walk fail (the guard below); production uses the real walk.
        walk ??= ReachableProgramBuilder.ReachablePcs;

        var roots = images.Select(static image => new List<uint>(image.Roots)).ToArray();
        var reachable = images.Select(image => walk(image.LoadAddress, image.Words, image.Roots)).ToArray();
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

            // The native block is built from the file's words and runs only while RAM holds them (the loaded-image guard), so a
            // target whose RAM word differs from the file's word, or was not observed, is not a valid native route: it is not planned.
            if (target.RamWord != image.Words[(int)((target.Pc - image.LoadAddress) / 4)])
            {
                Reject("ram-word-mismatch");
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

            var allowance = Math.Max(budget.MinExtraReachable, budget.MaxExtraReachableFraction * baseReachable);
            if (extra + 1 > allowance)
            {
                // A new root adds at least itself, so an exhausted allowance rejects without recomputing the image's reachable set.
                Reject("code-budget");
                continue;
            }

            IReadOnlySet<uint> grown;
            try
            {
                grown = walk(image.LoadAddress, image.Words, roots[index].Append(target.Pc));
            }
            catch (InvalidOperationException)
            {
                // Defensive: a candidate that is safe alone is rejected, not allowed to abort the plan, if the combined walk fails.
                Reject("not-structurally-safe");
                continue;
            }

            var growth = grown.Count - reachable[index].Count;
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

    /// <summary>
    /// The SHA-256 of the probe options that change which PCs a run observes: the <c>--code-bytes</c> count, the
    /// <c>--segments</c> and <c>--segment-budget</c> run limits, and the <c>--roots</c> PCs (order and repeats do not matter). The probe and the planner compute it from the same values, so an export is bound to them.
    /// </summary>
    public static string ProbeOptionsSha256(uint codeBytes, IEnumerable<uint> roots, uint segments = 10, uint segmentBudget = 100_000)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var canonical = $"code-bytes={codeBytes}\nsegments={segments}\nsegment-budget={segmentBudget}\nroots={string.Join(",", roots.Distinct().Order().Select(static pc => $"0x{pc:X8}"))}\n";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>The run identity as the probe records it and as <see cref="Verify"/> recomputes it from the current inputs.</summary>
    public static ObservedRunIdentity DescribeRun(string firmwareSha256, LoadImageManifest manifest, string? discSha256, string probeOptionsSha256)
    {
        ArgumentNullException.ThrowIfNull(firmwareSha256);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(probeOptionsSha256);
        return new ObservedRunIdentity(
            firmwareSha256.ToLowerInvariant(),
            manifest.Interpreted.Order().ToArray(),
            manifest.Images.Select(static i => new ObservedRunImage(
                i.Name, i.LoadAddress, LoadImageManifest.CodeWordsSha256(i.Words), i.SourceSha256, i.Roots.Order().ToArray())).ToArray(),
            discSha256?.ToLowerInvariant(),
            probeOptionsSha256.ToLowerInvariant());
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
        discSha256 = run.DiscSha256,
        probeOptionsSha256 = run.ProbeOptionsSha256,
    };

    /// <summary>Fail closed unless the export's run is exactly the run the current inputs describe.</summary>
    public static void Verify(ObservedRunIdentity recorded, ObservedRunIdentity current)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(current);
        static InvalidDataException Mismatch(string what) =>
            new($"fallback targets: the export was not taken over these inputs ({what} differs); re-run openbios-probe --fallback-targets with this firmware and manifest.");

        if (recorded.FirmwareSha256 != current.FirmwareSha256) throw Mismatch("firmware SHA-256");
        if (recorded.DiscSha256 != current.DiscSha256) throw Mismatch("disc image SHA-256");
        if (recorded.ProbeOptionsSha256 != current.ProbeOptionsSha256) throw Mismatch("probe options (--roots, --code-bytes, --segments, --segment-budget)");
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
    /// instructions, and a PC may appear only once. The run must have ended at a classified end (see <see cref="IsCompleteOutcome"/>).
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

            var outcome = root.GetProperty("outcome");
            // Names only: Enum.TryParse alone would also read a number (e.g. "99") as a state.
            var stateName = outcome.GetProperty("state").GetString();
            if (stateName is null || !Enum.IsDefined(typeof(TitleExecutionState), stateName) || !Enum.TryParse(stateName, out TitleExecutionState state))
            {
                throw new InvalidDataException("fallback targets: 'outcome.state' is not a known execution state.");
            }

            // A run that was paused at --stop-at is a deliberate partial observation (it ends in RuntimeHandoff), so it is refused.
            if (outcome.GetProperty("stopAt") is not { ValueKind: JsonValueKind.Null })
            {
                throw new InvalidDataException("fallback targets: the export was produced with --stop-at, a deliberate pause; its evidence is partial and is refused.");
            }

            if (outcome.GetProperty("complete").GetBoolean() != IsCompleteOutcome(state))
            {
                throw new InvalidDataException($"fallback targets: 'outcome.complete' contradicts the state {state}.");
            }

            if (!IsCompleteOutcome(state))
            {
                throw new InvalidDataException(
                    $"fallback targets: the run ended in {state}, so its evidence is partial and is refused; re-run openbios-probe to a classified end.");
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
                    e.GetProperty("roots").EnumerateArray().Select(static r => ParsePc(r.GetString())).ToArray())).ToArray(),
                runElement.GetProperty("discSha256") is { ValueKind: not JsonValueKind.Null } d ? Sha(d.GetString(), "discSha256") : null,
                Sha(runElement.GetProperty("probeOptionsSha256").GetString(), "probeOptionsSha256"));

            var seen = new HashSet<uint>();
            var targets = new List<ObservedFallbackTarget>();
            foreach (var element in root.GetProperty("targets").EnumerateArray())
            {
                var pc = ParsePc(element.GetProperty("pc").GetString());
                var cause = element.GetProperty("cause").GetString() ?? throw new InvalidDataException("fallback targets: a cause is null.");
                var ramWord = element.GetProperty("ramWord") is { ValueKind: not JsonValueKind.Null } w ? ParsePc(w.GetString()) : (uint?)null;
                var target = new ObservedFallbackTarget(pc, cause, element.GetProperty("entries").GetUInt64(), element.GetProperty("instructions").GetUInt64(), ramWord);
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
