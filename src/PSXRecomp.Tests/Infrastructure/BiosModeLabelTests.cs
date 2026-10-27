using FluentAssertions;
using PSXRecomp.Infrastructure.Standalone;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2 (MAJOR-1): the packaging-time declaration does not label a firmware run. The manifest carries the declaration and
/// reports the BIOS mode as classified at run time; HLE stays HLE.
/// </summary>
[Test]
public sealed class BiosModeLabelTests
{
    private static RuntimeHostManifest Manifest(bool guestFirmware, string? provenance = null) => new(
        RuntimeHostManifest.CurrentSchemaVersion, "../game", GuestFirmware: guestFirmware, MixedFallback: false, 0, 0,
        0x80010000u, string.Empty, [0x80010000u], 0x80010010u, 2, 1000, 0x80010000u, provenance);

    [Fact]
    public void FirmwareManifest_IsClassifiedAtRun_WhateverWasDeclared()
    {
        foreach (var declared in new[] { RuntimeHostManifest.BiosProvenanceUserSupplied, RuntimeHostManifest.BiosProvenanceSynthetic, null })
        {
            Manifest(guestFirmware: true, declared).BiosMode.Should().Be(RuntimeHostManifest.BiosModeClassifiedAtRun);
        }
    }

    [Fact]
    public void FirmwareManifest_RecordsTheDeclarationForComparisonOnly()
    {
        var text = RuntimeHostManifestJson.Serialize(Manifest(guestFirmware: true, RuntimeHostManifest.BiosProvenanceUserSupplied));

        text.Should().Contain("\"biosProvenance\": \"user-supplied\"");
        text.Should().NotContain("openbios-rom", "the packaging never writes a verified label");
    }

    [Fact]
    public void HleManifest_IsHle_AndNeverAFirmwareLabel()
    {
        var manifest = Manifest(guestFirmware: false);

        manifest.BiosMode.Should().Be(RuntimeHostManifest.BiosModeHle);
        RuntimeHostManifestJson.Serialize(manifest).Should().Contain("\"biosMode\": \"hle\"");
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
