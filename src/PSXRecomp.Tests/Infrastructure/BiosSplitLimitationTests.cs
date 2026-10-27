using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Infrastructure.Standalone;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2, KNOWN LIMIT (owner ruling, option 1: accepted residual). A complete BIOS split into several PS-X inputs is NOT detected as
/// BIOS: each piece is an ordinary executable to the structural checks, and a partial content match is a diagnostic only. These tests pin
/// that limit so that it cannot silently change, and they state that no complete detection is claimed. They use a synthetic BIOS-shaped
/// image (deterministic pseudo-random words), never the real BIOS bytes. A test named <c>KnownLimit_</c> describes a limit, not a guarantee.
/// </summary>
[Test]
public sealed class BiosSplitLimitationTests
{
    private const string Evidence = "synthetic-evidence";

    /// <summary>KnownLimit: a complete BIOS split into two PS-X inputs passes per piece and together. Not a guarantee.</summary>
    [Fact]
    public void KnownLimit_TwoPieceSplitBios_IsNotRefusedPerPieceOrTogether()
    {
        var bios = SyntheticBios();
        var words = bios.Words.ToArray();
        var first = Piece("split-0", 0x80010000u, words.AsSpan(0, words.Length / 2));
        var second = Piece("split-1", 0x80200000u, words.AsSpan(words.Length / 2));

        foreach (var piece in new[] { first, second })
        {
            BiosAddressGuard.DeriveInputClass(piece, bios).Should().Be(BiosInputClass.Executable, "a piece is not the complete image");
            ((Action)(() => BiosAddressGuard.RequireNoBiosImage(new LoadImageManifest([piece], new HashSet<uint>()), bios)))
                .Should().NotThrow("KnownLimit: a split piece is not refused individually");
        }

        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(new LoadImageManifest([first, second], new HashSet<uint>()), bios)))
            .Should().NotThrow("KnownLimit: the pieces are not refused together either (partial content is a diagnostic only)");
    }

    /// <summary>
    /// KnownLimit: a complete BIOS split into four PS-X inputs is not refused. It is reported by the content diagnostic only, which
    /// states that the provenance is unknown. This is not a claim of complete detection.
    /// </summary>
    [Fact]
    public void KnownLimit_FourPieceSplitBios_IsNotRefused_AndIsReportedOnly()
    {
        var bios = SyntheticBios();
        var words = bios.Words.ToArray();
        var quarter = words.Length / 4;
        var pieces = Enumerable.Range(0, 4)
            .Select(i => Piece($"split-{i}", 0x80010000u + (uint)i * 0x00100000u, words.AsSpan(i * quarter, quarter)))
            .ToArray();

        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(new LoadImageManifest(pieces, new HashSet<uint>()), bios)))
            .Should().NotThrow("KnownLimit: four pieces are not refused; the diagnostic reports their coverage instead");

        var diagnostic = BiosAddressGuard.DiagnoseContent(new LoadImageManifest(pieces, new HashSet<uint>()), bios);
        diagnostic.Provenance.Should().Be("unknown");
        diagnostic.HighVarietyBytes.Should().BeGreaterThan(0, "the diagnostic reports the coverage of the pieces");
    }

    /// <summary>KnownLimit: the diagnostic text claims no complete detection; it says it does not refuse and that the origin is unknown.</summary>
    [Fact]
    public void KnownLimit_TheDiagnosticClaimsNoCompleteDetection()
    {
        var bios = SyntheticBios();
        var words = bios.Words.ToArray();
        var pieces = new[] { Piece("split-0", 0x80010000u, words.AsSpan(0, words.Length / 2)) };

        var message = BiosAddressGuard.DiagnoseContent(new LoadImageManifest(pieces, new HashSet<uint>()), bios).Message;

        message.Should().Contain("does not refuse", "the diagnostic is a report, never a refusal");
        message.Should().Contain("unknown", "the origin of the inputs is unknown");
        message.Should().NotContainEquivalentOf("complete detection", "no complete detection is claimed");
        message.Should().NotContainEquivalentOf("detects all", "no complete detection is claimed");
        message.Should().NotContainEquivalentOf("all BIOS data", "no complete detection is claimed");
    }

    /// <summary>
    /// Guarantee, not a limit: the complete image itself is refused, as the guaranteed case of content identity. This test does not use
    /// the <c>KnownLimit_</c> prefix, which is reserved for the limits this class pins.
    /// </summary>
    [Fact]
    public void CompleteImage_IsStillRefused_AsTheGuaranteedCase()
    {
        var bios = SyntheticBios();
        var whole = new LoadedImageSpec("whole", 0x80010000u, bios.Words.ToArray(), [0x80010000u], "exe-file") { SourceSha256 = Evidence };

        ((Action)(() => BiosAddressGuard.RequireNoBiosImage(new LoadImageManifest([whole], new HashSet<uint>()), bios)))
            .Should().Throw<InvalidDataException>().WithMessage("*byte-identical to the complete BIOS image*");
    }

    private static LoadedImageSpec Piece(string name, uint loadAddress, ReadOnlySpan<uint> words) =>
        new(name, loadAddress, words.ToArray(), [loadAddress], "disc-exe") { SourceSha256 = Evidence };

    /// <summary>A deterministic 512 KiB image with no structure: synthetic, not the real BIOS.</summary>
    private static OpenBiosFirmware SyntheticBios()
    {
        var bytes = new byte[OpenBiosFirmware.ImageSize];
        var state = 0x5EED5EEDUL;
        for (var i = 0; i < bytes.Length; i++)
        {
            state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
            bytes[i] = (byte)(state >> 56);
        }

        return OpenBiosFirmware.FromBytes(bytes);
    }
}
