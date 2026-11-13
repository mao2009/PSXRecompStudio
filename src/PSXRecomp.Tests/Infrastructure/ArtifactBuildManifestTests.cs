using System.Security.Cryptography;
using System.Text.Json;
using PSXRecomp.Architecture;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>Issue #864: the written carrier's digests match the bytes on disk; firmware has no sidecar block. Synthetic bytes only.</summary>
[Test]
#pragma warning disable AARC003 // the packaging contract is files on disk; the test stages synthetic files and reads the carrier back
public sealed class ArtifactBuildManifestTests
{
    [Fact]
    public void HleCarrierDigestsMatchWrittenBytes()
    {
        var artifactBytes = new byte[] { 1, 2, 3, 4 };
        var sidecarBytes = new byte[] { 5, 6, 7 };
        using var doc = JsonDocument.Parse(ArtifactBuildManifest.SerializeHle("game.exe", artifactBytes, sidecarBytes));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(artifactBytes)).ToLowerInvariant(),
            root.GetProperty("binary").GetProperty("sha256").GetString());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(sidecarBytes)).ToLowerInvariant(),
            root.GetProperty("packaged").GetProperty("imageSidecar").GetProperty("sha256").GetString());
        Assert.Equal("artifact-image.bin",
            root.GetProperty("packaged").GetProperty("imageSidecar").GetProperty("file").GetString());
    }

    [Fact]
    public void FirmwareCarrierHasNoPackagedBlock()
    {
        using var doc = JsonDocument.Parse(ArtifactBuildManifest.SerializeFirmware("firmware", new byte[] { 9, 8 }));
        Assert.False(doc.RootElement.TryGetProperty("packaged", out _));
    }

    [Fact]
    public void HleDiskRoundTrip_DigestsMatchFileContent()
    {
        using var dir = new TempDirectory();
        var artifactBytes = new byte[] { 10, 20, 30, 40, 50 };
        var sidecarBytes = new byte[] { 60, 70, 80 };
        var artifactPath = dir.WriteFile("game.exe", artifactBytes);
        var runtimeDir = dir.CreateSubdirectory("runtime");
        dir.WriteFile("runtime/artifact-image.bin", sidecarBytes);

        ArtifactBuildManifest.WriteHle(runtimeDir, artifactPath, dir.Combine("runtime", "artifact-image.bin"));

        using var doc = JsonDocument.Parse(File.ReadAllBytes(dir.Combine("runtime", ArtifactBuildManifest.FileName)));
        var root = doc.RootElement;
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifactPath))).ToLowerInvariant(),
            root.GetProperty("binary").GetProperty("sha256").GetString());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dir.Combine("runtime", "artifact-image.bin")))).ToLowerInvariant(),
            root.GetProperty("packaged").GetProperty("imageSidecar").GetProperty("sha256").GetString());
        Assert.Equal(artifactBytes.Length, root.GetProperty("binary").GetProperty("sizeBytes").GetInt32());
    }

    [Fact]
    public void FirmwareDiskRoundTrip_HasNoPackagedBlock()
    {
        using var dir = new TempDirectory();
        var artifactPath = dir.WriteFile("firmware", new byte[] { 9, 8, 7 });
        var runtimeDir = dir.CreateSubdirectory("runtime");

        ArtifactBuildManifest.WriteFirmware(runtimeDir, artifactPath);

        using var doc = JsonDocument.Parse(File.ReadAllBytes(dir.Combine("runtime", ArtifactBuildManifest.FileName)));
        Assert.False(doc.RootElement.TryGetProperty("packaged", out _));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifactPath))).ToLowerInvariant(),
            doc.RootElement.GetProperty("binary").GetProperty("sha256").GetString());
    }
}
