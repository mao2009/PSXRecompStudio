using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
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
             "--instruction-budget", "2000", "--run-timeout-seconds", "120", "--fallback-segment-budget", "5000", "--fallback-max-transitions", "40", "--bios-provenance", "synthetic", "--output", output],
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
        File.Exists(Path.Combine(runtime, RuntimeHostLayout.ImageFileName)).Should().BeFalse("the BIOS is never copied into the distribution (Issue #B2)");
        File.Exists(Path.Combine(runtime, "disc", "disc.bin")).Should().BeTrue();
        var host = RuntimeHostManifestJson.Parse(File.ReadAllText(Path.Combine(runtime, RuntimeHostLayout.ManifestFileName)));
        host.GuestFirmware.Should().BeTrue();
        host.ImageLoadAddress.Should().Be(OpenBiosFirmware.ResetVector);
        host.MixedFallback.Should().BeTrue("mixed fallback is the firmware default");
        host.FallbackSegmentInstructionBudget.Should().Be(5000, "--fallback-segment-budget propagates to the runtime manifest");
        host.FallbackMaxTransitions.Should().Be(40, "--fallback-max-transitions propagates to the runtime manifest");
        host.BlockEntryPcs.Should().BeEmpty("the BIOS is never ahead-of-time translated (owner ruling, Issue #B2): no ROM block is named");
        var boot = FirmwareBootManifestJson.Parse(File.ReadAllText(Path.Combine(runtime, FirmwareBootManifest.FileName)));
        boot.BiosBackend.Should().Be("openbios");
        boot.AotInputs.Select(static i => i.Path).Should().Equal(
            ["firmware/images.txt", "firmware/rom.roots"],
            "every AOT input — rom.roots, images.txt and the files they reference — is copied into the distribution and pinned by digest");
        File.ReadAllText(Path.Combine(runtime, "firmware", "images.txt")).Should().Be("# no RAM-placed code images\n",
            "the images sidecar is the packaged copy the run rebuilds the plan from");
        File.ReadAllText(Path.Combine(output, RuntimeHostLayout.ReadmeFileName)).Should().Contain("#730");
        File.ReadAllText(Path.Combine(runtime, RuntimeHostLayout.ManifestFileName)).Should().Contain("\"biosProvenance\": \"synthetic\"",
            "the packaging records the declaration; the label is decided at run time");
        AssertNoBiosBytes(output, rom);
        AssertNoAbsoluteDeveloperPaths(output);
        // The developer-path check scans the whole tree: the native library must be in that tree (Issue #B2, review LOW-4).
        // The file name is per platform: PSXRecomp.Native.dll (Windows), libPSXRecomp.Native.so (Linux), libPSXRecomp.Native.dylib (macOS).
        ReadTree(output).Select(static file => file.Name.Replace('\\', '/'))
            .Where(static name => name.StartsWith("runtime/", StringComparison.Ordinal)
                && (name.StartsWith("runtime/PSXRecomp.Native.", StringComparison.Ordinal)
                    || name.StartsWith("runtime/libPSXRecomp.Native.", StringComparison.Ordinal))
                && (name.EndsWith(".dll", StringComparison.Ordinal) || name.EndsWith(".so", StringComparison.Ordinal)
                    || name.EndsWith(".dylib", StringComparison.Ordinal)))
            .Should().NotBeEmpty("the native library is scanned by AssertNoAbsoluteDeveloperPaths, so it must be in the distribution tree");

        using var process = Process.Start(new ProcessStartInfo(artifact)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment = { [RecompiledArtifactCodeGen.ExternalBiosEnvironmentVariable] = romPath },
        })!;
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEndAsync();
        WaitOrKill(process, 120_000, "the bounded run must finish");
        var stderr = stderrTask.GetAwaiter().GetResult();
        var processExit = process.ExitCode;
#pragma warning restore AARC003

        processExit.Should().Be(1, "the interpreted BIOS spins past its fallback budget: a classified runtime failure, not a clean stop: " + stderr);
        var report = JsonDocument.Parse(stderr.Split('\n').First(static l => l.StartsWith("{\"kind\":\"firmware-run\"", StringComparison.Ordinal))).RootElement;
        report.GetProperty("bios").GetString().Should().Be("openbios");
        report.GetProperty("biosMode").GetString().Should().Be("openbios-synthetic");
        report.GetProperty("romSha256").GetString().Should().Be(OpenBiosRomInput.Sha256Hex(rom));
        report.GetProperty("state").GetString().Should().Be("RuntimeFailure", "the interpreted BIOS loop ends at the fallback budget");
        report.GetProperty("aotBiosBlocks").GetInt32().Should().Be(0, "the generation stage translated no BIOS block (Issue #B2)"); report.GetProperty("diagnosticCode").GetString().Should().Be("ARTIFACT_FALLBACK_BUDGET_EXHAUSTED");
        report.GetProperty("mixedFallback").GetBoolean().Should().BeTrue("the manifest's fallback policy is reported on the firmware-run line");
        report.GetProperty("fallbackSegmentInstructionBudget").GetUInt64().Should().Be(5000);
        report.GetProperty("fallbackMaxTransitions").GetUInt64().Should().Be(40);
        report.GetProperty("nativeInstructions").GetUInt64().Should().Be(0, "the BIOS is never translated, so no ROM code runs natively");
        report.GetProperty("fallbackInstructions").GetUInt64().Should().BeGreaterThan(0, "the BIOS runs in the interpreter fallback");
        // Decision A (PR #839): a general-purpose register value is never published, so the firmware-run line carries no GPR field.
        report.TryGetProperty("finalGpr", out _).Should().BeFalse("no GPR value leaves the probe or the run output");
    }

    /// <summary>
    /// The P2-2 packaging gate, scoped to what a packaged, directly-launched executable can prove (Issue #732
    /// review P2-2): a distributable disc whose SYSTEM.CNF names a real PS-X EXE. Packaging compiles the boot
    /// executable ahead of time (the <c>boot-exe</c> image and its <c>rom.roots</c>/<c>images.txt</c> are pinned as
    /// AOT inputs), and the synthetic ROM stores those exact words into RAM and calls the entry, so the whole run —
    /// ROM blocks and the guarded game block — executes natively. The two fallback transitions show ROM → game → ROM
    /// control continuity from the run's own output (the register file is not published, decision A, PR #839). The runtime
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
             "--fallback-segment-budget", "5000", "--fallback-max-transitions", "40", "--bios-provenance", "synthetic", "--output", output],
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
            Environment = { [RecompiledArtifactCodeGen.ExternalBiosEnvironmentVariable] = romPath },
        })!;
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEndAsync();
        WaitOrKill(process, 120_000, "the bounded run must finish");
        var stderr = stderrTask.GetAwaiter().GetResult();
        var processExit = process.ExitCode;

        processExit.Should().Be(1, "the interpreted BIOS loop ends at the fallback budget: " + stderr);
        var report = JsonDocument.Parse(stderr.Split('\n').First(static l => l.StartsWith("{\"kind\":\"firmware-run\"", StringComparison.Ordinal))).RootElement;
        report.GetProperty("state").GetString().Should().Be("RuntimeFailure", "the interpreted BIOS loop ends at the fallback budget");
        report.GetProperty("aotBiosBlocks").GetInt32().Should().Be(0, "the generation stage translated no BIOS block (Issue #B2)");
        report.GetProperty("nativeInstructions").GetUInt64().Should().BeGreaterThan(0, "the ROM and the guarded game run natively");
        report.GetProperty("fallbackInstructions").GetUInt64().Should().BeGreaterThan(0, "the BIOS runs in the interpreter fallback; only the guarded game block is native");
        report.GetProperty("mixedFallback").GetBoolean().Should().BeTrue();
        report.GetProperty("fallbackSegmentInstructionBudget").GetUInt64().Should().Be(5000);
        report.GetProperty("fallbackMaxTransitions").GetUInt64().Should().Be(40);
        // Control returning into the firmware is shown by the two transitions (ROM -> game -> ROM) and the fallback
        // instructions that ran after it. The game's register results are not published (decision A, PR #839).
        report.GetProperty("fallbackTransitions").GetUInt64().Should().Be(2, "ROM -> game -> ROM: the game is entered once and control returns to the firmware" + stderr);
        report.GetProperty("fallbackInstructions").GetUInt64().Should().BeGreaterThan(0, "the interpreted firmware runs after the game returns");

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
            Environment = { [RecompiledArtifactCodeGen.ExternalBiosEnvironmentVariable] = romPath },
        })!;
        var hostStderrTask = hostProcess.StandardError.ReadToEndAsync();
        hostProcess.StandardOutput.ReadToEndAsync();
        WaitOrKill(hostProcess, 120_000, "the bounded host run must finish");
        var hostStderr = hostStderrTask.GetAwaiter().GetResult();
        hostProcess.ExitCode.Should().Be(1, "the host classifies the fallback-budget stop identically: " + hostStderr);
        File.Exists(diagnosticsPath).Should().BeTrue("the host wrote the Issue #763 stop diagnostic for the firmware run");
        using var diagnosticDocument = JsonDocument.Parse(File.ReadAllText(diagnosticsPath));
        var diagnostic = diagnosticDocument.RootElement;
        diagnostic.GetProperty("orchestratorState").GetString().Should().Be("RuntimeFailure");
        diagnostic.GetProperty("classifiedExitCode").GetInt32().Should().Be(1);
        diagnostic.GetProperty("fallbackEnabled").GetBoolean().Should().BeTrue();
        diagnostic.GetProperty("fallbackRetiredInstructions").GetUInt64().Should().BeGreaterThan(0, "the interpreted BIOS retires instructions in the fallback");
        diagnostic.GetProperty("aotRetiredInstructions").GetUInt64().Should().BeGreaterThan(0);
        diagnostic.GetProperty("finalPc").ValueKind.Should().Be(JsonValueKind.Number, "the budget stop has a known guest PC");
#pragma warning restore AARC003
    }

    /// <summary>Environment variable naming a locally built OpenBIOS ROM. The file is read in place, never copied or committed.</summary>
    public const string UserOpenBiosRomVariable = "PSXRECOMP_OPENBIOS_ROM";

#pragma warning disable AARC003 // Test-layer process control: a run that does not finish is killed, never left behind
    private static void WaitOrKill(Process process, int milliseconds, string because)
    {
        if (!process.WaitForExit(milliseconds))
        {
            process.Kill(entireProcessTree: true);
        }

        process.HasExited.Should().BeTrue(because);
    }
#pragma warning restore AARC003

    /// <summary>Every file under <paramref name="root"/> as (relative name, bytes), any extension.</summary>
    private static (string Name, byte[] Bytes)[] ReadTree(string root)
    {
#pragma warning disable AARC003 // Test-layer inspection of the produced distribution bytes
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => (Path.GetRelativePath(root, file), File.ReadAllBytes(file)))
            .ToArray();
#pragma warning restore AARC003
    }

    /// <summary>No file the packaging writes, of any extension (the executable and any PDB included), may name a developer path.</summary>
    private static void AssertNoAbsoluteDeveloperPaths(string root) =>
        DistributionLeakScanner.FindAbsolutePath(ReadTree(root)).Should().BeNull("the distribution must not name an absolute developer path");

    /// <summary>The first 256 bytes of the BIOS and its SHA-256 appear nowhere in the distribution. Needs no baseline.</summary>
    private static void AssertNoBiosBytes(string root, byte[] rom)
    {
        var needle = rom.AsSpan(0, 256).ToArray();
        needle.Should().Contain(static b => b != 0, "the probe window must hold BIOS code, not padding");
        var digest = OpenBiosRomInput.Sha256Hex(rom);
        foreach (var (name, bytes) in ReadTree(root))
        {
            bytes.AsSpan().IndexOf(needle.AsSpan()).Should().Be(-1, $"{name} must not contain BIOS bytes");
            Encoding.Latin1.GetString(bytes).Should().NotContain(digest, $"{name} must not contain the BIOS digest");
        }
    }

    /// <summary>
    /// Zero tolerance (Issue #B2, MEDIUM-1): no 8-byte window of the BIOS appears in the distribution unless the same window
    /// appears in <paramref name="baseline"/>, material that is not the BIOS (a synthetic run's output, the game disc).
    /// </summary>
    private static void AssertNoBiosWindows(string root, byte[] rom, IEnumerable<(string Name, byte[] Bytes)> baseline)
    {
        var report = DistributionLeakScanner.Scan(ReadTree(root), DistributionLeakScanner.BiosWindows(rom), baseline);
#pragma warning disable AARC003 // Test-layer evidence: the window-scan counts are echoed for the run log
        Console.WriteLine($"b2-bios-window-scan windowLength={DistributionLeakScanner.WindowLength} biosWindows={report.BiosWindows} " +
                          $"excusedByBaseline={report.ExcusedByBaseline} leaks={report.Leaks} " +
                          $"lowEntropyOccurrences={report.LowEntropyOccurrences} lowEntropyExcused={report.LowEntropyExcused}");
#pragma warning restore AARC003
        report.FirstLeak.Should().BeNull("no 8-byte window of the BIOS may appear in the distribution unless the baseline holds it");
    }

    /// <summary>
    /// Issue #B2: the same packaged path with a REAL OpenBIOS image the user supplies by path at run time. The ROM is
    /// read in place from <see cref="UserOpenBiosRomVariable"/> and is never copied into the repository, the test output
    /// or the distribution beyond the runtime sidecar the packager already writes. Skipped with its reason when the
    /// variable is unset or does not name a file. The disc is synthetic; only the BIOS is real.
    /// </summary>
    [SkippableFact]
    public void UserSuppliedOpenBiosRom_WiresTheRealRomAndPinsTheMeasuredRunOutcome()
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
             "--instruction-budget", "2000000", "--run-timeout-seconds", "300", "--build-timeout-seconds", "900",
             "--bios-provenance", "user-supplied", "--output", output],
            new StringWriter(), error);
        exit.Should().Be(0, error.ToString());
        AssertNoAbsoluteDeveloperPaths(output);
#pragma warning disable AARC003 // Test-layer inspection: the BIOS is read in place to check that the distribution holds none of it
        AssertNoBiosBytes(output, File.ReadAllBytes(romSource!));
        // The baseline is material that is not the BIOS: the output of the same packaging with a synthetic image, and the disc.
        var syntheticImage = _dir.WriteFile("synthetic-bios.bin", SyntheticRom());
        var baselineOutput = _dir.Combine("game-synthetic");
        var baselineError = new StringWriter();
        Program.Execute(
            ["package", discPath, "--bios", "openbios", "--bios-rom", syntheticImage, "--aot-dir", aot,
             "--instruction-budget", "2000000", "--run-timeout-seconds", "300", "--build-timeout-seconds", "900",
             "--bios-provenance", "synthetic", "--output", baselineOutput],
            new StringWriter(), baselineError).Should().Be(0, baselineError.ToString());
        var baseline = ReadTree(baselineOutput).Append(("disc input", File.ReadAllBytes(discPath))).ToArray();
        AssertNoBiosWindows(output, File.ReadAllBytes(romSource!), baseline);
#pragma warning restore AARC003

        var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var artifact = Path.Combine(output, "game" + (windows ? ".exe" : ""));
#pragma warning disable AARC003 // Test-layer process use to drive the bundled game executable
        using var process = Process.Start(new ProcessStartInfo(artifact)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment = { [RecompiledArtifactCodeGen.ExternalBiosEnvironmentVariable] = romSource! },
        })!;
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEndAsync();
        WaitOrKill(process, 600_000, "the bounded real-ROM run must finish");
        var stderr = stderrTask.GetAwaiter().GetResult();
#pragma warning restore AARC003

        var line = stderr.Split('\n').FirstOrDefault(static l => l.StartsWith("{\"kind\":\"firmware-run\"", StringComparison.Ordinal));
        line.Should().NotBeNull("the firmware run writes its summary line: " + stderr);
#pragma warning disable AARC003 // Test-layer evidence: the real-ROM gate's run line is echoed for the run log
        Console.WriteLine("b2-real-openbios-evidence " + line);
#pragma warning restore AARC003
        var report = JsonDocument.Parse(line!).RootElement;
        report.GetProperty("bios").GetString().Should().Be("openbios");
        report.GetProperty("biosMode").GetString().Should().Be("openbios-unverified",
            "the local build is not in the verified allowlist: it runs, but it is not a verified OpenBIOS run");
        report.GetProperty("romSha256").GetString().Should().Be(romSha256, "the run reports the digest of the image it loaded");
        report.GetProperty("nativeInstructions").GetUInt64().Should().Be(0, "no BIOS code is translated, and the timed-out run reached no native block (Issue #B2)");
        report.GetProperty("aotBiosBlocks").GetInt32().Should().Be(0, "the generation stage translated no BIOS block (Issue #B2)");
        report.GetProperty("aotLoadedBlocks").GetInt32().Should().Be(0, "this gate loads no RAM-placed code image");
        // Run outcome, reported apart from the wiring above. This is the measured result, not a clean run: the instance
        // hit the wall-clock bound. The test pins it so that any change in the outcome is noticed.
        report.GetProperty("state").GetString().Should().Be("RuntimeFailure", "measured run outcome (wiring PASS, run RuntimeFailure)");
        report.GetProperty("diagnosticCode").GetString().Should().Be("ARTIFACT_TIMEOUT", "measured run outcome: the wall-clock bound");
    }

    /// <summary>A reproducible 512 KiB BIOS-shaped image that no output contains. It is never OpenBIOS and never committed.</summary>
    private static byte[] BiosShapedImage()
    {
        var bytes = new byte[OpenBiosFirmware.ImageSize];
        // A fixed linear congruential sequence: reproducible, and unrelated to any BIOS.
        var state = 20261023UL;
        for (var i = 0; i < bytes.Length; i++)
        {
            state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
            bytes[i] = (byte)(state >> 56);
        }
        return bytes;
    }

    private string AotInputs(string name)
    {
        var aot = _dir.CreateSubdirectory(name);
        _dir.WriteFile(name + "/rom.roots", Text("0xBFC00000\n"));
        _dir.WriteFile(name + "/images.txt", Text("# no RAM-placed code images\n"));
        return aot;
    }

    private (int Exit, string Error) PackageFirmware(string discPath, string romPath, string aot, string output, string? provenance, params string[] extra)
    {
        var args = new List<string> { "package", discPath, "--bios", "openbios", "--bios-rom", romPath, "--aot-dir", aot };
        if (provenance is not null)
        {
            args.AddRange(["--bios-provenance", provenance]);
        }

        args.AddRange(extra);
        args.AddRange(["--output", output]);
        var error = new StringWriter();
        var exit = Program.Execute(args.ToArray(), new StringWriter(), error);
        return (exit, error.ToString());
    }

    /// <summary>Issue #B2 (MEDIUM-1 CI check): a clean package passes, and a planted BIOS fragment fails. Runs without the environment variable.</summary>
    [SkippableFact]
    public void CleanPackage_PassesTheBiosWindowCheck_AndAPlantedBiosFragmentFailsIt()
    {
        RuntimeHostPublishE2E.RequireGcc();
        var bios = BiosShapedImage();
        var biosPath = _dir.WriteFile("bios-shaped.bin", bios);
        var disc = _dir.WriteFile("disc.bin", new byte[2352]);
        var output = _dir.Combine("game");
        var (exit, error) = PackageFirmware(disc, biosPath, AotInputs("aot"), output, "synthetic");
        exit.Should().Be(0, error);

        var files = ReadTree(output);
        AssertNoAbsoluteDeveloperPaths(output);
        AssertNoBiosBytes(output, bios);
        AssertNoBiosWindows(output, bios, Array.Empty<(string Name, byte[] Bytes)>());
        var clean = DistributionLeakScanner.Scan(files, DistributionLeakScanner.BiosWindows(bios), Array.Empty<(string Name, byte[] Bytes)>());
        clean.Leaks.Should().Be(0, "a clean package holds no BIOS window; coincidental matches are counted, not excused");
#pragma warning disable AARC003 // Test-layer inspection: the Runtime host ships no PDB, so no build path rides along in one
        Directory.EnumerateFiles(output, "*.pdb", SearchOption.AllDirectories).Should().BeEmpty("the Runtime host is published with DebugType=none");
#pragma warning restore AARC003

        // Plant a 12-byte BIOS fragment into a copy of the game executable: the check must fail on it.
        var artifactName = "game" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "");
        var planted = files.Select(f =>
        {
            if (f.Name != artifactName) return f;
            var copy = (byte[])f.Bytes.Clone();
            Array.Copy(bios, 0x4000, copy, 0x100, 12);
            return (f.Name, copy);
        }).ToArray();
        var leak = DistributionLeakScanner.FindBiosLeak(planted, DistributionLeakScanner.BiosWindows(bios), Array.Empty<(string Name, byte[] Bytes)>());
        leak.Should().NotBeNull("a planted 12-byte BIOS fragment must be detected");
        leak.Should().Contain(artifactName);

        // Plant a UTF-16LE toolchain path into a copy of the game executable: the path check must fail on it (MEDIUM-2).
        var wide = (byte[])files.Single(f => f.Name == artifactName).Bytes.Clone();
        var widePath = System.Text.Encoding.Unicode.GetBytes(@"C:\Prog\winlibs_staging_ucrt\bin");
        Array.Copy(widePath, 0, wide, 0x200, widePath.Length);
        DistributionLeakScanner.FindAbsolutePath([(artifactName, wide)]).Should().NotBeNull("a UTF-16LE toolchain path must be detected");
    }

    /// <summary>
    /// Issue #B2 (run evidence): the same user-supplied image, but with a finite fallback budget, so the interpreter's segment
    /// ends and its instruction count is reported. The unbounded gate above reports no count (its segment never ends). A
    /// bounded stop is RuntimeFailure with ARTIFACT_FALLBACK_BUDGET_EXHAUSTED: a classified stop, not a clean run.
    /// </summary>
    [SkippableFact]
    public void UserSuppliedOpenBiosRom_BoundedInterpreterRun_ReportsTheInterpreterInstructionCount()
    {
#pragma warning disable AARC003 // The real-ROM gate reads its user-supplied path from the environment
        var romSource = Environment.GetEnvironmentVariable(UserOpenBiosRomVariable);
#pragma warning restore AARC003
        Skip.If(string.IsNullOrWhiteSpace(romSource),
            $"real-ROM bounded run skipped: set {UserOpenBiosRomVariable} to a locally built openbios.bin (never committed)");
#pragma warning disable AARC003 // Test-layer skip check on the user-supplied path
        Skip.IfNot(File.Exists(romSource), $"real-ROM bounded run skipped: {UserOpenBiosRomVariable} does not name an existing file");
#pragma warning restore AARC003
        RuntimeHostPublishE2E.RequireGcc();

        var disc = SyntheticDiscBuilder.Bootable(SyntheticPsxExeBuilder.BuildValid(instructionCount: 6, textStart: GameEntry));
#pragma warning disable AARC003 // Test-layer: the synthetic disc is written for the packaging run
        var discPath = _dir.WriteFile("disc.bin", disc);
#pragma warning restore AARC003
        var output = Path.Combine(_dir.CreateSubdirectory("bounded"), "game"); // the artifact is named after its directory
        var (exit, error) = PackageFirmware(
            discPath, romSource!, AotInputs("aot-bounded"), output, "user-supplied",
            "--run-timeout-seconds", "300", "--fallback-segment-budget", "2000000", "--fallback-max-transitions", "1000");
        exit.Should().Be(0, error);

        var artifact = Path.Combine(output, "game" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : ""));
#pragma warning disable AARC003 // Test-layer process use to drive the bundled game executable
        using var process = Process.Start(new ProcessStartInfo(artifact)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment = { [RecompiledArtifactCodeGen.ExternalBiosEnvironmentVariable] = romSource! },
        })!;
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEndAsync();
        WaitOrKill(process, 600_000, "the bounded real-ROM run must finish");
        var stderr = stderrTask.GetAwaiter().GetResult();
#pragma warning restore AARC003

        var line = stderr.Split('\n').FirstOrDefault(static l => l.StartsWith("{\"kind\":\"firmware-run\"", StringComparison.Ordinal));
        line.Should().NotBeNull("the firmware run writes its summary line: " + stderr);
#pragma warning disable AARC003 // Test-layer evidence: the bounded run's line is echoed for the run log
        Console.WriteLine("b2-real-openbios-bounded-evidence " + line);
        foreach (var hostLine in stderr.Split('\n').Where(static l => l.StartsWith("runtime host:", StringComparison.Ordinal)))
        {
            Console.WriteLine("b2-real-openbios-bounded-stop " + hostLine.TrimEnd());
        }
#pragma warning restore AARC003
        var report = JsonDocument.Parse(line!).RootElement;
        report.GetProperty("biosMode").GetString().Should().Be("openbios-unverified", "the local build is not a verified OpenBIOS run");
        report.GetProperty("aotBiosBlocks").GetInt32().Should().Be(0, "no BIOS block was ahead-of-time translated");
        report.GetProperty("nativeInstructions").GetUInt64().Should().Be(0, "the interpreted BIOS runs no native block");
        report.GetProperty("fallbackInstructions").GetUInt64().Should().BeGreaterThan(0,
            "the BIOS ran in the interpreter: its fallback instruction count is reported once the segment ends");
        report.GetProperty("state").GetString().Should().Be("RuntimeFailure", "a bounded stop is a classified failure, never a clean run");
        report.GetProperty("diagnosticCode").GetString().Should().Be("ARTIFACT_FALLBACK_BUDGET_EXHAUSTED");
    }

    /// <summary>Issue #B2 (LOW-1): without --bios-provenance the firmware is labelled as synthetic, never as a real ROM.</summary>
    [SkippableFact]
    public void PackageWithoutProvenance_IsLabelledOpenBiosSynthetic_ByDefault()
    {
        RuntimeHostPublishE2E.RequireGcc();
        var biosPath = _dir.WriteFile("bios-shaped.bin", BiosShapedImage());
        var disc = _dir.WriteFile("disc.bin", new byte[2352]);
        var output = _dir.Combine("game");
        var (exit, error) = PackageFirmware(disc, biosPath, AotInputs("aot"), output, provenance: null);
        exit.Should().Be(0, error);

#pragma warning disable AARC003 // Test-layer inspection of the generated runtime manifest
        var manifest = File.ReadAllText(Path.Combine(output, RuntimeHostLayout.RuntimeDirectoryName, RuntimeHostLayout.ManifestFileName));
#pragma warning restore AARC003
        manifest.Should().Contain("\"biosProvenance\": \"synthetic\"", "the default declaration is the conservative one");
        manifest.Should().NotContain("openbios-rom");
    }

    /// <summary>
    /// Issue #B2 (MAJOR-1): a stub declared user-supplied is never labelled openbios-rom. The run classifies it by its digest,
    /// the registered stub digest gives openbios-synthetic, and the declaration mismatch is reported as a warning.
    /// </summary>
    [SkippableFact]
    public void UserSuppliedDeclaration_OnARegisteredStub_IsNeverVerified_AndWarns()
    {
        RuntimeHostPublishE2E.RequireGcc();
        var bios = BiosShapedImage();
        var biosPath = _dir.WriteFile("bios-shaped.bin", bios);
        var disc = _dir.WriteFile("disc.bin", new byte[2352]);
        var output = _dir.Combine("game");
        var (exit, error) = PackageFirmware(
            disc, biosPath, AotInputs("aot"), output, "user-supplied",
            "--run-timeout-seconds", "60", "--fallback-segment-budget", "5000", "--fallback-max-transitions", "40");
        exit.Should().Be(0, error);

        var artifact = Path.Combine(output, "game" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : ""));
#pragma warning disable AARC003 // Test-layer process use to drive the bundled game executable
        using var process = Process.Start(new ProcessStartInfo(artifact)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment = { [RecompiledArtifactCodeGen.ExternalBiosEnvironmentVariable] = biosPath },
        })!;
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEndAsync();
        WaitOrKill(process, 120_000, "the bounded run must finish");
        var stderr = stderrTask.GetAwaiter().GetResult();
#pragma warning restore AARC003

        var line = stderr.Split('\n').FirstOrDefault(static l => l.StartsWith("{\"kind\":\"firmware-run\"", StringComparison.Ordinal));
        line.Should().NotBeNull("the firmware run writes its summary line: " + stderr);
        var report = JsonDocument.Parse(line!).RootElement;
        report.GetProperty("biosMode").GetString().Should().Be("openbios-synthetic", "the stub is classified by its own digest");
        report.GetProperty("declaredProvenance").GetString().Should().Be("user-supplied");
        stderr.Should().Contain("warning: the image was declared 'user-supplied'", "the declaration is not confirmed by the run");
        report.GetProperty("romSha256").GetString().Should().Be(OpenBiosRomInput.Sha256Hex(bios), "the run reports the digest of the image it loaded");
    }
}
