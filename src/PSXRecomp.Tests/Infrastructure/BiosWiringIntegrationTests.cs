using System.Runtime.InteropServices;
using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.E2E;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2 (MEDIUM-2): the production packaging path, not a copy of it, with each generation-stage guard's wiring disabled in turn.
/// The input is a firmware image whose manifest names a BIOS-range image (a <c>rom</c> image whose destination is the BIOS). Each
/// stage is load-bearing only when the stages before it are disabled too, so the table of tests shows which stage refuses.
/// </summary>
[Test]
public sealed class BiosWiringIntegrationTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>A reproducible ROM whose first words are valid MIPS (addiu, jr ra, nop); the rest is zero padding.</summary>
    private static byte[] CodeRom()
    {
        var bytes = new byte[OpenBiosFirmware.ImageSize];
        uint[] words = [0x24080001u, 0x03E00008u, 0u, 0x24090002u, 0x03E00008u, 0u];
        for (var i = 0; i < words.Length; i++)
        {
            BitConverter.GetBytes(words[i]).CopyTo(bytes, i * 4);
        }

        return bytes;
    }

    /// <summary>The production request over a tree whose manifest names a BIOS-destination image.</summary>
    private FirmwareDistributionRequest Request(string outputName)
    {
        var rom = _dir.WriteFile("openbios.bin", CodeRom());
        var aot = _dir.CreateSubdirectory("aot");
        _dir.WriteFile("aot/rom.roots", System.Text.Encoding.ASCII.GetBytes("0xBFC00000\n"));
        _dir.WriteFile("aot/roots-bios.txt", System.Text.Encoding.ASCII.GetBytes("0xBFC00000\n"));
        _dir.WriteFile("aot/images.txt", System.Text.Encoding.ASCII.GetBytes(
            "image bios-range 0xBFC00000 rom 0xBFC00000 0x40 roots-bios.txt\n"));
        var disc = _dir.WriteFile("disc.bin", new byte[2352]);
        return new FirmwareDistributionRequest(
            RomPath: rom,
            DiscPath: disc,
            AotDirectory: aot,
            OutputDirectory: _dir.Combine(outputName),
            CodeBytes: 64,
            RunTimeoutMs: 1000,
            SelfContained: false,
            BuildTimeout: TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void ProductionBuild_RefusesTheBiosRangeImage_AtTheManifestStage()
    {
        ((Action)(() => FirmwareDistributionBuilder.Build(Request("refused-manifest"), RuntimeInformation.IsOSPlatform(OSPlatform.Windows))))
            .Should().Throw<InvalidDataException>().WithMessage("*copies BIOS bytes*");
    }

    [Fact]
    public void WithTheManifestStageDisabled_ThePlanStageRefuses()
    {
        using var scope = BiosGuardWiring.DisableStages(BiosGuardWiring.Manifest);

        ((Action)(() => FirmwareDistributionBuilder.Build(Request("refused-plan"), RuntimeInformation.IsOSPlatform(OSPlatform.Windows))))
            .Should().Throw<InvalidOperationException>().WithMessage("*overlaps the BIOS*");
    }

    [Fact]
    public void WithTheManifestAndPlanStagesDisabled_TheDispatchStageRefuses()
    {
        using var scope = BiosGuardWiring.DisableStages(BiosGuardWiring.Manifest, BiosGuardWiring.Plan);

        ((Action)(() => FirmwareDistributionBuilder.Build(Request("refused-dispatch"), RuntimeInformation.IsOSPlatform(OSPlatform.Windows))))
            .Should().Throw<InvalidOperationException>().WithMessage("*overlaps the BIOS*");
    }

    [Fact]
    public void WithThreeStagesDisabled_TheSourceStageRefuses_OnTheGeneratedBlockName()
    {
        using var scope = BiosGuardWiring.DisableStages(BiosGuardWiring.Manifest, BiosGuardWiring.Plan, BiosGuardWiring.Dispatch);

        ((Action)(() => FirmwareDistributionBuilder.Build(Request("refused-source"), RuntimeInformation.IsOSPlatform(OSPlatform.Windows))))
            .Should().Throw<InvalidOperationException>().WithMessage("*names a block at 0xBFC00000*");
    }

    /// <summary>
    /// The load-bearing proof: with every stage disabled the production path builds an artifact whose generated C names a block at
    /// the BIOS address. So the four refusals above are what keeps BIOS code out, not an accident of the input.
    /// </summary>
    [SkippableFact]
    public void WithAllFourStagesDisabled_TheBiosBlockReachesTheGeneratedSource()
    {
        RuntimeHostPublishE2E.RequireGcc();
        using var scope = BiosGuardWiring.DisableStages(
            BiosGuardWiring.Manifest, BiosGuardWiring.Plan, BiosGuardWiring.Dispatch, BiosGuardWiring.Source);
        var request = Request("unguarded");

        var artifact = FirmwareDistributionBuilder.Build(request, RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        artifact.Should().NotBeNullOrEmpty();
#pragma warning disable AARC003 // Test-layer inspection of the generated C that the unguarded build wrote
        var source = string.Concat(Directory.EnumerateFiles(request.OutputDirectory, "*.c").Select(File.ReadAllText));
#pragma warning restore AARC003
        source.Should().Contain("recompiler_block_0xBFC00000", "with the guards off, the BIOS block reaches the generated source");
    }
}
