using System.Text;
using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Core.Runtime.CdRom;
using PSXRecomp.Infrastructure.Standalone;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Infrastructure;

/// <summary>
/// Issue #732 P1-2: the distribution's AOT plan must compile the game entry, not keep it interpreted. The probe's
/// milestone PCs (the shell load <c>0x80030000</c> and the executable boundary <c>0x80010000</c>) belong to the
/// probe run; a distribution compiles them and keeps only the exception vectors interpreted, so a PS-X EXE whose
/// entry is exactly <c>0x80010000</c> runs from a guarded native block. The block keeps its guard, so RAM that does
/// not hold the compiled words never runs it.
/// </summary>
[Test]
public sealed class FirmwareAotPlanTests
{
    private const uint GameEntry = 0x80010000u;
    private const uint ShellLoadAddress = 0x80030000u;

    private static uint I(uint opcode, uint rs, uint rt, uint imm) => opcode << 26 | rs << 21 | rt << 16 | (imm & 0xFFFFu);

    private static byte[] Text(string value) => Encoding.ASCII.GetBytes(value);

    private static byte[] SyntheticRom()
    {
        uint[] words =
        [
            I(0x0F, 0, 8, 0x8001), I(0x0D, 0, 9, 0x1234), I(0x2B, 8, 9, 0x100), 0,
            0x08000000u | ((OpenBiosFirmware.ResetVector + 0x10u & 0x0FFFFFFFu) >> 2), 0,
        ];
        var bytes = new byte[OpenBiosFirmware.ImageSize];
        for (var i = 0; i < words.Length; i++) BitConverter.GetBytes(words[i]).CopyTo(bytes, i * 4);
        return bytes;
    }

    private static FirmwareAotPlan BuildPlan(TempDirectory dir)
    {
        var rom = SyntheticRom();
        dir.WriteFile("rom.bin", rom);
        dir.WriteFile("aot/rom.roots", Text("0xBFC00000\n"));
        dir.WriteFile("aot/images.txt", Text("boot-exe game\n"));

        var exeBytes = SyntheticPsxExeBuilder.BuildValid(instructionCount: 4, textStart: GameEntry);
        var disc = SyntheticDiscBuilder.Bootable(exeBytes);
        var firmware = OpenBiosFirmware.FromBytes(rom);
        return FirmwareAotPlan.Build(
            firmware,
            dir.Combine("aot", "rom.roots"),
            codeBytes: 64,
            dir.Combine("aot", "images.txt"),
            new RawCdSectorSource(disc));
    }

    [Fact]
    public void GameEntry_IsCompiledToAGuardedNativeBlock_NotKeptInterpreted()
    {
        using var dir = new TempDirectory();
        var plan = BuildPlan(dir);

        plan.ObservationPcs.Should().NotContain(GameEntry, "the distribution must compile the game entry, not interpret it");
        plan.ObservationPcs.Should().NotContain(ShellLoadAddress, "the shell load address is probe observation, not a distribution exclusion");
        plan.ObservationPcs.Should().Contain(0x80000080u, "the general exception vector stays interpreted");
        plan.ObservationPcs.Should().Contain(0x000026A4u, "the kernel exception handler stays interpreted");

        plan.LoadedCode.Contains(GameEntry).Should().BeTrue("the boot executable's entry has a compiled version");
        var block = plan.LoadedCode.VersionsAt(GameEntry).Should().ContainSingle().Subject;
        block.Block.EntryPc.Should().Be(GameEntry);
        block.Words.Should().NotBeEmpty("the version carries its compiled words as a guard");
    }

    [Fact]
    public void TheGuardedBlock_RunsOnlyWhileRamHoldsItsCompiledWords()
    {
        using var dir = new TempDirectory();
        var plan = BuildPlan(dir);
        var block = plan.LoadedCode.VersionsAt(GameEntry).Should().ContainSingle().Subject;

        var ram = new uint[0x40];
        for (var i = 0; i < block.Words.Count; i++) ram[i] = block.Words[i];
        uint Read(uint address) => ram[(address - GameEntry) / 4];

        plan.LoadedCode.Match(GameEntry, Read).Should().BeSameAs(block, "RAM holds exactly the compiled words");

        ram[0] ^= 0xFFFF_FFFFu; // the guest overwrote the entry word: the compiled version is stale
        plan.LoadedCode.Match(GameEntry, Read).Should().BeNull("a stale version must never run over different RAM code");
    }

    /// <summary>
    /// Issue #732 review P2: the distribution's <c>rom.roots</c> reader must be the manifest's own roots parser, so
    /// comments, blank lines and an optional <c>0x</c> prefix are accepted, and a malformed line is an
    /// <see cref="InvalidDataException"/> the CLI classifies rather than a <see cref="FormatException"/> that
    /// escapes it.
    /// </summary>
    [Fact]
    public void RootsFile_AcceptsCommentsBlankLinesAndHexPrefix()
    {
        using var dir = new TempDirectory();
        var rom = SyntheticRom();
        dir.WriteFile("rom.bin", rom);
        dir.WriteFile("aot/rom.roots", Text("# reset vector\n\n0xBFC00000  # the reset PC\n"));
        dir.WriteFile("aot/images.txt", Text("# no images\n"));

        var plan = FirmwareAotPlan.Build(
            OpenBiosFirmware.FromBytes(rom),
            dir.Combine("aot", "rom.roots"),
            codeBytes: 64,
            dir.Combine("aot", "images.txt"),
            new RawCdSectorSource(new byte[2352]));

        // Owner ruling (Issue #B2): the roots file is validated, but the BIOS is never ahead-of-time translated.
        plan.Program.Blocks.Should().BeEmpty("the BIOS ROM is never compiled ahead of time; it runs in the interpreter fallback");
    }

    /// <summary>
    /// Owner ruling (Issue #B2, option 1): no BIOS code is ahead-of-time translated. Packaging a roots file that names the
    /// reset vector yields no ROM block, and the generated dispatch source names no block at a ROM address.
    /// </summary>
    [Fact]
    public void TheBiosIsNeverAheadOfTimeTranslated_NoRomBlockOrRomBlockFunctionIsGenerated()
    {
        using var dir = new TempDirectory();
        var rom = SyntheticRom();
        dir.WriteFile("aot/rom.roots", Text("0xBFC00000\n"));
        dir.WriteFile("aot/images.txt", Text("# no images\n"));

        var plan = FirmwareAotPlan.Build(
            OpenBiosFirmware.FromBytes(rom), dir.Combine("aot", "rom.roots"), 64, dir.Combine("aot", "images.txt"),
            new RawCdSectorSource(new byte[2352]));

        plan.Program.Blocks.Should().BeEmpty("the BIOS ROM is never compiled ahead of time");
        var dispatch = RecompilerHostCodeGen.Generate(plan.Program, plan.LoadedCode, allowEmptyProgram: true);
        dispatch.Success.Should().BeTrue(dispatch.DiagnosticMessage);
        dispatch.Source!.Should().NotContain("recompiler_block_0xBFC", "no BIOS block function reaches the generated source");
    }

    /// <summary>
    /// Issue #B2 (review: the exclusion is checked at the generation entry, not only in the output): the standalone source
    /// generated for an external BIOS names no BIOS block at a ROM address, in either the physical or the mapped range, and
    /// carries the external-image flag with an empty embedded image. The BIOS load address and the reset PC appear only as
    /// data (the image's load address and the initial PC), never as a block.
    /// </summary>
    [Fact]
    public void TheStandaloneSource_ForAnExternalBios_NamesNoBiosBlockInEitherRomRange()
    {
        using var dir = new TempDirectory();
        var rom = SyntheticRom();
        dir.WriteFile("aot/rom.roots", Text("0xBFC00000\n"));
        dir.WriteFile("aot/images.txt", Text("# no images\n"));
        var plan = FirmwareAotPlan.Build(
            OpenBiosFirmware.FromBytes(rom), dir.Combine("aot", "rom.roots"), 64, dir.Combine("aot", "images.txt"),
            new RawCdSectorSource(new byte[2352]));

        var dispatch = RecompilerHostCodeGen.Generate(plan.Program, plan.LoadedCode, allowEmptyProgram: true);
        dispatch.Success.Should().BeTrue(dispatch.DiagnosticMessage);
        var standalone = RecompiledArtifactCodeGen.GenerateStandalone(
            dispatch, new uint[TitleExecutionRequest.GprCount], 0, 0, OpenBiosFirmware.ResetVector, 1000, [],
            OpenBiosFirmware.ResetVector, [], hostBridge: true, directLaunchHostRelativePath: "runtime/host", externalBios: true);
        standalone.Success.Should().BeTrue(standalone.DiagnosticMessage);

        var source = standalone.Source!;
        source.Should().Contain("static const int embedded_bios_external = 1;");
        source.Should().Contain("static const uint32_t embedded_image_len = 0u;");
        foreach (var block in new[] { "recompiler_block_0xBFC", "recompiler_block_0x1FC", "recompiler_block_0x9FC" })
        {
            source.Should().NotContain(block, "no BIOS block function reaches the generated standalone source");
        }

        dispatch.Source!.Should().NotContain("recompiler_block_0xBFC");
        dispatch.Source!.Should().NotContain("recompiler_block_0x1FC");
    }

    [Fact]
    public void RootsFile_RejectsAnInvalidLineAsInvalidData()
    {
        using var dir = new TempDirectory();
        var rom = SyntheticRom();
        dir.WriteFile("rom.bin", rom);
        dir.WriteFile("aot/rom.roots", Text("0xBFC00000\nnot-hex\n"));
        dir.WriteFile("aot/images.txt", Text("# no images\n"));

        var act = () => FirmwareAotPlan.Build(
            OpenBiosFirmware.FromBytes(rom),
            dir.Combine("aot", "rom.roots"),
            codeBytes: 64,
            dir.Combine("aot", "images.txt"),
            new RawCdSectorSource(new byte[2352]));

        act.Should().Throw<InvalidDataException>().WithMessage("*hexadecimal*");
    }
}