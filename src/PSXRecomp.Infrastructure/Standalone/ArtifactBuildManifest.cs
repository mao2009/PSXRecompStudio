using System.Security.Cryptography;
using System.Text.Json;

using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// Issue #864 (writer, schema 1, minimal scope): writes <c>runtime/build-manifest.json</c>
/// carrying post-build digests. H2 (<c>binary.sha256</c>) is recorded for every packaged
/// build; H3 (<c>packaged.imageSidecar.sha256</c>) is recorded for HLE packages only.
/// Digests are computed from the bytes as written, never from a pre-write buffer held by
/// the caller. They are post-build outputs and are not part of any build key. The packaged
/// host must treat this carrier as read-only (Issue #865 owns the reader).
/// Full schema-1 fields owned by Issue C (buildKey, codegen hashes, toolchain, etc.) are
/// intentionally unwritten here; see HOLD note in the Issue #864 report. This writer must
/// not be extended unilaterally to finalize the contract.
/// </summary>
[Infrastructure]
public static class ArtifactBuildManifest
{
    public const string FileName = "build-manifest.json";
    public const int SchemaVersion = 1;

    public static string ComputeSha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Writes an HLE carrier (with <c>packaged</c> block) as the last file in staging.</summary>
    public static void WriteHle(string runtimeDir, string binaryFileName, byte[] artifactBytes, byte[] sidecarBytes)
    {
        File.WriteAllBytes(
            Path.Combine(runtimeDir, FileName),
            SerializeHle(binaryFileName, artifactBytes, sidecarBytes));
    }

    /// <summary>Writes a firmware carrier (H2 only, no <c>packaged</c> block).</summary>
    public static void WriteFirmware(string runtimeDir, string binaryFileName, byte[] artifactBytes)
    {
        File.WriteAllBytes(
            Path.Combine(runtimeDir, FileName),
            SerializeFirmware(binaryFileName, artifactBytes));
    }

    internal static byte[] SerializeHle(string binaryFileName, byte[] artifactBytes, byte[] sidecarBytes) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = SchemaVersion,
            binary = new { file = binaryFileName, sha256 = ComputeSha256Hex(artifactBytes), sizeBytes = artifactBytes.Length },
            packaged = new { imageSidecar = new { file = RuntimeHostLayout.ImageFileName, sha256 = ComputeSha256Hex(sidecarBytes), sizeBytes = sidecarBytes.Length } },
        });

    internal static byte[] SerializeFirmware(string binaryFileName, byte[] artifactBytes) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = SchemaVersion,
            binary = new { file = binaryFileName, sha256 = ComputeSha256Hex(artifactBytes), sizeBytes = artifactBytes.Length },
        });
}
