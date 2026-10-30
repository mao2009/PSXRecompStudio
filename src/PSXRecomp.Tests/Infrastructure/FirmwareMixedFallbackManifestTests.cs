using FluentAssertions;
using PSXRecomp.Infrastructure.Standalone;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2 (review LOW-7): the BIOS is never ahead-of-time translated, so a guest-firmware manifest may run mixed fallback
/// with no block entry at all. HLE games keep the requirement: their bridge needs a populated table.
/// </summary>
[Test]
public sealed class FirmwareMixedFallbackManifestTests
{
    private static string Manifest(bool guestFirmware, uint[] blockEntries) => RuntimeHostManifestJson.Serialize(new RuntimeHostManifest(
        RuntimeHostManifest.CurrentSchemaVersion, "../game", GuestFirmware: guestFirmware, MixedFallback: true,
        FallbackSegmentInstructionBudget: 4000, FallbackMaxTransitions: 40, ImageLoadAddress: 0xBFC00000u,
        Image: guestFirmware ? string.Empty : RuntimeHostLayout.ImageFileName, BlockEntryPcs: blockEntries, ProgramEnd: 0xBFC80000u, ResultRegister: null,
        RunTimeoutMs: 1000, EntryPc: 0xBFC00000u, BiosProvenance: RuntimeHostManifest.BiosProvenanceSynthetic));

    [Fact]
    public void FirmwareWithMixedFallback_MayNameNoBlockEntry()
    {
        var parsed = RuntimeHostManifestJson.Parse(Manifest(guestFirmware: true, blockEntries: []));

        parsed.BlockEntryPcs.Should().BeEmpty("the BIOS is interpreted; every PC runs through the fallback");
        parsed.MixedFallback.Should().BeTrue();
    }

    [Fact]
    public void HleWithMixedFallback_StillRequiresABlockEntry()
    {
        var text = Manifest(guestFirmware: false, blockEntries: []);

        ((Action)(() => RuntimeHostManifestJson.Parse(text))).Should().Throw<InvalidDataException>()
            .WithMessage("*block entry*");
    }
}

/// <summary>
/// The standalone firmware request must not default its interpreter-fallback budgets to unlimited: an unattended run with
/// no explicit budget would otherwise run one fallback segment for billions of instructions (#806 / #820 W01).
/// </summary>
[Test]
public sealed class FirmwareFallbackDefaultBudgetTests
{
    private static FirmwareDistributionRequest Request() =>
        new("bios.bin", "game.bin", "aot", "out", 37160, 1000, false);

    [Fact]
    public void DefaultRequest_FallbackBudgetsAreFinite()
    {
        var request = Request();

        request.FallbackSegmentInstructionBudget.Should().BeInRange(1u, uint.MaxValue - 1u, "the default must stop a runaway fallback");
        request.FallbackMaxTransitions.Should().BeInRange(1u, uint.MaxValue - 1u, "the default must stop a runaway fallback");
    }

    [Fact]
    public void ExplicitFallbackBudgets_AreKeptAsGiven()
    {
        var request = new FirmwareDistributionRequest("bios.bin", "game.bin", "aot", "out", 37160, 1000, false,
            FallbackSegmentInstructionBudget: 4000, FallbackMaxTransitions: 40);

        request.FallbackSegmentInstructionBudget.Should().Be(4000u);
        request.FallbackMaxTransitions.Should().Be(40u);
    }
}
