using System.Security.Cryptography;
using System.Text.Json;
using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// Fail-closed verifier for packaged host inputs H2 (game artifact) and H3
/// (program image sidecar) against the <c>runtime/build-manifest.json</c>
/// carrier (Issue #865, W12 §3.4). The carrier is read-only; this type never
/// writes it. Every failure throws <see cref="InvalidDataException"/> so the
/// caller runs no guest code.
/// </summary>
[Infrastructure]
public static class PackagedDigestVerifier
{
    public const string CarrierFileName = "build-manifest.json";

    /// <summary>
    /// The verified inputs to use. Owns the private-copy directory and
    /// deletes it on <see cref="Dispose"/> (F2): callers must dispose after
    /// the launched copy is no longer needed (when the run returns).
    /// </summary>
    public sealed class VerifiedInputs : IDisposable
    {
        public VerifiedInputs(string artifactLaunchPath, byte[] imageBytes, string privateCopyDirectory)
        {
            ArtifactLaunchPath = artifactLaunchPath;
            ImageBytes = imageBytes;
            PrivateCopyDirectory = privateCopyDirectory;
        }

        public string ArtifactLaunchPath { get; }
        public byte[] ImageBytes { get; }
        public string PrivateCopyDirectory { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(PrivateCopyDirectory))
                {
                    Directory.Delete(PrivateCopyDirectory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup: never fail the run on delete.
            }
        }
    }

    /// <summary>
    /// Verifies H2 (every package) and H3 (HLE only) and returns the bytes to
    /// use: a private copy of the artifact (launched path) and the single
    /// image byte array the hash was computed over. The original artifact path
    /// is never returned, so a post-verify swap of the original cannot change
    /// the launched bytes.
    /// </summary>
    public static VerifiedInputs VerifyAndStage(
        string manifestDirectory,
        string artifactPath,
        string? imagePath,
        bool isFirmware,
        string? privateCopyRoot = null)
    {
        var carrierPath = Path.Combine(Path.GetFullPath(manifestDirectory), CarrierFileName);
        string carrierText;
        try
        {
            carrierText = File.ReadAllText(carrierPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Packaged digest carrier missing or unreadable: {carrierPath}", ex);
        }

        var (expectedArtifactHex, expectedImageHex) = ParseCarrier(carrierText, artifactPath, imagePath, isFirmware);

        // H2: copy original -> private dir first, then hash the copy through a
        // FileShare.Read handle and launch the copy by path. Verified bytes ==
        // used bytes; replacing the original afterwards changes nothing.
        var copyDir = Directory.CreateDirectory(Path.Combine(
            privateCopyRoot ?? Path.GetTempPath(),
            "psxrecomp-h2-" + Guid.NewGuid().ToString("N"))).FullName;
        var copyPath = Path.Combine(copyDir, Path.GetFileName(artifactPath));
        File.Copy(artifactPath, copyPath);
        string actualArtifactHex;
        using (var handle = File.Open(copyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            actualArtifactHex = Convert.ToHexString(SHA256.HashData(ReadAll(handle))).ToLowerInvariant();
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedArtifactHex), Convert.FromHexString(actualArtifactHex)))
        {
            DeleteQuietly(copyDir);
            throw new InvalidDataException("Packaged artifact digest mismatch (H2).");
        }

        byte[] imageBytes = [];
        if (!isFirmware)
        {
            if (imagePath is null)
            {
                throw new InvalidDataException("HLE package requires the H3 image sidecar.");
            }

            // H3: single read; the hashed array is the array returned for use.
            using (var handle = File.Open(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                imageBytes = ReadAll(handle);
            }

            var actualImageHex = Convert.ToHexString(SHA256.HashData(imageBytes)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(expectedImageHex!), Convert.FromHexString(actualImageHex)))
            {
                DeleteQuietly(copyDir);
                throw new InvalidDataException("Packaged image digest mismatch (H3).");
            }
        }

        return new VerifiedInputs(copyPath, imageBytes, copyDir);
    }

    internal static (string ArtifactHex, string? ImageHex) ParseCarrier(
        string json, string artifactPath, string? imagePath, bool isFirmware)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Packaged digest carrier is not valid JSON.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Packaged digest carrier root must be an object.");
            }

            if (!root.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var v) || v != 1)
            {
                throw new InvalidDataException("Packaged digest carrier must have schemaVersion 1.");
            }

            RejectUnknownKeys(root, ["schemaVersion", "binary", "packaged"], "carrier");
            if (!root.TryGetProperty("binary", out var binary) || binary.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Packaged digest carrier is missing required 'binary'.");
            }

            RejectUnknownKeys(binary, ["file", "sha256", "sizeBytes"], "binary");
            var binaryFile = RequiredString(binary, "file", "binary");
            var binaryHex = RequiredHexDigest(binary, "sha256", "binary");
            AssertBareFileName(binaryFile, "binary.file");
            if (!string.Equals(binaryFile, Path.GetFileName(artifactPath), StringComparison.Ordinal))
            {
                throw new InvalidDataException("Carrier binary.file does not name the manifest artifact.");
            }

            var hasPackaged = root.TryGetProperty("packaged", out var packaged);
            if (isFirmware)
            {
                if (hasPackaged && packaged.ValueKind != JsonValueKind.Null)
                {
                    throw new InvalidDataException("Firmware carrier must not contain a 'packaged' block.");
                }

                return (binaryHex, null);
            }

            if (!hasPackaged || packaged.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("HLE carrier is missing required 'packaged.imageSidecar'.");
            }

            RejectUnknownKeys(packaged, ["imageSidecar"], "packaged");
            if (!packaged.TryGetProperty("imageSidecar", out var sidecar) || sidecar.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("HLE carrier is missing required 'packaged.imageSidecar'.");
            }

            RejectUnknownKeys(sidecar, ["file", "sha256", "sizeBytes"], "packaged.imageSidecar");
            var sidecarFile = RequiredString(sidecar, "file", "packaged.imageSidecar");
            var sidecarHex = RequiredHexDigest(sidecar, "sha256", "packaged.imageSidecar");
            AssertBareFileName(sidecarFile, "packaged.imageSidecar.file");
            if (imagePath is null || !string.Equals(sidecarFile, Path.GetFileName(imagePath), StringComparison.Ordinal))
            {
                throw new InvalidDataException("Carrier packaged.imageSidecar.file does not name the manifest image.");
            }

            return (binaryHex, sidecarHex);
        }
    }

    private static void RejectUnknownKeys(JsonElement obj, string[] allowed, string what)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (!allowed.Contains(prop.Name, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"Packaged digest carrier has unknown key '{prop.Name}' in {what}.");
            }
        }
    }

    private static string RequiredString(JsonElement obj, string name, string what)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Packaged digest carrier is missing required '{what}.{name}'.");
        }

        return el.GetString()!;
    }

    private static string RequiredHexDigest(JsonElement obj, string name, string what)
    {
        var value = RequiredString(obj, name, what);
        if (value.Length != 64 || !value.All(c => (c is >= '0' and <= '9') || (c is >= 'a' and <= 'f')))
        {
            throw new InvalidDataException($"Packaged digest carrier '{what}.{name}' must be 64 lowercase hex characters.");
        }

        return value;
    }

    private static void AssertBareFileName(string name, string what)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') ||
            name.Contains("..") || Path.IsPathRooted(name) || name.Contains(':'))
        {
            throw new InvalidDataException($"Packaged digest carrier '{what}' must be a bare file name.");
        }
    }

    private static void DeleteQuietly(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
