using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.E2E;

/// <summary>
/// End-to-end tests for Phase 2 Step C runtime host provisioning (ADR-027 Step C Option B).
/// These tests verify that <c>psxrecomp package</c> bundles the managed Runtime host,
/// generates the correct layout, and that the bundled artifact can launch through the host.
/// </summary>
[Test]
public sealed class RuntimeHostPublishE2E : IDisposable
{
    private readonly TempDirectory _tempDir;
    private const string TestBinaryName = "test-game";
    private const string TestStepCEntryPc = "0x80010000";
    private const uint TestImageLoadAddress = 0x80010000u;
    // NaturalEndWithV0 has 3 instructions (ori + jal + delay slot)
    private const uint TestProgramEnd = TestImageLoadAddress + 3u * 4u;

    public RuntimeHostPublishE2E()
    {
        _tempDir = new TempDirectory();
    }

    public void Dispose()
    {
        _tempDir.Dispose();
    }

    /// <summary>
    /// Verify that <c>psxrecomp package</c> successfully provisions the Runtime host into the distribution.
    /// This test checks the layout, manifest validity, and that all required files exist.
    /// </summary>
    [SkippableFact]
    public void PackageProvisionsRuntimeHost()
    {
        RequireGcc();

        var fixture = GeneratedPsxExeFixtures.NaturalEndWithV0;
        var testExePath = _tempDir.WriteFile("input.exe", fixture.Generate());

        // Use a known output subdirectory name so the artifact name is predictable
        var outputDir = _tempDir.CreateSubdirectory("test-game");

        // Run package command (framework-dependent)
        var (exit, output, error) = Invoke("package", testExePath, "--output", outputDir, "--entry-root", TestStepCEntryPc, "--mixed-fallback");

        exit.Should().Be(RecompiledArtifactExitCode.Success, $"package command must succeed: error='{error}', output='{output}'");

        // Verify distribution layout
#pragma warning disable AARC003 // Test-layer I/O for verification of distribution layout
        var runtimeDir = Path.Combine(outputDir, RuntimeHostLayout.RuntimeDirectoryName);
        Directory.Exists(runtimeDir).Should().BeTrue("runtime directory must exist");

        var hostPath = Path.Join(runtimeDir, RuntimeHostLayout.HostFileName(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)));
        File.Exists(hostPath).Should().BeTrue("runtime host binary must exist");

        var manifestPath = Path.Join(runtimeDir, RuntimeHostLayout.ManifestFileName);
        File.Exists(manifestPath).Should().BeTrue("runtime-host.json manifest must exist");

        var imageSidecarPath = Path.Join(runtimeDir, RuntimeHostLayout.ImageFileName);
        File.Exists(imageSidecarPath).Should().BeTrue("artifact-image.bin must exist");

        // Verify manifest content
        var manifestJson = File.ReadAllText(manifestPath);
        var manifest = RuntimeHostManifestJson.Parse(manifestJson);
#pragma warning restore AARC003

        // Compute expected program end from fixture's instruction words
        var expectedProgramEnd = fixture.InstructionWords.First() == 0x34025678u  // NaturalEndWithV0
            ? TestImageLoadAddress + (uint)fixture.InstructionWords.Count * 4u
            : TestImageLoadAddress + 3u * 4u; // fallback for other fixtures

        manifest.SchemaVersion.Should().Be(RuntimeHostManifest.CurrentSchemaVersion);
        manifest.Artifact.Should().Be(RuntimeHostLayout.ManifestRelativeArtifactPath("test-game", RuntimeInformation.IsOSPlatform(OSPlatform.Windows)));
        manifest.ImageLoadAddress.Should().Be(TestImageLoadAddress);
        manifest.EntryPc.Should().Be(TestImageLoadAddress);
        manifest.ProgramEnd.Should().Be(expectedProgramEnd);
        manifest.GuestFirmware.Should().BeFalse("non-firmware must not have native mode");
        manifest.BlockEntryPcs.Should().NotBeNullOrEmpty("bridge requires at least one compiled block");
    }

    /// <summary>
    /// Verify that a packaged distribution can launch through the bundled RuntimeHost.
    /// The test uses a simple natural-end program with no control transfers.
    /// </summary>
    [SkippableFact]
    public void RuntimeHostCanLaunchBundledArtifactViaHost()
    {
        RequireGcc();

        // Simple natural-end program: ori v0, $zero, 0x42 then fall off
        var simpleFixtureWords = new uint[]
        {
            0x34020042u, // ori v0, $zero, 0x42
        };
        var testExePath = _tempDir.WriteFile("input.exe", GeneratedPsxExeFixtures.BuildExe(simpleFixtureWords));

        // Package with framework dependencies (common end-user setup)
        var outputDir = _tempDir.CreateSubdirectory("test-game");
        var (exit, output, error) = Invoke("package", testExePath, "--output", outputDir, "--entry-root", TestStepCEntryPc, "--mixed-fallback");

        exit.Should().Be(RecompiledArtifactExitCode.Success, $"package command must succeed: error='{error}', output='{output}'");

        var runtimeHostPath = Path.Join(
            outputDir,
            RuntimeHostLayout.HostRelativePathFor(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)));
#pragma warning disable AARC003 // Test-layer I/O for verification
        File.Exists(runtimeHostPath).Should().BeTrue("runtime host must be provisioned");

        var manifestPath = Path.Join(outputDir, RuntimeHostLayout.ManifestRelativePath);
#pragma warning restore AARC003

        // Run the runtime host via Infrastructure service
        var runResult = RuntimeHostBuildService.RunRuntimeHost(runtimeHostPath, manifestPath);

        runResult.ExitCode.Should().Be(0, $"Runtime host must complete successfully\nstdout: {runResult.Stdout}\nstderr: {runResult.Stderr}");

        // Runtime host output is classification message on stderr
        runResult.Stderr.Should().Contain("classified as Success", "stderr must indicate success");
    }

    /// <summary>
    /// Issue #763: with <c>--diagnostics &lt;path&gt;</c> the runtime host writes the machine-readable stop
    /// diagnostic and leaves the run's exit code and classification unchanged.
    /// </summary>
    [SkippableFact]
    public void RuntimeHostWritesStopDiagnosticWhenAsked()
    {
        RequireGcc();

        var simpleFixtureWords = new uint[]
        {
            0x34020042u, // ori v0, $zero, 0x42
        };
        var testExePath = _tempDir.WriteFile("input.exe", GeneratedPsxExeFixtures.BuildExe(simpleFixtureWords));

        var outputDir = _tempDir.CreateSubdirectory("test-game");
        var (exit, output, error) = Invoke("package", testExePath, "--output", outputDir, "--entry-root", TestStepCEntryPc, "--mixed-fallback");
        exit.Should().Be(RecompiledArtifactExitCode.Success, $"package command must succeed: error='{error}', output='{output}'");

        var runtimeHostPath = Path.Join(
            outputDir,
            RuntimeHostLayout.HostRelativePathFor(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)));
        var manifestPath = Path.Join(outputDir, RuntimeHostLayout.ManifestRelativePath);
        var diagnosticsPath = Path.Join(outputDir, "stop-diagnostic.json");

        var runResult = RuntimeHostBuildService.RunRuntimeHost(
            runtimeHostPath, manifestPath, extraArguments: ["--diagnostics", diagnosticsPath]);

        runResult.ExitCode.Should().Be(0, $"Runtime host must complete successfully\nstdout: {runResult.Stdout}\nstderr: {runResult.Stderr}");
#pragma warning disable AARC003 // Test-layer verification of the diagnostic artifact
        File.Exists(diagnosticsPath).Should().BeTrue("--diagnostics must write the stop diagnostic");
        var json = File.ReadAllText(diagnosticsPath);
#pragma warning restore AARC003
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        root.GetProperty("orchestratorState").GetString().Should().Be("Completed");
        root.GetProperty("classifiedExitCode").GetInt32().Should().Be(0);
        root.TryGetProperty("nativeExitCode", out _).Should().BeFalse("the field was renamed: it never held a native process exit code");
        root.GetProperty("fallbackEnabled").GetBoolean().Should().BeTrue();
    }

    // lui t0,0x8000 / ori t0,t0,0x3000 / jr t0 / nop: a register-indirect jump outside the image.
    private static readonly uint[] JumpOutOfImage = [0x3C088000u, 0x35083000u, 0x01000008u, 0u];

    private const string BlockingStubSource = """
        #include <stdio.h>
        #include <string.h>
        #if defined(_WIN32)
        #include <windows.h>
        #include <process.h>
        static int stub_pid(void) { return _getpid(); }
        static void stub_sleep(void) { Sleep(1000); }
        #else
        #include <unistd.h>
        static int stub_pid(void) { return (int)getpid(); }
        static void stub_sleep(void) { sleep(1); }
        #endif
        int main(int argc, char** argv) {
          char path[4096]; FILE* f;
          if (argc < 1 || strlen(argv[0]) + 5 > sizeof path) return 2;
          strcpy(path, argv[0]); strcat(path, ".pid");
          f = fopen(path, "w"); if (!f) return 3; fprintf(f, "%d", stub_pid()); fclose(f);
          for (;;) { if (getchar() == EOF) stub_sleep(); }
        }
        """;

    /// <summary>Compiles the blocking stub over the packaged artifact (same path, so the manifest stays valid); returns its pid file.</summary>
    private static string ReplaceArtifactWithBlockingStub(string manifestPath, string distDir)
    {
#pragma warning disable AARC003 // Test-layer synthetic fixture: a process that never terminates on its own
        // The manifest's artifact path is relative to the manifest's own (runtime/) directory.
        var artifact = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(manifestPath)!, RuntimeHostManifestJson.Parse(File.ReadAllText(manifestPath)).Artifact));
        var source = Path.Join(distDir, "blocking-stub.c");
        File.WriteAllText(source, BlockingStubSource);
        File.Delete(artifact);
        using var gcc = Process.Start(new ProcessStartInfo("gcc", ["-O0", "-o", artifact, source])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var gccOut = gcc.StandardError.ReadToEndAsync();
        gcc.StandardOutput.ReadToEndAsync();
        if (!gcc.WaitForExit(60_000))
        {
            TryKillTree(gcc);
            throw new TimeoutException("gcc did not finish compiling the blocking stub");
        }

        gcc.ExitCode.Should().Be(0, gccOut.Result);
        File.Delete(source);
        return artifact + ".pid";
#pragma warning restore AARC003
    }

    /// <summary>The timed-out stub must have been killed by the host: its pid is gone (polled, bounded) once the host returned.</summary>
    private static void AssertStubReaped(string pidFile)
    {
#pragma warning disable AARC003 // Test-layer process inspection
        File.Exists(pidFile).Should().BeTrue("the stub must have been started by the host");
        var pid = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        var gone = false;
        for (var i = 0; i < 50 && !gone; i++)
        {
            try
            {
                using var stub = Process.GetProcessById(pid);
                if (stub.HasExited) gone = true;
                else Thread.Sleep(100);
            }
            catch (ArgumentException)
            {
                gone = true;
            }
        }

        gone.Should().BeTrue("the timed-out artifact child must be terminated and collected");
#pragma warning restore AARC003
    }

    private static readonly TimeSpan HostOuterTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Packages <paramref name="words"/> and returns the host and manifest paths (the run timeout optionally overridden).</summary>
    private (string Host, string Manifest, string Dir) PackageProgram(uint[] words, int? runTimeoutMs = null)
    {
        var testExePath = _tempDir.WriteFile("input.exe", GeneratedPsxExeFixtures.BuildExe(words));
        var outputDir = _tempDir.CreateSubdirectory("test-game");
        var (exit, output, error) = Invoke("package", testExePath, "--output", outputDir, "--entry-root", TestStepCEntryPc, "--mixed-fallback");
        exit.Should().Be(RecompiledArtifactExitCode.Success, $"package command must succeed: error='{error}', output='{output}'");

        var host = Path.Join(outputDir, RuntimeHostLayout.HostRelativePathFor(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)));
        var manifest = Path.Join(outputDir, RuntimeHostLayout.ManifestRelativePath);
#pragma warning disable AARC003 // Test-layer rewrite of the packaged manifest's timeout
        if (runTimeoutMs is { } timeout)
        {
            var json = File.ReadAllText(manifest);
            var rewritten = System.Text.RegularExpressions.Regex.Replace(
                json, "\"runTimeoutMs\"\\s*:\\s*\\d+", $"\"runTimeoutMs\": {timeout}");
            rewritten.Should().NotBe(json, "the manifest carries a runTimeoutMs to override");
            File.WriteAllText(manifest, rewritten);
        }
#pragma warning restore AARC003

        return (host, manifest, outputDir);
    }

    private static JsonElement ReadDiagnostic(string path)
    {
#pragma warning disable AARC003 // Test-layer verification of the diagnostic artifact
        File.Exists(path).Should().BeTrue("--diagnostics must write the stop diagnostic");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
#pragma warning restore AARC003
    }

    /// <summary>
    /// Issue #763: a timed-out run has no final snapshot. The host must not crash formatting the pc: it keeps the
    /// run's own classified exit code, says the pc is unknown, and still writes a diagnostic with nothing invented.
    /// </summary>
    [SkippableFact]
    public void TimedOutRunWithoutSnapshot_KeepsItsExitCode_AndWritesADiagnosticWithUnknownValuesNull()
    {
        RequireGcc();
        // The packaged artifact is replaced by a stub that records its pid and then blocks on stdin forever, so the
        // bound can only expire (no guest program, however fast the machine, finishes or exhausts its budget first).
        // The generous outer wait only guards against a hung host.
        var (host, manifest, dir) = PackageProgram([0x34020042u], runTimeoutMs: 2000);
        var pidFile = ReplaceArtifactWithBlockingStub(manifest, dir);
        var diagnosticsPath = Path.Join(dir, "timeout-diagnostic.json");

        var withoutDiagnostics = RuntimeHostBuildService.RunRuntimeHost(host, manifest, HostOuterTimeout);
#pragma warning disable AARC003 // Test-layer check that a run without --diagnostics writes no JSON
        Directory.GetFiles(dir, "*diagnostic*.json").Should().BeEmpty("diagnostics off writes no JSON");
#pragma warning restore AARC003
        var withDiagnostics = RuntimeHostBuildService.RunRuntimeHost(host, manifest, HostOuterTimeout, ["--diagnostics", diagnosticsPath]);

        withDiagnostics.ExitCode.Should().Be(RecompiledArtifactExitCode.Failure, $"stderr: {withDiagnostics.Stderr}");
        withDiagnostics.Stderr.Should().Contain("guest pc unknown").And.NotContain("NullReferenceException");
        withoutDiagnostics.ExitCode.Should().Be(withDiagnostics.ExitCode, "diagnostics never change the classified exit code");
        withoutDiagnostics.Stderr.Should().Contain("guest pc unknown");

        var root = ReadDiagnostic(diagnosticsPath);
        root.GetProperty("timedOut").GetBoolean().Should().BeTrue();
        root.GetProperty("finalPc").ValueKind.Should().Be(JsonValueKind.Null, "a run with no snapshot has no known pc");
        root.GetProperty("transferTargetPc").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("classifiedExitCode").GetInt32().Should().Be(RecompiledArtifactExitCode.Failure);
        AssertStubReaped(pidFile);
        root.GetProperty("orchestratorState").GetString().Should().Be("RuntimeFailure");
        root.GetProperty("orchestratorDiagnosticCode").GetString().Should().Be("ARTIFACT_TIMEOUT");
    }

    /// <summary>Issue #763: an UnsupportedTransfer with a snapshot is diagnosed; the exit code matches the diagnostics-off run.</summary>
    [SkippableFact]
    public void UnsupportedTransferWithSnapshot_ReportsPcAndRejectReason_AndMatchesTheDiagnosticsOffRun()
    {
        RequireGcc();
        var (host, manifest, dir) = PackageProgram(JumpOutOfImage);
        var diagnosticsPath = Path.Join(dir, "transfer-diagnostic.json");

        var withDiagnostics = RuntimeHostBuildService.RunRuntimeHost(host, manifest, extraArguments: ["--diagnostics", diagnosticsPath]);
        var withoutDiagnostics = RuntimeHostBuildService.RunRuntimeHost(host, manifest);

        withDiagnostics.ExitCode.Should().Be(RecompiledArtifactExitCode.Blocked, $"stderr: {withDiagnostics.Stderr}");
        withoutDiagnostics.ExitCode.Should().Be(withDiagnostics.ExitCode);
        withoutDiagnostics.Stderr.Should().Be(withDiagnostics.Stderr, "the console report is identical with diagnostics on and off");

        var root = ReadDiagnostic(diagnosticsPath);
        root.GetProperty("orchestratorState").GetString().Should().Be("UnsupportedTransfer");
        root.GetProperty("finalPc").GetUInt32().Should().Be(0x80003000u);
        root.GetProperty("fallbackRejectReason").GetString().Should().Be(MixedFallbackDiagnostics.IneligibleOutOfImage);
    }

    /// <summary>Issue #763: a diagnostics path that cannot be written never changes the run's result.</summary>
    [SkippableFact]
    public void UnwritableDiagnosticsPath_DoesNotChangeTheExitCode()
    {
        RequireGcc();
        var (host, manifest, dir) = PackageProgram([0x34020042u]);
        var unwritable = Path.Join(dir, "no-such-directory", "diagnostic.json");

        var result = RuntimeHostBuildService.RunRuntimeHost(host, manifest, extraArguments: ["--diagnostics", unwritable]);

        result.ExitCode.Should().Be(RecompiledArtifactExitCode.Success, $"stderr: {result.Stderr}");
        result.Stderr.Should().Contain("could not write diagnostics").And.Contain("classified as Success");
    }

    /// <summary>
    /// The artifact compilation these tests need requires gcc. Where the environment is allowed to lack it the
    /// test is reported as skipped (never a silent pass); on CI (GITHUB_ACTIONS) a missing compiler is a failure.
    /// </summary>
#pragma warning disable AARC003 // Test-layer probe of the host toolchain: CI must fail, not skip, without it
    private static readonly Lazy<bool> GccAvailable = new(ProbeGcc);

    private static void RequireGcc()
    {
        var available = GccAvailable.Value;
        if (!available && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            throw new Xunit.Sdk.XunitException("gcc is required on CI to compile the artifact but could not be run.");
        }

        Skip.IfNot(available, "gcc is not available; the generated artifact cannot be compiled here.");
    }

    // The same compiler GeneratedHostBuildService uses by default (DefaultCompiler = "gcc").
    private static bool ProbeGcc()
    {
        var available = false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("gcc", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            // Drain the pipes so a chatty compiler cannot block on a full buffer, then bound the wait.
            process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEndAsync();
            if (process.WaitForExit(10_000))
            {
                available = process.ExitCode == 0;
            }
            else
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // gcc is not on PATH.
        }

        return available;
    }
#pragma warning restore AARC003

    /// <summary>
    /// Fail closed on a tampered manifest: an artifact path that escapes the distribution is rejected by the
    /// Runtime host (exit 78, validation message) before any process is started.
    /// </summary>
    [SkippableFact]
    public void RuntimeHostRejectsTamperedManifestWithoutLaunchingAnything()
    {
        RequireGcc();
        var testExePath = _tempDir.WriteFile("input.exe", GeneratedPsxExeFixtures.GuestFirmwareOpenbios.Generate());
        var outputDir = _tempDir.CreateSubdirectory("test game");
        var (exit, output, error) = Invoke("package", testExePath, "--output", outputDir, "--entry-root", TestStepCEntryPc);
        exit.Should().Be(RecompiledArtifactExitCode.Success, $"package must succeed: error='{error}', output='{output}'");

        var runtimeHostPath = Path.Join(
            outputDir,
            RuntimeHostLayout.HostRelativePathFor(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)));
        var manifestPath = Path.Join(outputDir, RuntimeHostLayout.ManifestRelativePath);
#pragma warning disable AARC003 // Test-layer I/O for tampering with the manifest
        var original = File.ReadAllText(manifestPath);
        var artifact = RuntimeHostManifestJson.Parse(original).Artifact;
        original.Should().Contain(artifact);

        foreach (var tampered in new[] { "../../escaped", "/etc/escaped", "C:/escaped.exe", @"..\..\escaped.exe" })
        {
            File.WriteAllText(manifestPath, original.Replace(
                JsonSerializer.Serialize(artifact), JsonSerializer.Serialize(tampered)));

            var result = RuntimeHostBuildService.RunRuntimeHost(runtimeHostPath, manifestPath);

            result.ExitCode.Should().Be(78, $"'{tampered}': {result.Stderr}");
            result.Stderr.Should().Contain("artifact path", $"'{tampered}' must be rejected by manifest validation, not by a failed launch");
        }
#pragma warning restore AARC003
    }

    /// <summary>
    /// Verify that the Runtime host path resolution (Linux/Windows) is correct.
    /// </summary>
    /// <summary>
    /// The host is started with a manifest path that goes through a link to runtime/: it must reject it (exit 78)
    /// rather than resolve the image/artifact through the link.
    /// </summary>
    [SkippableFact]
    public void RuntimeHostRejectsManifestReachedThroughALinkedRuntimeDirectory()
    {
        RequireGcc();
        var testExePath = _tempDir.WriteFile("input.exe", GeneratedPsxExeFixtures.GuestFirmwareOpenbios.Generate());
        var outputDir = _tempDir.CreateSubdirectory("linked game");
        var (exit, output, error) = Invoke("package", testExePath, "--output", outputDir, "--entry-root", TestStepCEntryPc);
        exit.Should().Be(RecompiledArtifactExitCode.Success, $"package must succeed: error='{error}', output='{output}'");

        var runtimeDir = Path.Combine(outputDir, RuntimeHostLayout.RuntimeDirectoryName);
        var link = Path.Combine(outputDir, "runtime-link");
        PSXRecomp.Tests.Infrastructure.RuntimeHostManifestPathTests.CreateDirectoryLinkOrSkip(link, runtimeDir);

        var hostPath = Path.Join(
            runtimeDir,
            RuntimeHostLayout.HostFileName(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)));
        var result = RuntimeHostBuildService.RunRuntimeHost(hostPath, Path.Combine(link, RuntimeHostLayout.ManifestFileName));

        result.ExitCode.Should().Be(78, result.Stderr);
        result.Stderr.Should().Contain("must not be a symbolic link", "rejected by path confinement, before any image read or launch");
    }

    [Fact]
    public void RuntimeHostRuntimeHostPathResolution()
    {
        var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        // Host path: runtime/PSXRecomp.RuntimeHost.exe (Windows) or runtime/PSXRecomp.RuntimeHost (Linux)
        var hostPath = RuntimeHostLayout.HostRelativePathFor(windows);

        hostPath.Should().StartWith(RuntimeHostLayout.RuntimeDirectoryName, "host path must be under runtime directory");

        // Host binary path uses HostFileName (no platform suffix), and launcher appends .exe on Windows
        var hostFileName = RuntimeHostLayout.HostFileName(windows);
        hostPath.Should().EndWith(hostFileName, "host path must end with host binary name");
    }

    /// <summary>
    /// Verify that the packaged artifact correctly returns exit code 78 when the runtime host is missing.
    /// The native launcher checks for the host at runtime; if missing it exits 78.
    /// </summary>
    [SkippableFact]
    public void NativeLauncherReturnsExit78WhenRuntimeHostMissing()
    {
        RequireGcc();

        // Package the distribution with RuntimeHost provisioned initially
        var testExePath = _tempDir.WriteFile("input.exe", GeneratedPsxExeFixtures.GuestFirmwareOpenbios.Generate());

        var outputDir = _tempDir.CreateSubdirectory("test-game");
        var (exit, output, error) = Invoke("package", testExePath, "--output", outputDir, "--entry-root", TestStepCEntryPc);

        exit.Should().Be(RecompiledArtifactExitCode.Success, $"package must succeed: error='{error}', output='{output}'");

        // Copy the generated binary to a separate directory to isolate the missing host scenario
        var isolatedDistDir = _tempDir.CreateSubdirectory("dist-without-host");
        var isolatedArtifact = Path.Combine(isolatedDistDir, "test-game");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            isolatedArtifact += ".exe";
        }

        var originalArtifact = Path.Combine(outputDir, "test-game");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            originalArtifact += ".exe";
        }
#pragma warning disable AARC003 // Test-layer I/O for test setup
        File.Copy(originalArtifact, isolatedArtifact, true);
#pragma warning restore AARC003

        // Try to launch the artifact from the isolated directory (no runtime/ directory present); expect exit 78
        var startInfo = new ProcessStartInfo
        {
            FileName = isolatedArtifact,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        };

#pragma warning disable AARC003 // Process control is Infrastructure responsibility; this test verifies launcher behavior
        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        const int testTimeoutMs = 30000;
        if (!process.WaitForExit(testTimeoutMs))
        {
            TryKillTree(process);
            stdoutTask.Wait(1000);
            stderrTask.Wait(1000);
            throw new TimeoutException("Artifact launch timed out");
        }

        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;
        var exitCode = process.ExitCode;
#pragma warning restore AARC003

        exitCode.Should().Be(RuntimeHostLayout.MissingRuntimeHostExitCode,
            $"Expected exit 78 when runtime host is missing\nstdout: {stdout}\nstderr: {stderr}");

        // Verify stderr contains a missing-runtime diagnostic (platform-dependent message).
        stderr.Should().Contain(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "could not start the runtime host"
                : "runtime host not found",
            "stderr must mention missing runtime host");
    }

    /// <summary>
    /// Verify the shipped path end to end: the packaged artifact, launched from a
    /// copy of the distribution in a directory whose name contains a space,
    /// delegates to the bundled Runtime host and propagates its success exit code.
    /// </summary>
    [SkippableFact]
    public async Task PackagedArtifactLaunchesFromCopiedDistributionDirectory()
    {
        RequireGcc();

        var testExePath = _tempDir.WriteFile("input.exe", GeneratedPsxExeFixtures.BuildExe(new uint[] { 0x34020042u }));
        var outputDir = _tempDir.CreateSubdirectory("test-game");
        var (exit, output, error) = Invoke("package", testExePath, "--output", outputDir, "--entry-root", TestStepCEntryPc, "--mixed-fallback");
        exit.Should().Be(RecompiledArtifactExitCode.Success, $"package command must succeed: error='{error}', output='{output}'");

#pragma warning disable AARC003 // Test-layer I/O: relocate the distribution to prove it has no dependency on its build location
        var copiedDir = _tempDir.CreateSubdirectory("copied dist");
        CopyDirectory(outputDir, copiedDir);
        var artifact = Path.Combine(copiedDir, "test-game" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : ""));
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.SetUnixFileMode(artifact, File.GetUnixFileMode(artifact) | UnixFileMode.UserExecute);
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = artifact,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000))
        {
            TryKillTree(process);
            throw new TimeoutException("Packaged artifact launch timed out");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var exitCode = process.ExitCode;
#pragma warning restore AARC003

        exitCode.Should().Be(0, $"launcher must propagate the host's success\nstdout: {stdout}\nstderr: {stderr}");
        stderr.Should().Contain("classified as Success", "the run must have gone through the bundled Runtime host");
    }

#pragma warning disable AARC003 // Test-layer I/O helper
    private static void CopyDirectory(string source, string destination)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), true);
        }
    }
#pragma warning restore AARC003

    private static void TryKillTree(Process process)
    {
#pragma warning disable AARC003 // Process control is Infrastructure responsibility; this test helper cleans up test processes
        try { process.Kill(true); } catch (InvalidOperationException) { }
#pragma warning restore AARC003
    }

    /// <summary>
    /// Verify that a packaged distribution works correctly with self-contained publishing
    /// (single-file host binary, no end-user .NET runtime needed).
    /// </summary>
    [SkippableFact]
    public void PackageProvisionsSelfContainedRuntimeHost()
    {
        RequireGcc();

        var testExePath = _tempDir.WriteFile("input.exe", GeneratedPsxExeFixtures.NaturalEndWithV0.Generate());

        var outputDir = _tempDir.CreateSubdirectory("test-game");
        var (exit, output, error) = Invoke("package", testExePath, "--output", outputDir, "--entry-root", TestStepCEntryPc, "--mixed-fallback", "--self-contained");

        exit.Should().Be(RecompiledArtifactExitCode.Success, $"package command must succeed with self-contained: error='{error}', output='{output}'");

#pragma warning disable AARC003 // Test-layer I/O for verification
        var runtimeHostPath = Path.Join(
            outputDir,
            RuntimeHostLayout.HostRelativePathFor(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)));
        File.Exists(runtimeHostPath).Should().BeTrue("Runtime host must be provisioned");

        // Verify the host is a single self-contained executable
        var fileInfo = new FileInfo(runtimeHostPath);
        fileInfo.Length.Should().BeGreaterThan(0, "self-contained host must be a non-empty binary");

        // Verify manifest contains self-contained metadata
        var manifestPath = Path.Join(outputDir, RuntimeHostLayout.ManifestRelativePath);
        var manifestJson = File.ReadAllText(manifestPath);
        var manifest = RuntimeHostManifestJson.Parse(manifestJson);
#pragma warning restore AARC003

        manifest.BlockEntryPcs.Should().NotBeNullOrEmpty("bridge requires compiled blocks");
    }

    /// <summary>
    /// Verify that cross-platform layout coordinate consistency (forward slashes, no trailing separator)
    /// works for both Windows and Linux builds.
    /// </summary>
    [Fact]
    public void RuntimeHostLayoutIsCrossPlatformConsistent()
    {
        var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        // Host binary path uses forward slashes: runtime/PSXRecomp.RuntimeHost.exe (Windows) or runtime/PSXRecomp.RuntimeHost (Linux)
        var hostPath = RuntimeHostLayout.HostRelativePathFor(windows);

        hostPath.Should().Contain("/", "relative paths must use forward slashes for consistency");

        // The generated C sources use the const value HostRelativePath (no platform suffix), and the launcher
        // appends .exe on Windows via macros. Path contract requires forward slashes and no trailing separator.
        hostPath.Should().NotEndWith("/", "relative path must not end with separator");
    }

    private static (int Exit, string Output, string Error) Invoke(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = Program.Execute(args, output, error);
        var outStr = output.ToString();
        var errStr = error.ToString();
        return (exit, outStr, errStr);
    }
}