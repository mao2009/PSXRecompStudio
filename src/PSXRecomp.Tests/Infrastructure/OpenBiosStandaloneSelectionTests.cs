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

    private static FirmwareBootManifest Manifest(
        string backend = "openbios", string rom = "", string roots = "firmware/rom.roots",
        string images = "firmware/images.txt", string disc = "disc/disc.chd", uint codeBytes = 37160) =>
        new(FirmwareBootManifest.CurrentSchemaVersion, backend, rom.Length == 0 ? Sha : rom, codeBytes, roots, images, disc, Sha);

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
        parsed.Should().Be(Manifest());
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
    [InlineData("rom", "ABC")]
    public void FirmwareManifest_RejectsEscapingOrMalformedFields(string field, string value)
    {
        var manifest = field switch
        {
            "roots" => Manifest(roots: value),
            "images" => Manifest(images: value),
            "disc" => Manifest(disc: value),
            _ => Manifest(rom: value),
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

    [Fact]
    public void Launcher_RefusesATamperedRomOrDisc_BeforeRunningAnything()
    {
        using var dir = new TempDirectory();
        var runtime = Directory.CreateDirectory(dir.Combine("runtime")).FullName;
        Directory.CreateDirectory(Path.Combine(runtime, "disc"));
        var rom = new byte[OpenBiosFirmware.ImageSize];
        var romPath = Path.Combine(runtime, "artifact-image.bin");
        File.WriteAllBytes(romPath, rom);
        File.WriteAllBytes(Path.Combine(runtime, "disc", "disc.bin"), [1, 2, 3]);
        var boot = new FirmwareBootManifest(1, "openbios", OpenBiosRomInput.Sha256Hex(rom), 64, "firmware/rom.roots", "firmware/images.txt",
            "disc/disc.bin", OpenBiosRomInput.Sha256Hex([1, 2, 3]));
        File.WriteAllText(Path.Combine(runtime, FirmwareBootManifest.FileName), FirmwareBootManifestJson.Serialize(boot));
        FirmwareRuntimeLauncher.IsFirmwareDistribution(runtime).Should().BeTrue();

        var host = new RuntimeHostManifest(1, "../g", true, true, 1, 1, OpenBiosFirmware.ResetVector, "artifact-image.bin", [], 0xBFC80000u, null, 1000, OpenBiosFirmware.ResetVector);

        rom[0] = 1; // tampered ROM
        File.WriteAllBytes(romPath, rom);
        var error = new StringWriter();
        FirmwareRuntimeLauncher.Run(host, runtime, Path.Combine(dir.FullPath, "g"), romPath, error).Should().Be(FirmwareRuntimeLauncher.SetupFailureExitCode);
        error.ToString().Should().Contain("does not match the expected");

        rom[0] = 0;
        File.WriteAllBytes(romPath, rom);
        File.WriteAllBytes(Path.Combine(runtime, "disc", "disc.bin"), [9, 9, 9]); // tampered disc
        error = new StringWriter();
        FirmwareRuntimeLauncher.Run(host, runtime, Path.Combine(dir.FullPath, "g"), romPath, error).Should().Be(FirmwareRuntimeLauncher.SetupFailureExitCode);
        error.ToString().Should().Contain("disc image does not match");
    }
}
