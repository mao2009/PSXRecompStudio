using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using PSXRecomp.Infrastructure.RuntimeHost;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2 (R1 scope): the packaged-run routes (the game window's <see cref="PackagedArtifactLiveRunner"/> and the RuntimeHost
/// program) refuse a packaged image whose load range lies in the BIOS ROM, before anything starts. The package is synthetic
/// (a one-word image), and the BIOS is never read.
/// </summary>
[Test]
public sealed class BiosRangeRouteGuardTests
{
    private const uint BiosLoadAddress = 0xBFC00000u;

    /// <summary>Writes a distribution (artifact, runtime/manifest, runtime/image) whose image loads at <paramref name="imageLoadAddress"/>.</summary>
    private static string WriteDistribution(TempDirectory dir, uint imageLoadAddress)
    {
        dir.WriteFile("game", new byte[] { 0 });
        var image = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(image, 0u);
        dir.WriteFile(Path.Combine(RuntimeHostLayout.RuntimeDirectoryName, RuntimeHostLayout.ImageFileName), image);
        var manifest = RuntimeHostManifestJson.Serialize(new RuntimeHostManifest(
            RuntimeHostManifest.CurrentSchemaVersion, "../game", GuestFirmware: false, MixedFallback: false, 0, 0,
            imageLoadAddress, RuntimeHostLayout.ImageFileName, [imageLoadAddress], imageLoadAddress + 4, null, 1000, imageLoadAddress));
        return dir.WriteFile(
            Path.Combine(RuntimeHostLayout.RuntimeDirectoryName, RuntimeHostLayout.ManifestFileName),
            Encoding.UTF8.GetBytes(manifest));
    }

    [Fact]
    public void BiosRange_PackagedArtifactLiveRunner_ImageInBiosRange_IsRefusedBeforeAnythingStarts()
    {
        using var dir = new TempDirectory();
        var manifestPath = WriteDistribution(dir, BiosLoadAddress);

        Action act = () => _ = PackagedArtifactLiveRunner.RunAsync(manifestPath, new LiveFrameSlot());

        act.Should().Throw<InvalidDataException>().WithMessage("*loads into the BIOS range*");
    }

    [Fact]
    public void BiosRange_RuntimeHostProgram_ImageInBiosRange_IsAFailedRunWithTheRangeRefusal()
    {
        using var dir = new TempDirectory();
        var manifestPath = WriteDistribution(dir, BiosLoadAddress);

        var original = Console.Error;
        using var captured = new StringWriter();
        int code;
#pragma warning disable AARC003 // Test-layer capture of the host's diagnostic stream
        Console.SetError(captured);
        try
        {
            code = Program.Run([manifestPath], new FileSystemIdentityProbe());
        }
        finally
        {
            Console.SetError(original);
        }
#pragma warning restore AARC003

        code.Should().Be(78);
        captured.ToString().Should().Contain("loads into the BIOS range");
    }
}
