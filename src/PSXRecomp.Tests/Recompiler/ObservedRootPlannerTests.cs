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

    [Theory]
    [InlineData("""{"format":"other/v9","targets":[]}""")]
    [InlineData("""{"format":"psxrecomp.fallback-targets/v1","targets":[{"pc":"0x80010008","cause":"unknown-code","entries":1,"instructions":1},{"pc":"0x80010008","cause":"unknown-code","entries":1,"instructions":1}]}""")]
    [InlineData("""{"format":"psxrecomp.fallback-targets/v1","targets":[{"pc":"0xZZ","cause":"unknown-code","entries":1,"instructions":1}]}""")]
    [InlineData("""{"format":"psxrecomp.fallback-targets/v1"}""")]
    public void ReadTargets_RejectsAMalformedDocument(string json)
    {
        var act = () => ObservedRootPlanner.ReadTargets(json);

        act.Should().Throw<InvalidDataException>();
    }
}
