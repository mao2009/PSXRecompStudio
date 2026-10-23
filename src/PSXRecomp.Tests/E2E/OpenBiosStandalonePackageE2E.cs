using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// Issue #732: <c>package --bios openbios</c> end to end with synthetic 512 KiB ROMs, raw discs and no commercial
/// data. The produced game executable is launched directly; it must reach the Runtime host, boot the ROM as guest
/// firmware with the disc attached, run the ROM natively and stop at the bounded budget with the ROM verified and
/// no BIOS HLE.
///
/// Scope (Issue #732 review P2-2): the packaged E2E here verifies the <em>packaging + AOT-mapping</em> path — the
/// built artifact, the guarded game block the <c>boot-exe</c> AOT image contributes, zero-fallback native
/// execution and ROM → game → ROM control/data continuity in the one guest state. The ROMs below are synthetic:
/// <see cref="RomWithGame"/> embeds the game's words as MIPS immediates and stores them into RAM itself, so the
/// run-time disc path (OpenBIOS's own CD driver reading raw sectors from the disc into RAM and then
/// <c>exec</c>-ing the EXE) is <b>not</b> exercised here. That runtime disc load is a separate gate, verified by
/// real OpenBIOS over the shared device graph with the synthetic Mode 2 disc (see
/// docs/runtime/openbios-firmware-backend.md, "Disc in the drive" and the measured table). Real OpenBIOS / Persona
/// runs are separate local gates.
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
    /// A synthetic ROM whose "CD driver" <em>is its own code</em>: it stores the disc executable's words into RAM
    /// (the <c>boot-exe</c> AOT image's destination), calls the entry, and only then records the post-game markers
    /// that prove control and data continuity (S1/S3/S4/S6), in the same one guest state the firmware owns.
    ///
    /// This models the <em>outcome</em> of a disc load — the game's words present in RAM at the entry — deliberately
    /// bypassing the guest CD driver, so packaging → guarded game AOT → ROM continuity can be verified without a
    /// synthetic CD driver. It is not evidence that OpenBIOS's own CD driver reads the disc at run time; that is the
    /// separate runtime disc-load gate (see the class docstring).
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

#pragma warning disable AARC003 // Test-layer inspection of the produced distribution layout
        Directory.GetDirectories(_dir.FullPath, ".game.staging-*").Should().BeEmpty(
            "a successful build commits from staging and leaves no staging directory behind");
#pragma warning restore AARC003

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
        File.ReadAllText(Path.Combine(runtime, RuntimeHostLayout.ManifestFileName)).Should().Contain("\"biosMode\": \"openbios-rom\"",
            "the runtime manifest labels the run as a real OpenBIOS ROM, never as HLE");
        AssertNoAbsoluteDeveloperPaths(output);

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
        report.GetProperty("biosMode").GetString().Should().Be("openbios-rom");
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
    /// The P2-2 packaging gate, scoped to what a packaged, directly-launched executable can prove (Issue #732
    /// review P2-2): a distributable disc whose SYSTEM.CNF names a real PS-X EXE. Packaging compiles the boot
    /// executable ahead of time (the <c>boot-exe</c> image and its <c>rom.roots</c>/<c>images.txt</c> are pinned as
    /// AOT inputs), and the synthetic ROM stores those exact words into RAM and calls the entry, so the whole run —
    /// ROM blocks and the guarded game block — executes natively. The final register file proves ROM → game → ROM
    /// control/data continuity <em>from the run's own output</em>; the game's own <c>$t0</c> (written only by the
    /// game's final instruction, never by the post-game ROM code) is the game-block attribution inside the
    /// aggregate native count, and <c>fallbackInstructions=0</c> shows nothing reached the interpreter. The runtime
    /// disc-sector load (OpenBIOS's CD driver reading the disc and <c>exec</c>-ing the EXE) is <b>not</b> part of
    /// this test: it is the separate gate recorded by the real-OpenBIOS probe. The explicit fallback budgets prove
    /// P1-1's CLI → manifest propagation; the direct host run with <c>--diagnostics</c> proves P2-1's Issue #763
    /// stop diagnostic is written for a firmware run.
    /// </summary>
    [SkippableFact]
    public void BootExeAotImage_RunsNativelyInTheOneGuestState_AndReportsControlContinuity()
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
             "--instruction-budget", "2000", "--run-timeout-seconds", "120", "--build-timeout-seconds", "180",
             "--fallback-segment-budget", "5000", "--fallback-max-transitions", "40", "--output", output],
            new StringWriter(), error);
        exit.Should().Be(0, error.ToString());

#pragma warning disable AARC003 // Test-layer inspection of the produced distribution layout
        Directory.GetDirectories(_dir.FullPath, ".game.staging-*").Should().BeEmpty(
            "a successful build commits from staging and leaves no staging directory behind");
#pragma warning restore AARC003

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

    /// <summary>Environment variable naming a locally built OpenBIOS ROM. The file is read in place, never copied or committed.</summary>
    public const string UserOpenBiosRomVariable = "PSXRECOMP_OPENBIOS_ROM";

    private static readonly Regex AbsolutePathPattern = new(@"(?<![A-Za-z])[A-Za-z]:(\\|[\/])|/(home|Users)/", RegexOptions.CultureInvariant);

    /// <summary>No text file the packaging writes may name an absolute developer path (the output must be relocatable).</summary>
    private static void AssertNoAbsoluteDeveloperPaths(string root)
    {
#pragma warning disable AARC003 // Test-layer inspection of the produced distribution text
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(static f => Path.GetExtension(f) is ".json" or ".txt" or ".md"))
        {
            AbsolutePathPattern.IsMatch(File.ReadAllText(file)).Should().BeFalse(
                $"{Path.GetFileName(file)} must not contain an absolute path");
        }
#pragma warning restore AARC003
    }

    /// <summary>
    /// Issue #B2: the same packaged path with a REAL OpenBIOS image the user supplies by path at run time. The ROM is
    /// read in place from <see cref="UserOpenBiosRomVariable"/> and is never copied into the repository, the test output
    /// or the distribution beyond the runtime sidecar the packager already writes. Skipped with its reason when the
    /// variable is unset or does not name a file. The disc is synthetic; only the BIOS is real.
    /// </summary>
    [SkippableFact]
    public void UserSuppliedOpenBiosRom_PackagesAndRunsNativelyThroughTheRuntimeHost()
    {
#pragma warning disable AARC003 // The real-ROM gate reads its user-supplied path from the environment
        var romSource = Environment.GetEnvironmentVariable(UserOpenBiosRomVariable);
#pragma warning restore AARC003
        Skip.If(string.IsNullOrWhiteSpace(romSource),
            $"real-ROM gate skipped: set {UserOpenBiosRomVariable} to a locally built openbios.bin (never committed)");
        Skip.IfNot(File.Exists(romSource), $"real-ROM gate skipped: {UserOpenBiosRomVariable} does not name an existing file");
        RuntimeHostPublishE2E.RequireGcc();

#pragma warning disable AARC003 // Test-layer read of the user-supplied ROM to pin its digest
        var romSha256 = OpenBiosRomInput.Sha256Hex(File.ReadAllBytes(romSource!));
#pragma warning restore AARC003
        var disc = SyntheticDiscBuilder.Bootable(SyntheticPsxExeBuilder.BuildValid(instructionCount: 6, textStart: GameEntry));
        var aot = _dir.CreateSubdirectory("aot");
        _dir.WriteFile("aot/rom.roots", Text("0xBFC00000\n"));
        _dir.WriteFile("aot/images.txt", Text("# no RAM-placed code images\n"));
        var discPath = _dir.WriteFile("disc.bin", disc);
        var output = _dir.CreateSubdirectory("game");

        var error = new StringWriter();
        var exit = Program.Execute(
            ["package", discPath, "--bios", "openbios", "--bios-rom", romSource!, "--aot-dir", aot,
             "--instruction-budget", "2000000", "--run-timeout-seconds", "300", "--build-timeout-seconds", "900", "--output", output],
            new StringWriter(), error);
        exit.Should().Be(0, error.ToString());
        AssertNoAbsoluteDeveloperPaths(output);

        var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var artifact = Path.Combine(output, "game" + (windows ? ".exe" : ""));
#pragma warning disable AARC003 // Test-layer process use to drive the bundled game executable
        using var process = Process.Start(new ProcessStartInfo(artifact)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEndAsync();
        process.WaitForExit(600_000).Should().BeTrue("the bounded real-ROM run must finish");
        var stderr = stderrTask.GetAwaiter().GetResult();
#pragma warning restore AARC003

        var line = stderr.Split('\n').FirstOrDefault(static l => l.StartsWith("{\"kind\":\"firmware-run\"", StringComparison.Ordinal));
        line.Should().NotBeNull("the firmware run writes its summary line: " + stderr);
        var report = JsonDocument.Parse(line!).RootElement;
        report.GetProperty("bios").GetString().Should().Be("openbios");
        report.GetProperty("biosMode").GetString().Should().Be("openbios-rom");
        report.GetProperty("romSha256").GetString().Should().Be(romSha256, "the run verified the user's ROM by digest");
        report.GetProperty("nativeInstructions").GetUInt64().Should().BeGreaterThan(0, "the real OpenBIOS ROM runs natively");
    }
}
