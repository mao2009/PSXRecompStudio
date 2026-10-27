using System.Runtime.InteropServices;
using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.E2E;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #B2 (MEDIUM-2, L2): the production packaging path with each generation-stage guard's wiring disabled in turn. The input's
/// manifest names a BIOS-destination image, a rom image. Each stage's refusal carries its own stage name, so a test can tell which
/// stage fired: a stage is load-bearing only when the stages before it are disabled.
/// </summary>
[Test]
public sealed class BiosWiringIntegrationTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>A reproducible ROM whose first words are valid MIPS (addiu, jr ra, nop); the rest is zero padding.</summary>
    private static byte[] CodeRom()
    {
        var bytes = new byte[OpenBiosFirmware.ImageSize];
        uint[] words = [0x24080001u, 0x03E00008u, 0u, 0x24090002u, 0x03E00008u, 0u];
        for (var i = 0; i < words.Length; i++)
        {
            BitConverter.GetBytes(words[i]).CopyTo(bytes, i * 4);
        }

        return bytes;
    }

    private FirmwareDistributionRequest Request(string outputName)
    {
        var rom = _dir.WriteFile("openbios.bin", CodeRom());
        var aot = _dir.CreateSubdirectory("aot");
        _dir.WriteFile("aot/rom.roots", System.Text.Encoding.ASCII.GetBytes("0xBFC00000\n"));
        _dir.WriteFile("aot/roots-bios.txt", System.Text.Encoding.ASCII.GetBytes("0xBFC00000\n"));
        _dir.WriteFile("aot/images.txt", System.Text.Encoding.ASCII.GetBytes(
            "image bios-range 0xBFC00000 rom 0xBFC00000 0x40 roots-bios.txt\n"));
        var disc = _dir.WriteFile("disc.bin", new byte[2352]);
        return new FirmwareDistributionRequest(
            RomPath: rom,
            DiscPath: disc,
            AotDirectory: aot,
            OutputDirectory: _dir.Combine(outputName),
            CodeBytes: 64,
            RunTimeoutMs: 1000,
            SelfContained: false,
            BuildTimeout: TimeSpan.FromMinutes(5));
    }

    private static void Build(FirmwareDistributionRequest request) =>
        FirmwareDistributionBuilder.Build(request, RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

    [Fact]
    public void ProductionBuild_RefusesTheBiosRangeImage_AtTheManifestStage()
    {
        ((Action)(() => Build(Request("refused-manifest"))))
            .Should().Throw<InvalidDataException>().WithMessage("*structural kind 'rom-image'*");
    }

    [Fact]
    public void WithTheManifestAndDispatchStagesDisabled_ThePlanStageRefuses_UnderItsOwnName()
    {
        // Plan on, dispatch off: the refusal that fires is the plan's own, named [stage: plan].
        using var scope = BiosGuardWiring.DisableStages(BiosGuardWiring.Manifest, BiosGuardWiring.Dispatch);

        ((Action)(() => Build(Request("refused-plan"))))
            .Should().Throw<InvalidOperationException>().WithMessage("*[stage: plan]*");
    }

    [Fact]
    public void WithTheManifestAndPlanStagesDisabled_TheDispatchStageRefuses_UnderItsOwnName()
    {
        using var scope = BiosGuardWiring.DisableStages(BiosGuardWiring.Manifest, BiosGuardWiring.Plan);

        ((Action)(() => Build(Request("refused-dispatch"))))
            .Should().Throw<InvalidOperationException>().WithMessage("*[stage: dispatch]*");
    }

    [Fact]
    public void WithThreeStagesDisabled_TheSourceStageRefuses_OnTheGeneratedBlockName()
    {
        using var scope = BiosGuardWiring.DisableStages(BiosGuardWiring.Manifest, BiosGuardWiring.Plan, BiosGuardWiring.Dispatch);

        ((Action)(() => Build(Request("refused-source"))))
            .Should().Throw<InvalidOperationException>().WithMessage("*names a block at 0xBFC00000*");
    }

    /// <summary>
    /// The load-bearing proof: with every stage disabled the production path builds an artifact whose generated C names a block at
    /// the BIOS address. So the refusals above are what keeps BIOS code out.
    /// </summary>
    [SkippableFact]
    public void WithAllFourStagesDisabled_TheBiosBlockReachesTheGeneratedSource()
    {
        RuntimeHostPublishE2E.RequireGcc();
        using var scope = BiosGuardWiring.DisableStages(
            BiosGuardWiring.Manifest, BiosGuardWiring.Plan, BiosGuardWiring.Dispatch, BiosGuardWiring.Source);
        var request = Request("unguarded");

        Build(request);

#pragma warning disable AARC003 // Test-layer inspection of the generated C that the unguarded build wrote
        var source = string.Concat(Directory.EnumerateFiles(request.OutputDirectory, "*.c").Select(File.ReadAllText));
#pragma warning restore AARC003
        source.Should().Contain("recompiler_block_0xBFC00000", "with the guards off, the BIOS block reaches the generated source");
    }

    /// <summary>A deterministic 512 KiB BIOS-shaped image (the same LCG as the copy tests): not OpenBIOS, not a registered stub.</summary>
    private static byte[] LcgRom()
    {
        var bytes = new byte[OpenBiosFirmware.ImageSize];
        var state = 0x5EED5EEDUL;
        for (var i = 0; i < bytes.Length; i++)
        {
            state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
            bytes[i] = (byte)(state >> 56);
        }

        return bytes;
    }

    /// <summary>The little-endian bytes of the ROM's words from <paramref name="firstWord"/>, <paramref name="count"/> of them.</summary>
    private static byte[] WordsBytes(byte[] rom, int firstWord, int count)
    {
        var bytes = new byte[count * 4];
        Array.Copy(rom, firstWord * 4, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>A production request whose manifest names the given AOT inputs; the disc and the extra AOT files are the test's.</summary>
    private FirmwareDistributionRequest CopyRequest(string name, byte[] rom, byte[] disc, string imageLines, params (string File, byte[] Bytes)[] aotFiles)
    {
        var romPath = _dir.WriteFile(name + "-bios.bin", rom);
        var aot = _dir.CreateSubdirectory(name + "-aot");
        _dir.WriteFile(name + "-aot/rom.roots", System.Text.Encoding.ASCII.GetBytes("0xBFC00000\n"));
        _dir.WriteFile(name + "-aot/images.txt", System.Text.Encoding.ASCII.GetBytes(imageLines));
        foreach (var (file, bytes) in aotFiles)
        {
            _dir.WriteFile(name + "-aot/" + file, bytes);
        }

        var discPath = _dir.WriteFile(name + "-disc.bin", disc);
        return new FirmwareDistributionRequest(
            RomPath: romPath,
            DiscPath: discPath,
            AotDirectory: aot,
            OutputDirectory: _dir.Combine(name),
            CodeBytes: 64,
            RunTimeoutMs: 1000,
            SelfContained: false,
            BuildTimeout: TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void ProductionManifest_DiagnosesA252ByteBiosCopyInADiscExecutable_AndDoesNotRefuseIt()
    {
        // The disc's boot executable carries 63 whole words of the BIOS (252 bytes). Its provenance is unknown from content: the
        // production manifest stage does not refuse it; the plan carries the content diagnostic instead (Issue #B2, option 1).
        var rom = LcgRom();
        var exe = SyntheticPsxExeBuilder.Build(0x80010000u, 0x80010000u, 252, WordsBytes(rom, 8192, 63));
        var request = CopyRequest("copy-252", rom, SyntheticDiscBuilder.Bootable(exe), "boot-exe game\n");

        using var disc = DiscSectorSourceOpener.Open(request.DiscPath);
        var plan = FirmwareAotPlan.Build(
            OpenBiosFirmware.FromBytes(rom),
            Path.Combine(request.AotDirectory, "rom.roots"),
            request.CodeBytes,
            Path.Combine(request.AotDirectory, "images.txt"),
            disc.Source);

        plan.ContentDiagnostic.Should().NotBeNull();
        plan.ContentDiagnostic!.HighVarietyBytes.Should().Be(252);
        plan.ContentDiagnostic.Provenance.Should().Be("unknown");
    }

    [Fact]
    public void DistributionReadme_DoesNotClaimThatTheRomIsCompiled()
    {
        // The distributed README is the user's statement of what is compiled. It must say that the ROM is not compiled, and must not say
        // that the artifact embeds the ROM or holds a BIOS digest (Issue #B2, review MEDIUM-1).
        var readme = FirmwareDistributionBuilder.Readme("game.exe");

        readme.Should().Contain("The OpenBIOS ROM is not compiled", "the README states the ROM is interpreted, not compiled");
        readme.Should().Contain("compiled code", "the README says what the native artifact holds");
        readme.Should().NotContain("ROM compiled ahead of time", "the README must not claim the ROM is compiled");
        readme.Should().NotContain("embeds the ROM", "the distribution holds no BIOS bytes");
        readme.Should().Contain("No BIOS bytes or digest are written", "the README states what the distribution holds");
    }

    [Fact]
    public void ProductionManifest_DiagnosesA124ByteCopySplitAcrossTwoAotInputs_AndDoesNotRefuseIt()
    {
        // A 127-byte copy's 31 whole words (124 bytes), split: 16 words in the disc's boot executable, 15 in a PS-X EXE file.
        // Reported across the inputs; not refused by content (Issue #B2, option 1).
        var rom = LcgRom();
        var first = SyntheticPsxExeBuilder.Build(0x80010000u, 0x80010000u, 64, WordsBytes(rom, 4096, 16));
        var second = SyntheticPsxExeBuilder.Build(0x80020000u, 0x80020000u, 60, WordsBytes(rom, 20000, 15));
        var request = CopyRequest(
            "copy-split", rom, SyntheticDiscBuilder.Bootable(first),
            "boot-exe game\nexe half2 half2.exe\n",
            ("half2.exe", second));

        using var disc = DiscSectorSourceOpener.Open(request.DiscPath);
        var plan = FirmwareAotPlan.Build(
            OpenBiosFirmware.FromBytes(rom),
            Path.Combine(request.AotDirectory, "rom.roots"),
            request.CodeBytes,
            Path.Combine(request.AotDirectory, "images.txt"),
            disc.Source);

        plan.ContentDiagnostic!.HighVarietyBytes.Should().Be(124, "the cumulative count across the two inputs");
    }

    [Fact]
    public void TheFirmwarePlan_CompilesNoRomBlock_AndNoCompiledBlockStartsInTheBios()
    {
        // Owner ruling R1 (MAJOR-2 option 2): the normal package path compiles no BIOS code. The plan's own program is empty, and no
        // compiled block starts in the BIOS range. The synthetic executable may compile to no block at all, so the BIOS check is the
        // assertion that matters here; the real packaged C is checked by ThePackagedArtifact_NamesNoBlockAtABiosAddress.
        var rom = LcgRom();
        var exe = SyntheticPsxExeBuilder.Build(0x80010000u, 0x80010000u, 64, WordsBytes(rom, 4096, 16));
        var request = CopyRequest("plan-r1", rom, SyntheticDiscBuilder.Bootable(exe), "boot-exe game\n");

        using var disc = DiscSectorSourceOpener.Open(request.DiscPath);
        var plan = FirmwareAotPlan.Build(
            OpenBiosFirmware.FromBytes(rom),
            Path.Combine(request.AotDirectory, "rom.roots"),
            request.CodeBytes,
            Path.Combine(request.AotDirectory, "images.txt"),
            disc.Source);

        plan.Program.Blocks.Should().BeEmpty("the firmware plan compiles no ROM block (R1)");
        plan.LoadedCode.Blocks.Should().NotContain(static version => BiosAddressGuard.IsBiosAddress(version.Block.EntryPc),
            "no compiled block starts in the BIOS range");
    }

}
