using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2 (owner ruling, MEDIUM-1, option 2): the production disc path. The PS-X executables of a real disc are read through the
/// production input path (<see cref="DiscSectorSourceOpener"/>, <see cref="LoadImageManifest.Read"/> with a <c>disc-exes</c> directive,
/// and <see cref="FirmwareAotPlan.Build"/>), so the image kind and the load address (the header's TextStart) are the ones production
/// uses. No fixed address and no <c>exe-file</c> stand-in is used. Skipped unless <c>PSXRECOMP_MEASURE_CHD</c> and
/// <c>PSXRECOMP_OPENBIOS_ROM</c> are set: a real disc and a real BIOS are never committed, and CI has neither, so it skips there.
/// Only counts, kinds, load addresses and verdicts are printed; no disc, BIOS or executable bytes.
/// </summary>
[Test]
public sealed class BiosProductionDiscPathTests
{
    private const uint ProductionCodeBytes = 37160; // FirmwarePackageCommand.DefaultCodeBytes: the production default.

    [SkippableFact]
    public void PersonaDiscExecutables_PassTheProductionBiosRefusal_OnTheDiscExeKind()
    {
#pragma warning disable AARC003 // Test-layer evidence: the disc and the BIOS are read from the environment, never committed
        var discPath = Environment.GetEnvironmentVariable("PSXRECOMP_MEASURE_CHD");
        var biosPath = Environment.GetEnvironmentVariable("PSXRECOMP_OPENBIOS_ROM");
#pragma warning restore AARC003
        Skip.If(string.IsNullOrWhiteSpace(discPath) || string.IsNullOrWhiteSpace(biosPath),
            "set PSXRECOMP_MEASURE_CHD (a disc image) and PSXRECOMP_OPENBIOS_ROM (a local OpenBIOS ROM); neither is in CI");

        using var temp = new TempDirectory();
        var firmware = OpenBiosFirmware.FromBytes(ReadBios(biosPath!));
        var roots = temp.WriteFile("rom.roots", Array.Empty<byte>());
        var images = temp.WriteFile("images.txt", System.Text.Encoding.ASCII.GetBytes("disc-exes persona\n"));

        using var disc = DiscSectorSourceOpener.Open(discPath!);

        // The production manifest stage, as FirmwareAotPlan.Build calls it.
        var manifest = LoadImageManifest.Read(images, firmware, disc.Source);
        // Five is a fact of this disc (the Persona measurement), not a rule: a different disc has another count, and the test then
        // needs its count updated with the measurement that it came from.
        manifest.Images.Should().HaveCount(5, "the disc holds five PS-X executables (the Persona measurement)");
        manifest.Images.Should().OnlyContain(static image => image.Kind == "disc-exe", "every image comes from the disc-exes directive");
        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(manifest, firmware))).Should().NotThrow(
            "no ordinary disc executable is refused by the production manifest stage");

        // The whole production plan, from the same inputs: it refuses nothing and carries the content diagnostic.
        var plan = FirmwareAotPlan.Build(firmware, roots, ProductionCodeBytes, images, disc.Source);
        plan.ContentDiagnostic.Should().NotBeNull();
        plan.ContentDiagnostic!.Provenance.Should().Be("unknown");

        for (var index = 0; index < manifest.Images.Count; index++)
        {
            var image = manifest.Images[index];
            var single = new LoadImageManifest([image], new HashSet<uint>());
            var refused = Refused(single, firmware);
            var diagnostic = BiosAddressGuard.DiagnoseContent(single, firmware);
            Emit($"b2-persona-disc image={index} kind={image.Kind} textStart=0x{image.LoadAddress:X8} words={image.Words.Length} " +
                 $"refused={(refused ? "yes" : "no")} highVarietyBytes={diagnostic.HighVarietyBytes} allMatchBytes={diagnostic.AllMatchBytes}");
            refused.Should().BeFalse($"PS-X executable {index} is an ordinary disc executable and must pass the production stage");
        }
    }

    private static bool Refused(LoadImageManifest manifest, OpenBiosFirmware firmware)
    {
        try
        {
            BiosAddressGuard.RequireNoBiosImage(manifest, firmware);
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    private static byte[] ReadBios(string path)
    {
#pragma warning disable AARC003 // Test-layer evidence: the local BIOS ROM named by the environment
        return File.ReadAllBytes(path);
#pragma warning restore AARC003
    }

    private static void Emit(string line)
    {
#pragma warning disable AARC003 // Test-layer evidence: counts and verdicts only
        Console.WriteLine(line);
#pragma warning restore AARC003
    }
}
