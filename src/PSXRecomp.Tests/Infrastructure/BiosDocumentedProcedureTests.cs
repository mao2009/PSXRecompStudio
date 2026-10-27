using System.Diagnostics;
using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2 (MAJOR-1): the documented OpenBIOS procedure must not fail on the manifest stage. The procedure runs
/// <c>scripts/openbios/prepare-aot.py</c> and then passes its output to <c>package --bios openbios --aot-dir</c>; these tests
/// run that same production input path (the script, <see cref="LoadImageManifest.Read"/>, and <see cref="FirmwareAotPlan.Build"/>).
/// The executed check needs a locally built OpenBIOS ELF, a ROM and a disc, so it is skipped unless they are named in the
/// environment; CI has none of them. The static check runs everywhere.
/// </summary>
[Test]
public sealed class BiosDocumentedProcedureTests
{
#pragma warning disable AARC003 // Test-layer I/O: runs the documented script and inspects its output and the local inputs
    private const uint DocumentedCodeBytes = 37160; // the documented --code-bytes (FirmwarePackageCommand.DefaultCodeBytes).

    /// <summary>R1: the preparation script emits no BIOS RAM image: it writes no <c>image</c> directive.</summary>
    [Fact]
    public void ThePreparationScript_WritesNoImageDirective_SoNoBiosImageIsAnAotInput()
    {
        var script = Path.Combine(FindRepositoryRoot(), "scripts", "openbios", "prepare-aot.py");
        var source = File.ReadAllText(script);
        source.Should().NotContain("f'image ", "a manifest image line copies BIOS bytes into RAM (R1); the script must not write one");
        source.Should().NotContain("'image ", "no image directive may be written by the script");
    }

    /// <summary>
    /// The documented procedure, executed: <c>prepare-aot.py</c> on the local OpenBIOS ELF, then the production manifest stage and
    /// plan build on its output with the local ROM and disc. The output holds no <c>rom</c> image, and the plan is not refused.
    /// </summary>
    [SkippableFact]
    public void TheDocumentedProcedure_IsNotRefusedOnTheManifestStage_AndHoldsNoBiosImage()
    {
        var elf = Environment.GetEnvironmentVariable("PSXRECOMP_OPENBIOS_ELF");
        var rom = Environment.GetEnvironmentVariable("PSXRECOMP_OPENBIOS_ROM");
        var disc = Environment.GetEnvironmentVariable("PSXRECOMP_MEASURE_CHD");
        var python = Environment.GetEnvironmentVariable("PSXRECOMP_PYTHON") ?? "python";
        Skip.If(string.IsNullOrWhiteSpace(elf) || string.IsNullOrWhiteSpace(rom) || string.IsNullOrWhiteSpace(disc),
            "set PSXRECOMP_OPENBIOS_ELF (openbios.elf), PSXRECOMP_OPENBIOS_ROM (openbios.bin) and PSXRECOMP_MEASURE_CHD (a disc); none is in CI");

        using var temp = new TempDirectory();
        var aot = temp.Combine("aot");
        Run(python, Path.Combine(FindRepositoryRoot(), "scripts", "openbios", "prepare-aot.py"), elf!, aot);

        var images = Path.Combine(aot, "images.txt");
        var roots = Path.Combine(aot, "rom.roots");
        File.Exists(images).Should().BeTrue("the procedure writes images.txt");
        File.Exists(roots).Should().BeTrue("the procedure writes rom.roots");
        File.ReadAllLines(images).Where(static line => line.StartsWith("image ", StringComparison.Ordinal))
            .Should().BeEmpty("the documented procedure writes no RAM image (R1)");

        var firmware = OpenBiosFirmware.FromBytes(ReadFile(rom!));
        using var opened = DiscSectorSourceOpener.Open(disc!);

        var manifest = LoadImageManifest.Read(images, firmware, opened.Source);
        manifest.Images.Should().NotContain(static image => image.Kind == "rom-image", "no image copies BIOS bytes into RAM");
        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(manifest, firmware))).Should().NotThrow(
            "the documented output passes the production manifest stage");

        var plan = FirmwareAotPlan.Build(firmware, roots, DocumentedCodeBytes, images, opened.Source);
        plan.ContentDiagnostic.Should().NotBeNull();
    }

    private static void Run(string python, string script, string elf, string output)
    {
        using var process = Process.Start(new ProcessStartInfo(python)
        {
            ArgumentList = { script, elf, output },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("the python interpreter did not start");
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, $"prepare-aot.py must succeed: {error}");
    }

    private static byte[] ReadFile(string path)
    {
        return File.ReadAllBytes(path);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "scripts", "openbios", "prepare-aot.py")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("the repository root with scripts/openbios was not found");
    }
#pragma warning restore AARC003
}
