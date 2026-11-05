using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// The firmware run's public stderr line must not carry the ROM digest. The internal classification still reads the digest.
/// The ROM is seeded random data written to a temporary directory, never a real image.
/// </summary>
[Test]
public sealed class FirmwareRunPublicOutputTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>A deterministic 512 KiB synthetic image (a fixed LCG stream from the seed): never a real ROM.</summary>
    private static byte[] SyntheticRom(ulong seed)
    {
        var bytes = new byte[OpenBiosFirmware.ImageSize];
        var state = seed;
        for (var i = 0; i < bytes.Length; i++)
        {
            state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
            bytes[i] = (byte)(state >> 56);
        }

        return bytes;
    }

    private static string Sha256Lower(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static FirmwareRunSummary Summary(string romSha256, string biosMode) => new(
        Bios: "openbios",
        BiosMode: biosMode,
        DeclaredProvenance: null,
        RomSha256: romSha256,
        AotBiosBlocks: 0,
        AotLoadedBlocks: 3,
        State: "RuntimeFailure",
        GuestPc: "0x80010000",
        DiagnosticCode: "ARTIFACT_FALLBACK_BUDGET_EXHAUSTED",
        DiagnosticMessage: "fallback budget exhausted",
        MixedFallback: true,
        FallbackSegmentInstructionBudget: 100u,
        FallbackMaxTransitions: 10u,
        NativeInstructions: 42ul,
        FallbackInstructions: 7ul,
        FallbackTransitions: 2u,
        FinalGpr: ["0x00000000", "0x00000001"]);

    [Fact]
    public void PublicLine_NeverCarriesTheRomDigest_AndReportsTheLabel()
    {
        var rom = SyntheticRom(4101);
        var digest = Sha256Lower(rom);
        var path = _dir.WriteFile("openbios.bin", rom);

        // The digest is registered as verified, so the internal classification yields the verified label.
        var (_, sha256, label) = FirmwareRuntimeLauncher.LoadAndClassify(
            path,
            declared: null,
            new StringWriter(),
            verified: ImmutableHashSet.Create(StringComparer.Ordinal, digest),
            synthetic: ImmutableHashSet<string>.Empty);
        label.Should().Be(BiosProvenance.Verified, "the classification still follows the digest");
        sha256.Should().Be(digest);

        var publicLine = Summary(sha256, label).ToPublicJson();

        publicLine.Should().NotContain(digest, "the ROM digest is never written to a public channel");
        publicLine.Should().NotContain("romSha256");
        Regex.IsMatch(publicLine, "[0-9a-fA-F]{64}").Should().BeFalse("no 64-hex digest may appear in the public line");

        using var document = JsonDocument.Parse(publicLine);
        var root = document.RootElement;
        root.GetProperty("kind").GetString().Should().Be("firmware-run");
        root.GetProperty("biosMode").GetString().Should().Be(BiosProvenance.Verified);
        root.GetProperty("state").GetString().Should().Be("RuntimeFailure");
        // PR 3: run counts are not published. The compatibility fields that remain keep their values.
        root.TryGetProperty("nativeInstructions", out _).Should().BeFalse("run counts are not published");
        root.GetProperty("mixedFallback").GetBoolean().Should().BeTrue();
        root.GetProperty("fallbackMaxTransitions").GetUInt32().Should().Be(10);
    }

    [Fact]
    public void InternalClassification_IsUnverified_WhenTheDigestIsNotRegistered()
    {
        var rom = SyntheticRom(4102);
        var path = _dir.WriteFile("openbios.bin", rom);

        var (_, sha256, label) = FirmwareRuntimeLauncher.LoadAndClassify(
            path,
            declared: null,
            new StringWriter(),
            verified: ImmutableHashSet<string>.Empty,
            synthetic: ImmutableHashSet<string>.Empty);

        label.Should().Be(BiosProvenance.Unverified, "an unregistered digest is never reported as verified");
        Summary(sha256, label).ToPublicJson().Should().NotContain(sha256);
    }

    [Fact]
    public void ContentDiagnosticMessage_CarriesStatusOnly_NotTheBiosMatchCounts()
    {
        // The message is printed in packaging output. It names the status, not the byte counts derived from the ROM.
        var firmware = OpenBiosFirmware.FromBytes(SyntheticRom(4103));
        // A copy of 60 BIOS words: a real match, so the diagnostic holds non-zero counts internally.
        var copy = firmware.Words.Skip(20000).Take(60).ToArray();
        var manifest = new PSXRecomp.Core.Recompiler.LoadImageManifest(
            [new PSXRecomp.Core.Recompiler.LoadedImageSpec("copy", 0x80100000u, copy, [], "disc-exe")],
            new HashSet<uint>());

        var diagnostic = BiosAddressGuard.DiagnoseContent(manifest, firmware);

        diagnostic.HighVarietyBytes.Should().BeGreaterThan(0, "the counts stay internal to the record");
        diagnostic.Message.Should().Contain("content check");
        Regex.IsMatch(diagnostic.Message, @"\d+ bytes").Should().BeFalse("the public message holds no byte count");
    }
}
