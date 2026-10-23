using FluentAssertions;
using PSXRecomp.Infrastructure.Standalone;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2 (MAJOR-1): the run classifies the BIOS by its SHA-256. An unregistered image is never synthetic, and a declaration
/// does not make a label. The verified path is exercised through the injection entry point, not by registering a real digest.
/// </summary>
[Test]
public sealed class BiosProvenanceClassificationTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void AnUnregisteredImage_IsUnverified_NeverSynthetic()
    {
        BiosProvenance.Classify(Digest, new HashSet<string>(), new HashSet<string>()).Should().Be(BiosProvenance.Unverified);
    }

    [Fact]
    public void OnlyADigestInTheVerifiedAllowlist_IsVerified()
    {
        var verified = new HashSet<string> { Digest };

        BiosProvenance.Classify(Digest, verified, new HashSet<string>()).Should().Be(BiosProvenance.Verified);
        BiosProvenance.Classify(Digest.ToUpperInvariant(), verified, new HashSet<string>()).Should().Be(BiosProvenance.Verified,
            "the digest is compared case-insensitively");
        BiosProvenance.Classify(Digest, new HashSet<string>(), new HashSet<string>()).Should().NotBe(BiosProvenance.Verified);
    }

    [Fact]
    public void OnlyAKnownStubDigest_IsSynthetic()
    {
        BiosProvenance.Classify(Digest, new HashSet<string>(), new HashSet<string> { Digest }).Should().Be(BiosProvenance.Synthetic);
    }

    [Fact]
    public void TheDefaultRegistries_HoldNoVerifiedOpenBiosDigest()
    {
        BiosProvenance.VerifiedOpenBiosSha256.Should().BeEmpty(
            "no OpenBIOS build has an independently verified reproducible procedure yet (Issues #794, #732, #730)");
        BiosProvenance.Classify("96889cfc16e3637e58e3b709a8de4f47725a4c9f2cafde57e0c24dc075f5cfb7").Should().Be(BiosProvenance.Unverified,
            "the local build used by the real-image gate is not registered");
    }

    [Fact]
    public void ADeclarationThatTheRunDoesNotConfirm_ProducesAWarning()
    {
        BiosProvenance.DeclarationWarning("user-supplied", BiosProvenance.Unverified).Should().Contain("warning");
        BiosProvenance.DeclarationWarning("user-supplied", BiosProvenance.Verified).Should().BeNull();
        BiosProvenance.DeclarationWarning("synthetic", BiosProvenance.Synthetic).Should().BeNull();
        BiosProvenance.DeclarationWarning(null, BiosProvenance.Unverified).Should().BeNull();
    }
}
