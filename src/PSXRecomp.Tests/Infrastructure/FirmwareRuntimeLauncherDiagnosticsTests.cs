using System.Text;
using System.Text.Json;
using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #732 P2-1: a firmware distribution honors the Runtime host's <c>--diagnostics</c> sink. The launcher
/// writes the same Issue #763 stop diagnostic the HLE path writes, from a completed run
/// (<see cref="RuntimeHostDiagnostic.From"/>) or from a setup failure (<see cref="RuntimeHostDiagnostic.FromFailure"/>),
/// and the write is best-effort: off by default, and it never changes the run's classified exit code.
/// </summary>
[Test]
public sealed class FirmwareRuntimeLauncherDiagnosticsTests
{
    private const uint RomBase = OpenBiosFirmware.ResetVector;

    private static byte[] Text(string value) => Encoding.ASCII.GetBytes(value);

    private static byte[] SyntheticRom()
    {
        uint[] words =
        [
            0x08000000u | ((RomBase & 0x0FFFFFFFu) >> 2), 0, 0, 0, 0, 0,
        ];
        var bytes = new byte[OpenBiosFirmware.ImageSize];
        for (var i = 0; i < words.Length; i++) BitConverter.GetBytes(words[i]).CopyTo(bytes, i * 4);
        return bytes;
    }

    private static RuntimeHostManifest Host() => new(
        RuntimeHostManifest.CurrentSchemaVersion,
        "../game",
        GuestFirmware: true,
        MixedFallback: true,
        FallbackSegmentInstructionBudget: uint.MaxValue,
        FallbackMaxTransitions: uint.MaxValue,
        ImageLoadAddress: RomBase,
        Image: RuntimeHostLayout.ImageFileName,
        BlockEntryPcs: [RomBase],
        ProgramEnd: RomBase + (uint)OpenBiosFirmware.ImageSize,
        ResultRegister: null,
        RunTimeoutMs: 120_000,
        EntryPc: RomBase);

    /// <summary>Seeds a plausible distribution tree (AOT inputs under runtime/firmware/, ROM and disc sidecars).</summary>
    private static string Seed(TempDirectory dir, byte[] rom, byte[] disc, string? romSha256)
    {
        dir.WriteFile("aot/rom.roots", Text("0xBFC00000\n"));
        dir.WriteFile("aot/images.txt", Text("# no RAM-placed code images\n"));
        var (inputs, files) = FirmwareAotBundle.Collect(dir.Combine("aot"));

        var runtime = dir.CreateSubdirectory("runtime");
        var firmware = dir.CreateSubdirectory("runtime/firmware");
        FirmwareAotBundle.CopyInto(files, firmware);
        dir.WriteFile("runtime/artifact-image.bin", rom);
        dir.WriteFile("runtime/disc.bin", disc);
        var boot = new FirmwareBootManifest(
            FirmwareBootManifest.CurrentSchemaVersion,
            "openbios",
            romSha256 ?? OpenBiosRomInput.Sha256Hex(rom),
            64,
            "firmware/rom.roots",
            "firmware/images.txt",
            "disc/disc.bin",
            OpenBiosRomInput.Sha256Hex(disc),
            inputs);
        dir.WriteFile("runtime/firmware-boot.json", Text(FirmwareBootManifestJson.Serialize(boot)));
        return runtime;
    }

    [Fact]
    public void Run_WritesAFailureDiagnostic_WhenTheRomIdentityDoesNotMatch()
    {
        using var dir = new TempDirectory();
        var runtime = Seed(dir, SyntheticRom(), new byte[2352], romSha256: new string('0', 64));
        var diagnostics = dir.Combine("firmware-diagnostics.json");
        var stderr = new StringWriter();

        var exit = FirmwareRuntimeLauncher.Run(
            Host(), runtime, dir.Combine("game"), Path.Combine(runtime, RuntimeHostLayout.ImageFileName), stderr, diagnostics);

        exit.Should().Be(FirmwareRuntimeLauncher.SetupFailureExitCode, "a mismatched ROM stops the run before anything executes");
        stderr.ToString().Should().Contain("runtime host: firmware distribution:").And.Contain("InvalidDataException").And.Contain("does not match");

#pragma warning disable AARC003 // Test-layer inspection of the produced diagnostic document
        File.Exists(diagnostics).Should().BeTrue("the launcher wrote the Issue #763 stop diagnostic");
        using var document = JsonDocument.Parse(File.ReadAllText(diagnostics));
#pragma warning restore AARC003
        var diagnostic = document.RootElement;
        diagnostic.GetProperty("failureExceptionType").GetString().Should().EndWith("InvalidDataException");
        diagnostic.GetProperty("classifiedExitCode").GetInt32().Should().Be(FirmwareRuntimeLauncher.SetupFailureExitCode);
        diagnostic.GetProperty("fallbackEnabled").GetBoolean().Should().BeTrue("the manifest's own policy is unchanged by the failure");
        diagnostic.GetProperty("orchestratorState").ValueKind.Should().Be(JsonValueKind.Null, "no run existed, so no state is guessed");
    }

    [Fact]
    public void Run_WritesNoDiagnostics_WithoutAPath_AndLeavesTheExitCodeUnchanged()
    {
        using var dir = new TempDirectory();
        var runtime = Seed(dir, SyntheticRom(), new byte[2352], romSha256: new string('0', 64));
        var stderr = new StringWriter();

        var exit = FirmwareRuntimeLauncher.Run(
            Host(), runtime, dir.Combine("game"), Path.Combine(runtime, RuntimeHostLayout.ImageFileName), stderr);

        exit.Should().Be(FirmwareRuntimeLauncher.SetupFailureExitCode, "the classified exit code is independent of the diagnostics sink");
#pragma warning disable AARC003 // Test-layer inspection of the produced distribution layout
        Directory.GetFiles(runtime, "*.json").Should().ContainSingle(e => Path.GetFileName(e) == FirmwareBootManifest.FileName, "only the boot manifest: no diagnostic document was written by default");
#pragma warning restore AARC003
    }
}