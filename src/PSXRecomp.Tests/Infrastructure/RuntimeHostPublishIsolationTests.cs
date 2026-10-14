using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using PSXRecomp.Infrastructure;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Regression coverage for <see cref="RuntimeHostBuildService.ProvisionRuntimeHost"/> running a
/// self-contained and a framework-dependent publish at the same time (PR #776). Before the fix the
/// two calls shared the Runtime host project's <c>obj/</c> (notably <c>project.assets.json</c> and the
/// MSBuild intermediates), so a concurrent framework-dependent publish could pick up a self-contained
/// <c>runtimeconfig.json</c> (or vice versa). The CI failure surfaced as a host started with no
/// framework and a missing <c>libhostpolicy.so</c>.
/// </summary>
[Test]
public sealed class RuntimeHostPublishIsolationTests
{
    /// <summary>
    /// Runs the two publish modes concurrently and asserts each artifact is internally consistent,
    /// executable where applicable, and free of the other mode's outputs.
    /// </summary>
    [SkippableFact]
    public async Task ParallelProvision_SelfContainedAndFrameworkDependent_AreIsolatedAndBothSucceed()
    {
        using var temp = new TempDirectory();
        var frameworkDir = temp.CreateSubdirectory("framework-dependent");
        var selfContainedDir = temp.CreateSubdirectory("self-contained");

        var outcome = await RunParallelPairAsync(frameworkDir, selfContainedDir);

        outcome.FrameworkError.Should().BeNull($"the framework-dependent publish must succeed: {outcome.FrameworkError}");
        outcome.SelfContainedError.Should().BeNull($"the self-contained publish must succeed: {outcome.SelfContainedError}");
        outcome.FrameworkResult.Should().NotBeNull();
        outcome.SelfContainedResult.Should().NotBeNull();

        // The two dedicated threads rendezvous on a barrier before marking themselves in flight, and
        // each stays in flight for the whole (multi-second) restore+publish. Sampling the in-flight
        // counter therefore proves the two calls actually overlapped; Task.WhenAll alone would not.
        // This is a best-effort observation of overlap, not a proof that the shared-obj race is
        // reproduced (the race is timing-dependent and pre-fix); its value is that the corrective
        // path is exercised under real concurrency.
        outcome.MaxConcurrentProvisions.Should().BeGreaterThanOrEqualTo(
            2, "both provision operations must have been in flight at the same time");

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var frameworkHost = Path.Combine(frameworkDir, RuntimeHostLayout.HostFileName(isWindows));
        var selfContainedHost = Path.Combine(selfContainedDir, RuntimeHostLayout.HostFileName(isWindows));

        var frameworkHostLength = AssertFrameworkDependentArtifact(frameworkDir, frameworkHost);
        AssertSelfContainedArtifact(selfContainedDir, selfContainedHost, frameworkHostLength);

        // Requirement 4: the self-contained artifact must actually run as a standalone executable.
        AssertRunsAsExecutable(selfContainedHost);
    }

    /// <summary>
    /// Optional repeated stress run of the parallel pair, to catch the timing-dependent race over
    /// many iterations without slowing the default CI suite. Skipped unless
    /// <c>PSXRECOMP_PUBLISH_ISOLATION_STRESS</c> is set to a positive iteration count.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "PublishIsolationStress")]
    public async Task ParallelProvision_RepeatedStress_RemainsIsolated()
    {
#pragma warning disable AARC003 // Test-layer opt-in switch for an otherwise-skipped stress run.
        var raw = Environment.GetEnvironmentVariable("PSXRECOMP_PUBLISH_ISOLATION_STRESS");
#pragma warning restore AARC003
        Skip.IfNot(
            int.TryParse(raw, out var iterations) && iterations > 0,
            "set PSXRECOMP_PUBLISH_ISOLATION_STRESS=<iterations> to run the repeated parallel publish stress");

        for (var i = 0; i < iterations; i++)
        {
            using var temp = new TempDirectory();
            var frameworkDir = temp.CreateSubdirectory("framework-dependent");
            var selfContainedDir = temp.CreateSubdirectory("self-contained");

            var outcome = await RunParallelPairAsync(frameworkDir, selfContainedDir);

            outcome.FrameworkError.Should().BeNull($"iteration {i}: framework-dependent publish: {outcome.FrameworkError}");
            outcome.SelfContainedError.Should().BeNull($"iteration {i}: self-contained publish: {outcome.SelfContainedError}");
            outcome.MaxConcurrentProvisions.Should().BeGreaterThanOrEqualTo(2, $"iteration {i}: the pair must overlap");

            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var frameworkHost = Path.Combine(frameworkDir, RuntimeHostLayout.HostFileName(isWindows));
            var selfContainedHost = Path.Combine(selfContainedDir, RuntimeHostLayout.HostFileName(isWindows));
            var frameworkHostLength = AssertFrameworkDependentArtifact(frameworkDir, frameworkHost);
            AssertSelfContainedArtifact(selfContainedDir, selfContainedHost, frameworkHostLength);
        }
    }

    /// <summary>Cleanup must remove a populated tree.</summary>
    [Fact]
    public void TryDeleteDirectory_RemovesAPopulatedTree()
    {
        using var temp = new TempDirectory();
        var nested = Path.Combine(temp.FullPath, "artifacts", "obj", "Release");
#pragma warning disable AARC003 // Test-layer I/O verifying the cleanup contract
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "project.assets.json"), "{}");
        Directory.Exists(nested).Should().BeTrue();
#pragma warning restore AARC003

        RuntimeHostBuildService.TryDeleteDirectory(temp.FullPath);

#pragma warning disable AARC003
        Directory.Exists(temp.FullPath).Should().BeFalse("the scratch tree must be gone");
#pragma warning restore AARC003
    }

    /// <summary>Cleanup is a no-op for a missing directory and for null, and never throws.</summary>
    [Fact]
    public void TryDeleteDirectory_IsANoOpForMissingDirectoryAndNull()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.FullPath, "does-not-exist");

        var act = () =>
        {
            RuntimeHostBuildService.TryDeleteDirectory(missing);
            RuntimeHostBuildService.TryDeleteDirectory(null);
        };

        act.Should().NotThrow("cleanup must never mask the provisioning outcome");
    }

    private sealed record ParallelOutcome(
        RuntimeHostBuildService.RuntimeHostProvisionResult? FrameworkResult,
        RuntimeHostBuildService.RuntimeHostProvisionResult? SelfContainedResult,
        Exception? FrameworkError,
        Exception? SelfContainedError,
        int MaxConcurrentProvisions);

    /// <summary>
    /// Starts a framework-dependent and a self-contained provision on dedicated (LongRunning) tasks
    /// behind a shared barrier, so they enter the long restore+publish together. Overlap is proven
    /// by recording high-resolution timestamps around each provision and asserting the intervals
    /// intersect — avoiding any polling APIs forbidden by the architecture analyzer.
    /// </summary>
    private static async Task<ParallelOutcome> RunParallelPairAsync(string frameworkDirectory, string selfContainedDirectory)
    {
        var start = new Barrier(2);
        RuntimeHostBuildService.RuntimeHostProvisionResult? frameworkResult = null;
        RuntimeHostBuildService.RuntimeHostProvisionResult? selfContainedResult = null;
        Exception? frameworkError = null;
        Exception? selfContainedError = null;
        long frameworkStart = 0, frameworkEnd = 0;
        long selfContainedStart = 0, selfContainedEnd = 0;

        void Provision(
            bool selfContained,
            string target,
            Action<RuntimeHostBuildService.RuntimeHostProvisionResult> onSuccess,
            Action<Exception> onError,
            out long startTs,
            out long endTs)
        {
            start.SignalAndWait();
            startTs = Stopwatch.GetTimestamp();
            try
            {
                onSuccess(RuntimeHostBuildService.ProvisionRuntimeHost(
                    target, selfContained, provisionTimeout: TimeSpan.FromMinutes(4)));
            }
            catch (Exception exception)
            {
                onError(exception);
            }
            finally
            {
                endTs = Stopwatch.GetTimestamp();
            }
        }

        var frameworkTask = Task.Factory.StartNew(
            () => Provision(false, frameworkDirectory, r => frameworkResult = r, e => frameworkError = e, out frameworkStart, out frameworkEnd),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        var selfContainedTask = Task.Factory.StartNew(
            () => Provision(true, selfContainedDirectory, r => selfContainedResult = r, e => selfContainedError = e, out selfContainedStart, out selfContainedEnd),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        await Task.WhenAll(frameworkTask, selfContainedTask);

        // Intervals overlap iff fwStart < scEnd && scStart < fwEnd.
        var overlapped = frameworkStart < selfContainedEnd && selfContainedStart < frameworkEnd;

        return new ParallelOutcome(
            frameworkResult,
            selfContainedResult,
            frameworkError,
            selfContainedError,
            overlapped ? 2 : 1);
    }

    /// <summary>
    /// A framework-dependent publish carries a runtimeconfig that names the shared framework and no
    /// self-contained <c>includedFrameworks</c>; returns the apphost length for the size contrast.
    /// </summary>
    private static long AssertFrameworkDependentArtifact(string directory, string hostPath)
    {
        var runtimeConfigPath = Path.Combine(directory, "PSXRecomp.RuntimeHost.runtimeconfig.json");
        var managedAssembly = Path.Combine(directory, "PSXRecomp.RuntimeHost.dll");
        var depsFile = Path.Combine(directory, "PSXRecomp.RuntimeHost.deps.json");

#pragma warning disable AARC003 // Test-layer verification of the published artifact
        File.Exists(runtimeConfigPath).Should().BeTrue("a framework-dependent publish must leave a runtimeconfig.json");
        File.Exists(managedAssembly).Should().BeTrue("a framework-dependent publish must leave the managed assembly");
        File.Exists(depsFile).Should().BeTrue("a framework-dependent publish must leave the deps file");
        var hostLength = new FileInfo(hostPath).Length;

        var runtimeOptions = JsonDocument.Parse(File.ReadAllText(runtimeConfigPath)).RootElement
            .GetProperty("runtimeOptions");
#pragma warning restore AARC003

        runtimeOptions.GetProperty("framework").GetProperty("name").GetString()
            .Should().Be("Microsoft.NETCore.App", "the artifact must depend on the shared .NET framework");
        runtimeOptions.TryGetProperty("includedFrameworks", out _).Should().BeFalse(
            "a self-contained runtimeconfig leaking into the framework-dependent artifact is the regression under test");
        hostLength.Should().BeLessThan(5 * 1024 * 1024, "a framework-dependent apphost is small; a huge host means a self-contained artifact leaked in");
        return hostLength;
    }

    /// <summary>
    /// A self-contained single-file publish bundles the managed assembly and its runtimeconfig into
    /// one large host binary and must not carry the framework-dependent sidecar files.
    /// </summary>
    private static void AssertSelfContainedArtifact(string directory, string hostPath, long frameworkHostLength)
    {
        var runtimeConfigPath = Path.Combine(directory, "PSXRecomp.RuntimeHost.runtimeconfig.json");
        var managedAssembly = Path.Combine(directory, "PSXRecomp.RuntimeHost.dll");

#pragma warning disable AARC003 // Test-layer verification of the published artifact
        File.Exists(hostPath).Should().BeTrue("a self-contained publish must leave the host binary");
        var hostLength = new FileInfo(hostPath).Length;
        File.Exists(runtimeConfigPath).Should().BeFalse("a single-file self-contained host bundles its runtimeconfig.json");
        File.Exists(managedAssembly).Should().BeFalse("a single-file self-contained host bundles the managed assembly");
#pragma warning restore AARC003

        hostLength.Should().BeGreaterThan(5 * 1024 * 1024, "a self-contained single-file host bundles the .NET runtime");
        hostLength.Should().BeGreaterThan(
            frameworkHostLength * 4,
            "the self-contained bundle must be far larger than the framework-dependent apphost (no cross-contamination)");
    }

    /// <summary>
    /// Proves the self-contained artifact is a working standalone executable: with no manifest it
    /// prints usage (loader + managed host started) rather than a host-policy failure.
    /// </summary>
    private static void AssertRunsAsExecutable(string hostPath)
    {
#pragma warning disable AARC003 // Test-layer process launch to prove the published host is executable
        using var process = Process.Start(new ProcessStartInfo(hostPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExit(60_000);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }

        exited.Should().BeTrue("the self-contained host must start and exit promptly with no manifest");
#pragma warning restore AARC003

        stderr.Result.Should().Contain("usage:", "a host-policy/loader failure would print a fatal error instead of the host's usage");
        stderr.Result.Should().NotContain("libhostpolicy", "the self-contained bundle must not depend on an external host policy");
        stdout.Result.Should().NotContain("libhostpolicy");
    }
}
