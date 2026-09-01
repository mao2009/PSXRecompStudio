using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PSXRecomp.Architecture;

namespace PSXRecomp.Core.DiscImage.AnalysisArtifacts;

/// <summary>
/// Canonical JSON encoding for deterministic analysis artifacts.
///
/// "Canonical" here means: the same document value always produces the same byte
/// sequence, on any machine, under any locale, on any operating system. Three
/// properties are load-bearing and must not be relaxed:
///
/// <list type="bullet">
///   <item>Line endings are normalized to LF. <c>System.Text.Json</c>'s indenting
///   writer defaults to the platform newline, which would make artifacts written on
///   Windows differ byte-for-byte from artifacts written on Linux for identical input.</item>
///   <item>Null-valued properties are written explicitly rather than omitted, so a
///   document's key set depends only on its schema version and never on its data.
///   Textual diffs between two fixtures then line up field-for-field.</item>
///   <item>Property order is the declaration order of the document records, and the
///   naming policy is camelCase. Both are pinned by a golden-text test.</item>
/// </list>
///
/// Serialization is pure: no timestamp, path, host, user or environment value can
/// enter an artifact through this type. The Domain-layer architecture rules
/// (PSXR005) enforce that mechanically for the whole namespace.
/// </summary>
[Domain]
public static class ArtifactJson
{
    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// UTF-8 without a byte-order mark. Artifacts are compared byte-for-byte, so the
    /// BOM must never be emitted.
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Serializes a document to canonical artifact text: camelCase keys, two-space
    /// indentation, LF line endings, and a single trailing LF so the file is
    /// POSIX-clean and diff tools do not report a missing final newline.
    /// </summary>
    public static string Serialize<T>(T document)
    {
        var json = JsonSerializer.Serialize(document, CanonicalOptions);
        return NormalizeNewLines(json) + "\n";
    }

    /// <summary>
    /// Encodes canonical artifact text as the exact bytes that must be written to disk
    /// (UTF-8, no BOM). This is the unit of comparison for "byte-for-byte identical".
    /// </summary>
    public static byte[] ToUtf8Bytes(string canonicalJson)
    {
        return Utf8NoBom.GetBytes(canonicalJson);
    }

    /// <summary>
    /// Lowercase hex SHA-256 of the canonical UTF-8 bytes of an artifact document.
    /// Used by <c>manifest.json</c> to reference the detailed documents; a document
    /// never hashes itself, so no artifact hash is self-referential.
    /// </summary>
    public static string Sha256Hex(ReadOnlySpan<byte> content)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(content, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeNewLines(string text)
    {
        return text.Contains('\r', StringComparison.Ordinal)
            ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal)
            : text;
    }
}
