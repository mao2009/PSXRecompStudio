using System.Text.Json.Nodes;
using FluentAssertions;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2: the packaging-time BIOS declaration is a recorded label only. With no declaration it is null (no warning); an
/// explicit declaration is kept as written; the run label is never overwritten by the declaration; and manifests with the label
/// absent, null or synthetic still load.
/// </summary>
[Test]
public sealed class BiosProvenanceDeclarationTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static FirmwareDistributionRequest Request(string? provenance = null) => new(
        "bios.bin", "disc.bin", "aot", "out", 37160u, 1000, SelfContained: false, BiosProvenance: provenance);

    // The public package path: the request is built from the command's options, not from a caller-supplied null.
    private static FirmwareDistributionRequest PublicPathRequest(string? provenance) => FirmwarePackageCommand.BuildRequest(
        new FirmwareOptions("openbios", "bios.bin", "aot", CodeBytes: null, RunTimeoutSeconds: null, BiosProvenance: provenance),
        "disc.bin", "out", selfContained: false, mixedFallback: false, fallbackSegmentBudget: null, fallbackMaxTransitions: null);

    private static RuntimeHostManifest FirmwareManifest(string? provenance) => new(
        RuntimeHostManifest.CurrentSchemaVersion, "../game", GuestFirmware: true, MixedFallback: false,
        FallbackSegmentInstructionBudget: 0, FallbackMaxTransitions: 0, ImageLoadAddress: 0x80010000u, Image: string.Empty,
        BlockEntryPcs: [0x80010000u], ProgramEnd: 0x80010010u, ResultRegister: 2, RunTimeoutMs: 1000, EntryPc: 0x80010000u,
        BiosProvenance: provenance);

    [Fact]
    public void SyntheticDeclaredButUnverified_ProducesTheWarning()
    {
        var warning = BiosProvenance.DeclarationWarning(RuntimeHostManifest.BiosProvenanceSynthetic, BiosProvenance.Unverified);

        warning.Should().NotBeNull("a synthetic declaration is not confirmed by an unverified image");
        warning.Should().Contain("warning");
        warning.Should().Contain("'synthetic'");
    }

    [Fact]
    public void NoDeclaration_ProducesNoWarning_WhateverTheRunLabel()
    {
        foreach (var label in new[] { BiosProvenance.Verified, BiosProvenance.Synthetic, BiosProvenance.Unverified })
        {
            BiosProvenance.DeclarationWarning(null, label).Should().BeNull("no declaration means nothing to confirm");
        }
    }

    [Fact]
    public void DefaultPackagingDeclaration_IsNull()
    {
        Request().BiosProvenance.Should().BeNull("no --bios-provenance means no recorded declaration");
    }

    [Fact]
    public void PublicPackagePath_WithoutProvenanceOption_RecordsNull()
    {
        PublicPathRequest(null).BiosProvenance.Should().BeNull("no --bios-provenance on the public path means no recorded declaration");
    }

    [Fact]
    public void RequestRecord_DefaultDeclaration_IsNull()
    {
        new FirmwareDistributionRequest("bios.bin", "disc.bin", "aot", "out", 37160u, 1000, SelfContained: false)
            .BiosProvenance.Should().BeNull("the record default must not be synthetic");
    }

    [Theory]
    [InlineData("user-supplied")]
    [InlineData("synthetic")]
    public void ExplicitPackagingDeclaration_IsKeptUnchanged(string declared)
    {
        PublicPathRequest(declared).BiosProvenance.Should().Be(declared);
        Request(declared).BiosProvenance.Should().Be(declared);
    }

    [Fact]
    public void RunLabel_IsNotOverwrittenByAnExplicitSyntheticDeclaration()
    {
        // A registered digest (here the verified allowlist) keeps its label even when the declaration says synthetic.
        // Deterministic content (the Test layer forbids Random): any fixed, non-trivial pattern works, since the digest is taken from it.
        var bytes = new byte[512 * 1024];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 31 + 7 + i / 251);
        var path = _dir.WriteFile("bios.bin", bytes);
        var sha = OpenBiosRomInput.Sha256Hex(bytes);
        var error = new StringWriter();

        var (_, _, label) = FirmwareRuntimeLauncher.LoadAndClassify(
            path, RuntimeHostManifest.BiosProvenanceSynthetic, error,
            verified: new HashSet<string> { sha }, synthetic: new HashSet<string>());

        label.Should().Be(BiosProvenance.Verified, "the run classifies the digest; the declaration is not evidence");
        error.ToString().Should().Contain("warning", "the declaration is not confirmed, so the mismatch is reported");
    }

    [Fact]
    public void ManifestWithoutBiosProvenance_StillLoads()
    {
        var node = JsonNode.Parse(RuntimeHostManifestJson.Serialize(FirmwareManifest(null)))!.AsObject();
        node.Remove("biosProvenance");

        var parsed = RuntimeHostManifestJson.Parse(node.ToJsonString());

        parsed.GuestFirmware.Should().BeTrue();
        parsed.BiosProvenance.Should().BeNull();
        parsed.BiosMode.Should().Be(RuntimeHostManifest.BiosModeClassifiedAtRun);
    }

    [Fact]
    public void ManifestWithNullBiosProvenance_StillLoads()
    {
        var parsed = RuntimeHostManifestJson.Parse(RuntimeHostManifestJson.Serialize(FirmwareManifest(null)));

        parsed.GuestFirmware.Should().BeTrue();
        parsed.BiosProvenance.Should().BeNull();
    }

    [Fact]
    public void OlderManifestWithSyntheticBiosProvenance_StillLoads()
    {
        var node = JsonNode.Parse(RuntimeHostManifestJson.Serialize(FirmwareManifest(null)))!.AsObject();
        node["biosProvenance"] = RuntimeHostManifest.BiosProvenanceSynthetic;

        var parsed = RuntimeHostManifestJson.Parse(node.ToJsonString());

        parsed.GuestFirmware.Should().BeTrue();
        parsed.BiosProvenance.Should().Be("synthetic", "the label is read back as recorded");
    }
}
