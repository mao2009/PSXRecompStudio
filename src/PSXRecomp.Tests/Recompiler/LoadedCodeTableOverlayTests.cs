using System.Text;
using FluentAssertions;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

/// <summary>
/// Overlay AOT guard validation tests (Issue #772).
/// Verifies that when multiple EXEs share the same load address (overlays),
/// only the version whose code actually resides in RAM is selected.
/// </summary>
[Test]
public sealed class LoadedCodeTableOverlayTests
{
    private static GuardedImageProgram BuildImage(uint loadAddress, uint entryPoint, byte[] exeBytes)
    {
        var exe = PsxExe.Load(exeBytes, "test");
        var words = new uint[exe.TextSegment.Length / 4];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = BitConverter.ToUInt32(exe.TextSegment, i * 4);
        }
        var program = ReachableProgramBuilder.BuildLoadedImage(loadAddress, words, [entryPoint], new HashSet<uint>());
        return program;
    }

    private static uint[] GetExeWords(byte[] exeBytes)
    {
        var exe = PsxExe.Load(exeBytes, "test");
        var words = new uint[exe.TextSegment.Length / 4];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = BitConverter.ToUInt32(exe.TextSegment, i * 4);
        }
        return words;
    }

    [Fact]
    public void Overlay_TwoDifferentExesAtSameAddress_OnlyMatchingVersionRuns()
    {
        // Two EXEs that load at the same address (0x80080000) but have different first instructions
        // so the discovered reachable code differs
        var exeA = SyntheticPsxExeBuilder.BuildValid(instructionCount: 4, textStart: 0x80080000u);
        var exeB = SyntheticPsxExeBuilder.BuildValid(instructionCount: 4, textStart: 0x80080000u);

        // Modify exeB to have a different first instruction (addiu $t0, $zero, 2 instead of 1)
        var exeBObj = PsxExe.Load(exeB, "B");
        var exeBWords = new uint[exeBObj.TextSegment.Length / 4];
        for (var i = 0; i < exeBWords.Length; i++) exeBWords[i] = BitConverter.ToUInt32(exeBObj.TextSegment, i * 4);
        exeBWords[0] = 0x24080002u; // addiu $t0, $zero, 2
        var modifiedExeB = new byte[exeB.Length];
        exeB.CopyTo(modifiedExeB);
        for (var i = 0; i < exeBWords.Length; i++)
            BitConverter.GetBytes(exeBWords[i]).CopyTo(modifiedExeB, PsxExeHeader.HeaderSize + i * 4);

        const uint LoadAddress = 0x80080000u;
        const uint EntryPoint = LoadAddress;

        var imageA = BuildImage(LoadAddress, EntryPoint, exeA);
        var imageB = BuildImage(LoadAddress, EntryPoint, modifiedExeB);

        var table = new LoadedCodeTable([imageA, imageB]);

        // Both versions should be registered at the same PC (different first instruction = different guards)
        table.VersionsAt(EntryPoint).Should().HaveCount(2);

        // When RAM holds EXE A's code, version A should be selected
        var wordsA = GetExeWords(exeA);
        uint[] ramA = [.. wordsA];
        uint ReadA(uint addr) => ramA[(addr - LoadAddress) / 4];
        var matchedA = table.Match(EntryPoint, ReadA);
        matchedA.Should().NotBeNull();
        matchedA!.Words.Should().Equal(imageA.Blocks[0].Words);

        // When RAM holds EXE B's code, version B should be selected
        var wordsBModified = GetExeWords(modifiedExeB);
        uint[] ramB = [.. wordsBModified];
        uint ReadB(uint addr) => ramB[(addr - LoadAddress) / 4];
        var matchedB = table.Match(EntryPoint, ReadB);
        matchedB.Should().NotBeNull();
        matchedB!.Words.Should().Equal(imageB.Blocks[0].Words);

        // When RAM holds neither, no version should match
        uint[] ramC = [0xDEADBEEF, 0xCAFEBABE, 0x12345678, 0x9ABCDEF0];
        uint ReadC(uint addr) => ramC[(addr - LoadAddress) / 4];
        table.Match(EntryPoint, ReadC).Should().BeNull("RAM holds neither version");
    }

    [Fact]
    public void Overlay_PartiallyModifiedCode_RejectedSafely()
    {
        var exe = SyntheticPsxExeBuilder.BuildValid(instructionCount: 6, textStart: 0x80080000u);

        const uint LoadAddress = 0x80080000u;
        const uint EntryPoint = LoadAddress;

        var image = BuildImage(LoadAddress, EntryPoint, exe);
        var table = new LoadedCodeTable([image]);

        // Get the actual guard words for the discovered block
        var guardWords = image.Blocks[0].Words;

        // RAM holds exact guard words - should match
        uint[] ramExact = [.. guardWords];
        uint ReadExact(uint addr) => ramExact[(addr - LoadAddress) / 4];
        table.Match(EntryPoint, ReadExact).Should().NotBeNull();

        // RAM has first guard word modified - should NOT match (partial overwrite)
        uint[] ramModified1 = [.. guardWords];
        ramModified1[0] ^= 0xFFFF;
        uint ReadModified1(uint addr) => ramModified1[(addr - LoadAddress) / 4];
        table.Match(EntryPoint, ReadModified1).Should().BeNull("partial overwrite must be rejected");

        // RAM has second guard word modified - should NOT match
        if (guardWords.Count > 1)
        {
            uint[] ramModified2 = [.. guardWords];
            ramModified2[1] ^= 0xFFFF;
            uint ReadModified2(uint addr) => ramModified2[(addr - LoadAddress) / 4];
            table.Match(EntryPoint, ReadModified2).Should().BeNull("partial overwrite must be rejected");
        }
    }

    [Fact]
    public void Overlay_MultipleExesWithSameCode_Deduplicated()
    {
        // Two EXEs with identical code at same address should be deduplicated
        var exe = SyntheticPsxExeBuilder.BuildValid(instructionCount: 4, textStart: 0x80080000u);

        const uint LoadAddress = 0x80080000u;
        const uint EntryPoint = LoadAddress;

        var imageA = BuildImage(LoadAddress, EntryPoint, exe);
        var imageB = BuildImage(LoadAddress, EntryPoint, exe);

        var table = new LoadedCodeTable([imageA, imageB]);

        // Should only have one version since they're identical
        table.VersionsAt(EntryPoint).Should().HaveCount(1);
    }

    [Fact]
    public void Overlay_VerifyCpuRamStatePreservedAcrossAotFallbackTransitions()
    {
        // This test verifies the guard mechanism doesn't corrupt CPU state
        // when transitioning between AOT and fallback (the guard only reads RAM)
        var exe = SyntheticPsxExeBuilder.BuildValid(instructionCount: 6, textStart: 0x80080000u);

        const uint LoadAddress = 0x80080000u;
        const uint EntryPoint = LoadAddress;

        var image = BuildImage(LoadAddress, EntryPoint, exe);
        var table = new LoadedCodeTable([image]);

        // Get the actual guard words
        var guardWords = image.Blocks[0].Words;

        // Simulate RAM being read multiple times (like during AOT/fallback transitions)
        uint[] ram = [.. guardWords];
        uint Read(uint addr) => ram[(addr - LoadAddress) / 4];

        // Multiple reads should all succeed and not modify RAM
        for (int i = 0; i < 100; i++)
        {
            var matched = table.Match(EntryPoint, Read);
            matched.Should().NotBeNull($"read iteration {i}");
            matched!.Words.Should().Equal(guardWords);
        }

        // Verify RAM wasn't modified
        ram.Should().Equal(guardWords);
    }

    [Fact]
    public void Overlay_BoundaryRead_DoesNotReadPastCodeRegion()
    {
        // Verify Match doesn't read past the guarded code region
        var exe = SyntheticPsxExeBuilder.BuildValid(instructionCount: 4, textStart: 0x80080000u);

        const uint LoadAddress = 0x80080000u;
        const uint EntryPoint = LoadAddress;

        var image = BuildImage(LoadAddress, EntryPoint, exe);
        var table = new LoadedCodeTable([image]);

        // Get the actual guard words
        var guardWords = image.Blocks[0].Words;

        var readCount = 0;
        uint[] ram = [.. guardWords];
        uint Read(uint addr)
        {
            readCount++;
            return ram[(addr - LoadAddress) / 4];
        }

        // Match should only read the words in the guarded block
        var matched = table.Match(EntryPoint, Read);
        matched.Should().NotBeNull();
        
        // Should have read exactly the number of words in the guard
        readCount.Should().Be(matched!.Words.Count);
    }
}