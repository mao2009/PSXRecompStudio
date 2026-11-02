using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Recompiler;

/// <summary>
/// Opt-in observation of handoffs at RAM-placed code whose artifact versions matched no guest word (Issue #773 Phase B). Every
/// handoff at a PC that has loaded-code versions is counted; per (PC, version) the first differing word, the compared range as
/// the AOT build holds it and as guest RAM holds it, and SHA-256 of the AOT words is kept, with SHA-256 of the latest refusal's RAM (ramSha256). The compiled guard's own decision is not
/// recorded, so a record is an observed handoff, not a confirmed guard refusal: the comparison is the host's, made against RAM
/// at the handoff, while the generated native code makes its own guard decision (Rejections is never evidence of it). Records are aggregated per (PC, version),
/// capped, and written in a deterministic order. Diagnostics only: nothing read here feeds <see cref="LoadedCodeTable.Match"/>
/// or dispatch.
/// </summary>
[Domain]
public sealed class LoadedCodeMismatchDiagnostics
{
    /// <summary>Default cap on distinct (PC, version) records.</summary>
    public const int DefaultMaxRecords = 64;

    /// <summary>Default budget of compared guest words: 16 Mi words, about 64 MiB of reads per run.</summary>
    public const ulong DefaultMaxComparedWords = 1UL << 24;

    /// <summary>Schema tag written into the JSON report.</summary>
    public const string SchemaName = "loaded-code-mismatch/1";

    private const int WindowRadius = 2;

    private readonly int _maxRecords;
    private readonly ulong _maxComparedWords;
    private readonly Dictionary<(uint Pc, int Version), Entry> _entries = [];

    /// <param name="maxRecords">Maximum distinct (PC, version) records kept; further ones are counted, not recorded.</param>
    /// <param name="maxComparedWords">
    /// Guest words the observation may compare in one run, enforced strictly per version: a version is compared whole or, when
    /// it does not fit the remaining budget, not read at all (counted in <see cref="SkippedVersions"/> and
    /// <see cref="SkippedWords"/>, never stored as a match or mismatch). Reads therefore never exceed this value.
    /// </param>
    public LoadedCodeMismatchDiagnostics(int maxRecords = DefaultMaxRecords, ulong maxComparedWords = DefaultMaxComparedWords)
    {
        if (maxRecords < 1) throw new ArgumentOutOfRangeException(nameof(maxRecords), "At least one record must be kept.");
        _maxRecords = maxRecords;
        _maxComparedWords = maxComparedWords;
    }

    /// <summary>Handoffs observed at a PC with loaded-code versions, including ones whose records were suppressed or skipped.</summary>
    public ulong TotalObservations { get; private set; }

    /// <summary>Per-version observations that had no record because the record cap was already reached.</summary>
    public ulong SuppressedObservations { get; private set; }

    /// <summary>Observations in which at least one version was not compared because it did not fit the word budget. A non-zero value makes the report incomplete (as does a non-zero <see cref="SuppressedObservations"/>).</summary>
    public ulong SkippedObservations { get; private set; }

    /// <summary>Versions not compared because they did not fit the remaining word budget.</summary>
    public ulong SkippedVersions { get; private set; }

    /// <summary>Guest words in the versions counted by <see cref="SkippedVersions"/> (saturating).</summary>
    public ulong SkippedWords { get; private set; }

    /// <summary>Guest words compared so far, across every version of every observation.</summary>
    public ulong ComparedWords { get; private set; }

    /// <summary>
    /// Handoffs at a PC with loaded-code versions that the fallback returned before they could be compared (for example, the
    /// transition budget stopped the run first). They have no record, so a non-zero value makes the report incomplete.
    /// </summary>
    public ulong UnobservedHandoffs { get; private set; }

    /// <summary>Whether the run finished its execution. A report written without it (a failed or killed run) is not complete.</summary>
    public bool Executed { get; private set; }

    /// <summary>
    /// Records whether the run reached a classified end. False when the host stopped it (for example a timeout): the
    /// report then stays incomplete, whatever it observed.
    /// </summary>
    public void MarkExecuted(bool classifiedEnd) => Executed = classifiedEnd;

    /// <summary>Counts one handoff at a versioned PC that was returned before <see cref="Observe"/>.</summary>
    public void NoteUnobservedHandoff() => UnobservedHandoffs++;

    /// <summary>Distinct (PC, version) records currently kept.</summary>
    public int DistinctRecords => _entries.Count;

    /// <summary>
    /// Observes one refused PC. <paramref name="versions"/> are the versions registered at <paramref name="pc"/> in link order;
    /// <paramref name="readWord"/> reads the guest word at a virtual address, as <see cref="LoadedCodeTable.Match"/> does.
    /// <paramref name="origins"/> are the images the manifest declared for each version (<see cref="LoadedCodeTable.OriginsAt"/>); null when unknown.
    /// </summary>
    public void Observe(
        uint pc, IReadOnlyList<GuardedBlock> versions, Func<uint, uint> readWord, IReadOnlyList<IReadOnlyList<LoadedImageOrigin>>? origins = null)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(readWord);

        TotalObservations++;
        var skippedAny = false;
        for (var index = 0; index < versions.Count; index++)
        {
            // A version that would get no record is not read at all: the cap bounds the reads as well as the storage.
            var key = (pc, index);
            if (!_entries.ContainsKey(key) && _entries.Count >= _maxRecords)
            {
                SuppressedObservations++;
                continue;
            }

            // Strict budget: a version is compared whole or not at all. ComparedWords never exceeds the budget, so the
            // remaining budget below cannot underflow, and nothing is read or allocated for a version that does not fit.
            var aot = versions[index].Words;
            if ((ulong)aot.Count > _maxComparedWords - ComparedWords)
            {
                SkippedVersions++;
                SkippedWords = SkippedWords > ulong.MaxValue - (ulong)aot.Count ? ulong.MaxValue : SkippedWords + (ulong)aot.Count;
                skippedAny = true;
                continue;
            }

            var ram = new uint[aot.Count];
            var firstMismatch = -1;
            var mismatched = 0;
            for (var i = 0; i < aot.Count; i++)
            {
                ram[i] = readWord(pc + (uint)i * 4u);
                if (ram[i] == aot[i]) continue;
                mismatched++;
                firstMismatch = firstMismatch < 0 ? i : firstMismatch;
            }

            ComparedWords += (ulong)aot.Count;
            if (!_entries.TryGetValue(key, out var entry))
            {
                var versionOrigins = origins is not null && index < origins.Count ? origins[index] : [];
                // The words are the table's own immutable data: the entry references them rather than copying (memory cost, #786 review).
                entry = new Entry(pc, index, versions.Count, aot, versionOrigins);
                _entries[key] = entry;
            }

            entry.Add(firstMismatch, mismatched, ram);
        }

        if (skippedAny) SkippedObservations++;
    }

    /// <summary>The report as indented JSON with a fixed property order and no timestamps: equal observations give equal text.</summary>
    public string ToJson()
    {
        var records = new JsonArray();
        foreach (var entry in _entries.Values.OrderBy(static e => e.Pc).ThenBy(static e => e.Version))
        {
            records.Add(entry.ToJsonNode());
        }

        var root = new JsonObject
        {
            ["schema"] = SchemaName,
            ["totalObservations"] = JsonValue.Create(TotalObservations),
            ["suppressedObservations"] = JsonValue.Create(SuppressedObservations),
            // Explicit so a cut-short comparison, a dropped record, an unobserved handoff or a run that did not finish is never
            // mistaken for a complete report.
            ["executed"] = JsonValue.Create(Executed),
            ["incomplete"] = JsonValue.Create(!Executed || SkippedObservations > 0 || SuppressedObservations > 0 || UnobservedHandoffs > 0),
            ["unobservedHandoffs"] = JsonValue.Create(UnobservedHandoffs),
            ["skippedObservations"] = JsonValue.Create(SkippedObservations),
            ["comparedWordTotal"] = JsonValue.Create(ComparedWords),
            ["maxComparedWords"] = JsonValue.Create(_maxComparedWords),
            ["skippedVersions"] = JsonValue.Create(SkippedVersions),
            ["skippedWords"] = JsonValue.Create(SkippedWords),
            ["guardDecisionRecorded"] = JsonValue.Create(false),
            ["attribution"] = "host-handoff-ram-comparison",
            // An origin is the manifest's declaration (label and directive), not evidence of which file the game loaded.
            ["originBasis"] = "manifest-declared",
            ["rejectionsMeaning"] = "Per-version rejections count host-side RAM comparisons at the fallback handoff; they are not a confirmed native guard refusal.",
            ["maxRecords"] = JsonValue.Create(_maxRecords),
            ["records"] = records,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Hex(uint value) => $"0x{value:X8}";

    private static string Sha256(IReadOnlyList<uint> words)
    {
        var bytes = new byte[words.Count * 4];
        for (var i = 0; i < words.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
        }

        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>One (PC, version) line of the report, aggregated over every handoff that refused it.</summary>
    private sealed class Entry
    {
        private readonly IReadOnlyList<uint> _aot;
        private readonly string _aotSha256;
        private readonly IReadOnlyList<LoadedImageOrigin> _origins;
        // SHA-256 of the RAM range of the latest refusal. A hash, not the words: keeping every record's last RAM range
        // would hold up to the whole comparison budget in memory (#786 review).
        private string? _lastRamSha256;
        private uint[] _windowAot = [];
        private int _windowStart;

        public Entry(uint pc, int version, int versionCount, IReadOnlyList<uint> aot, IReadOnlyList<LoadedImageOrigin> origins)
        {
            Pc = pc;
            Version = version;
            VersionCount = versionCount;
            _aot = aot;
            _aotSha256 = Sha256(aot);
            _origins = origins;
        }

        public uint Pc { get; }

        public int Version { get; }

        public int VersionCount { get; }

        /// <summary>
        /// Handoffs at this PC where the host's RAM comparison differed from this version. Not a confirmed native guard
        /// refusal: the generated code's own guard decision is not recorded.
        /// </summary>
        public ulong Rejections { get; private set; }

        /// <summary>
        /// Handoffs at this PC where the host's RAM comparison matched this version in full. Not evidence of the native
        /// guard's decision, which is not recorded.
        /// </summary>
        public ulong FullMatches { get; private set; }

        public int FirstMismatchMin { get; private set; } = -1;

        public int FirstMismatchMax { get; private set; } = -1;

        public int MismatchedWordsMin { get; private set; } = int.MaxValue;

        public int MismatchedWordsMax { get; private set; }

        /// <summary>Handoffs where the RAM range hashed differently from the previous refusal (the difference changed over time).</summary>
        public ulong RamHashChanges { get; private set; }

        public void Add(int firstMismatch, int mismatched, uint[] ram)
        {
            if (firstMismatch < 0)
            {
                FullMatches++;
                return;
            }

            Rejections++;
            FirstMismatchMin = FirstMismatchMin < 0 ? firstMismatch : Math.Min(FirstMismatchMin, firstMismatch);
            FirstMismatchMax = Math.Max(FirstMismatchMax, firstMismatch);
            MismatchedWordsMin = Math.Min(MismatchedWordsMin, mismatched);
            MismatchedWordsMax = Math.Max(MismatchedWordsMax, mismatched);

            var ramSha256 = Sha256(ram);
            if (_lastRamSha256 is not null && _lastRamSha256 != ramSha256) RamHashChanges++;
            _lastRamSha256 = ramSha256;

            if (Rejections == 1)
            {
                // The window is taken at the first refusal only: a bounded sample, not a dump of the image. Only the image's own words
                // (windowAot) are kept: the RAM words are hashed and never stored or written (PR #839 ruling).
                _windowStart = Math.Max(0, firstMismatch - WindowRadius);
                var end = Math.Min(_aot.Count, firstMismatch + WindowRadius + 1);
                _windowAot = new uint[end - _windowStart];
                for (var i = 0; i < _windowAot.Length; i++) _windowAot[i] = _aot[_windowStart + i];
            }
        }

        public JsonObject ToJsonNode()
        {
            var words = _aot.Count;
            var physical = Pc & 0x1FFFFFFFu;
            return new JsonObject
            {
                ["pc"] = Hex(Pc),
                ["version"] = JsonValue.Create(Version),
                ["versionCount"] = JsonValue.Create(VersionCount),
                ["comparedWords"] = JsonValue.Create(words),
                ["ramPage"] = Hex((physical & 0x001FFFFFu) >> 12),
                ["rejections"] = JsonValue.Create(Rejections),
                ["fullMatches"] = JsonValue.Create(FullMatches),
                ["firstMismatchIndexRange"] = Rejections == 0 ? null : new JsonArray(JsonValue.Create(FirstMismatchMin), JsonValue.Create(FirstMismatchMax)),
                ["mismatchedWordsRange"] = Rejections == 0 ? null : new JsonArray(JsonValue.Create(MismatchedWordsMin), JsonValue.Create(MismatchedWordsMax)),
                ["ramHashChanges"] = JsonValue.Create(RamHashChanges),
                ["aotSha256"] = _aotSha256,
                ["ramSha256"] = _lastRamSha256,
                ["windowStartPc"] = Rejections == 0 ? null : Hex(Pc + (uint)_windowStart * 4u),
                ["windowAot"] = new JsonArray(_windowAot.Select(static w => (JsonNode)Hex(w)).ToArray()),
                ["origins"] = OriginsJson(),
            };
        }

        /// <summary>Every image that registered this exact code, with the word offset of the entry inside it. Empty means unknown.</summary>
        private JsonArray OriginsJson()
        {
            var array = new JsonArray();
            foreach (var origin in _origins)
            {
                array.Add(new JsonObject
                {
                    ["name"] = origin.Name,
                    ["kind"] = origin.Kind,
                    ["loadAddress"] = Hex(origin.LoadAddress),
                    ["imageWords"] = JsonValue.Create(origin.WordCount),
                    ["wordOffset"] = JsonValue.Create(((long)Pc - origin.LoadAddress) / 4),
                });
            }

            if (array.Count == 0)
            {
                array.Add(new JsonObject { ["name"] = null, ["kind"] = "unknown" });
            }

            return array;
        }
    }
}
