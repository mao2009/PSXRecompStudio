using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime.CdRom;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2 (owner ruling, option 3): the BIOS is never an input of ahead-of-time code generation. Each stage the code passes
/// through (manifest, plan, dispatch, generated C) refuses BIOS code, and each stage has a negative test that injects BIOS code
/// at that stage's entry and expects the refusal.
/// </summary>
[Test]
public sealed class BiosGenerationStageTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static uint[] BiosWords(int count, uint seed)
    {
        // Valid MIPS code in the shape of a routine: addiu words, a jr $ra with its delay slot, repeated from the seed.
        var words = new uint[count];
        for (var i = 0; i < count; i++)
        {
            words[i] = (i % 4) switch
            {
                0 => 0x24080000u | ((seed + (uint)i) & 0xFFFFu),
                1 => 0x24090000u | (uint)i,
                2 => 0x03E00008u,
                _ => 0u,
            };
        }

        return words;
    }

    [Theory]
    [InlineData(0xBFC00000u)]
    [InlineData(0x9FC00000u)]
    [InlineData(0x1FC00000u)]
    [InlineData(0xBFC7FFFCu)]
    [InlineData(0x9FC00100u)]
    public void EveryCpuAliasOfTheBios_IsBiosAddress(uint address) =>
        BiosAddressGuard.IsBiosAddress(address).Should().BeTrue($"0x{address:X8} aliases the BIOS ROM");

    [Theory]
    [InlineData(0x80010000u)]
    [InlineData(0xBFC80000u)]
    [InlineData(0x1FBFFFFCu)]
    [InlineData(0x00000500u)]
    public void RamAndTheAddressesJustOutsideTheBios_AreNotBiosAddresses(uint address) =>
        BiosAddressGuard.IsBiosAddress(address).Should().BeFalse($"0x{address:X8} is not in the BIOS ROM");

    [Fact]
    public void AManifestRomImage_IsRefused_BecauseItIsBiosDerivedCode()
    {
        var rom = new byte[OpenBiosFirmware.ImageSize];
        var firmware = OpenBiosFirmware.FromBytes(rom);
        _dir.WriteFile("aot/rom.roots", System.Text.Encoding.ASCII.GetBytes("0xBFC00000\n"));
        _dir.WriteFile("aot/images.txt", System.Text.Encoding.ASCII.GetBytes("image bios-copy 0x00000500 rom 0xBFC00000 0x40\n"));

        ((Action)(() => FirmwareAotPlan.Build(
            firmware, _dir.Combine("aot", "rom.roots"), 64, _dir.Combine("aot", "images.txt"), new RawCdSectorSource(new byte[2352]))))
            .Should().Throw<InvalidDataException>().WithMessage("*BIOS-derived*");
    }

    [Fact]
    public void ManifestStage_InjectedBiosRangeImage_IsRefused()
    {
        var injected = new LoadImageManifest(
            [new LoadedImageSpec("injected", OpenBiosFirmware.ResetVector, BiosWords(4, seed: 1), [], "exe-file")],
            new HashSet<uint>());

        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(injected)))
            .Should().Throw<InvalidDataException>().WithMessage("*loads into the BIOS range*");
    }

    [Fact]
    public void PlanStage_InjectedBiosBlock_IsRefused()
    {
        var words = BiosWords(16, seed: 2);
        var injected = ReachableProgramBuilder.BuildFirmwareImage(
            OpenBiosFirmware.ResetVector, words, OpenBiosFirmware.ResetVector, [OpenBiosFirmware.ResetVector]).Program;
        injected.Blocks.Should().NotBeEmpty("the injection carries real BIOS blocks");

        ((Action)(() => BiosAddressGuard.RequireNoBiosCode(injected, LoadedCodeTable.Empty)))
            .Should().Throw<InvalidOperationException>().WithMessage("*lies in the BIOS*");
    }

    [Fact]
    public void LoadedStage_InjectedBiosRangeCode_IsRefused()
    {
        var observed = new HashSet<uint>();
        var spec = new LoadedImageSpec("injected", OpenBiosFirmware.ResetVector, BiosWords(16, seed: 3), [OpenBiosFirmware.ResetVector], "exe-file");
        var block = ReachableProgramBuilder.BuildLoadedImage(spec.LoadAddress, spec.Words, spec.Roots, observed) with { Origin = spec.Origin };
        var loaded = new LoadedCodeTable([block]);
        loaded.Blocks.Should().NotBeEmpty("the injection carries a real loaded block in the BIOS range");

        ((Action)(() => BiosAddressGuard.RequireNoBiosCode(new RecompilerIrProgram(Array.Empty<RecompilerIrBlock>()), loaded)))
            .Should().Throw<InvalidOperationException>().WithMessage("*overlaps the BIOS*");
    }

    [Fact]
    public void SourceStage_InjectedBiosBlockName_IsRefused_AndACleanSourceIsNot()
    {
        ((Action)(() => BiosAddressGuard.RequireNoBiosSource("void recompiler_block_0xBFC00000(void) {}")))
            .Should().Throw<InvalidOperationException>().WithMessage("*BIOS address*");
        ((Action)(() => BiosAddressGuard.RequireNoBiosSource("void recompiler_block_0x80010000(void) {}")))
            .Should().NotThrow("a game block is a legitimate translation target");
    }
}
