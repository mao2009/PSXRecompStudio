using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Standalone;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #732 P1-1: a firmware distribution propagates its interpreter-fallback policy from the package command to
/// the runtime manifest and the launcher. When mixed fallback is off the manifest records zero budgets and the
/// launcher's <see cref="FirmwareRuntimeLauncher.FallbackOptionsFor"/> seam returns null, so the engine stops an
/// unresolved in-image transfer instead of reaching the interpreter; when it is on, the exact per-segment and
/// transition budgets the packaging step recorded are what the engine runs with.
/// </summary>
[Test]
public sealed class FirmwareRuntimeLauncherTests
{
    private const uint RomBase = OpenBiosFirmware.ResetVector;

    private static RuntimeHostManifest Host(bool mixedFallback, uint segmentBudget, uint maxTransitions) => new(
        RuntimeHostManifest.CurrentSchemaVersion,
        "../game",
        GuestFirmware: true,
        MixedFallback: mixedFallback,
        FallbackSegmentInstructionBudget: segmentBudget,
        FallbackMaxTransitions: maxTransitions,
        ImageLoadAddress: RomBase,
        Image: RuntimeHostLayout.ImageFileName,
        BlockEntryPcs: [RomBase],
        ProgramEnd: RomBase + (uint)OpenBiosFirmware.ImageSize,
        ResultRegister: null,
        RunTimeoutMs: 120_000,
        EntryPc: RomBase);

    [Fact]
    public void FallbackOptionsFor_ReturnsTheManifestBudgets_WhenMixedFallbackIsOn()
    {
        var options = FirmwareRuntimeLauncher.FallbackOptionsFor(Host(mixedFallback: true, segmentBudget: 5000, maxTransitions: 40));

        options.Should().NotBeNull("mixed fallback is on, so an unresolved in-image transfer may reach the interpreter");
        options!.SegmentInstructionBudget.Should().Be(5000, "the per-segment cap recorded in the manifest is the one the engine runs with");
        options.MaxTransitions.Should().Be(40, "the artifact-to-interpreter handoff cap recorded in the manifest is the one the engine runs with");
    }

    [Fact]
    public void FallbackOptionsFor_ReturnsNull_WhenMixedFallbackIsOff()
    {
        FirmwareRuntimeLauncher.FallbackOptionsFor(Host(mixedFallback: false, segmentBudget: 0, maxTransitions: 0))
            .Should().BeNull("an unresolved in-image transfer must stop the run instead of reaching the interpreter");
    }

    [Fact]
    public void ADisabledManifest_PersistsZeroBudgets()
    {
        // The Distribution builder stores 0 budgets when mixed fallback is off; this must be a valid manifest, not
        // a validation failure, and the launcher must read the off state back from it.
        var manifest = Host(mixedFallback: false, segmentBudget: 0, maxTransitions: 0);

        var parsed = RuntimeHostManifestJson.Parse(RuntimeHostManifestJson.Serialize(manifest));
        parsed.MixedFallback.Should().BeFalse();
        parsed.FallbackSegmentInstructionBudget.Should().Be(0);
        parsed.FallbackMaxTransitions.Should().Be(0);
        FirmwareRuntimeLauncher.FallbackOptionsFor(parsed).Should().BeNull();
    }

    [Fact]
    public void FallbackOptionsFor_RejectsANullManifest()
    {
        ((Action)(() => FirmwareRuntimeLauncher.FallbackOptionsFor(null!))).Should().Throw<ArgumentNullException>();
    }
}