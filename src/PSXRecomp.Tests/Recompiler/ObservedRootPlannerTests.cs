using FluentAssertions;
using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.Recompiler;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

// Issue #773: an observed fallback target becomes a root only when it is an unknown-code PC inside exactly one EXE image
// with a source identity, is structurally safe and not already reachable, and fits the deterministic budget. Every
// candidate is counted once, as accepted or under one reason.
[Test]
public sealed class ObservedRootPlannerTests
{
    private const uint Dest = ObservedRootsFixtures.Dest;
    private const uint A = Dest + 8, B = Dest + 20, C = Dest + 32;
    private const string Sha = "0000000000000000000000000000000000000000000000000000000000000000";
    private static readonly ObservedRootBudget Open = new(64, 256, 0.0, int.MaxValue);

    // Entry jr/nop; callee A (prologue) at word 2; callee B (prologue) at word 5; callee C ends in a branch whose delay slot
    // lies outside the image, so discovery from C alone is a structural error.
    private static uint[] Words() =>
    [
        MipsEncoding.JumpRegister(31), MipsEncoding.Nop,
        MipsEncoding.I(0x09, 29, 29, 0xFFF0), MipsEncoding.JumpRegister(31), MipsEncoding.Nop,
        MipsEncoding.I(0x09, 29, 29, 0xFFF0), MipsEncoding.JumpRegister(31), MipsEncoding.Nop,
        MipsEncoding.Branch(0x04, 0, 0, C, C + 4),
    ];

    private static LoadedImageSpec Image(string name = "img") => new(name, Dest, Words(), [Dest]) { SourceSha256 = Sha };

    // The RAM word a target reports by default: the word the image holds there (0 outside the image, which is rejected anyway).
    private static uint WordAt(uint pc)
    {
        var words = Words();
        var index = (pc - Dest) / 4;
        return pc >= Dest && index < words.Length ? words[index] : 0u;
    }

    private static ObservedFallbackTarget T(uint pc, ulong instructions, string cause = "unknown-code", ulong entries = 1) =>
        new(pc, cause, entries, instructions, WordAt(pc));

    [Fact]
    public void OnlySafeUnknownCodeTargetsInOneImage_BecomeRoots_AndEveryCandidateIsCountedOnce()
    {
        var targets = new[]
        {
            T(Dest, 500),                          // already reachable from the entry
            T(A, 100), T(B, 50),                   // accepted, in observed-instruction order
            T(C, 80),                              // not structurally safe
            T(Dest + 0x1000, 60),                  // outside the image
            T(Dest + 2, 40),                       // unaligned
            T(B + 4, 30, cause: "version-mismatch"),
        };

        var plan = ObservedRootPlanner.Plan([Image()], targets, Open);

        plan.Images.Single().Roots.Select(static r => r.Pc).Should().Equal(A, B);
        plan.Counts.Candidates.Should().Be(7);
        plan.Counts.Accepted.Should().Be(2);
        plan.Counts.Rejected.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["already-reachable"] = 1,
            ["not-structurally-safe"] = 1,
            ["outside-images"] = 1,
            ["unaligned"] = 1,
            ["cause-not-candidate"] = 1,
        });
        (plan.Counts.Accepted + plan.Counts.Rejected.Values.Sum()).Should().Be(plan.Counts.Candidates);
    }

    // Issue #773 (synthetic case, not an observed Persona result): the file places one word at the PC, the interpreter fetched
    // another from RAM. Native code for the file word would not be the code that runs, so the root is not planned.
    [Fact]
    public void ATargetWhoseRamWordDiffersFromTheFileImage_IsNotPlannedAsARoot()
    {
        var mismatched = new ObservedFallbackTarget(A, "unknown-code", 1, 100, WordAt(A) ^ 0x0000FFFFu);

        var plan = ObservedRootPlanner.Plan([Image()], [mismatched], Open);

        plan.Images.Should().BeEmpty();
        plan.Counts.Accepted.Should().Be(0);
        plan.Counts.Rejected.Should().ContainKey("ram-word-mismatch").WhoseValue.Should().Be(1);
    }

    // A target whose RAM word the probe did not observe cannot be checked against the file, so it is not planned either.
    [Fact]
    public void ATargetWithoutAnObservedRamWord_IsNotPlannedAsARoot()
    {
        var unobserved = new ObservedFallbackTarget(A, "unknown-code", 1, 100, RamWord: null);

        var plan = ObservedRootPlanner.Plan([Image()], [unobserved], Open);

        plan.Images.Should().BeEmpty();
        plan.Counts.Rejected.Should().ContainKey("ram-word-mismatch").WhoseValue.Should().Be(1);
    }

    [Fact]
    public void ALoadedImageCoverageGap_IsACandidate_LikeAnUnknownCodeTarget()
    {
        var plan = ObservedRootPlanner.Plan([Image()], [T(A, 100, cause: "aot-coverage-gap:loaded-image")], Open);

        plan.Images.Single().Roots.Select(static r => r.Pc).Should().Equal(A);
    }

    [Fact]
    public void TheSameCandidates_InAnyOrder_ProduceTheSamePlan()
    {
        var forward = ObservedRootPlanner.Plan([Image()], [T(A, 100), T(B, 50), T(C, 80)], Open);
        var reversed = ObservedRootPlanner.Plan([Image()], [T(C, 80), T(B, 50), T(A, 100)], Open);

        reversed.Should().BeEquivalentTo(forward);
    }

    [Fact]
    public void PerImageBudget_CapsTheRootsOfOneImage()
    {
        var plan = ObservedRootPlanner.Plan([Image()], [T(A, 100), T(B, 50)], new ObservedRootBudget(1, 256, 0.0, int.MaxValue));

        plan.Images.Single().Roots.Select(static r => r.Pc).Should().Equal(A);
        plan.Counts.Rejected.Should().ContainKey("per-image-budget").WhoseValue.Should().Be(1);
    }

    [Fact]
    public void TotalBudget_CapsTheRootsOfAllImages()
    {
        var plan = ObservedRootPlanner.Plan([Image()], [T(A, 100), T(B, 50)], new ObservedRootBudget(64, 1, 0.0, int.MaxValue));

        plan.Images.Single().Roots.Select(static r => r.Pc).Should().Equal(A);
        plan.Counts.Rejected.Should().ContainKey("total-budget").WhoseValue.Should().Be(1);
    }

    [Fact]
    public void CodeBudget_RejectsEveryRootThatAddsCode_WhenNoGrowthIsAllowed()
    {
        var plan = ObservedRootPlanner.Plan([Image()], [T(A, 100), T(B, 50)], new ObservedRootBudget(64, 256, 0.0, 0));

        plan.Images.Should().BeEmpty();
        plan.Counts.Rejected.Should().ContainKey("code-budget").WhoseValue.Should().Be(2);
        plan.Counts.ExtraReachable.Should().Be(0);
    }

    [Fact]
    public void ATargetInTwoImages_IsAmbiguous_AndRejected()
    {
        var plan = ObservedRootPlanner.Plan([Image("first"), Image("second")], [T(A, 100)], Open);

        plan.Images.Should().BeEmpty();
        plan.Counts.Rejected.Should().ContainKey("ambiguous-image").WhoseValue.Should().Be(1);
    }

    [Fact]
    public void AnImageWithoutSourceIdentity_TakesNoRoots()
    {
        var rom = new LoadedImageSpec("rom", Dest, Words(), [Dest]);

        var plan = ObservedRootPlanner.Plan([rom], [T(A, 100)], Open);

        plan.Images.Should().BeEmpty();
        plan.Counts.Rejected.Should().ContainKey("image-without-source-identity").WhoseValue.Should().Be(1);
    }

    [Fact]
    public void AnImageNameThatAManifestFieldCannotCarry_TakesNoRoots()
    {
        var plan = ObservedRootPlanner.Plan([Image("disc:EXE/A B.EXE")], [T(A, 100)], Open);

        plan.Counts.Rejected.Should().ContainKey("unrepresentable-image-name").WhoseValue.Should().Be(1);
    }

    [Fact]
    public void ReadTargets_ParsesTheExportFormat()
    {
        var json = ObservedRootsFixtures.TargetsJson(A, "unknown-code", 100);

        var targets = ObservedRootPlanner.ReadTargets(json);

        // The public export carries no RAM word (the ruling on #839): the target has none until the local evidence supplies it.
        targets.Should().ContainSingle().Which.Should().Be(new ObservedFallbackTarget(A, "unknown-code", 1, 100, RamWord: null));
    }

    private const string Dup = """{"pc":"0x80010008","cause":"unknown-code","entries":1,"instructions":1}""";

    public static TheoryData<string> Malformed => new()
    {
        """{"format":"other/v9","targets":[]}""",
        """{"format":"psxrecomp.fallback-targets/v1","sha256":"0","targets":[]}""",
        """{"format":"psxrecomp.fallback-targets/v2","sha256":"0000000000000000000000000000000000000000000000000000000000000000","targets":[]}""",
        ObservedRootsFixtures.TargetsJson(null, Dup, Dup),
        ObservedRootsFixtures.TargetsJson(null, """{"pc":"0xZZ","cause":"unknown-code","entries":1,"instructions":1}"""),
        ObservedRootsFixtures.TargetsJson(PlaceholderWithFirmware('A'), Dup),
        ObservedRootsFixtures.TargetsJson(null, Dup).Replace("\"sha256\":\"0000", "\"sha256\":\"1111"),
    };

    private static ObservedRunIdentity PlaceholderWithFirmware(char c) => ObservedRootsFixtures.PlaceholderRun with { FirmwareSha256 = new string(c, 64) };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void ReadTargets_RejectsAMalformedDocument(string json)
    {
        var act = () => ObservedRootPlanner.ReadTargets(json);

        act.Should().Throw<InvalidDataException>();
    }

    // Issue #773 (review M1): evidence from a run that did not reach a classified end is partial, and a partial set must not
    // read as "these are all the targets". A run that stopped at a legitimate end (including a deliberate StopAt handoff) is complete.
    public static TheoryData<string> IncompleteStates => ["BudgetExhausted", "RuntimeFailure", "UnsupportedTransfer", "InvalidState"];

    [Theory]
    [MemberData(nameof(IncompleteStates))]
    public void ReadExport_RejectsEvidenceFromARunThatDidNotFinish(string state)
    {
        var json = ObservedRootsFixtures.TargetsJsonInState(state, null, ObservedRootsFixtures.TargetObject(A, "unknown-code", 1, 1));

        var act = () => ObservedRootPlanner.ReadExport(json);

        act.Should().Throw<InvalidDataException>().WithMessage($"*{state}*");
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Returned")]
    [InlineData("RuntimeHandoff")]
    public void ReadExport_AcceptsEvidenceFromARunThatReachedAClassifiedEnd(string state)
    {
        var json = ObservedRootsFixtures.TargetsJsonInState(state, null);

        var act = () => ObservedRootPlanner.ReadExport(json);

        act.Should().NotThrow();
    }

    [Fact]
    public void ReadExport_RejectsAnExportWithoutAnOutcome_AndAnOutcomeThatContradictsItsState()
    {
        var complete = ObservedRootsFixtures.TargetsJsonInState("Completed", null);
        var withoutOutcome = complete.Replace("\"outcome\":{\"state\":\"Completed\",\"complete\":true,\"stopAt\":null},", "");
        var contradictory = complete.Replace("\"complete\":true", "\"complete\":false");

        var noOutcome = () => ObservedRootPlanner.ReadExport(withoutOutcome);
        var contradicts = () => ObservedRootPlanner.ReadExport(contradictory);
        noOutcome.Should().Throw<InvalidDataException>();
        contradicts.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ReadExport_RoundTripsTheRunIdentity()
    {
        var run = new ObservedRunIdentity(new string('c', 64), [0x80000100u], [new ObservedRunImage("img", Dest, new string('d', 64), Sha.Replace('0', 'e'), [Dest, A])], new string('a', 64), new string('b', 64));

        var read = ObservedRootPlanner.ReadExport(ObservedRootsFixtures.TargetsJson(A, "unknown-code", 1, run)).Run;

        read.Should().BeEquivalentTo(run);
    }

    [Fact]
    public void Verify_AcceptsTheSameRun_AndRejectsEachDifferingField()
    {
        var run = new ObservedRunIdentity(new string('c', 64), [0x80000100u], [new ObservedRunImage("img", Dest, new string('d', 64), new string('e', 64), [Dest])], new string('a', 64), new string('b', 64));
        var image = run.Images[0];
        ObservedRootPlanner.Verify(run, run with { });

        foreach (var (changed, what) in new (ObservedRunIdentity, string)[]
        {
            (run with { FirmwareSha256 = new string('f', 64) }, "firmware"),
            (run with { Interpreted = [] }, "interpret"),
            (run with { Images = [] }, "image count"),
            (run with { Images = [image with { Name = "x" }] }, "name"),
            (run with { Images = [image with { LoadAddress = Dest + 4 }] }, "load address"),
            (run with { Images = [image with { WordsSha256 = new string('0', 64) }] }, "code words"),
            (run with { Images = [image with { SourceSha256 = null }] }, "source SHA-256"),
            (run with { Images = [image with { Roots = [Dest, A] }] }, "roots"),
            (run with { DiscSha256 = null }, "disc image"),
            (run with { DiscSha256 = new string('9', 64) }, "disc image"),
            (run with { ProbeOptionsSha256 = new string('8', 64) }, "probe options"),
        })
        {
            var act = () => ObservedRootPlanner.Verify(changed, run);
            act.Should().Throw<InvalidDataException>().WithMessage($"*{what}*");
        }
    }

    // Issue #773 (review M2, owner decision): the export is bound to the ROM, the disc and the probe options that change which PCs run.
    [Fact]
    public void ProbeOptionsSha256_IsDistinctPerRootsAndCodeBytes_AndIgnoresTheOrderAndRepeatsOfRoots()
    {
        var baseline = ObservedRootPlanner.ProbeOptionsSha256(0, []);

        ObservedRootPlanner.ProbeOptionsSha256(0, [A, B]).Should().Be(ObservedRootPlanner.ProbeOptionsSha256(0, [B, A, A]));
        ObservedRootPlanner.ProbeOptionsSha256(0, [A]).Should().NotBe(baseline);
        ObservedRootPlanner.ProbeOptionsSha256(4096, []).Should().NotBe(baseline);
        // Issue #773 review M3: the run limits change which PCs a run observes, so they are part of the identity.
        ObservedRootPlanner.ProbeOptionsSha256(0, [], segments: 3).Should().NotBe(baseline);
        ObservedRootPlanner.ProbeOptionsSha256(0, [], segmentBudget: 5).Should().NotBe(baseline);
    }

    [Fact]
    public void ReadExport_RefusesAnExportProducedWithStopAt_BecauseItIsAPartialPause()
    {
        var json = ObservedRootsFixtures.TargetsJsonWithStop("RuntimeHandoff", null, "0x80030000", ObservedRootsFixtures.TargetObject(A, "unknown-code", 1, 1));

        var act = () => ObservedRootPlanner.ReadExport(json);

        act.Should().Throw<InvalidDataException>().WithMessage("*--stop-at*");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("99")]
    public void ReadExport_RejectsAStateGivenAsANumber(string state)
    {
        var json = ObservedRootsFixtures.TargetsJsonInState(state, null);

        var act = () => ObservedRootPlanner.ReadExport(json);

        act.Should().Throw<InvalidDataException>().WithMessage("*not a known execution state*");
    }

    [Fact]
    public void ReadExport_RefusesAPreChangeExport_WithoutTheRunInputs()
    {
        // The export format before the run-input binding carried no disc or probe-option hash: it is refused, not read as "no disc".
        var json = ObservedRootsFixtures.TargetsJsonInState("Completed", null).Replace(",\"probeOptionsSha256\":\"" + new string('0', 64) + "\"", "");

        var act = () => ObservedRootPlanner.ReadExport(json);

        act.Should().Throw<InvalidDataException>();
    }

    // Issue #773 (review, MEDIUM): a candidate whose combined walk fails is rejected alone with a reason; it must not abort the plan.
    // The walk is injected here: with real inputs a combined walk fails only where a single walk does too, so the guard is defensive.
    [Fact]
    public void ACandidateWhoseCombinedWalkFails_IsRejectedAlone_AndThePlanContinues()
    {
        IReadOnlySet<uint> Walk(uint loadAddress, IReadOnlyList<uint> words, IEnumerable<uint> roots)
        {
            var list = roots.ToArray();
            if (list.Contains(A)) throw new InvalidOperationException("the combined walk reaches a structurally invalid transfer");
            return ReachableProgramBuilder.ReachablePcs(loadAddress, words, list);
        }

        var plan = ObservedRootPlanner.Plan([Image()], [T(A, 100), T(B, 50)], Open, Walk);

        plan.Images.Single().Roots.Select(static r => r.Pc).Should().Equal(B);
        plan.Counts.Candidates.Should().Be(2);
        plan.Counts.Rejected.Should().ContainKey("not-structurally-safe").WhoseValue.Should().Be(1);
        (plan.Counts.Accepted + plan.Counts.Rejected.Values.Sum()).Should().Be(plan.Counts.Candidates);
    }

    // Issue #773 (review): only a candidate's walk is isolated. An image whose own entry-driven walk is structurally invalid is
    // not a candidate and must fail the whole plan.
    [Fact]
    public void AnImageWhoseOwnWalkIsStructurallyInvalid_FailsTheWholePlan()
    {
        var broken = new LoadedImageSpec("img", Dest, [MipsEncoding.Branch(0x04, 0, 0, Dest, Dest + 0x1000)], [Dest]) { SourceSha256 = Sha };

        var act = () => ObservedRootPlanner.Plan([broken], [T(A, 100)], Open);

        act.Should().Throw<InvalidOperationException>();
    }
}
