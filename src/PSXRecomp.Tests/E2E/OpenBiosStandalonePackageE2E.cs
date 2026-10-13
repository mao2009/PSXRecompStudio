using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.E2E;

/// <summary>
/// Issue #732: <c>package --bios openbios</c> end to end with a synthetic 512 KiB ROM, a blank raw disc and no AOT
/// images (no OpenBIOS, no commercial data). The produced game executable is launched directly; it must reach the
/// Runtime host, boot the ROM as guest firmware with the disc attached, run the ROM natively and stop at the
/// bounded budget with the ROM verified and no BIOS HLE. Real OpenBIOS / Persona runs are separate local gates.
/// </summary>
[Test]
public sealed class OpenBiosStandalonePackageE2E : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static uint I(uint opcode, uint rs, uint rt, uint imm) => opcode << 26 | rs << 21 | rt << 16 | (imm & 0xFFFFu);

    /// <summary>Writes a marker to RAM, then spins at 0xBFC00010.</summary>
    private static byte[] SyntheticRom()
    {
        uint[] words =
        [
            I(0x0F, 0, 8, 0x8001), I(0x0D, 0, 9, 0x1234), I(0x2B, 8, 9, 0x100), 0,
            0x08000000u | ((OpenBiosFirmware.ResetVector + 0x10u & 0x0FFFFFFFu) >> 2), 0,
        ];
        var bytes = new byte[OpenBiosFirmware.ImageSize];
        for (var i = 0; i < words.Length; i++) BitConverter.GetBytes(words[i]).CopyTo(bytes, i * 4);
        return bytes;
    }

    [SkippableFact]
    public void PackagedFirmwareDistribution_BootsTheRomThroughTheRuntimeHost_WhenLaunchedDirectly()
    {
        RuntimeHostPublishE2E.RequireGcc();

        var rom = SyntheticRom();
        var romPath = _dir.WriteFile("openbios.bin", rom);
        var aot = _dir.CreateSubdirectory("aot");
        _dir.WriteFile("aot/rom.roots", []);
        _dir.WriteFile("aot/images.txt", "# no RAM-placed code images\n"u8.ToArray());
        var discPath = _dir.WriteFile("disc.bin", new byte[2352]);
        var output = _dir.CreateSubdirectory("game");

        var error = new StringWriter();
        var exit = Program.Execute(
            ["package", discPath, "--bios", "openbios", "--bios-rom", romPath, "--aot-dir", aot, "--code-bytes", "64",
             "--instruction-budget", "2000", "--run-timeout-seconds", "120", "--output", output],
            new StringWriter(), error);
        exit.Should().Be(0, error.ToString());

        var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var runtime = Path.Combine(output, RuntimeHostLayout.RuntimeDirectoryName);
        var artifact = Path.Combine(output, "game" + (windows ? ".exe" : ""));
#pragma warning disable AARC003 // Test-layer inspection of the produced distribution layout
        File.Exists(artifact).Should().BeTrue("the game executable sits at the distribution root");
        File.Exists(Path.Combine(runtime, RuntimeHostLayout.HostFileName(windows))).Should().BeTrue("the shared Runtime host is bundled");
        File.ReadAllBytes(Path.Combine(runtime, RuntimeHostLayout.ImageFileName)).Should().Equal(rom, "the ROM sidecar is exactly the supplied ROM");
        File.Exists(Path.Combine(runtime, "disc", "disc.bin")).Should().BeTrue();
        var host = RuntimeHostManifestJson.Parse(File.ReadAllText(Path.Combine(runtime, RuntimeHostLayout.ManifestFileName)));
        host.GuestFirmware.Should().BeTrue();
        host.ImageLoadAddress.Should().Be(OpenBiosFirmware.ResetVector);
        var boot = FirmwareBootManifestJson.Parse(File.ReadAllText(Path.Combine(runtime, FirmwareBootManifest.FileName)));
        boot.BiosBackend.Should().Be("openbios");
        boot.RomSha256.Should().Be(OpenBiosRomInput.Sha256Hex(rom));
        File.ReadAllText(Path.Combine(output, RuntimeHostLayout.ReadmeFileName)).Should().Contain("#730");

        using var process = Process.Start(new ProcessStartInfo(artifact)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEndAsync();
        process.WaitForExit(120_000).Should().BeTrue("the bounded run must finish");
        var stderr = stderrTask.GetAwaiter().GetResult();
        var processExit = process.ExitCode;
#pragma warning restore AARC003

        processExit.Should().Be(2, "the instruction budget ends the run: " + stderr);
        var report = JsonDocument.Parse(stderr.Split('\n').First(static l => l.StartsWith("{\"kind\":\"firmware-run\"", StringComparison.Ordinal))).RootElement;
        report.GetProperty("bios").GetString().Should().Be("openbios");
        report.GetProperty("romSha256").GetString().Should().Be(OpenBiosRomInput.Sha256Hex(rom));
        report.GetProperty("state").GetString().Should().Be("BudgetExhausted");
        report.GetProperty("nativeInstructions").GetUInt64().Should().BeGreaterThan(0, "ROM code with a block runs natively");
        report.GetProperty("fallbackInstructions").GetUInt64().Should().Be(0, "nothing in this run needs the interpreter");
    }
}
