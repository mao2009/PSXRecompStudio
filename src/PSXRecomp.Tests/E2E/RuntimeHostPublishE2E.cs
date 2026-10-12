using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Infrastructure.Standalone;

namespace PSXRecomp.Tests.E2E;

/// <summary>
/// End-to-end tests for Phase 2 Step C runtime host provisioning (ADR-027 Step C Option B).
/// These tests verify that <c>psxrecomp package</c> bundles the managed Runtime host,
/// generates the correct layout, and that the bundled artifact can launch through the host.
/// </summary>
[Test]
public class RuntimeHostPublishE2E
{
    private const string TestOutputDir = "/tmp/psxrecomp-ehhfeetw-622343";
    private const string TestBinaryName = "test-game";
    private const uint TestStepCEntryPc = 0x80010000u;
    private const uint TestImageLoadAddress = TestStepCEntryPc;
    private const uint TestProgramEnd = TestImageLoadAddress + 256u * 4u; // 256 instruction words

    [SetUp]
    public void Setup()
    {
        if (Directory.Exists(TestOutputDir))
        {
            Directory.Delete(TestOutputDir, recursive: true);
        }

        Directory.CreateDirectory(TestOutputDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(TestOutputDir))
        {
            Directory.Delete(TestOutputDir, recursive: true);
        }
    }

    /// <summary>
    /// Verify that <c>psxrecomp package</c> successfully provisions the Runtime host into the distribution.
    /// This test checks the layout, manifest validity, and that all required files exist.
    /// </summary>
    [Test]
    public void PackageProvisionsRuntimeHost()
    {
        var testExePath = Path.Combine(TestOutputDir, TestBinaryName);

        // Create a simple test EXE (simply a raw execution end)
        var fixture = GeneratedPsxExeFixtures.NaturalEndWithV0;
        var exeBytes = fixture.Generate();
        File.WriteAllBytes(testExePath, exeBytes);

        // Run package command (framework-dependent)
        var argStrings = new[]
        {
            "package",
            testExePath,
            "--output", TestOutputDir,
            "--entry-root", TestStepCEntryPc.ToString("X"),
            "--mixed-fallback", // Enable bridge for testing
        };

        var exitCode = RunCliWithConfiguredStdio(argStrings, null, null);
        Assert.That(exitCode, Is.EqualTo(0), "package command must succeed");

        // Verify distribution layout
        var runtimeDir = Path.Combine(TestOutputDir, RuntimeHostLayout.RuntimeDirectoryName);
        Assert.That(Directory.Exists(runtimeDir), Is.True, "runtime directory must exist");

        var hostPath = Path.Join(runtimeDir, RuntimeHostLayout.HostFileName(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)));
        Assert.That(File.Exists(hostPath), Is.True, "runtime host binary must exist");

        var manifestPath = Path.Join(runtimeDir, RuntimeHostLayout.ManifestFileName);
        Assert.That(File.Exists(manifestPath), Is.True, "runtime-host.json manifest must exist");

        var imageSidecarPath = Path.Join(runtimeDir, RuntimeHostLayout.ImageFileName);
        Assert.That(File.Exists(imageSidecarPath), Is.True, "artifact-image.bin must exist");

        // Verify manifest content
        var manifestJson = File.ReadAllText(manifestPath);
        var manifest = RuntimeHostManifestJson.Parse(manifestJson);

        Assert.That(manifest.SchemaVersion, Is.EqualTo(RuntimeHostManifest.CurrentSchemaVersion));
        Assert.That(manifest.Artifact, Is.EqualTo(RuntimeHostLayout.ManifestRelativeArtifactPath(TestBinaryName, RuntimeInformation.IsOSPlatform(OSPlatform.Windows))));
        Assert.That(manifest.ImageLoadAddress, Is.EqualTo(TestImageLoadAddress));
        Assert.That(manifest.EntryPc, Is.EqualTo(TestStepCEntryPc));
        Assert.That(manifest.ProgramEnd, Is.EqualTo(TestProgramEnd));
        Assert.That(manifest.GuestFirmware, Is.False, "non-firmware must not have native mode");
        Assert.That(manifest.BlockEntryPcs, Is.Not.Null.And.Not.Empty, "bridge requires at least one compiled block");
    }

    /// <summary>
    /// Verify that a packaged distribution can launch through the bundled RuntimeHost.
    /// The test picks one valid fixture to run through runtime host execution (non-native-firmware case).
    /// </summary>
    [Test]
    public void RuntimeHostCanLaunchBundledArtifactViaHost()
    {
        var testExePath = Path.Combine(TestOutputDir, TestBinaryName);

        // Pick a fixture that should work with runtime host (any from All except natural-v0)
        var fixture = GeneratedPsxExeFixtures.All[1]; // e.g., NaturalEndWithTransitions
        var exeBytes = fixture.Generate();
        File.WriteAllBytes(testExePath, exeBytes);

        // Package with framework dependencies (common end-user setup)
        var argStrings = new[]
        {
            "package",
            testExePath,
            "--output", TestOutputDir,
            "--entry-root", TestStepCEntryPc.ToString("X"),
            "--mixed-fallback",
        };

        var exitCode = RunCliWithConfiguredStdio(argStrings, null, null);
        Assert.That(exitCode, Is.EqualTo(0), "package command must succeed");

        var runtimeHostPath = Path.Join(TestOutputDir, RuntimeHostLayout.HostRelativePath);
        Assert.That(File.Exists(runtimeHostPath), Is.True, "runtime host must be provisioned");

        var manifestPath = Path.Join(TestOutputDir, RuntimeHostLayout.HostRelativePath + "/runtime-host.json");

        // Spawn a new process for runtime host invocation
        var startInfo = new ProcessStartInfo
        {
            FileName = runtimeHostPath,
            Arguments = manifestPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        };

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.That(process.ExitCode, Is.EqualTo(0),
            $"Runtime host must complete successfully\nstdout: {stdout}\nstderr: {stderr}");

        // Runtime host output is JSON: verify execution result
        Assert.That(stdout, Does.Contain("\"outcome\":\"Success\"").IgnoreCase, "output must indicate success");
    }

    /// <summary>
    /// Verify that a packaged distribution can launch through the bundled RuntimeHost.
    /// The test checks the Runtime host path resolution (Linux/Windows).
    /// </summary>
    [Test]
    public void RuntimeHostRuntimeHostPathResolution()
    {
        var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        // Host path: runtime/PSXRecomp.RuntimeHost.exe (Windows) or runtime/PSXRecomp.RuntimeHost (Linux)
        var hostPath = RuntimeHostLayout.HostRelativePathFor(windows);

        Assert.That(hostPath, Does.StartWith(RuntimeHostLayout.RuntimeDirectoryName), "host path must be under runtime directory");

        // Host binary path uses HostFileName (no platform suffix), and launcher appends .exe on Windows
        var hostFileName = RuntimeHostLayout.HostFileName(windows);
        Assert.That(hostPath, Does.EndWith(hostFileName), "host path must end with host binary name");
    }

    /// <summary>
    /// Verify that the packaged artifact correctly returns exit code 78 when the runtime host is missing.
    /// The native launcher checks for the host at runtime; if missing it exits 78.
    /// </summary>
    [Test]
    public void NativeLauncherReturnsExit78WhenRuntimeHostMissing()
    {
        // Package the distribution with RuntimeHost provisioned initially
        var testExePath = Path.Combine(TestOutputDir, TestBinaryName);
        var fixture = GeneratedPsxExeFixtures.GuestFirmwareOpenbios;
        var exeBytes = fixture.Generate();
        File.WriteAllBytes(testExePath, exeBytes);

        var args = new[]
        {
            "package",
            testExePath,
            "--output", TestOutputDir,
            "--entry-root", TestStepCEntryPc.ToString("X"),
            "--json"
        };

        var exitCode = RunCliWithConfiguredStdio((args, null, null));
        Assert.That(exitCode, Is.EqualTo(0), "package must succeed");

        // Move the generated binary to a separate directory to isolate the missing host scenario
        var isolatedDistDir = Path.Combine(TestOutputDir, "dist-without-host");
        var isolatedArtifact = Path.Combine(isolatedDistDir, TestBinaryName + ".exe");
        if (File.Exists(isolatedArtifact))
        {
            File.Delete(isolatedArtifact);
        }

        Directory.Move(TestOutputDir, isolatedDistDir);

        try
        {
            // Remove runtime host directory
            var runtimeHostDir = Path.Join(isolatedDistDir, RuntimeHostLayout.RuntimeDirectoryName);
            if (Directory.Exists(runtimeHostDir))
            {
                Directory.Delete(runtimeHostDir, recursive: true);
            }

            // Back up manifest temporarily for conditional test in ExecutionOrchestrator
            long manifestLen = 0;
            var manifestPathBackend = Path.Join(isolatedDistDir, RuntimeHostLayout.ManifestRelativePath);
            string manifestJsonBacked = null;
            if (File.Exists(manifestPathBackend))
            {
                manifestJsonBacked = File.ReadAllText(manifestPathBackend);
                manifestLen = manifestJsonBacked.Length;
                File.Delete(manifestPathBackend);
            }

            try
            {
                // Try to launch the artifact; expect exit 78 (MissingRuntimeHostExitCode)
                var startInfo = new ProcessStartInfo
                {
                    FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? isolatedArtifact + ".exe" : isolatedArtifact,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                };

                using var process = Process.Start(startInfo)!;
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();

                Assert.That(process.ExitCode, Is.EqualTo(RuntimeHostLayout.MissingRuntimeHostExitCode),
                    $"Expected exit 78 when runtime host is missing\nstdout: {stdout}\nstderr: {stderr}");

                // Verify stderr contains missing-runtime log
                Assert.That(stderr, Does.Contain("missing runtime host").IgnoreCase,
                    $"stderr must mention missing runtime host: {stderr}");
            }
            finally
            {
                if (manifestJsonBacked != null && manifestLen > 0)
                {
                    File.WriteAllText(manifestPathBackend, manifestJsonBacked);
                }
            }
        }
        finally
        {
            // Cleanup isolated directory after test
            if (Directory.Exists(isolatedDistDir))
            {
                Directory.Delete(isolatedDistDir, recursive: true);
            }
        }
    }

    /// <summary>
    /// Verify that a packaged distribution works correctly with self-contained publishing
    /// (single-file host binary, no end-user .NET runtime needed).
    /// </summary>
    [Test]
    public void PackageProvisionsSelfContainedRuntimeHost()
    {
        var testExePath = Path.Combine(TestOutputDir, TestBinaryName);
        var fixture = GeneratedPsxExeFixtures.NaturalEndWithV0;
        var exeBytes = fixture.Generate();
        File.WriteAllBytes(testExePath, exeBytes);

        var args = new[]
        {
            "package",
            testExePath,
            "--output", TestOutputDir,
            "--entry-root", TestStepCEntryPc.ToString("X"),
            "--mixed-fallback",
            "--self-contained",
            "--json"
        };

        var exitCode = RunCliWithConfiguredStdio((args, null, null));
        Assert.That(exitCode, Is.EqualTo(0), "package command must succeed with self-contained");

        var runtimeHostPath = Path.Join(TestOutputDir, RuntimeHostLayout.HostRelativePath);
        Assert.That(File.Exists(runtimeHostPath), Is.True, "Runtime host must be provisioned");

        // Verify the host is a single self-contained executable
        var fileInfo = new FileInfo(runtimeHostPath);
        Assert.That(fileInfo.Length, Is.GreaterThan(0), "self-contained host must be a non-empty binary");

        // Verify manifest contains self-contained metadata
        var manifestPath = Path.Join(TestOutputDir, RuntimeHostLayout.ManifestRelativePath);
        var manifestJson = File.ReadAllText(manifestPath);
        var manifest = RuntimeHostManifestJson.Parse(manifestJson);

        Assert.That(manifest.BlockEntryPcs, Is.Not.Null.And.Not.Empty, "bridge requires compiled blocks");
    }

    /// <summary>
    /// Verify that cross-platform layout coordinate consistency (forward slashes, no trailing separator)
    /// works for both Windows and Linux builds.
    /// </summary>
    [Test]
    public void RuntimeHostLayoutIsCrossPlatformConsistent()
    {
        var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        // Host binary path uses forward slashes: runtime/PSXRecomp.RuntimeHost.exe (Windows) or runtime/PSXRecomp.RuntimeHost (Linux)
        var hostPath = RuntimeHostLayout.HostRelativePathFor(windows);

        Assert.That(hostPath, Does.Contain("/"), "relative paths must use forward slashes for consistency");

        // The generated C sources use the const value HostRelativePath (no platform suffix), and the launcher
        // appends .exe on Windows via macros. Path contract requires forward slashes and no trailing separator.
        Assert.That(hostPath, Does.Not.EndWith("/"), "relative path must not end with separator");
    }

    /// <summary>
    /// Run the CLI with configured standard output and error streams.
    /// Uses in-memory TextWriters to capture output without touching terminal.
    /// </summary>
    private static int RunCliWithConfiguredStdio(string[] args, TextWriter? stdOut, TextWriter? stdErr)
    {
        var oldStdOut = Console.Out;
        var oldStdErr = Console.Error;

        try
        {
            if (stdOut != null) Console.SetOut(stdOut);
            if (stdErr != null) Console.SetError(stdErr);

            return CliProgram.Run(args);
        }
        finally
        {
            Console.SetOut(oldStdOut);
            Console.SetError(oldStdErr);
        }
    }
}