using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure.Cli;

/// <summary>
/// The typed form of one VBlank boundary record written by <see cref="OpenBiosProbeBoundaryLog"/>. Parsing validates the
/// whole schema (object shape, exact key set, value types and formats) so that a comparison never works on loose JSON.
/// </summary>
[Infrastructure]
internal sealed record OpenBiosProbeBoundaryRecord(
    string Engine,
    ulong VBlank,
    ulong Cycles,
    string IStat,
    string IMask,
    string Dicr,
    string GpuStat,
    string DisplayMode,
    string DisplayVramStart,
    string VramSha256)
{
    /// <summary>The keys every record must hold, in the order the writer emits them.</summary>
    public static readonly IReadOnlyList<string> RequiredKeys =
        ["engine", "vblank", "cycles", "iStat", "iMask", "dicr", "gpuStat", "displayMode", "displayVramStart", "vramSha256"];

    private static readonly Regex HexWord = new("^0x[0-9A-F]{8}$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Hex = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    /// <summary>The fields a comparison looks at, in the order the first difference is reported. The engine label is not one.</summary>
    public IReadOnlyList<(string Name, string Value)> ComparedFields() =>
    [
        ("vblank", VBlank.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ("cycles", Cycles.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ("iStat", IStat),
        ("iMask", IMask),
        ("dicr", Dicr),
        ("gpuStat", GpuStat),
        ("displayMode", DisplayMode),
        ("displayVramStart", DisplayVramStart),
        ("vramSha256", VramSha256),
    ];

    /// <summary>
    /// Parses one JSON Lines record. Throws <see cref="BoundaryLogFormatException"/> with a diagnostic naming the problem when
    /// the line is not a boundary record: not valid JSON, not an object, a missing or extra key, or a value of the wrong type or format.
    /// </summary>
    public static OpenBiosProbeBoundaryRecord Parse(string line)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(line);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new BoundaryLogFormatException($"not valid JSON ({ex.Message})");
        }

        if (root is not JsonObject obj)
            throw new BoundaryLogFormatException($"expected a JSON object, got {DescribeKind(root)}");

        foreach (var key in RequiredKeys)
        {
            if (!obj.ContainsKey(key)) throw new BoundaryLogFormatException($"missing required field \"{key}\"");
        }
        foreach (var key in obj.Select(static p => p.Key))
        {
            if (!RequiredKeys.Contains(key)) throw new BoundaryLogFormatException($"unexpected field \"{key}\"");
        }

        return new OpenBiosProbeBoundaryRecord(
            Engine: ReadString(obj, "engine", nonEmpty: true),
            VBlank: ReadUInt64(obj, "vblank"),
            Cycles: ReadUInt64(obj, "cycles"),
            IStat: ReadHex(obj, "iStat"),
            IMask: ReadHex(obj, "iMask"),
            Dicr: ReadHex(obj, "dicr"),
            GpuStat: ReadHex(obj, "gpuStat"),
            DisplayMode: ReadHex(obj, "displayMode"),
            DisplayVramStart: ReadHex(obj, "displayVramStart"),
            VramSha256: ReadMatching(obj, "vramSha256", Sha256Hex, "64 lowercase hex digits"));
    }

    private static string ReadString(JsonObject obj, string key, bool nonEmpty)
    {
        if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && (!nonEmpty || text.Length > 0))
            return text;
        throw new BoundaryLogFormatException($"field \"{key}\" must be a non-empty string");
    }

    private static ulong ReadUInt64(JsonObject obj, string key)
    {
        // TryGetValue<ulong> accepts only a JSON number that is a non-negative integer; a string such as "1" is refused.
        if (obj[key] is JsonValue value && value.TryGetValue<ulong>(out var number))
            return number;
        throw new BoundaryLogFormatException($"field \"{key}\" must be a non-negative integer");
    }

    private static string ReadHex(JsonObject obj, string key) =>
        ReadMatching(obj, key, HexWord, "0x followed by 8 uppercase hex digits");

    private static string ReadMatching(JsonObject obj, string key, Regex pattern, string expected)
    {
        if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && pattern.IsMatch(text))
            return text;
        throw new BoundaryLogFormatException($"field \"{key}\" must be {expected}");
    }

    private static string DescribeKind(JsonNode? node) => node switch
    {
        null => "null",
        JsonArray => "a JSON array",
        JsonValue => "a JSON scalar",
        _ => "a non-object",
    };
}

/// <summary>A boundary log line that does not hold a valid boundary record. The message is the diagnostic; it names no raw data.</summary>
[Infrastructure]
internal sealed class BoundaryLogFormatException(string message) : Exception(message);
