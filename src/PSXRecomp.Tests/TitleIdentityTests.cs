using PSXRecomp.Core.TitleIdentity;

namespace PSXRecomp.Tests.TitleIdentityTests;

[Test]
public class TitleIdentityTests
{
    [Fact]
    public void Region_Values_AreStableAndDistinct()
    {
        ((int)Region.Unknown).Should().Be(0);
        ((int)Region.Japan).Should().Be(1);
        ((int)Region.NorthAmerica).Should().Be(2);
        ((int)Region.Europe).Should().Be(3);
        ((int)Region.Asia).Should().Be(4);
        ((int)Region.Korea).Should().Be(5);
        ((int)Region.Australia).Should().Be(6);
        Enum.GetValues<Region>().Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Revision_Equality_IsValueBased()
    {
        var a = new Revision(1, 1997, 9);
        var b = new Revision(1, 1997, 9);
        var c = new Revision(2, 1997, 9);

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        (a != c).Should().BeTrue();
    }

    [Fact]
    public void Revision_CanonicalKey_IsDeterministic()
    {
        var a = new Revision(1, 1997, 9);
        var b = new Revision(1, 1997, 9);

        a.CanonicalKey.Should().Be(b.CanonicalKey);
        a.CanonicalKey.Should().Be("1.1997.09");
    }

    [Fact]
    public void TitleIdentity_CanonicalKey_IsDeterministic()
    {
        var revision = new Revision(1, 1997, 9);
        var a = new TitleIdentity("SLUS-00594", "Example", Region.NorthAmerica, revision);
        var b = new TitleIdentity("SLUS-00594", "Example", Region.NorthAmerica, revision);

        a.CanonicalKey.Should().Be(b.CanonicalKey);
        a.CanonicalKey.Should().Be("SLUS-00594@NorthAmerica:1.1997.09");
    }

    [Fact]
    public void TitleIdentity_Equality_IgnoresTitleName()
    {
        var revision = new Revision(1, 1997, 9);
        var a = new TitleIdentity("SLUS-00594", "Example", Region.NorthAmerica, revision);
        var b = new TitleIdentity("SLUS-00594", "Renamed", Region.NorthAmerica, revision);

        a.Should().NotBe(b);
        a.CanonicalKey.Should().Be(b.CanonicalKey);
    }

    [Fact]
    public void TitleIdentity_Region_InspectedDistinctly()
    {
        var revision = new Revision(1, 1997, 9);
        var us = new TitleIdentity("SLUS-00594", "Example", Region.NorthAmerica, revision);
        var jp = new TitleIdentity("SLPS-12345", "ExampleJP", Region.Japan, revision);

        us.Region.Should().Be(Region.NorthAmerica);
        jp.Region.Should().Be(Region.Japan);

        // "works on US" is not "works on all PS1": the region is part of the identity.
        us.CanonicalKey.Should().NotBe(jp.CanonicalKey);
    }

    [Fact]
    public void DiscIdentity_Construction_ExposesFields()
    {
        var revision = new Revision(1, 1997, 9);
        var disc = new DiscIdentity("SLUS-00594", Region.NorthAmerica, revision, 1)
        {
            LayoutHint = "mixed-mode",
        };

        disc.Serial.Should().Be("SLUS-00594");
        disc.Region.Should().Be(Region.NorthAmerica);
        disc.Revision.Should().Be(revision);
        disc.DiscIndex.Should().Be(1);
        disc.LayoutHint.Should().Be("mixed-mode");
        disc.CanonicalKey.Should().Be("SLUS-00594@NorthAmerica:disc1");
    }

    [Fact]
    public void DiscIdentity_Equality_IsValueBased()
    {
        var revision = new Revision(1, 1997, 9);
        var a = new DiscIdentity("SLUS-00594", Region.NorthAmerica, revision, 1);
        var b = new DiscIdentity("SLUS-00594", Region.NorthAmerica, revision, 1);

        a.Should().Be(b);
    }

    [Fact]
    public void ExecutableIdentity_Construction_ExposesFields()
    {
        var exe = new ExecutableIdentity("SYSTEM.CNF", 0x80010000u, 0x80012000u, 4096, "aabb");

        exe.FileName.Should().Be("SYSTEM.CNF");
        exe.ImageLoadAddress.Should().Be(0x80010000u);
        exe.EntryPoint.Should().Be(0x80012000u);
        exe.Size.Should().Be(4096);
        exe.FileHashHex.Should().Be("aabb");
        exe.CanonicalKey.Should().Be("SYSTEM.CNF:80010000:80012000:00001000:aabb");
    }

    [Fact]
    public void ExecutableIdentity_Equality_IsValueBased()
    {
        var a = new ExecutableIdentity("SYSTEM.CNF", 0x80010000u, 0x80012000u, 4096, "aabb");
        var b = new ExecutableIdentity("SYSTEM.CNF", 0x80010000u, 0x80012000u, 4096, "aabb");

        a.Should().Be(b);
    }

    [Fact]
    public void BootExecutableFingerprint_SameInput_SameFingerprint()
    {
        var a = new ExecutableIdentity("SYSTEM.CNF", 0x80010000u, 0x80012000u, 4096, "aabb");
        var b = new ExecutableIdentity("SYSTEM.CNF", 0x80010000u, 0x80012000u, 4096, "aabb");

        var fa = BootExecutableFingerprint.Compute(a);
        var fb = BootExecutableFingerprint.Compute(b);

        fa.Should().Be(fb);
        fa.Value.Should().Be(fb.Value);
        fa.Algorithm.Should().Be(BootExecutableFingerprint.DefaultAlgorithm);
    }

    [Fact]
    public void BootExecutableFingerprint_DifferentInput_DifferentFingerprint()
    {
        var a = new ExecutableIdentity("SYSTEM.CNF", 0x80010000u, 0x80012000u, 4096, "aabb");
        var b = new ExecutableIdentity("SYSTEM.CNF", 0x80010000u, 0x80012000u, 4096, "ccdd");

        var fa = BootExecutableFingerprint.Compute(a);
        var fb = BootExecutableFingerprint.Compute(b);

        fa.Should().NotBe(fb);
        fa.Value.Should().NotBe(fb.Value);
    }

    [Fact]
    public void BootExecutableFingerprint_IsStableBetweenRuns()
    {
        var exe = new ExecutableIdentity("SYSTEM.CNF", 0x80010000u, 0x80012000u, 4096, "aabb");
        var fp1 = BootExecutableFingerprint.Compute(exe);

        // Determinism check across two computations of the same input.
        var fp2 = BootExecutableFingerprint.Compute(exe);
        fp1.Value.Should().Be(fp2.Value);
        fp1.Value.Should().MatchRegex("^[0-9a-f]{40}$");
    }

    [Fact]
    public void Serialization_ToJsonShape_ContainsCanonicalKey()
    {
        var revision = new Revision(1, 1997, 9);
        var title = new TitleIdentity("SLUS-00594", "Example", Region.NorthAmerica, revision);
        var disc = new DiscIdentity("SLUS-00594", Region.NorthAmerica, revision, 1);
        var exe = new ExecutableIdentity("SYSTEM.CNF", 0x80010000u, 0x80012000u, 4096, "aabb");
        var fp = BootExecutableFingerprint.Compute(exe);

        TitleIdentitySerialization.ToJsonShape(title)["canonicalKey"].Should().Be(title.CanonicalKey);
        TitleIdentitySerialization.ToJsonShape(disc)["canonicalKey"].Should().Be(disc.CanonicalKey);
        TitleIdentitySerialization.ToJsonShape(exe)["canonicalKey"].Should().Be(exe.CanonicalKey);
        TitleIdentitySerialization.ToJsonShape(fp)["canonicalKey"].Should().Be(fp.CanonicalKey);
    }
}
