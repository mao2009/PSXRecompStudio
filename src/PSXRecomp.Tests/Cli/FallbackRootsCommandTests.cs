using System.Text;
using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Tests.Recompiler;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Cli;

// Issue #773: `psxrecomp fallback-roots` plans observed roots into a new manifest beside the input. Small end-to-end runs:
// the output reads back through the manifest parser, the input is never changed, and no existing file is overwritten.
[Test]
public sealed class FallbackRootsCommandTests
{
    private const uint Dest = ObservedRootsFixtures.Dest;
    private const uint Callee = ObservedRootsFixtures.Callee;

    private sealed record Fixture(TempDirectory Dir, string Rom, string Manifest, string Targets);

    private static Fixture Create(TempDirectory dir, string targetsJson)
    {
        var rom = dir.WriteFile("openbios.bin", new byte[OpenBiosFirmware.ImageSize]);
        dir.WriteFile("test.exe", ObservedRootsFixtures.Exe(ObservedRootsFixtures.Text));
        var manifest = dir.WriteFile("images.txt", Encoding.ASCII.GetBytes("exe test test.exe\n"));
        var targets = dir.WriteFile("targets.json", Encoding.UTF8.GetBytes(targetsJson));
        return new Fixture(dir, rom, manifest, targets);
    }

    private static int Run(Fixture fixture, string output, out string stdout, out string stderr)
    {
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var code = FallbackRootsCommand.Run(
            [fixture.Rom, "--load-images", fixture.Manifest, "--fallback-targets", fixture.Targets, "--out", output],
            outWriter,
            errWriter);
        stdout = outWriter.ToString();
        stderr = errWriter.ToString();
        return code;
    }

    [Fact]
    public void AnUnknownCodeTarget_BecomesAnObservedRoot_InANewManifest_ThatReadsBack()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, ObservedRootsFixtures.TargetsJson(Callee, "unknown-code", 40));
        var output = dir.Combine("images-observed.txt");

        var code = Run(fixture, output, out var stdout, out var stderr);

        code.Should().Be(0, stderr);
        stdout.Should().Contain("\"accepted\": 1");
        var planned = LoadImageManifest.Read(output, OpenBiosFirmware.FromBytes(new byte[OpenBiosFirmware.ImageSize]), disc: null);
        planned.Images.Single().Roots.Should().Equal(Dest, Callee);
    }

    [Fact]
    public void TheInputManifest_IsNeverChanged()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, ObservedRootsFixtures.TargetsJson(Callee, "unknown-code", 40));

        Run(fixture, dir.Combine("images-observed.txt"), out _, out _);

        var original = LoadImageManifest.Read(fixture.Manifest, OpenBiosFirmware.FromBytes(new byte[OpenBiosFirmware.ImageSize]), disc: null);
        original.Images.Single().Roots.Should().Equal(Dest);
    }

    [Fact]
    public void AnExistingOutput_IsRefused_AndNothingIsOverwritten()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, ObservedRootsFixtures.TargetsJson(Callee, "unknown-code", 40));
        var output = dir.Combine("images-observed.txt");
        Run(fixture, output, out _, out _).Should().Be(0);

        var code = Run(fixture, output, out _, out var stderr);

        code.Should().Be(1);
        stderr.Should().Contain("already exists");
    }

    [Fact]
    public void AnOutputOutsideTheManifestDirectory_IsRefused()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, ObservedRootsFixtures.TargetsJson(Callee, "unknown-code", 40));
        var elsewhere = dir.CreateSubdirectory("elsewhere");

        var code = Run(fixture, Path.Combine(elsewhere, "images-observed.txt"), out _, out var stderr);

        code.Should().Be(1);
        stderr.Should().Contain("same directory");
    }

    [Fact]
    public void AMalformedTargetDocument_IsRefused()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, """{"format":"other/v9","targets":[]}""");

        var code = Run(fixture, dir.Combine("images-observed.txt"), out _, out var stderr);

        code.Should().Be(1);
        stderr.Should().Contain("format");
    }
}
