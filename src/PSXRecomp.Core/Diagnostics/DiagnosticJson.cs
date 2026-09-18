using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.Diagnostics;

/// <summary>
/// Canonical JSON encoding for diagnostics, following the same determinism
/// contract as <c>ArtifactJson</c> for analysis artifacts: camelCase keys,
/// two-space indentation, LF line endings, a single trailing LF, and a key set
/// that depends only on the schema (nulls are written, never omitted). Enum
/// values and <see cref="DiagnosticCode"/> serialize as their stable string
/// representations. No timestamp, host name or absolute path can enter through
/// the model, so the same problem always yields the same document.
/// </summary>
[Domain]
public static class DiagnosticJson
{
    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Serializes a diagnostic document to deterministic JSON text.</summary>
    public static string Serialize<T>(T document)
    {
        var json = JsonSerializer.Serialize(document, CanonicalOptions);
        return NormalizeNewLines(json) + "\n";
    }

    /// <summary>Deserializes a diagnostic document from its JSON text.</summary>
    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(NormalizeNewLines(json).TrimEnd('\n'), CanonicalOptions)
        ?? throw new InvalidOperationException($"Deserializing '{typeof(T).Name}' produced null.");

    /// <summary>Encodes deterministic JSON text as the exact UTF-8 bytes (no BOM) to write.</summary>
    public static byte[] ToUtf8Bytes(string canonicalJson)
    {
        return Utf8NoBom.GetBytes(canonicalJson);
    }

    private static string NormalizeNewLines(string text)
    {
        return text.Contains('\r', StringComparison.Ordinal)
            ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal)
            : text;
    }
}