using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.Infrastructure;
using PSXRecomp.Tests.RealRomAnalysis;
using PSXRecomp.Tests.Recompiler;
using static PSXRecomp.Tests.Infrastructure.MixedFallbackTestSupport;

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

    private const uint GameEntry = 0x80010000u;
    private const uint Marker = 0x80020000u;

    private static byte[] Text(string value) => Encoding.ASCII.GetBytes(value);

    /// <summary>
    /// A ROM whose "CD driver" is its own code: it copies the disc executable's words into RAM, calls the entry
    /// (the <c>boot-exe</c> AOT image), and only then records the post-game markers that prove control and data
    /// continuity (S1/S3/S4/S6), in the same one guest state the firmware owns.
    /// </summary>
    private static byte[] RomWithGame(uint[] gameWords)
    {
        var rom = new Block(OpenBiosFirmware.ResetVector);
        rom.Emit(Li(T0, 0x00400000u), [Mtc0(T0, 12), Nop]); // SR the game must not disturb
        rom.Emit(Li(T1, Marker), Li(T2, 0xB105u), [Sw(T2, T1, 0)]); // RAM the game must not disturb
        for (var i = 0; i < gameWords.Length; i++)
        {
            rom.Emit(Li(A0, GameEntry + (uint)i * 4), Li(T5, gameWords[i]), [Sw(T5, A0, 0)]);
        }

        rom.Emit(Li(T9, GameEntry), [Jalr(T9), Nop]); // the disc's boot EXE, called from the firmware
        rom.Emit(Ori(S1, Zero, 0x66), Mfc0(S3, 12), Nop, Lw(S4, T1, 0), Nop, Ori(S6, Zero, 0x42));
        rom.Emit(MipsEncoding.Branch(0x04, 0, 0, rom.Here, rom.Here), Nop); // spin until the budget ends the run

        var words = new uint[0x800 / 4];
        for (var i = 0; i < rom.Words.Count; i++) words[i] = rom.Words[i];
        var bytes = new byte[OpenBiosFirmware.ImageSize];
        for (var i = 0; i < words.Length; i++) BitConverter.GetBytes(words[i]).CopyTo(bytes, i * 4);
        return bytes;
    }

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
        host.MixedFallback.Should().BeTrue("mixed fallback is the firmware default");
        host.FallbackSegmentInstructionBudget.Should().Be(uint.MaxValue, "the firmware default per-segment budget");
        host.FallbackMaxTransitions.Should().Be(uint.MaxValue, "the firmware default transition budget");
        host.BlockEntryPcs.Should().NotBeEmpty().And.NotContain(GameEntry, "block entry PCs are the ROM's static blocks only; RAM-placed entries are guarded versions");
        var boot = FirmwareBootManifestJson.Parse(File.ReadAllText(Path.Combine(runtime, FirmwareBootManifest.FileName)));
        boot.BiosBackend.Should().Be("openbios");
        boot.RomSha256.Should().Be(OpenBiosRomInput.Sha256Hex(rom));
        boot.AotInputs.Select(static i => i.Path).Should().Equal(
            ["firmware/images.txt", "firmware/rom.roots"],
            "every AOT input — rom.roots, images.txt and the files they reference — is copied into the distribution and pinned by digest");
        File.ReadAllText(Path.Combine(runtime, "firmware", "images.txt")).Should().Be("# no RAM-placed code images\n",
            "the images sidecar is the packaged copy the run rebuilds the plan from");
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
        report.GetProperty("mixedFallback").GetBoolean().Should().BeTrue("the manifest's fallback policy is reported on the firmware-run line");
        report.GetProperty("fallbackSegmentInstructionBudget").GetUInt64().Should().Be(uint.MaxValue);
        report.GetProperty("fallbackMaxTransitions").GetUInt64().Should().Be(uint.MaxValue);
        report.GetProperty("nativeInstructions").GetUInt64().Should().BeGreaterThan(0, "ROM code with a block runs natively");
        report.GetProperty("fallbackInstructions").GetUInt64().Should().Be(0, "nothing in this run needs the interpreter");
        report.GetProperty("finalGpr").GetArrayLength().Should().Be(TitleExecutionRequest.GprCount,
            "the firmware-run line carries the final register file for continuity checks");
    }

    /// <summary>
    /// The P2-2 gate: a distributable disc whose SYSTEM.CNF boots a real PS-X EXE (Issue #732 P2-2). Packaging
    /// compiles the boot executable ahead of time (the <c>boot-exe</c> image), the ROM calls it once its words are
    /// in RAM, and the whole run — ROM blocks and the guarded game block — executes natively with zero fallback
    /// instructions. The final register file proves ROM → game → ROM control/data continuity from the run's own
    /// output; the explicit fallback budgets prove P1-1's CLI → manifest propagation; and the direct host run with
    /// <c>--diagnostics</c> proves P2-1's Issue #763 stop diagnostic is written for a firmware run.
    /// </summary>
    [SkippableFact]
    public void BootableDiscGame_RunsNatively_AndReportsControlContinuity()
    {
        RuntimeHostPublishE2E.RequireGcc();

        var exeBytes = SyntheticPsxExeBuilder.BuildValid(instructionCount: 6, textStart: GameEntry);
        var text = SyntheticPsxExeBuilder.BuildText(6);
        var gameWords = new uint[text.Length / 4];
        for (var i = 0; i < gameWords.Length; i++) gameWords[i] = BitConverter.ToUInt32(text, i * 4);
        var disc = SyntheticDiscBuilder.Bootable(exeBytes);

        var rom = RomWithGame(gameWords);
        var romPath = _dir.WriteFile("openbios.bin", rom);
        var aot = _dir.CreateSubdirectory("aot");
        _dir.WriteFile("aot/rom.roots", Text("0xBFC00000\n"));
        _dir.WriteFile("aot/images.txt", Text("boot-exe game\n"));
        var discPath = _dir.WriteFile("disc.bin", disc);
        var output = _dir.CreateSubdirectory("game");

        var error = new StringWriter();
        var exit = Program.Execute(
            ["package", discPath, "--bios", "openbios", "--bios-rom", romPath, "--aot-dir", aot, "--code-bytes", "256",
             "--instruction-budget", "2000", "--run-timeout-seconds", "120",
             "--fallback-segment-budget", "5000", "--fallback-max-transitions", "40", "--output", output],
            new StringWriter(), error);
        exit.Should().Be(0, error.ToString());

        var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var runtime = Path.Combine(output, RuntimeHostLayout.RuntimeDirectoryName);
        var artifact = Path.Combine(output, "game" + (windows ? ".exe" : ""));
#pragma warning disable AARC003 // Test-layer inspection of the produced distribution layout
        var host = RuntimeHostManifestJson.Parse(File.ReadAllText(Path.Combine(runtime, RuntimeHostLayout.ManifestFileName)));
        host.MixedFallback.Should().BeTrue();
        host.FallbackSegmentInstructionBudget.Should().Be(5000, "--fallback-segment-budget propagates to the runtime manifest");
        host.FallbackMaxTransitions.Should().Be(40, "--fallback-max-transitions propagates to the runtime manifest");
        host.BlockEntryPcs.Should().NotContain(GameEntry, "RAM-placed code is never a block entry PC: the game entry lives in the AOT loaded table");
        var boot = FirmwareBootManifestJson.Parse(File.ReadAllText(Path.Combine(runtime, FirmwareBootManifest.FileName)));
        boot.AotInputs.Select(static i => i.Path).Should().Contain(["firmware/rom.roots", "firmware/images.txt"],
            "the boot-executable manifest and its roots are the packaged, hashed AOT inputs");
        boot.DiscSha256.Should().Be(OpenBiosRomInput.Sha256Hex(disc), "the bootable disc the game comes from is pinned by digest");
        File.ReadAllText(Path.Combine(runtime, "firmware", "images.txt")).Should().Be("boot-exe game\n");
#pragma warning restore AARC003

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

        processExit.Should().Be(2, "the instruction budget ends the run: " + stderr);
        var report = JsonDocument.Parse(stderr.Split('\n').First(static l => l.StartsWith("{\"kind\":\"firmware-run\"", StringComparison.Ordinal))).RootElement;
        report.GetProperty("state").GetString().Should().Be("BudgetExhausted");
        report.GetProperty("nativeInstructions").GetUInt64().Should().BeGreaterThan(0, "the ROM and the guarded game run natively");
        report.GetProperty("fallbackInstructions").GetUInt64().Should().Be(0, "the pre-generated boot executable never reaches the interpreter");
        report.GetProperty("mixedFallback").GetBoolean().Should().BeTrue();
        report.GetProperty("fallbackSegmentInstructionBudget").GetUInt64().Should().Be(5000);
        report.GetProperty("fallbackMaxTransitions").GetUInt64().Should().Be(40);
        var finalGpr = report.GetProperty("finalGpr").EnumerateArray()
            .Select(static e => uint.Parse(e.GetString()![2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        finalGpr[(int)R3000aRegister.S1].Should().Be(0x66u, "control returned from the game into the firmware");
        finalGpr[(int)R3000aRegister.S3].Should().Be(0x00400000u, "the firmware's COP0 SR survives the game's run");
        finalGpr[(int)R3000aRegister.S4].Should().Be(0xB105u, "RAM the firmware initialised is unchanged after the game ran");
        finalGpr[(int)R3000aRegister.S6].Should().Be(0x42u, "the post-game marker the firmware alone writes is present");
        finalGpr[(int)R3000aRegister.T0].Should().Be(3u, "the game retired its own instructions: the boot EXE's last addiu wrote its immediate");

        // P2-1: the Runtime host honors --diagnostics for a firmware distribution exactly as for an HLE run.
        var hostBinary = Path.Combine(runtime, RuntimeHostLayout.HostFileName(windows));
        var diagnosticsPath = _dir.Combine("firmware-diagnostics.json");
        var hostDiagnostics = Path.Combine(runtime, RuntimeHostLayout.ManifestFileName);
#pragma warning disable AARC003 // Test-layer process use to drive the bundled Runtime host
        using var hostProcess = Process.Start(new ProcessStartInfo(hostBinary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "--diagnostics", diagnosticsPath, hostDiagnostics },
        })!;
        var hostStderrTask = hostProcess.StandardError.ReadToEndAsync();
        hostProcess.StandardOutput.ReadToEndAsync();
        hostProcess.WaitForExit(120_000).Should().BeTrue("the bounded host run must finish");
        var hostStderr = hostStderrTask.GetAwaiter().GetResult();
        hostProcess.ExitCode.Should().Be(2, "the host classifies the budget stop identically: " + hostStderr);
        File.Exists(diagnosticsPath).Should().BeTrue("the host wrote the Issue #763 stop diagnostic for the firmware run");
        using var diagnosticDocument = JsonDocument.Parse(File.ReadAllText(diagnosticsPath));
        var diagnostic = diagnosticDocument.RootElement;
        diagnostic.GetProperty("orchestratorState").GetString().Should().Be("BudgetExhausted");
        diagnostic.GetProperty("classifiedExitCode").GetInt32().Should().Be(2);
        diagnostic.GetProperty("fallbackEnabled").GetBoolean().Should().BeTrue();
        diagnostic.GetProperty("fallbackRetiredInstructions").GetUInt64().Should().Be(0);
        diagnostic.GetProperty("aotRetiredInstructions").GetUInt64().Should().BeGreaterThan(0);
        diagnostic.GetProperty("finalPc").ValueKind.Should().Be(JsonValueKind.Number, "the budget stop has a known guest PC");
#pragma warning restore AARC003
    }
}
