using FluentAssertions;
using PSXRecomp.Infrastructure.Standalone;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2: an HLE BIOS run and a real-ROM OpenBIOS run are separate, labelled modes in the runtime manifest.
/// The label is derived from <c>GuestFirmware</c>, so the two can never be written with the same mode.
/// </summary>
[Test]
public sealed class BiosModeLabelTests
{
    private static RuntimeHostManifest Manifest(bool guestFirmware) => new(
        RuntimeHostManifest.CurrentSchemaVersion, "../game", GuestFirmware: guestFirmware, MixedFallback: false, 0, 0,
        0x80010000u, RuntimeHostLayout.ImageFileName, [0x80010000u], 0x80010010u, 2, 1000, 0x80010000u);

    [Fact]
    public void OpenBiosFirmwareManifest_IsLabelledOpenBiosRom_AndNotHle()
    {
        var manifest = Manifest(guestFirmware: true);
        manifest.BiosMode.Should().Be("openbios-rom");
        RuntimeHostManifestJson.Serialize(manifest).Should().Contain("\"biosMode\": \"openbios-rom\"");
    }

    [Fact]
    public void HleManifest_IsLabelledHle_AndNeverOpenBiosRom()
    {
        var manifest = Manifest(guestFirmware: false);
        manifest.BiosMode.Should().Be("hle");
        RuntimeHostManifestJson.Serialize(manifest).Should().Contain("\"biosMode\": \"hle\"")
            .And.NotContain("openbios-rom");
    }

    [Fact]
    public void BiosModeLabel_DoesNotChangeParsing()
    {
        var text = RuntimeHostManifestJson.Serialize(Manifest(guestFirmware: true));
        RuntimeHostManifestJson.Parse(text).GuestFirmware.Should().BeTrue();
    }
}
