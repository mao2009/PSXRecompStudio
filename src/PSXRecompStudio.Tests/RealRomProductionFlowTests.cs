using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Execution;
using PSXRecompStudio.Services;
using PSXRecompStudio.ViewModels;

namespace PSXRecompStudio.Tests;

// Issue #409: the Studio product flow must retain the analyzed PS-X EXE from the analysis
// outcome and feed that same executable into the production execution service
// (TitleExecutionService.Run(PsxExe) → ExecutionOrchestrator → InterpreterTitleExecutionEngine).
// These are product-flow tests, not direct Run(exe) unit tests: each drives the Studio's
// service/view-model layer from a synthetic disc input and asserts the classified result.
[Test]
public class RealRomProductionFlowTests
{
    [Fact]
    public void AnalyzeAndExecuteFromDiscImage_HoldsTheAnalyzedExeAndRunsItThroughProductionExecution()
    {
        const uint textStart = 0x80010000u;
        const uint gpInitial = 0xAAAABBCCu;

        // The program reads its own first instruction word from the loaded image (proving the
        // bytes landed at TextStart), prints the GP-initial low byte, then falls off the end of
        // the text region → Completed. Same semantics as the direct Run(exe) unit tests, but here
        // the executable enters through disc analysis first.
        var exeBytes = SyntheticDiscImage.BuildSyntheticExe(
            textStart,
            entryPoint: textStart,
            spInitial: 0x801FFF00u,
            gpInitial: gpInitial,
            [
                0x3C0E8001u,    // word0: lui $t6, 0x8001      (0x3C0E8001) — first-word oracle
                0x8DC40000u,    // word1: lw  $a0, 0($t6)
                0u,             // word2: nop (load delay)
                0x308400FFu,    // word3: andi $a0, $a0, 0xFF  — low byte → 0x01
                0x3409003Cu,    // word4: ori $t1, $zero, 0x3C — BIOS function: A0:3C putchar
                0x0C000028u,    // word5: jal 0x800000A0
                0u,             // word6: nop (branch delay slot)
                0x3409003Cu,    // word7: ori $t1, $zero, 0x3C
                0x001C2020u,    // word8: add $a0, $zero, $gp
                0x308400FFu,    // word9: andi $a0, $a0, 0xFF  — gp low byte → 0xCC
                0x0C000028u,    // word10: jal 0x800000A0
                0u,             // word11: nop; then PC == textEnd → Completed
            ]);

        // Wrap the EXE in a synthetic disc: SYSTEM.CNF (BOOT points at the EXE) + the EXE file.
        var discImageBytes = SyntheticDiscImage.BuildExecIso(exeBytes);
        var sha256 = SyntheticDiscImage.Sha256Hex(discImageBytes);

        var result = new RealRomTitleExecutionService()
            .AnalyzeAndExecuteFromDiscImage(discImageBytes, sha256);

        // The analysis outcome passed and preserved its executable...
        result.Analysis.Status.Should().Be(RomAnalysisStatus.Pass);
        result.Analysis.Executable.Should().NotBeNull();
        // ...and that exact executable object was handed to the production execution service.
        result.Executable.Should().BeSameAs(result.Analysis.Executable,
            "the analyzed executable must be the same object passed to TitleExecutionService.Run(PsxExe)");

        // The production execution path classified the run: interpreter engine, natural end,
        // and the EXE's own program output — proving the retained executable was executed.
        result.Run.Should().NotBeNull();
        result.Run!.Result.State.Should().Be(TitleExecutionState.Completed);
        result.Run.Result.EngineName.Should().Be(InterpreterTitleExecutionEngine.EngineName);
        result.Run.Result.SegmentsRetired.Should().BeGreaterThan(0);
        result.Run.Output.Should().Equal(0x01, 0xCC);
    }

    [Fact]
    public void AnalyzeAndExecuteFromDiscImage_WhenAnalysisYieldsNoExecutable_ReturnsOutcomeWithoutRun()
    {
        // SYSTEM.CNF points at a boot file that is not a PS-X EXE → PSX_EXE stage fails.
        var discImageBytes = SyntheticDiscImage.BuildExecIso(
            exeBytes: [0x00, 0x11, 0x22, 0x33],
            bootPath: "cdrom:\\NOTANEXE.BIN;1",
            isoName: "NOTANEXE.BIN;1");
        var sha256 = SyntheticDiscImage.Sha256Hex(discImageBytes);

        var result = new RealRomTitleExecutionService()
            .AnalyzeAndExecuteFromDiscImage(discImageBytes, sha256);

        result.Analysis.Status.Should().NotBe(RomAnalysisStatus.Pass);
        result.Analysis.Executable.Should().BeNull();
        result.Executable.Should().BeNull();
        result.Run.Should().BeNull();
    }

    [Fact]
    public async Task MainWindowViewModel_RunRealTitleCommand_ReportsClassifiedOutcome()
    {
        const uint textStart = 0x80010000u;
        var exeBytes = SyntheticDiscImage.BuildSyntheticExe(
            textStart, entryPoint: textStart, spInitial: 0x801FFF00u,
            gpInitial: 0xAAAABBCCu,
            [
                0x3C0E8001u, 0x8DC40000u, 0u, 0x308400FFu,
                0x3409003Cu, 0x0C000028u, 0u,
                0x3409003Cu, 0x001C2020u, 0x308400FFu, 0x0C000028u, 0u,
            ]);
        var discImageBytes = SyntheticDiscImage.BuildExecIso(exeBytes);

        var viewModel = new MainWindowViewModel();
        viewModel.DiscImageBytes = discImageBytes;

        viewModel.RealTitleExecutionStatus.Should().Be("Not run");
        viewModel.RunRealTitleCommand.Should().BeAssignableTo<IAsyncRelayCommand>(
            "the real-title command must run off the UI thread (Issue #409 follow-up)");
        await viewModel.RunRealTitleCommand.ExecuteAsync(null);

        viewModel.RealTitleExecutionStatus.Should().Contain(nameof(TitleExecutionState.Completed));
        viewModel.RealTitleExecutionStatus.Should().Contain(InterpreterTitleExecutionEngine.EngineName);
    }

    [Fact]
    public async Task MainWindowViewModel_RunRealTitleCommand_WithoutLoadedDisc_ReportsNotLoaded()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.DiscImageBytes = null;

        await viewModel.RunRealTitleCommand.ExecuteAsync(null);

        viewModel.RealTitleExecutionStatus.Should().Contain("Load a disc image");
    }

    [Fact]
    public async Task MainWindowViewModel_RunRealTitleCommand_WhenExecutableFailsLayoutValidation_ReportsRejectionWithoutThrowing()
    {
        // Text region [0x7FFFFFF0..0x80000010) straddles the KUSEG/KSEG0 boundary.
        // RomAnalysisPipeline's own EXE_HEADER/ENTRY_POINT/TEXT_REGION stages never call
        // Ps1AddressTranslation, so analysis passes; PsxExeTitleInput.Build rejects the
        // non-contiguous physical span before a single instruction runs. The command must
        // classify this instead of letting the ArgumentException escape to the UI.
        const uint textStart = 0x7FFFFFF0u;
        var exeBytes = SyntheticDiscImage.BuildSyntheticExe(
            textStart, entryPoint: textStart, spInitial: 0x801FFF00u,
            gpInitial: 0xAAAABBCCu,
            [0u, 0u, 0u, 0u, 0u, 0u, 0u, 0x03E00008u]); // nops, then jr $ra
        var discImageBytes = SyntheticDiscImage.BuildExecIso(exeBytes);

        var viewModel = new MainWindowViewModel();
        viewModel.DiscImageBytes = discImageBytes;

        var act = async () => await viewModel.RunRealTitleCommand.ExecuteAsync(null);

        await act.Should().NotThrowAsync(
            "an execution-layout rejection must be classified, not thrown from the command");
        viewModel.RealTitleExecutionStatus.Should().Contain("rejected for execution");
    }
}

/// <summary>
/// Builds minimal in-memory ISO 9660 images (2048-byte user-data sectors) and synthetic
/// PS-X EXE byte arrays so the real-ROM product flow can be exercised without a copyrighted
/// disc image. Mirrors the Core test assembly's <c>SyntheticDiscBuilder</c>, but this copy
/// lives in the Studio's test assembly because Application → Test is a forbidden dependency.
/// </summary>
[Test]
internal static class SyntheticDiscImage
{
    private const int SectorSize = Iso9660Reader.SectorSize;
    private const int FirstFileSector = 20;

    /// <summary>Builds a PS-X EXE with spec offsets (entry@0x10, GP@0x14, textStart@0x18, textSize@0x1C, SP@0x30).</summary>
    public static byte[] BuildSyntheticExe(uint textStart, uint entryPoint, uint spInitial, uint gpInitial, uint[] words)
    {
        var fileContent = new byte[PsxExeHeader.HeaderSize + words.Length * 4];

        Buffer.BlockCopy(BitConverter.GetBytes(PsxExeHeader.Magic), 0, fileContent, 0, 8);
        BitConverter.GetBytes(entryPoint).CopyTo(fileContent, 0x10);
        BitConverter.GetBytes(gpInitial).CopyTo(fileContent, 0x14);
        BitConverter.GetBytes(textStart).CopyTo(fileContent, 0x18);
        BitConverter.GetBytes((uint)(words.Length * 4)).CopyTo(fileContent, 0x1C);
        BitConverter.GetBytes(spInitial).CopyTo(fileContent, 0x30);

        for (var i = 0; i < words.Length; i++)
        {
            BitConverter.GetBytes(words[i]).CopyTo(fileContent, PsxExeHeader.HeaderSize + i * 4);
        }

        return fileContent;
    }

    /// <summary>
    /// Builds a SYSTEM.CNF + <paramref name="isoName"/> disc whose BOOT entry points at the executable.
    /// </summary>
    public static byte[] BuildExecIso(byte[] exeBytes, string bootPath, string isoName)
    {
        var systemCnf = Encoding.ASCII.GetBytes(
            $"BOOT = {bootPath}\r\nTCB = 4\r\nEVENT = 10\r\nSTACK = 801FFFF0\r\n");
        return BuildIso(
            [
                ("SYSTEM.CNF;1", systemCnf),
                (isoName, exeBytes),
            ]);
    }

    public static byte[] BuildExecIso(byte[] exeBytes, string isoName = "SLPS_TEST.EXE;1") =>
        BuildExecIso(exeBytes, $"cdrom:\\{isoName}", isoName);

    public static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static byte[] BuildIso(List<(string Name, byte[] Content)> files)
    {
        var locations = new uint[files.Count];
        var cursor = FirstFileSector;
        for (var i = 0; i < files.Count; i++)
        {
            locations[i] = (uint)cursor;
            var sectors = Math.Max(1, (files[i].Content.Length + SectorSize - 1) / SectorSize);
            cursor += sectors;
        }

        var image = new byte[cursor * SectorSize];

        WritePrimaryVolumeDescriptor(image, (uint)cursor);
        WriteRootDirectory(image, locations, files);

        for (var i = 0; i < files.Count; i++)
        {
            Buffer.BlockCopy(files[i].Content, 0, image, (int)locations[i] * SectorSize, files[i].Content.Length);
        }

        return image;
    }

    private static void WritePrimaryVolumeDescriptor(byte[] image, uint volumeSpaceSize)
    {
        const int pvdSector = 16;
        const int rootDirectorySector = 18;
        const int rootRecordOffset = 156;

        var offset = pvdSector * SectorSize;

        image[offset] = 1; // primary volume descriptor
        Encoding.ASCII.GetBytes("CD001").CopyTo(image, offset + 1);
        image[offset + 6] = 1; // version
        Encoding.ASCII.GetBytes("PSXRECOMP_TEST".PadRight(32)[..32]).CopyTo(image, offset + 40);
        BitConverter.GetBytes(volumeSpaceSize).CopyTo(image, offset + 80);

        WriteDirectoryRecord(image, offset + rootRecordOffset,
            (uint)rootDirectorySector, SectorSize, flags: 0x02, name: "\0");
    }

    private static void WriteRootDirectory(byte[] image, uint[] locations, List<(string Name, byte[] Content)> files)
    {
        const int rootDirectorySector = 18;

        var offset = rootDirectorySector * SectorSize;
        offset += WriteDirectoryRecord(image, offset, rootDirectorySector, SectorSize, flags: 0x02, name: "\0");

        for (var i = 0; i < files.Count; i++)
        {
            offset += WriteDirectoryRecord(image, offset, locations[i],
                (uint)files[i].Content.Length, flags: 0x00, name: files[i].Name);
        }
    }

    private static int WriteDirectoryRecord(byte[] buffer, int offset, uint location, uint size, byte flags, string name)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var recordLength = 33 + nameBytes.Length;
        if (recordLength % 2 != 0)
        {
            recordLength++;
        }

        buffer[offset] = (byte)recordLength;
        buffer[offset + 1] = 0; // extended attribute length
        BitConverter.GetBytes(location).CopyTo(buffer, offset + 2);
        BitConverter.GetBytes(size).CopyTo(buffer, offset + 10);
        buffer[offset + 25] = flags;
        buffer[offset + 32] = (byte)nameBytes.Length;
        nameBytes.CopyTo(buffer, offset + 33);

        return recordLength;
    }
}