using FluentAssertions;
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

    private static ObservedFallbackTarget T(uint pc, ulong instructions, string cause = "unknown-code", ulong entries = 1) =>
        new(pc, cause, entries, instructions);

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

        targets.Should().ContainSingle().Which.Should().Be(new ObservedFallbackTarget(A, "unknown-code", 1, 100));
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
        var withoutOutcome = complete.Replace("\"outcome\":{\"state\":\"Completed\",\"complete\":true},", "");
        var contradictory = complete.Replace("\"complete\":true", "\"complete\":false");

        var noOutcome = () => ObservedRootPlanner.ReadExport(withoutOutcome);
        var contradicts = () => ObservedRootPlanner.ReadExport(contradictory);
        noOutcome.Should().Throw<InvalidDataException>();
        contradicts.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ReadExport_RoundTripsTheRunIdentity()
    {
        var run = new ObservedRunIdentity(new string('c', 64), [0x80000100u], [new ObservedRunImage("img", Dest, new string('d', 64), Sha.Replace('0', 'e'), [Dest, A])]);

        var read = ObservedRootPlanner.ReadExport(ObservedRootsFixtures.TargetsJson(A, "unknown-code", 1, run)).Run;

        read.Should().BeEquivalentTo(run);
    }

    [Fact]
    public void Verify_AcceptsTheSameRun_AndRejectsEachDifferingField()
    {
        var run = new ObservedRunIdentity(new string('c', 64), [0x80000100u], [new ObservedRunImage("img", Dest, new string('d', 64), new string('e', 64), [Dest])]);
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
        })
        {
            var act = () => ObservedRootPlanner.Verify(changed, run);
            act.Should().Throw<InvalidDataException>().WithMessage($"*{what}*");
        }
    }
}
