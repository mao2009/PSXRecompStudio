using System.Text;
using FluentAssertions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Runtime;
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
    private const uint Entry = 0x80001000u;

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

    // Issue #773 (synthetic case, not an observed Persona result): the file word at the PC is not the word the interpreter fetched
    // from RAM. The root is not a valid native route, so the plan reports it rejected, and no roots file or roots directive is
    // written for it.
    [Fact]
    public void ATargetWhoseRamWordDiffersFromTheFile_IsNotAccepted_AndWritesNoRoot()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, run => ObservedRootsFixtures.TargetsJson(run, ObservedRootsFixtures.TargetObject(Callee, "unknown-code", 1, 40, ramWord: 0xDEADBEEFu)));
        var output = dir.Combine("images-observed.txt");

        var code = Run(fixture, output, out var stdout, out var stderr);

        code.Should().Be(0, stderr);
        stdout.Should().Contain("\"accepted\": 0");
        stdout.Should().Contain("\"ram-word-mismatch\": 1");
        var planned = LoadImageManifest.Read(output, OpenBiosFirmware.FromBytes(new byte[OpenBiosFirmware.ImageSize]), disc: null);
        planned.Images.Single().Roots.Should().Equal(Dest);
    }

    // Issue #773 (review): every fallback fetch at a PC is observed. A later fetch that reads a different word leaves the PC
    // unobserved, so the planner refuses it even though the first fetch matched the file.
    [Fact]
    public void ALaterDifferingFetch_AtTheSamePc_LeavesThePcUnobserved_AndTheRootIsRejected()
    {
        var log = new FallbackRamWordLog();
        log.Observe(Callee, ObservedRootsFixtures.Text[4]);
        log.Observe(Callee, 0x12345678u);
        var image = new LoadedImageSpec("test.exe", Dest, ObservedRootsFixtures.Text, [Dest]) { SourceSha256 = ObservedRootsFixtures.Sha256Hex(ObservedRootsFixtures.Exe(ObservedRootsFixtures.Text)) };

        log.Get(Callee).Should().BeNull();
        var plan = ObservedRootPlanner.Plan([image], [new ObservedFallbackTarget(Callee, "unknown-code", 1, 40, log.Get(Callee))], ObservedRootBudget.Default);
        plan.Images.Should().BeEmpty();
        plan.Counts.Rejected.Should().ContainKey("ram-word-mismatch").WhoseValue.Should().Be(1);
    }

    [Fact]
    public void FetchesThatAllReadTheFileWord_KeepThePcObserved()
    {
        var log = new FallbackRamWordLog();
        log.Observe(Callee, ObservedRootsFixtures.Text[4]);
        log.Observe(Callee, ObservedRootsFixtures.Text[4]);

        log.Get(Callee).Should().Be(ObservedRootsFixtures.Text[4]);
    }

    // Issue #773 (review): a fetch that cannot be read (outside RAM and ROM) is unobserved, not the zero word. The file word at
    // this PC is 0, so reading it as 0 would wrongly match.
    [Fact]
    public void AnUnreadableFetch_LeavesThePcUnobserved_EvenWhenTheFileWordIsZero()
    {
        var log = new FallbackRamWordLog();
        var zeroPc = Dest + 8;
        log.Observe(zeroPc, null);
        var image = new LoadedImageSpec("test.exe", Dest, ObservedRootsFixtures.Text, [Dest]) { SourceSha256 = ObservedRootsFixtures.Sha256Hex(ObservedRootsFixtures.Exe(ObservedRootsFixtures.Text)) };
        ObservedRootsFixtures.Text[2].Should().Be(0u, "the file word at this PC is zero");

        var plan = ObservedRootPlanner.Plan([image], [new ObservedFallbackTarget(zeroPc, "unknown-code", 1, 40, log.Get(zeroPc))], ObservedRootBudget.Default);

        plan.Images.Should().BeEmpty();
        plan.Counts.Rejected.Should().ContainKey("ram-word-mismatch").WhoseValue.Should().Be(1);
    }

    // Issue #773 (review): the fetch observer the probe installs (OpenBiosProbeCommand.CreateFallbackFetchObserver) feeds the export.
    // Two fallback fetches at one PC read different RAM words, so the exported target carries no ramWord.
    [Fact]
    public void TheInstalledFetchObserver_ExportsNoRamWord_WhenTheSamePcReadsDifferentWords()
    {
        var export = ProbeExportAt(Entry, new[] { MipsEncoding.Nop }, new[] { MipsEncoding.JumpRegister(31) });

        export.Targets.Should().ContainSingle().Which.RamWord.Should().BeNull();
    }

    [Fact]
    public void TheInstalledFetchObserver_ExportsTheRamWord_WhenEveryFetchReadsTheSameWord()
    {
        var export = ProbeExportAt(Entry, new[] { MipsEncoding.Nop }, new[] { MipsEncoding.Nop });

        export.Targets.Should().ContainSingle().Which.RamWord.Should().Be(MipsEncoding.Nop);
    }

    [Fact]
    public void TheInstalledFetchObserver_StillRunsTheProbesOwnObservation_OncePerFetch()
    {
        var log = new FallbackRamWordLog();
        var calls = 0;
        var observer = OpenBiosProbeCommand.CreateFallbackFetchObserver(log, (_, _) => calls++);
        using var graph = new PsxDeviceGraph();
        var scheduler = new DeviceScheduler(graph.Core, graph.InterruptControllerAdapter, graph.GpuAdapter, graph.CdRomDevice, graph.CdRomDmaTransfer);
        using var engine = InterpreterTitleExecutionEngine.Attach([MipsEncoding.Nop], Entry, graph, scheduler);

        observer(engine, Entry);
        observer(engine, Entry);

        calls.Should().Be(2);
    }

    // Drives the observer the probe installs for two fallback fetches at Pc (the RAM word at Pc changes between them), then builds
    // the export the probe would write and reads it back through the planner's parser.
    private static ObservedFallbackExport ProbeExportAt(uint pc, uint[] firstRam, uint[] secondRam)
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, _ => "{}");
        var firmwareBytes = new byte[OpenBiosFirmware.ImageSize];
        var hash = ObservedRootsFixtures.Sha256Hex(firmwareBytes);
        var manifest = LoadImageManifest.Read(fixture.Manifest, OpenBiosFirmware.FromBytes(firmwareBytes), disc: null);
        var log = new FallbackRamWordLog();
        // One shared guest RAM: the word at pc is what the first fetch reads; the RAM is then changed before the second fetch.
        using var graph = new PsxDeviceGraph();
        var scheduler = new DeviceScheduler(graph.Core, graph.InterruptControllerAdapter, graph.GpuAdapter, graph.CdRomDevice, graph.CdRomDmaTransfer);
        graph.Core.CopyRamFrom(pc & 0x1FFFFFFFu, BitConverter.GetBytes(firstRam[0]));
        var observer = OpenBiosProbeCommand.CreateFallbackFetchObserver(log, (_, _) => { });
        using (var first = InterpreterTitleExecutionEngine.Attach(firstRam, pc, graph, scheduler)) observer(first, pc);
        graph.Core.CopyRamFrom(pc & 0x1FFFFFFFu, BitConverter.GetBytes(secondRam[0]));
        using (var second = InterpreterTitleExecutionEngine.Attach(secondRam, pc, graph, scheduler)) observer(second, pc);
        var evidence = new MixedFallbackEvidence(0, 0, 0, 0, 0, [new MixedFallbackTarget(pc, Entries: 2, Instructions: 2, LastReturnPc: Dest)]);

        var json = OpenBiosProbeCommand.BuildFallbackTargetsDocument(evidence, hash, manifest, _ => "unknown-code", TitleExecutionState.Completed, null, 0, [], null, log.Get);

        return ObservedRootPlanner.ReadExport(json!);
    }

    // Issue #773 (review): the probe's observer also drives the boot monitor and the fallback accounting. The tests run the
    // installed observer with real monitor and accounting objects (the code the probe runs), not a stand-in rest.
    [Fact]
    public void TheInstalledObserver_CountsEachFallbackFetchOnce_InTheAccounting()
    {
        using var engine = new InterpreterTitleExecutionEngine([MipsEncoding.Nop], Entry);
        var monitor = new OpenBiosBootMonitor(() => (engine.Cop0Diagnostics.Cause, engine.Cop0Diagnostics.Epc, engine.Cop0Diagnostics.BadVAddr));
        var accounting = new OpenBiosProbeAccounting();
        ProbeGuestState? host = null;
        var observer = OpenBiosProbeCommand.CreateFallbackFetchObserver(new FallbackRamWordLog(), (interpreter, pc) =>
            OpenBiosProbeCommand.ObserveFallbackFetchAccounting(monitor, accounting, () => (engine.Cop0Diagnostics.Cause, engine.Cop0Diagnostics.Epc), engine.ReadGuestWord, interpreter, pc, ref host));

        observer(engine, Entry);
        observer(engine, Entry);
        observer(engine, Entry);

        accounting.FallbackFetches.Should().Be(3);
        host.Should().BeNull("no exception-vector entry was fetched");
    }

    [Fact]
    public void TheInstalledObserver_ForwardsAnExceptionVectorEntry_ToTheMonitor_AndCapturesTheFirstHostInterrupt()
    {
        using var engine = new InterpreterTitleExecutionEngine([MipsEncoding.Nop], Entry);
        var monitor = new OpenBiosBootMonitor(() => (engine.Cop0Diagnostics.Cause, engine.Cop0Diagnostics.Epc, engine.Cop0Diagnostics.BadVAddr));
        var accounting = new OpenBiosProbeAccounting();
        ProbeGuestState? host = null;
        var observer = OpenBiosProbeCommand.CreateFallbackFetchObserver(new FallbackRamWordLog(), (interpreter, pc) =>
            OpenBiosProbeCommand.ObserveFallbackFetchAccounting(monitor, accounting, () => (engine.Cop0Diagnostics.Cause, engine.Cop0Diagnostics.Epc), engine.ReadGuestWord, interpreter, pc, ref host));
        const uint vector = 0x80000080u;

        observer(engine, vector);
        monitor.Evaluate(_ => 0u).InterruptEntries.Should().Be(1, "the monitor sees the vector entry once");
        host.Should().NotBeNull("the first vector entry with no pending cause is captured");
        host!.Pc.Should().Be(vector);
        host.AtFetch.Should().Be(1);

        observer(engine, vector);
        monitor.Evaluate(_ => 0u).InterruptEntries.Should().Be(2, "every vector entry reaches the monitor");
        host.AtFetch.Should().Be(1, "only the first host interrupt is kept");
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

    // PR #784 review (MAJOR): the roots file names do not depend on --out, so the directory is the shared namespace. A run that
    // targets another output name must still be refused while a run holds the directory's publish lock.
    [Fact]
    public void AHeldDirectoryLock_RefusesAnotherOutputName_AndPublishesNothing()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, run => ObservedRootsFixtures.TargetsJson(Callee, "unknown-code", 40, run));
        using var held = new FileStream(dir.Combine(".observed-roots.lock"), FileMode.CreateNew, FileAccess.Write, FileShare.None);

        var code = Run(fixture, dir.Combine("images-other.txt"), out _, out _);

        code.Should().Be(1);
        Leftovers(dir).Should().Equal(".observed-roots.lock");
    }

    // Parallel runs with different --out names in one directory: exactly one publishes, and what it publishes reads back.
    [Fact]
    public void ParallelRunsWithDifferentOutputNames_InOneDirectory_PublishExactlyOnce()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, run => ObservedRootsFixtures.TargetsJson(Callee, "unknown-code", 40, run));
        var outputs = Enumerable.Range(0, 4).Select(i => dir.Combine($"images-{i}.txt")).ToArray();

        var codes = outputs.AsParallel().WithDegreeOfParallelism(4)
            .Select(output => Run(fixture, output, out _, out _)).ToArray();

        codes.Count(static c => c == 0).Should().Be(1);
        var published = Leftovers(dir).Where(static name => name.StartsWith("images-", StringComparison.Ordinal)).Single();
        Leftovers(dir).Should().BeEquivalentTo([published, "observed-roots-000.txt"]);
        LoadImageManifest.Read(dir.Combine(published), OpenBiosFirmware.FromBytes(new byte[OpenBiosFirmware.ImageSize]), disc: null)
            .Images.Single().Roots.Should().Equal(Dest, Callee);
    }

    // An existing roots file is never overwritten, whatever --out the run names; the run publishes nothing.
    [Fact]
    public void AnExistingRootsFile_IsNeverOverwritten_ByAnotherOutputName()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, run => ObservedRootsFixtures.TargetsJson(Callee, "unknown-code", 40, run));
        File.WriteAllText(dir.Combine("observed-roots-000.txt"), "foreign");

        var code = Run(fixture, dir.Combine("images-other.txt"), out _, out _);

        code.Should().Be(1);
        File.ReadAllText(dir.Combine("observed-roots-000.txt")).Should().Be("foreign");
        Leftovers(dir).Should().Equal("observed-roots-000.txt");
    }

    // An existing manifest is never replaced, and the roots file of the refused run is not created.
    [Fact]
    public void AnExistingManifest_IsNeverReplaced_AndItsRunPublishesNoRoots()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, run => ObservedRootsFixtures.TargetsJson(Callee, "unknown-code", 40, run));
        File.WriteAllText(dir.Combine("images-other.txt"), "old");

        var code = Run(fixture, dir.Combine("images-other.txt"), out _, out _);

        code.Should().Be(1);
        File.ReadAllText(dir.Combine("images-other.txt")).Should().Be("old");
        Leftovers(dir).Should().Equal("images-other.txt");
    }

    // A reader that opens the manifest name while a publish holds its reservation must fail closed, not load zero images.
    [Fact]
    public void AReservationOfAManifest_IsRejectedByTheReader()
    {
        using var dir = new TempDirectory();
        var reservation = dir.WriteFile("images-reserved.txt", "reserved: fallback-roots publish in progress\n"u8.ToArray());

        var act = () => LoadImageManifest.Read(reservation, OpenBiosFirmware.FromBytes(new byte[OpenBiosFirmware.ImageSize]), disc: null);

        act.Should().Throw<InvalidDataException>();
    }

    // A zero-byte manifest (the reservation state between CreateNew and its write) is refused, not loaded as zero images.
    [Fact]
    public void AnEmptyManifest_IsRefused()
    {
        using var dir = new TempDirectory();
        var empty = dir.WriteFile("images-empty.txt", Array.Empty<byte>());

        var act = () => LoadImageManifest.Read(empty, OpenBiosFirmware.FromBytes(new byte[OpenBiosFirmware.ImageSize]), disc: null);

        act.Should().Throw<InvalidDataException>().WithMessage("*is empty*");
    }

    // The same reservation text in a roots file is refused too, so a roots reader that opens it mid-publish fails closed.
    [Fact]
    public void AReservationOfARootsFile_IsRejectedByTheRootsReader()
    {
        using var dir = new TempDirectory();
        var reservation = dir.WriteFile("roots-reserved.txt", "reserved: fallback-roots publish in progress\n"u8.ToArray());

        var act = () => OpenBiosProbeCommand.ReadRoots(reservation);

        // A FormatException, which the command's outer handler reports as an input error (exit 1).
        act.Should().Throw<FormatException>();
    }

    // A refused run for another output leaves a published manifest and its roots file intact and readable.
    [Fact]
    public void AFailedRunForAnotherOutput_KeepsTheFilesOfTheSuccessfulRun()
    {
        using var dir = new TempDirectory();
        var fixture = Create(dir, run => ObservedRootsFixtures.TargetsJson(Callee, "unknown-code", 40, run));
        Run(fixture, dir.Combine("images-a.txt"), out _, out _).Should().Be(0);
        var before = File.ReadAllText(dir.Combine("observed-roots-000.txt"));

        var code = Run(fixture, dir.Combine("images-b.txt"), out _, out _);

        code.Should().Be(1);
        File.ReadAllText(dir.Combine("observed-roots-000.txt")).Should().Be(before);
        LoadImageManifest.Read(dir.Combine("images-a.txt"), OpenBiosFirmware.FromBytes(new byte[OpenBiosFirmware.ImageSize]), disc: null)
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

    // A target whose RAM word is the word of IntegratedRootSourcesTests.Text at the PC (the image these tests plan over).
    private static string Target(uint pc, ulong instructions) =>
        ObservedRootsFixtures.TargetObject(pc, "unknown-code", 1, instructions, IntegratedRootSourcesTests.Text[(pc - Dest) / 4]);

    [Fact]
    public void AnUnfinishedProbeRun_WritesNoExport()
    {
        // No fallback evidence means the run did not finish: an export would claim "no targets" and plan nothing, falsely.
        using var dir = new TempDirectory();
        var fixture = Create(dir, _ => "{}");
        var manifest = LoadImageManifest.Read(fixture.Manifest, OpenBiosFirmware.FromBytes(new byte[OpenBiosFirmware.ImageSize]), disc: null);

        OpenBiosProbeCommand.BuildFallbackTargetsDocument(null, ObservedRootsFixtures.Sha256Hex(new byte[OpenBiosFirmware.ImageSize]), manifest, _ => "unknown-code", TitleExecutionState.Completed, null, 0, [], null, _ => null)
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

        var json = OpenBiosProbeCommand.BuildFallbackTargetsDocument(evidence, hash, manifest, _ => "unknown-code", TitleExecutionState.Completed, null, 0, [], null, _ => ObservedRootsFixtures.Text[4]);

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
            OpenBiosProbeCommand.BuildFallbackTargetsDocument(evidence, hash, manifest, _ => "unknown-code", result.State, null, 0, [], null, _ => ObservedRootsFixtures.Text[4])!)) };
        var output = dir.Combine("images-observed.txt");

        var code = Run(partial, output, out _, out var stderr);

        code.Should().Be(1);
        stderr.Should().Contain("BudgetExhausted");
        File.Exists(output).Should().BeFalse();
        Directory.GetFiles(dir.FullPath, "observed-roots-*").Should().BeEmpty();
    }

    // Issue #773 review M2: an existing export is never replaced; the write fails and the old file is kept byte for byte.
    [Fact]
    public void AnExistingExport_IsNeverReplaced()
    {
        using var dir = new TempDirectory();
        var destination = dir.Combine("export.json");
        File.WriteAllText(destination, "old");

        var act = () => OpenBiosProbeCommand.WriteAtomically(destination, "{}");

        act.Should().Throw<IOException>();
        File.ReadAllText(destination).Should().Be("old");
        Directory.GetFileSystemEntries(dir.FullPath, "*.tmp").Should().BeEmpty();
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
