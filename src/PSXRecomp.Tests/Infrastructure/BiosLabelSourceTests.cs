using System.Text;
using FluentAssertions;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2 (MEDIUM-3): the BIOS label comes from the digest of the bytes the run loaded, and never from the packaging-time
/// declaration. This is an unskipped unit test of the label source (the call the run makes). The packaged E2E checks the same
/// wiring through a real process, but it needs a toolchain; this test does not.
/// </summary>
[Test]
public sealed class BiosLabelSourceTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>The synthetic stub the registry holds: the same generator as the BIOS-shaped test image (LCG, seed 20261023).</summary>
    private static byte[] StubImage(ulong seed)
    {
        var bytes = new byte[512 * 1024];
        var state = seed;
        for (var i = 0; i < bytes.Length; i++)
        {
            state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
            bytes[i] = (byte)(state >> 56);
        }

        return bytes;
    }

    private string Write(byte[] bytes) => _dir.WriteFile("bios.bin", bytes);

    [Fact]
    public void ARegisteredStubDeclaredUserSupplied_IsLabelledByItsDigest_NotByTheDeclaration()
    {
        var path = Write(StubImage(20261023UL));
        var error = new StringWriter();

        var (_, _, label) = FirmwareRuntimeLauncher.LoadAndClassify(path, "user-supplied", error);

        label.Should().Be(BiosProvenance.Synthetic, "the stub's digest is registered; the declaration is not the source");
        error.ToString().Should().Contain("warning");
    }

    [Fact]
    public void AnUnregisteredImageDeclaredUserSupplied_IsUnverified_NotVerified()
    {
        // The mutation target: a label taken from the declaration would make this openbios-rom.
        var path = Write(StubImage(12345UL));
        var error = new StringWriter();

        var (_, _, label) = FirmwareRuntimeLauncher.LoadAndClassify(path, "user-supplied", error);

        label.Should().Be(BiosProvenance.Unverified);
        label.Should().NotBe(BiosProvenance.Verified);
        error.ToString().Should().Contain("warning: the image was declared 'user-supplied'");
    }

    [Fact]
    public void AnUnregisteredImageDeclaredSynthetic_IsUnverified_NotSynthetic()
    {
        var path = Write(StubImage(12345UL));

        var (_, _, label) = FirmwareRuntimeLauncher.LoadAndClassify(path, "synthetic", new StringWriter());

        label.Should().Be(BiosProvenance.Unverified, "an unregistered image is never synthetic automatically");
    }

    [Fact]
    public void AnImageInTheVerifiedAllowlist_IsVerified_ThroughTheInjectionPoint()
    {
        var path = Write(StubImage(12345UL));
        var sha = OpenBiosRomInput.Sha256Hex(StubImage(12345UL));
        var error = new StringWriter();

        var (_, _, label) = FirmwareRuntimeLauncher.LoadAndClassify(
            path, "user-supplied", error, verified: new HashSet<string> { sha }, synthetic: new HashSet<string>());

        label.Should().Be(BiosProvenance.Verified, "only a digest in the allowlist is verified");
        error.ToString().Should().BeEmpty("the declaration is confirmed, so no warning");
    }

    [Fact]
    public void NoDeclaration_NoWarning_AndTheLabelStillFollowsTheDigest()
    {
        var path = Write(Encoding.ASCII.GetBytes("unused").Concat(StubImage(20261023UL)).Take(512 * 1024).ToArray());
        var error = new StringWriter();

        var (_, _, label) = FirmwareRuntimeLauncher.LoadAndClassify(path, null, error);

        label.Should().Be(BiosProvenance.Unverified, "a modified stub is no longer the registered digest");
        error.ToString().Should().BeEmpty();
    }
}
