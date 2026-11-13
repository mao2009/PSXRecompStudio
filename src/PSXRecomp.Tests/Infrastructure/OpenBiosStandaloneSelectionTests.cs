using System.Text.Json.Nodes;
using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #732: the OpenBIOS standalone surface fails closed. The backend is chosen explicitly (OpenBIOS by default),
/// an unimplemented backend is a clear error rather than a substitution, and the user-supplied ROM, disc and
/// firmware manifest are rejected when malformed, so nothing silently falls back to HLE.
/// </summary>
[Test]
#pragma warning disable AARC003 // the launcher/packager contract is files on disk; the test lays out and inspects a distribution
public sealed class OpenBiosStandaloneSelectionTests
{
    private static readonly string Sha = new('a', 64);

    private static IReadOnlyList<FirmwareAotInput> Aot(string roots = "firmware/rom.roots", string images = "firmware/images.txt") =>
        [new(roots, Sha), new(images, Sha)];

    private static FirmwareBootManifest Manifest(
        string backend = "openbios", string roots = "firmware/rom.roots",
        string images = "firmware/images.txt", string disc = "disc/disc.chd", uint codeBytes = 37160) =>
        new(FirmwareBootManifest.CurrentSchemaVersion, backend, codeBytes, roots, images, disc, Sha, Aot(roots, images));

    [Fact]
    public void Default_IsOpenBios_AndNamesRoundTrip()
    {
        BiosBackendSelection.Parse(null).Should().Be(BiosBackendKind.OpenBios);
        BiosBackendSelection.Parse("OpenBIOS").Should().Be(BiosBackendKind.OpenBios);
        BiosBackendSelection.Parse("custom-hle").Should().Be(BiosBackendKind.CustomHle);
        BiosBackendSelection.Parse("genuine").Should().Be(BiosBackendKind.Genuine);
    }

    [Theory]
    [InlineData("custom-hle")]
    [InlineData("genuine")]
    public void UnimplementedBackends_StopWithAClearError_NeverFallBack(string name)
    {
        var act = () => BiosBackendSelection.RequireImplemented(BiosBackendSelection.Parse(name));
        act.Should().Throw<BiosBackendNotImplementedException>().WithMessage($"*'{name}' is not implemented*No other backend is substituted*");
    }

    [Fact]
    public void UnknownBackend_IsRejected_NotDefaulted() =>
        ((Action)(() => BiosBackendSelection.Parse("hle"))).Should().Throw<ArgumentException>();

    [Fact]
    public void FirmwareManifest_RoundTrips()
    {
        var parsed = FirmwareBootManifestJson.Parse(FirmwareBootManifestJson.Serialize(Manifest()));
        parsed.Should().BeEquivalentTo(Manifest());
    }

    [Fact]
    public void FirmwareManifest_RejectsMissingBackendInsteadOfDefaultingToOpenBios()
    {
        var json = JsonNode.Parse(FirmwareBootManifestJson.Serialize(Manifest()))!.AsObject();
        json.Remove("biosBackend").Should().BeTrue();
        Action parse = () => FirmwareBootManifestJson.Parse(json.ToJsonString());
        parse.Should().Throw<InvalidDataException>().WithMessage("*biosBackend*");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void FirmwareManifest_RejectsBlankBackend(string backend)
    {
        var json = FirmwareBootManifestJson.Serialize(Manifest() with { BiosBackend = backend });
        Action parse = () => FirmwareBootManifestJson.Parse(json);
        parse.Should().Throw<InvalidDataException>().WithMessage("*biosBackend*");
    }

    [Theory]
    [InlineData("custom-hle")]
    [InlineData("nonsense")]
    public void FirmwareManifest_RejectsABackendThatCannotRun(string backend) =>
        ((Action)(() => FirmwareBootManifestJson.Parse(FirmwareBootManifestJson.Serialize(Manifest(backend)))))
            .Should().Throw<InvalidDataException>();

    [Theory]
    [InlineData("roots", "firmware/../rom.roots")]
    [InlineData("roots", "firmware\\rom.roots")]
    [InlineData("roots", "disc/rom.roots")]
    [InlineData("roots", "/etc/passwd")]
    [InlineData("images", "firmware/")]
    [InlineData("disc", "disc/a:b")]
    [InlineData("disc", "firmware/disc.chd")]
    public void FirmwareManifest_RejectsEscapingOrMalformedFields(string field, string value)
    {
        var manifest = field switch
        {
            "roots" => Manifest(roots: value),
            "images" => Manifest(images: value),
            "disc" => Manifest(disc: value),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        ((Action)(() => FirmwareBootManifestJson.Parse(FirmwareBootManifestJson.Serialize(manifest)))).Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]
    [InlineData(OpenBiosFirmware.ImageSize + 4u)]
    public void FirmwareManifest_RejectsABadCodeSize(uint codeBytes) =>
        ((Action)(() => FirmwareBootManifestJson.Parse(FirmwareBootManifestJson.Serialize(Manifest(codeBytes: codeBytes)))))
            .Should().Throw<InvalidDataException>();

    [Fact]
    public void RomInput_RejectsMissingTruncatedAndMismatchedRoms()
    {
        using var dir = new TempDirectory();
        ((Action)(() => OpenBiosRomInput.Read(dir.Combine("none.bin")))).Should().Throw<FileNotFoundException>();
        ((Action)(() => OpenBiosRomInput.Read(dir.WriteFile("short.bin", new byte[1024])))).Should().Throw<InvalidDataException>().WithMessage("*524288*");
        var rom = dir.WriteFile("rom.bin", new byte[OpenBiosFirmware.ImageSize]);
        var (bytes, hash) = OpenBiosRomInput.Read(rom);
        bytes.Length.Should().Be(OpenBiosFirmware.ImageSize);
        OpenBiosRomInput.Read(rom, hash).Sha256.Should().Be(hash);
        ((Action)(() => OpenBiosRomInput.Read(rom, Sha))).Should().Throw<InvalidDataException>().WithMessage("*does not match*");
    }

    private static (int Exit, string Error) Package(params string[] args)
    {
        var error = new StringWriter();
        var exit = PSXRecomp.Infrastructure.Cli.Program.Execute(["package", .. args], new StringWriter(), error);
        return (exit, error.ToString());
    }

    [Theory]
    [InlineData("custom-hle")]
    [InlineData("genuine")]
    public void Cli_PackageWithAnUnimplementedBios_FailsClosedBeforeAnyWork(string bios)
    {
        using var dir = new TempDirectory();
        var (exit, error) = Package(dir.WriteFile("d.chd", [1]), "--bios", bios, "--output", dir.Combine("out"));
        exit.Should().Be(1);
        error.Should().Contain("not implemented");
        Directory.Exists(dir.Combine("out")).Should().BeFalse("nothing is produced for a backend that cannot run");
    }

    [Fact]
    public void Cli_PackageWithOpenBios_RequiresTheRomAndAotInputs_AndRejectsABadRom()
    {
        using var dir = new TempDirectory();
        var disc = dir.WriteFile("d.chd", [1]);
        var (missing, message) = Package(disc, "--bios", "openbios", "--output", dir.Combine("out"));
        missing.Should().Be(1);
        message.Should().Contain("--bios-rom");

        var (bad, badMessage) = Package(
            disc, "--bios", "openbios", "--bios-rom", dir.WriteFile("rom.bin", new byte[10]), "--aot-dir", dir.FullPath, "--output", dir.Combine("out"));
        bad.Should().Be(1);
        badMessage.Should().Contain("524288");
    }

    [Fact]
    public void Cli_BiosOptions_AreNotAcceptedByRun()
    {
        var error = new StringWriter();
        PSXRecomp.Infrastructure.Cli.Program.Execute(["run", "x.exe", "--bios", "openbios"], new StringWriter(), error).Should().NotBe(0);
        error.ToString().Should().Contain("only valid for 'package'");
    }

    /// <summary>
    /// <c>--build-timeout-seconds</c> is the per-step host-toolchain bound for a firmware package build. It is a
    /// package-only option, independent of <c>--run-timeout-seconds</c>, and must be a bounded positive integer:
    /// zero, negative, non-numeric and over-the-cap values fail closed at parse time, before any file is touched.
    /// </summary>
    [Fact]
    public void Cli_BuildTimeoutOption_IsNotAcceptedByRun()
    {
        var error = new StringWriter();
        PSXRecomp.Infrastructure.Cli.Program.Execute(["run", "x.exe", "--build-timeout-seconds", "60"], new StringWriter(), error).Should().NotBe(0);
        error.ToString().Should().Contain("only valid for 'package'");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("86401")]
    public void Cli_Package_RejectsAnInvalidBuildTimeout(string value)
    {
        var (exit, error) = Package("d.chd", "--bios", "openbios", "--build-timeout-seconds", value, "--output", "out");
        exit.Should().Be(1, "a bounded positive integer is required: " + error);
        error.Should().Contain("--build-timeout-seconds");
    }

    /// <summary>
    /// Issue #732 review P2: a malformed AOT <c>rom.roots</c> must be a stable, classified tooling failure (exit 1
    /// with a diagnostic), never an unhandled <see cref="FormatException"/>, and it must not leave a partial
    /// distribution or a staging directory behind.
    /// </summary>
    [Fact]
    public void Cli_PackageWithMalformedRoots_FailsWithExitOne_AndLeavesNoPartialOutput()
    {
        using var dir = new TempDirectory();
        var rom = dir.WriteFile("rom.bin", new byte[OpenBiosFirmware.ImageSize]);
        // A raw image must hold more than the 150-sector lead-in (Issue #10); the roots error is what this test checks.
        var disc = dir.WriteFile("d.bin", new byte[(PSXRecomp.Core.Runtime.CdRom.CdSectorAddress.LeadInFrames + 1) * 2352]);
        var aot = dir.CreateSubdirectory("aot");
        dir.WriteFile("aot/rom.roots", System.Text.Encoding.ASCII.GetBytes("not-hex\n"));
        dir.WriteFile("aot/images.txt", System.Text.Encoding.ASCII.GetBytes("# none\n"));
        var output = dir.Combine("out");

        var (exit, error) = Package(
            disc, "--bios", "openbios", "--bios-rom", rom, "--aot-dir", aot, "--output", output);

        exit.Should().Be(1, "a malformed roots file is a classified tooling failure: " + error);
        error.Should().Contain("not a hexadecimal number");
        Directory.Exists(output).Should().BeFalse("no partial distribution is committed");
        Directory.GetDirectories(dir.FullPath, ".out.staging-*").Should().BeEmpty("no staging directory is left behind");
    }

    [Fact]
    public void Launcher_RefusesATamperedDisc_BeforeRunningAnything()
    {
        using var dir = new TempDirectory();
        var runtime = Directory.CreateDirectory(dir.Combine("runtime")).FullName;
        Directory.CreateDirectory(Path.Combine(runtime, "disc"));
        var rom = new byte[OpenBiosFirmware.ImageSize];
        var romPath = Path.Combine(runtime, "artifact-image.bin");
        File.WriteAllBytes(romPath, rom);
        File.WriteAllBytes(Path.Combine(runtime, "disc", "disc.bin"), [1, 2, 3]);
        var boot = new FirmwareBootManifest(FirmwareBootManifest.CurrentSchemaVersion, "openbios", 64,
            "firmware/rom.roots", "firmware/images.txt", "disc/disc.bin", OpenBiosRomInput.Sha256Hex([1, 2, 3]),
            [new("firmware/rom.roots", Sha), new("firmware/images.txt", Sha)]);
        File.WriteAllText(Path.Combine(runtime, FirmwareBootManifest.FileName), FirmwareBootManifestJson.Serialize(boot));
        FirmwareRuntimeLauncher.IsFirmwareDistribution(runtime).Should().BeTrue();

        var host = new RuntimeHostManifest(1, "../g", true, true, 1, 1, OpenBiosFirmware.ResetVector, string.Empty, [], 0xBFC80000u, null, 1000, OpenBiosFirmware.ResetVector);

        File.WriteAllBytes(Path.Combine(runtime, "disc", "disc.bin"), [9, 9, 9]); // tampered disc
        var error = new StringWriter();
        FirmwareRuntimeLauncher.Run(host, runtime, Path.Combine(dir.FullPath, "g"), romPath, error).Should().Be(FirmwareRuntimeLauncher.SetupFailureExitCode);
        error.ToString().Should().Contain("disc image does not match");
    }
}
