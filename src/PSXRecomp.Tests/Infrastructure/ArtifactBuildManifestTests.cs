using System.Security.Cryptography;
using System.Text.Json;
using PSXRecomp.Architecture;
using PSXRecomp.Infrastructure.Standalone;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>Issue #864: the written carrier's digests match the bytes on disk; firmware has no sidecar block. Synthetic bytes only.</summary>
[Test]
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
}
