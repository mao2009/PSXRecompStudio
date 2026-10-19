using System.Text;
using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure.Cli;
using PSXRecomp.Tests.Recompiler;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Cli;

// Issue #773: `psxrecomp fallback-roots` plans observed roots into a new manifest beside the input. Small end-to-end runs:
// the output reads back through the manifest parser, the input is never changed, and no existing file is overwritten.
[Test]
public sealed class FallbackRootsCommandTests
{
    private const uint Dest = ObservedRootsFixtures.Dest;
    private const uint Callee = ObservedRootsFixtures.Callee;

    private sealed record Fixture(TempDirectory Dir, string Rom, string Manifest, string Targets);

    private static Fixture Create(TempDirectory dir, Func<ObservedRunIdentity, string> targetsJson, string manifestText = "exe test test.exe\n", uint[]? text = null)
    {
        var firmwareBytes = new byte[OpenBiosFirmware.ImageSize];
        var rom = dir.WriteFile("openbios.bin", firmwareBytes);
        dir.WriteFile("test.exe", ObservedRootsFixtures.Exe(text ?? ObservedRootsFixtures.Text));
        var manifest = dir.WriteFile("images.txt", Encoding.ASCII.GetBytes(manifestText));
        // The run the export claims: what the probe would record for exactly these inputs.
        var run = ObservedRootPlanner.DescribeRun(ObservedRootsFixtures.Sha256Hex(firmwareBytes), LoadImageManifest.Read(manifest, OpenBiosFirmware.FromBytes(firmwareBytes), disc: null), discSha256: null, ObservedRootPlanner.ProbeOptionsSha256(0, []));
        var targets = dir.WriteFile("targets.json", Encoding.UTF8.GetBytes(targetsJson(run)));
        return new Fixture(dir, rom, manifest, targets);
    }

    private static int Run(Fixture fixture, string output, out string stdout, out string stderr)
    {
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var code = FallbackRootsCommand.Run(
            [fixture.Rom, "--load-images", fixture.Manifest, "--fallback-targets", fixture.Targets, "--out", output],
            outWriter,
            errWriter);
        stdout = outWriter.ToString();
        stderr = errWriter.ToString();
        return code;
    }

    [Fact]
    public void AnUnknownCodeTarget_BecomesAnObservedRoot_InANewManifest_ThatReadsBack()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, run => ObservedRootsFixtures.TargetsJson(Callee, "unknown-code", 40, run));
        var output = dir.Combine("images-observed.txt");

        var code = Run(fixture, output, out var stdout, out var stderr);

        code.Should().Be(0, stderr);
        stdout.Should().Contain("\"accepted\": 1");
        var planned = LoadImageManifest.Read(output, OpenBiosFirmware.FromBytes(new byte[OpenBiosFirmware.ImageSize]), disc: null);
        planned.Images.Single().Roots.Should().Equal(Dest, Callee);
    }

    [Fact]
    public void TheInputManifest_IsNeverChanged()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, run => ObservedRootsFixtures.TargetsJson(Callee, "unknown-code", 40, run));

        Run(fixture, dir.Combine("images-observed.txt"), out _, out _);

        var original = LoadImageManifest.Read(fixture.Manifest, OpenBiosFirmware.FromBytes(new byte[OpenBiosFirmware.ImageSize]), disc: null);
        original.Images.Single().Roots.Should().Equal(Dest);
    }

    [Fact]
    public void AnExistingOutput_IsRefused_AndNothingIsOverwritten()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, run => ObservedRootsFixtures.TargetsJson(Callee, "unknown-code", 40, run));
        var output = dir.Combine("images-observed.txt");
        Run(fixture, output, out _, out _).Should().Be(0);

        var code = Run(fixture, output, out _, out var stderr);

        code.Should().Be(1);
        stderr.Should().Contain("already exists");
    }

    [Fact]
    public void AnOutputOutsideTheManifestDirectory_IsRefused()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, run => ObservedRootsFixtures.TargetsJson(Callee, "unknown-code", 40, run));
        var elsewhere = dir.CreateSubdirectory("elsewhere");

        var code = Run(fixture, Path.Combine(elsewhere, "images-observed.txt"), out _, out var stderr);

        code.Should().Be(1);
        stderr.Should().Contain("same directory");
    }

    [Fact]
    public void AMalformedTargetDocument_IsRefused()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, _ => """{"format":"other/v9","targets":[]}""");

        var code = Run(fixture, dir.Combine("images-observed.txt"), out _, out var stderr);

        code.Should().Be(1);
        stderr.Should().Contain("format");
    }

#pragma warning disable AARC003 // the tests inspect what the command left on disk
    private static string[] Leftovers(TempDirectory dir) =>
        Directory.GetFileSystemEntries(dir.FullPath).Select(Path.GetFileName).Cast<string>()
            .Except(["openbios.bin", "test.exe", "images.txt", "targets.json"]).Order().ToArray();

    [Fact]
    public void AnOutputThatIsADirectory_IsRefused_AndLeavesNothingBehind()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, run => ObservedRootsFixtures.TargetsJson(Callee, "unknown-code", 40, run));
        var occupied = dir.CreateSubdirectory("images-observed.txt");

        var code = Run(fixture, occupied, out _, out var stderr);

        code.Should().Be(1);
        stderr.Should().Contain("already exists");
        Leftovers(dir).Should().Equal("images-observed.txt");
        Directory.Delete(occupied);
        Run(fixture, occupied, out _, out _).Should().Be(0, "a retry must succeed once the obstruction is gone");
    }

    [Fact]
    public void ARefusedRun_LeavesNothingBehind_AndARerunOnceTheObstacleIsGone_Succeeds()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, run => ObservedRootsFixtures.TargetsJson(Callee, "unknown-code", 40, run));
        var output = dir.Combine("images-observed.txt");
        Directory.CreateDirectory(output);

        Run(fixture, output, out _, out _).Should().Be(1);
        Directory.GetFiles(dir.FullPath, "observed-roots-*").Should().BeEmpty();
        Directory.Delete(output);

        Run(fixture, output, out _, out var stderr).Should().Be(0, stderr);
        LoadImageManifest.Read(output, OpenBiosFirmware.FromBytes(new byte[OpenBiosFirmware.ImageSize]), disc: null)
            .Images.Single().Roots.Should().Equal(Dest, Callee);
    }

    [Fact]
    public void ARootsFileNameTakenByAUserFile_IsRefused_AndTheUserFileIsKept()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, run => ObservedRootsFixtures.TargetsJson(Callee, "unknown-code", 40, run));
        var userFile = dir.WriteFile("observed-roots-000.txt", Encoding.ASCII.GetBytes("mine"));

        var code = Run(fixture, dir.Combine("images-observed.txt"), out _, out _);

        code.Should().Be(1);
        File.ReadAllText(userFile).Should().Be("mine");
        Leftovers(dir).Should().Equal("observed-roots-000.txt");
    }

    [Fact]
    public void ASuccessfulRun_LeavesNoStagingFiles()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, run => ObservedRootsFixtures.TargetsJson(Callee, "unknown-code", 40, run));

        Run(fixture, dir.Combine("images-observed.txt"), out _, out _).Should().Be(0);

        Leftovers(dir).Should().Equal("images-observed.txt", "observed-roots-000.txt");
    }

    [Fact]
    public void ConcurrentRunsToTheSameOutput_PublishExactlyOnce_AndLeaveAConsistentSet()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, run => ObservedRootsFixtures.TargetsJson(Callee, "unknown-code", 40, run));
        var output = dir.Combine("images-observed.txt");

        var codes = Enumerable.Range(0, 4).AsParallel().WithDegreeOfParallelism(4)
            .Select(i => Run(fixture, output, out var so, out var se)).ToArray();

        codes.Count(static c => c == 0).Should().Be(1);
        Leftovers(dir).Should().Equal("images-observed.txt", "observed-roots-000.txt");
        LoadImageManifest.Read(output, OpenBiosFirmware.FromBytes(new byte[OpenBiosFirmware.ImageSize]), disc: null)
            .Images.Single().Roots.Should().Equal(Dest, Callee);
    }

    [Fact]
    public void AnExportFromADifferentFirmware_IsRefused_BeforeAnythingIsWritten()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, run => ObservedRootsFixtures.TargetsJson(Callee, "unknown-code", 40, run with { FirmwareSha256 = new string('a', 64) }));

        var code = Run(fixture, dir.Combine("images-observed.txt"), out _, out var stderr);

        code.Should().Be(1);
        stderr.Should().Contain("firmware SHA-256");
        Leftovers(dir).Should().BeEmpty();
    }

    [Fact]
    public void AnExportFromADifferentExe_IsRefused()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, run => ObservedRootsFixtures.TargetsJson(Callee, "unknown-code", 40,
            run with { Images = [run.Images[0] with { SourceSha256 = new string('b', 64) }] }));

        var code = Run(fixture, dir.Combine("images-observed.txt"), out _, out var stderr);

        code.Should().Be(1);
        stderr.Should().Contain("source SHA-256");
        Leftovers(dir).Should().BeEmpty();
    }

    [Fact]
    public void AV1ExportWithoutARunIdentity_IsRefused()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, _ => """{"format":"psxrecomp.fallback-targets/v1","sha256":"0","targets":[]}""");

        var code = Run(fixture, dir.Combine("images-observed.txt"), out _, out var stderr);

        code.Should().Be(1);
        stderr.Should().Contain("format");
    }

    // C-3: observed roots on top of indirect roots. The export is taken over the manifest as the probe read it, indirect roots
    // included, so the same-PC case is rejected by the planner (already reachable) and never reaches the manifest parser's refusal.
    [Fact]
    public void ObservedRoots_AreAddedAfterIndirectRoots_AndASamePcFromBothIsPlannedOnlyOnce()
    {
        using var dir = new TempDirectory();
        var fixture = Create(
            dir,
            run => ObservedRootsFixtures.TargetsJson(run, Target(IntegratedRootSourcesTests.CalleeA, 90), Target(IntegratedRootSourcesTests.CalleeC, 80)),
            "exe test test.exe\nindirect-roots\n",
            IntegratedRootSourcesTests.Text);
        var output = dir.Combine("images-observed.txt");

        var code = Run(fixture, output, out var stdout, out var stderr);

        code.Should().Be(0, stderr);
        stdout.Should().Contain("\"accepted\": 1").And.Contain("\"already-reachable\": 1");
        var read = LoadImageManifest.Read(output, OpenBiosFirmware.FromBytes(new byte[OpenBiosFirmware.ImageSize]), disc: null).Images.Single();
        read.Roots.Should().Equal(Dest, IntegratedRootSourcesTests.CalleeA, IntegratedRootSourcesTests.CalleeB, IntegratedRootSourcesTests.CalleeC);
    }

    [Fact]
    public void TheSameInputs_GiveTheSameBytes_InAnyOutputName()
    {
        using var dir = new TempDirectory();
        var fixture = Create(
            dir, run => ObservedRootsFixtures.TargetsJson(run, Target(IntegratedRootSourcesTests.CalleeC, 80)),
            "exe test test.exe\nindirect-roots\n", IntegratedRootSourcesTests.Text);
        Run(fixture, dir.Combine("a.txt"), out _, out _).Should().Be(0);
        var rootsFile = File.ReadAllText(dir.Combine("observed-roots-000.txt"));
        File.Delete(dir.Combine("observed-roots-000.txt"));
        Run(fixture, dir.Combine("b.txt"), out _, out _).Should().Be(0);

        File.ReadAllText(dir.Combine("observed-roots-000.txt")).Should().Be(rootsFile);
        File.ReadAllText(dir.Combine("b.txt")).Should().Be(File.ReadAllText(dir.Combine("a.txt")));
    }

    [Fact]
    public void AnExportTakenWithoutIndirectRoots_IsRefused_WhenThePlanningManifestEnablesThem()
    {
        using var dir = new TempDirectory();
        // The export's run is described over a manifest without indirect-roots; the planning manifest adds the directive.
        using var other = new TempDirectory();
        var withoutIndirect = Create(other, run => ObservedRootsFixtures.TargetsJson(run, Target(IntegratedRootSourcesTests.CalleeC, 80)),
            "exe test test.exe\n", IntegratedRootSourcesTests.Text);
        var staleExport = File.ReadAllText(withoutIndirect.Targets);
        var fixture = Create(dir, _ => staleExport, "exe test test.exe\nindirect-roots\n", IntegratedRootSourcesTests.Text);

        var code = Run(fixture, dir.Combine("images-observed.txt"), out _, out var stderr);

        code.Should().Be(1);
        stderr.Should().Contain("roots differs");
        Leftovers(dir).Should().BeEmpty();
    }

    private static string Target(uint pc, ulong instructions) =>
        $$"""{"pc":"0x{{pc:X8}}","cause":"unknown-code","entries":1,"instructions":{{instructions}},"lastReturnPc":"0x{{Dest:X8}}"}""";

    [Fact]
    public void AnUnfinishedProbeRun_WritesNoExport()
    {
        // No fallback evidence means the run did not finish: an export would claim "no targets" and plan nothing, falsely.
        using var dir = new TempDirectory();
        var fixture = Create(dir, _ => "{}");
        var manifest = LoadImageManifest.Read(fixture.Manifest, OpenBiosFirmware.FromBytes(new byte[OpenBiosFirmware.ImageSize]), disc: null);

        OpenBiosProbeCommand.BuildFallbackTargetsDocument(null, ObservedRootsFixtures.Sha256Hex(new byte[OpenBiosFirmware.ImageSize]), manifest, _ => "unknown-code", TitleExecutionState.Completed, null, 0, [], null)
            .Should().BeNull();
    }

    [Fact]
    public void AFinishedProbeRun_ExportsItsTargets_AndVerifiesAgainstItsOwnInputs()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, _ => "{}");
        var firmwareBytes = new byte[OpenBiosFirmware.ImageSize];
        var hash = ObservedRootsFixtures.Sha256Hex(firmwareBytes);
        var manifest = LoadImageManifest.Read(fixture.Manifest, OpenBiosFirmware.FromBytes(firmwareBytes), disc: null);
        var evidence = new MixedFallbackEvidence(0, 0, 0, 0, 0, [new MixedFallbackTarget(Callee, Entries: 1, Instructions: 40, LastReturnPc: Dest)]);

        var json = OpenBiosProbeCommand.BuildFallbackTargetsDocument(evidence, hash, manifest, _ => "unknown-code", TitleExecutionState.Completed, null, 0, [], null);

        json.Should().NotBeNull();
        var export = ObservedRootPlanner.ReadExport(json!);
        export.Targets.Select(t => t.Pc).Should().Equal(Callee);
        ObservedRootPlanner.Verify(export.Run, ObservedRootPlanner.DescribeRun(hash, manifest, null, ObservedRootPlanner.ProbeOptionsSha256(0, [])));
    }

    // An engine whose outer budget is spent before its first segment: the orchestrator ends the run as BudgetExhausted,
    // so the probe's evidence is partial (review M1). The real orchestrator produces the state; nothing is hand-set.
    private sealed class NoSegmentEngine : IRecompiledExecutionEngine
    {
        public string Name => "test-no-segment";
        public void Load(TitleExecutionRequest request) { }
        public RecompilerExecutionResult RunSegment(TitleExecutionSegmentRequest segmentRequest) =>
            throw new InvalidOperationException("the outer budget is spent before any segment runs");
        public void Dispose() { }
    }

    [Fact]
    public void AnExportOfARunThatExhaustedItsBudget_IsRefusedByThePlanner_AndWritesNothing()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, _ => "{}");
        var firmwareBytes = new byte[OpenBiosFirmware.ImageSize];
        var hash = ObservedRootsFixtures.Sha256Hex(firmwareBytes);
        var manifest = LoadImageManifest.Read(fixture.Manifest, OpenBiosFirmware.FromBytes(firmwareBytes), disc: null);
        var result = new ExecutionOrchestrator().Execute(new NoSegmentEngine(), handoff: null,
            new TitleExecutionRequest(Dest, new uint[TitleExecutionRequest.GprCount], 0, 0, [], outerBudget: 0, segmentBudget: 1));
        result.State.Should().Be(TitleExecutionState.BudgetExhausted);
        var evidence = new MixedFallbackEvidence(0, 0, 0, 0, 0, [new MixedFallbackTarget(Callee, Entries: 1, Instructions: 40, LastReturnPc: Dest)]);
        var partial = fixture with { Targets = dir.WriteFile("partial.json", Encoding.UTF8.GetBytes(
            OpenBiosProbeCommand.BuildFallbackTargetsDocument(evidence, hash, manifest, _ => "unknown-code", result.State, null, 0, [], null)!)) };
        var output = dir.Combine("images-observed.txt");

        var code = Run(partial, output, out _, out var stderr);

        code.Should().Be(1);
        stderr.Should().Contain("BudgetExhausted");
        File.Exists(output).Should().BeFalse();
        Directory.GetFiles(dir.FullPath, "observed-roots-*").Should().BeEmpty();
    }

    [Fact]
    public void AFailedExportWrite_LeavesNoTemporaryFile()
    {
        using var dir = new TempDirectory();
        // The destination is a directory, so the rename fails after the temporary has been written.
        var destination = dir.Combine("export.json");
        Directory.CreateDirectory(destination);

        var act = () => OpenBiosProbeCommand.WriteAtomically(destination, "{}");

        act.Should().Throw<Exception>();
        Directory.GetFileSystemEntries(dir.FullPath, "*.tmp").Should().BeEmpty();
    }

#pragma warning restore AARC003

    // Issue #773 (review M2, owner decision): an export is refused when its disc image or its probe options (--roots, --code-bytes)
    // are not the ones the planner was given, so its PCs are never read as evidence about different inputs.
    [Fact]
    public void AnExportFromAnotherDisc_IsRefused()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, run => ObservedRootsFixtures.TargetsJson(Callee, "unknown-code", 40, run with { DiscSha256 = new string('d', 64) }));

        var code = Run(fixture, dir.Combine("images-observed.txt"), out _, out var stderr);

        code.Should().Be(1);
        stderr.Should().Contain("disc image");
    }

    [Fact]
    public void AnExportFromDifferentProbeOptions_IsRefused()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, run => ObservedRootsFixtures.TargetsJson(Callee, "unknown-code", 40, run with { ProbeOptionsSha256 = ObservedRootPlanner.ProbeOptionsSha256(0, [Callee]) }));

        var code = Run(fixture, dir.Combine("images-observed.txt"), out _, out var stderr);

        code.Should().Be(1);
        stderr.Should().Contain("probe options");
    }
}
