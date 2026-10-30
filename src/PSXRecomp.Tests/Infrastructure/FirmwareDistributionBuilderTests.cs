using System.Text;
using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.E2E;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #732 review P1: packaging must fail closed <em>before</em> anything is generated. Every AOT dependency is
/// enumerated, contained and frozen into a staging directory first, the native build runs against those frozen
/// copies, and only a fully built distribution is committed into place. A reference that escapes the AOT root, or a
/// malformed roots file, therefore fails without ever creating the output directory, without leaving a staging
/// directory behind and without touching a pre-existing distribution.
/// </summary>
[Test]
#pragma warning disable AARC003 // the packaging contract is files on disk; the test lays out and inspects inputs and output
public sealed class FirmwareDistributionBuilderTests
{
    private static byte[] Text(string value) => Encoding.ASCII.GetBytes(value);

    private static FirmwareDistributionRequest Request(string rom, string disc, string aot, string output) =>
        new(rom, disc, aot, output, CodeBytes: 64, RunTimeoutMs: 1000, SelfContained: false);

    private static string StagingGlob(string output) => "." + Path.GetFileName(output) + ".staging-*";

    private static void AssertNothingLeftBehind(TempDirectory dir, string output)
    {
        Directory.Exists(output).Should().BeFalse("a failed build never commits an output directory");
        Directory.GetDirectories(dir.FullPath, StagingGlob(output))
            .Should().BeEmpty("a failed build deletes its staging directory");
    }

    [Fact]
    public void Build_RejectsAnEscapingAotReference_BeforeCreatingAnyOutput()
    {
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("rom.bin", new byte[OpenBiosFirmware.ImageSize]);
        var disc = dir.WriteFile("disc.bin", new byte[(PSXRecomp.Core.Runtime.CdRom.CdSectorAddress.LeadInFrames + 1) * 2352]);
        var aot = dir.CreateSubdirectory("aot");
        dir.WriteFile("aot/rom.roots", Text("0xBFC00000\n"));
        dir.WriteFile("aot/images.txt", Text("exe sample ../external/game.exe\n"));

        // The referenced file is readable, but it lives outside the AOT root: packaging must refuse it, not read it.
        var external = dir.WriteFile("external/game.exe", SyntheticPsxExeBuilder.BuildValid());
        File.Exists(external).Should().BeTrue();
        var output = dir.Combine("game");

        ((Action)(() => FirmwareDistributionBuilder.Build(Request(rom, disc, aot, output), windows: false)))
            .Should().Throw<InvalidDataException>().WithMessage("*outside*");
        AssertNothingLeftBehind(dir, output);
    }

    [Fact]
    public void Build_RejectsAMalformedRootsFile_WithoutLeavingStagingOrOutput()
    {
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("rom.bin", new byte[OpenBiosFirmware.ImageSize]);
        var disc = dir.WriteFile("disc.bin", new byte[(PSXRecomp.Core.Runtime.CdRom.CdSectorAddress.LeadInFrames + 1) * 2352]);
        var aot = dir.CreateSubdirectory("aot");
        dir.WriteFile("aot/rom.roots", Text("0xBFC00000\nnot-hex\n"));
        dir.WriteFile("aot/images.txt", Text("# none\n"));
        var output = dir.Combine("game");

        ((Action)(() => FirmwareDistributionBuilder.Build(Request(rom, disc, aot, output), windows: false)))
            .Should().Throw<InvalidDataException>().WithMessage("*hexadecimal*");
        AssertNothingLeftBehind(dir, output);
    }

    [Fact]
    public void Build_FailurePreservesAPreExistingOutput()
    {
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("rom.bin", new byte[OpenBiosFirmware.ImageSize]);
        var disc = dir.WriteFile("disc.bin", new byte[(PSXRecomp.Core.Runtime.CdRom.CdSectorAddress.LeadInFrames + 1) * 2352]);
        var aot = dir.CreateSubdirectory("aot");
        dir.WriteFile("aot/rom.roots", Text("nope\n"));
        dir.WriteFile("aot/images.txt", Text("# none\n"));

        var output = dir.Combine("game");
        dir.WriteFile("game/artifact.exe", Text("previous distribution"));
        dir.WriteFile("game/runtime/runtime-host.json", Text("previous manifest"));

        ((Action)(() => FirmwareDistributionBuilder.Build(Request(rom, disc, aot, output), windows: false)))
            .Should().Throw<InvalidDataException>();

        File.ReadAllText(Path.Combine(output, "artifact.exe")).Should().Be("previous distribution",
            "a failed build must not destroy a good previous distribution");
        File.ReadAllText(Path.Combine(output, "runtime", "runtime-host.json")).Should().Be("previous manifest");
        Directory.GetDirectories(dir.FullPath, StagingGlob(output)).Should().BeEmpty();
    }

    [Fact]
    public void DefaultBuildTimeout_IsAFiniteThreeMinutes_AndARequestKeepsItUnlessOverridden()
    {
        FirmwareDistributionBuilder.DefaultBuildTimeout.Should().Be(TimeSpan.FromMinutes(3),
            "the firmware build keeps the historical bounded per-step timeout");
        Request("r", "d", "a", "o").BuildTimeout.Should().BeNull(
            "an unspecified build timeout keeps the bounded default rather than an unbounded wait");
    }

    /// <summary>
    /// The request's <see cref="FirmwareDistributionRequest.BuildTimeout"/> is the per-step host-toolchain bound
    /// ("--build-timeout-seconds"): a timeout far below a real compile must fail closed with the classified
    /// timeout, must clean up staging, and must never leave a partial distribution. This proves the request's
    /// timeout — not the run timeout — reaches the native build service.
    /// </summary>
    [SkippableFact]
    public void Build_HonorsAnExplicitBuildTimeout_AndFailsClosedWhenTheToolchainExceedsIt()
    {
        RuntimeHostPublishE2E.RequireGcc();

        using var dir = new TempDirectory();
        var rom = dir.WriteFile("rom.bin", new byte[OpenBiosFirmware.ImageSize]);
        var disc = dir.WriteFile("disc.bin", new byte[(PSXRecomp.Core.Runtime.CdRom.CdSectorAddress.LeadInFrames + 1) * 2352]);
        var aot = dir.CreateSubdirectory("aot");
        dir.WriteFile("aot/rom.roots", Text("0xBFC00000\n"));
        dir.WriteFile("aot/images.txt", Text("# none\n"));
        var output = dir.Combine("game");

        var request = Request(rom, disc, aot, output) with { BuildTimeout = TimeSpan.FromMilliseconds(1) };

        ((Action)(() => FirmwareDistributionBuilder.Build(request, windows: false)))
            .Should().Throw<InvalidOperationException>().WithMessage("*exceeded the build timeout*");
        AssertNothingLeftBehind(dir, output);
    }
}
