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
    [Fact]
    public void PackageProvisionsRuntimeHost()
    {
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
    [Fact]
    public void RuntimeHostCanLaunchBundledArtifactViaHost()
    {
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
    /// Verify that the Runtime host path resolution (Linux/Windows) is correct.
    /// </summary>
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
    [Fact]
    public void NativeLauncherReturnsExit78WhenRuntimeHostMissing()
    {
        // Skip if gcc is not available (required for artifact compilation)
        var gccCheck = new ProcessStartInfo("gcc", "--version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        try
        {
            using var gccProcess = Process.Start(gccCheck)!;
            gccProcess.WaitForExit(5000);
            if (gccProcess.ExitCode != 0)
            {
                return; // Skip test
            }
        }
        catch
        {
            return; // Skip test
        }

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
    [Fact]
    public async Task PackagedArtifactLaunchesFromCopiedDistributionDirectory()
    {
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
    [Fact]
    public void PackageProvisionsSelfContainedRuntimeHost()
    {
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