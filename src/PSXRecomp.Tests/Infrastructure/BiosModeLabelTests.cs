using FluentAssertions;
using PSXRecomp.Infrastructure.Standalone;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2: an HLE run, a synthetic firmware run and a real-ROM OpenBIOS run are separate, labelled modes in the
/// runtime manifest. <c>openbios-rom</c> is only ever written for a user-supplied BIOS.
/// </summary>
[Test]
public sealed class BiosModeLabelTests
{
    private static RuntimeHostManifest Manifest(bool guestFirmware, string? provenance = null) => new(
        RuntimeHostManifest.CurrentSchemaVersion, "../game", GuestFirmware: guestFirmware, MixedFallback: false, 0, 0,
        0x80010000u, string.Empty, [0x80010000u], 0x80010010u, 2, 1000, 0x80010000u, provenance);

    [Fact]
    public void UserSuppliedFirmwareManifest_IsLabelledOpenBiosRom_AndNotHle()
    {
        var manifest = Manifest(guestFirmware: true, RuntimeHostManifest.BiosProvenanceUserSupplied);
        manifest.BiosMode.Should().Be("openbios-rom");
        RuntimeHostManifestJson.Serialize(manifest).Should().Contain("\"biosMode\": \"openbios-rom\"");
    }

    [Fact]
    public void SyntheticFirmwareManifest_IsLabelledOpenBiosSynthetic_NeverOpenBiosRom()
    {
        var manifest = Manifest(guestFirmware: true, RuntimeHostManifest.BiosProvenanceSynthetic);
        manifest.BiosMode.Should().Be("openbios-synthetic");
        RuntimeHostManifestJson.Serialize(manifest).Should().Contain("\"biosMode\": \"openbios-synthetic\"")
            .And.NotContain("openbios-rom");
    }

    [Fact]
    public void FirmwareManifestWithoutProvenance_IsNeverLabelledOpenBiosRom()
    {
        Manifest(guestFirmware: true).BiosMode.Should().Be("openbios-synthetic",
            "only an explicitly user-supplied image may claim the real-ROM label");
    }

    [Fact]
    public void HleManifest_IsLabelledHle_AndNeverOpenBios()
    {
        var manifest = Manifest(guestFirmware: false);
        manifest.BiosMode.Should().Be("hle");
        RuntimeHostManifestJson.Serialize(manifest).Should().Contain("\"biosMode\": \"hle\"")
            .And.NotContain("openbios");
    }

    [Fact]
    public void BiosModeLabel_DoesNotChangeParsing()
    {
        var text = RuntimeHostManifestJson.Serialize(Manifest(guestFirmware: true, RuntimeHostManifest.BiosProvenanceSynthetic));
        RuntimeHostManifestJson.Parse(text).GuestFirmware.Should().BeTrue();
    }

    [Fact]
    public void FirmwareManifest_MayOmitTheImageSidecar()
    {
        RuntimeHostManifestJson.Parse(RuntimeHostManifestJson.Serialize(Manifest(guestFirmware: true, RuntimeHostManifest.BiosProvenanceUserSupplied)))
            .Image.Should().BeEmpty("the BIOS is supplied at run time, so no sidecar is named");
    }
}
