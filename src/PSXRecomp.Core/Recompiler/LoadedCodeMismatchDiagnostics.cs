using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Recompiler;

/// <summary>
/// Opt-in observation of RAM-placed code that the artifact refused (Issue #773 Phase B). Every handoff at a PC that has
/// loaded-code versions is counted; per (PC, version) the first differing word, the compared range as the AOT build holds
/// it and as guest RAM holds it, and SHA-256 of both are kept. Records are aggregated per (PC, version), capped, and written
/// in a deterministic order. Diagnostics only: nothing read here feeds <see cref="LoadedCodeTable.Match"/> or dispatch.
/// </summary>
[Domain]
public sealed class LoadedCodeMismatchDiagnostics
{
    /// <summary>Default cap on distinct (PC, version) records.</summary>
    public const int DefaultMaxRecords = 64;

    /// <summary>Schema tag written into the JSON report.</summary>
    public const string SchemaName = "loaded-code-mismatch/1";

    private const int WindowRadius = 2;

    private readonly int _maxRecords;
    private readonly Dictionary<(uint Pc, int Version), Entry> _entries = [];

    /// <param name="maxRecords">Maximum distinct (PC, version) records kept; further ones are counted, not recorded.</param>
    public LoadedCodeMismatchDiagnostics(int maxRecords = DefaultMaxRecords)
    {
        if (maxRecords < 1) throw new ArgumentOutOfRangeException(nameof(maxRecords), "At least one record must be kept.");
        _maxRecords = maxRecords;
    }

    /// <summary>Handoffs observed at a PC with loaded-code versions, including ones whose records were suppressed.</summary>
    public ulong TotalObservations { get; private set; }

    /// <summary>Per-version observations that had no record because the record cap was already reached.</summary>
    public ulong SuppressedObservations { get; private set; }

    /// <summary>Distinct (PC, version) records currently kept.</summary>
    public int DistinctRecords => _entries.Count;

    /// <summary>
    /// Observes one refused PC. <paramref name="versions"/> are the versions registered at <paramref name="pc"/> in link order;
    /// <paramref name="readWord"/> reads the guest word at a virtual address, as <see cref="LoadedCodeTable.Match"/> does.
    /// </summary>
    public void Observe(uint pc, IReadOnlyList<GuardedBlock> versions, Func<uint, uint> readWord)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(readWord);

        TotalObservations++;
        for (var index = 0; index < versions.Count; index++)
        {
            var aot = versions[index].Words;
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

            if (!_entries.TryGetValue((pc, index), out var entry))
            {
                if (_entries.Count >= _maxRecords)
                {
                    SuppressedObservations++;
                    continue;
                }

                entry = new Entry(pc, index, versions.Count, aot.ToArray());
                _entries[(pc, index)] = entry;
            }

            entry.Add(firstMismatch, mismatched, ram);
        }
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
            ["maxRecords"] = JsonValue.Create(_maxRecords),
            ["records"] = records,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Hex(uint value) => $"0x{value:X8}";

    private static string Sha256(uint[] words)
    {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
        }

        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>One (PC, version) line of the report, aggregated over every handoff that refused it.</summary>
    private sealed class Entry
    {
        private readonly uint[] _aot;
        private readonly string _aotSha256;
        // The RAM range of the latest refusal: hashed once when the report is written, not on every handoff.
        private uint[]? _lastRam;
        private uint[] _windowAot = [];
        private uint[] _windowRam = [];
        private int _windowStart;

        public Entry(uint pc, int version, int versionCount, uint[] aot)
        {
            Pc = pc;
            Version = version;
            VersionCount = versionCount;
            _aot = aot;
            _aotSha256 = Sha256(aot);
        }

        public uint Pc { get; }

        public int Version { get; }

        public int VersionCount { get; }

        /// <summary>Handoffs at this PC that refused this version.</summary>
        public ulong Rejections { get; private set; }

        /// <summary>Handoffs at this PC where this version matched in full (a correct version that was not selected).</summary>
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

            if (_lastRam is not null && !_lastRam.AsSpan().SequenceEqual(ram)) RamHashChanges++;
            _lastRam = ram;

            if (Rejections == 1)
            {
                // The window is taken at the first refusal only: a bounded sample, not a dump of the image.
                _windowStart = Math.Max(0, firstMismatch - WindowRadius);
                var end = Math.Min(_aot.Length, firstMismatch + WindowRadius + 1);
                _windowAot = _aot[_windowStart..end];
                _windowRam = ram[_windowStart..end];
            }
        }

        public JsonObject ToJsonNode()
        {
            var words = _aot.Length;
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
                ["ramSha256"] = _lastRam is null ? null : Sha256(_lastRam),
                ["windowStartPc"] = Rejections == 0 ? null : Hex(Pc + (uint)_windowStart * 4u),
                ["windowAot"] = new JsonArray(_windowAot.Select(static w => (JsonNode)Hex(w)).ToArray()),
                ["windowRam"] = new JsonArray(_windowRam.Select(static w => (JsonNode)Hex(w)).ToArray()),
                // Handoffs run after the artifact found no matching version, so none was selected.
                ["selectedVersion"] = null,
            };
        }
    }
}
