using PSXRecomp.Core.Cpu;
using PSXRecomp.Core.DiscImage;
using PSXRecomp.Core.Execution;

namespace PSXRecompStudio.Tests;

// Issue #409: unit tests for the PsxExeTitleInput bridge that converts a parsed
// PS-X EXE into the production execution request used by TitleExecutionService.
[Test]
public class PsxExeTitleInputTests
{
    private const uint TextStart = 0x80010000u;
    private const uint SpInitial = 0x801FFF00u;
    private const uint GpInitial = 0xAAAABBCCu;

    [Fact]
    public void Build_SetsLoadAddressToTextStart()
    {
        var exe = BuildExe(TextStart, TextStart, SpInitial, GpInitial, [0x03E00008u]); // jr $ra

        var result = PsxExeTitleInput.Build(exe, outerBudget: 2, segmentBudget: 16);

        result.LoadAddress.Should().Be(TextStart);
    }

    [Fact]
    public void Build_SetsEntryPcFromHeader()
    {
        var entry = TextStart + 4;
        var exe = BuildExe(TextStart, entry, SpInitial, GpInitial, [0u, 0x03E00008u]); // nop, jr $ra

        var result = PsxExeTitleInput.Build(exe, outerBudget: 2, segmentBudget: 16);

        result.Request.EntryPc.Should().Be(entry);
    }

    [Fact]
    public void Build_SeedsSpAndGpFromHeader()
    {
        var exe = BuildExe(TextStart, TextStart, SpInitial, GpInitial, [0x03E00008u]);

        var result = PsxExeTitleInput.Build(exe, outerBudget: 2, segmentBudget: 16);

        result.Request.InitialGpr[(int)R3000aRegister.Gp].Should().Be(GpInitial);
        result.Request.InitialGpr[(int)R3000aRegister.Sp].Should().Be(SpInitial);
    }

    [Fact]
    public void Build_PinsRegisterZeroToZeroRegardlessOfHeader()
    {
        var exe = BuildExe(TextStart, TextStart, SpInitial, GpInitial, [0x03E00008u]);

        var result = PsxExeTitleInput.Build(exe, outerBudget: 2, segmentBudget: 16);

        result.Request.InitialGpr[0].Should().Be(0u);
    }

    [Fact]
    public void Build_SetsHiLoToZero()
    {
        var exe = BuildExe(TextStart, TextStart, SpInitial, GpInitial, [0x03E00008u]);

        var result = PsxExeTitleInput.Build(exe, outerBudget: 2, segmentBudget: 16);

        result.Request.InitialHi.Should().Be(0u);
        result.Request.InitialLo.Should().Be(0u);
    }

    [Fact]
    public void Build_ConvertsTextSegmentToInstructionWords()
    {
        uint[] words = [0x03E00008u, 0x3C018001u, 0x0C000028u, 0u]; // jr $ra, lui, jal 0xA0, nop
        var exe = BuildExe(TextStart, TextStart, SpInitial, GpInitial, words);

        var result = PsxExeTitleInput.Build(exe, outerBudget: 2, segmentBudget: 16);

        result.InstructionWords.Should().HaveCount(words.Length);
        for (var i = 0; i < words.Length; i++)
        {
            result.InstructionWords[i].Should().Be(words[i], $"word {i} must match");
        }
    }

    [Fact]
    public void Build_ThrowsOnEmptyTextSegment()
    {
        var headerBytes = new byte[PsxExeHeader.HeaderSize];
        WriteMagic(headerBytes);
        WriteU32(headerBytes, 0x10, TextStart);
        WriteU32(headerBytes, 0x18, TextStart);
        // TextSize = 0 and no trailing bytes.
        var exe = PsxExe.Load(headerBytes, "EMPTY.EXE");

        var act = () => PsxExeTitleInput.Build(exe, 2, 16);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("empty");
    }

    [Fact]
    public void Build_ThrowsWhenTextSegmentNotWordAligned()
    {
        // PsxExe.Load caps textLength = min(TextSize, available). If we set
        // TextSize = 1 but have only 2049 total bytes, the loaded text is 1 byte.
        var fileContent = new byte[PsxExeHeader.HeaderSize + 1];
        WriteMagic(fileContent);
        WriteU32(fileContent, 0x18, TextStart);
        WriteU32(fileContent, 0x1C, 1u); // TextSize = 1 byte
        fileContent[PsxExeHeader.HeaderSize] = 0xFF;
        var exe = PsxExe.Load(fileContent, "UNALIGNED.EXE");

        var act = () => PsxExeTitleInput.Build(exe, 2, 16);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("not a whole number");
    }

    [Fact]
    public void Build_ThrowsWhenEntryPointOutsideTextRegion()
    {
        // EntryPoint is beyond the text region.
        var exe = BuildExe(TextStart, TextStart + 8, SpInitial, GpInitial, [0x03E00008u]); // text is 1 word at TextStart..+4; entry at +8.

        var act = () => PsxExeTitleInput.Build(exe, 2, 16);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("outside the text region");
    }

    [Fact]
    public void Build_ThrowsWhenEntryPointBeforeTextStart()
    {
        var exe = BuildExe(TextStart, TextStart - 4, SpInitial, GpInitial, [0x03E00008u]);

        var act = () => PsxExeTitleInput.Build(exe, 2, 16);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("outside the text region");
    }

    [Fact]
    public void Build_ThrowsWhenEntryPointNotAligned()
    {
        var exe = BuildExe(TextStart, TextStart + 2, SpInitial, GpInitial, [0x03E00008u]); // entry is +2 (2 bytes), not word-aligned

        var act = () => PsxExeTitleInput.Build(exe, 2, 16);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("not 4-byte aligned");
    }

    [Fact]
    public void Build_ThrowsWhenTextStartNotTranslatable()
    {
        // KSEG2 (0xC0000000) is not translatable in the simple KUSEG/KSEG0/KSEG1 model.
        const uint kseg2 = 0xC0000000u;
        var exe = BuildExe(kseg2, kseg2, SpInitial, GpInitial, [0x03E00008u]);

        var act = () => PsxExeTitleInput.Build(exe, 2, 16);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("translatable");
    }

    [Fact]
    public void Build_ThrowsWhenTextRangeOverflowsAddressSpace()
    {
        // TextStart must pass the 4-byte alignment check before overflow is evaluated;
        // use an aligned KUSEG start whose span wraps past uint.MaxValue.
        const uint textStart = 0xFFFFFFF8u;
        var fileContent = new byte[PsxExeHeader.HeaderSize + 16];
        WriteMagic(fileContent);
        WriteU32(fileContent, 0x18, textStart);
        WriteU32(fileContent, 0x1C, 16u);
        WriteU32(fileContent, 0x10, textStart);
        for (var i = 0; i < 4; i++)
        {
            WriteU32(fileContent, PsxExeHeader.HeaderSize + i * 4, 0x03E00008u);
        }

        var exe = PsxExe.Load(fileContent, "OVERFLOW.EXE");

        var act = () => PsxExeTitleInput.Build(exe, 2, 16);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("overflows");
    }

    [Fact]
    public void Build_ThrowsWhenTextRangeCrossesNonContiguousTranslationBoundary()
    {
        // The text region [0x7FFFF800..0x80000800) straddles the KUSEG/KSEG0 boundary:
        // the start translates to itself but the last byte falls in KSEG0 and is masked
        // into low physical RAM, so the whole span is not contiguous and must fail closed.
        const uint textStart = 0x7FFFF800u;
        var fileContent = new byte[PsxExeHeader.HeaderSize + 4096];
        WriteMagic(fileContent);
        WriteU32(fileContent, 0x18, textStart);
        WriteU32(fileContent, 0x1C, 4096u);
        WriteU32(fileContent, 0x10, textStart);
        for (var i = 0; i < 4096 / 4; i++)
        {
            WriteU32(fileContent, PsxExeHeader.HeaderSize + i * 4, 0x03E00008u);
        }

        var exe = PsxExe.Load(fileContent, "STRADDLES.EXE");

        var act = () => PsxExeTitleInput.Build(exe, 2, 16);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("contiguous");
    }

    [Fact]
    public void Build_ThrowsWhenTextStartNotWordAligned()
    {
        const uint unaligned = TextStart + 2;
        var fileContent = new byte[PsxExeHeader.HeaderSize + 4];
        WriteMagic(fileContent);
        WriteU32(fileContent, 0x18, unaligned);
        WriteU32(fileContent, 0x1C, 4u);
        WriteU32(fileContent, 0x10, unaligned);
        WriteU32(fileContent, PsxExeHeader.HeaderSize, 0x03E00008u);
        var exe = PsxExe.Load(fileContent, "BAD.EXE");

        var act = () => PsxExeTitleInput.Build(exe, 2, 16);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("not 4-byte aligned");
    }

    [Fact]
    public void Build_PropagatesBudgetsIntoRequest()
    {
        var exe = BuildExe(TextStart, TextStart, SpInitial, GpInitial, [0x03E00008u]);

        var result = PsxExeTitleInput.Build(exe, outerBudget: 42, segmentBudget: 99);

        result.Request.OuterBudget.Should().Be(42);
        result.Request.SegmentBudget.Should().Be(99);
    }

    [Fact]
    public void Build_NullExe_ThrowsArgumentNullException()
    {
        var act = () => PsxExeTitleInput.Build(null!, 2, 16);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Build_EntryExactlyAtTextEnd_Throws()
    {
        // textEnd = TextStart + 1 word = TextStart + 4. EntryPoint = textEnd.
        var exe = BuildExe(TextStart, TextStart + 4, SpInitial, GpInitial, [0x03E00008u]);

        var act = () => PsxExeTitleInput.Build(exe, 2, 16);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("outside the text region");
    }

    [Fact]
    public void Build_ThrowsOnTruncatedTextSegment()
    {
        // Header claims 8 bytes of text but the file carries only 4; PsxExe.Load
        // silently keeps the shorter 4, so Build must reject the mismatch rather
        // than execute a partial image.
        var fileContent = new byte[PsxExeHeader.HeaderSize + 4];
        WriteMagic(fileContent);
        WriteU32(fileContent, 0x10, TextStart);
        WriteU32(fileContent, 0x18, TextStart);
        WriteU32(fileContent, 0x1C, 8u); // declared 8 bytes
        WriteU32(fileContent, PsxExeHeader.HeaderSize, 0x03E00008u);
        var exe = PsxExe.Load(fileContent, "TRUNCATED.EXE");

        var act = () => PsxExeTitleInput.Build(exe, 2, 16);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("truncated");
    }

    [Fact]
    public void Build_TruncationCheckIsSkippedWhenTextSizeIsZero()
    {
        // TextSize == 0 means "use every available byte": PsxExe.Load takes all of
        // them, so Build must accept the image instead of flagging a mismatch.
        var exe = BuildExe(TextStart, TextStart, SpInitial, GpInitial, [0x03E00008u]);
        var truncated = exe with { Header = exe.Header with { TextSize = 0 } };

        var act = () => PsxExeTitleInput.Build(truncated, 2, 16);

        act.Should().NotThrow();
    }

    [Fact]
    public void Build_PropagatesSpOffsetFromHeader()
    {
        // SP base 0x801FFF00 + offset 0x40 → SpInitial = 0x801FFF40.
        var exe = BuildExe(TextStart, TextStart, spInitial: 0x801FFF00u, GpInitial, [0x03E00008u], spOffset: 0x40u);

        var result = PsxExeTitleInput.Build(exe, outerBudget: 2, segmentBudget: 16);

        result.Request.InitialGpr[(int)R3000aRegister.Sp].Should().Be(0x801FFF40u);
    }

    // ---- helpers ----

    private static PsxExe BuildExe(
        uint textStart,
        uint entryPoint,
        uint spInitial,
        uint gpInitial,
        uint[] words,
        uint spOffset = 0u)
    {
        var fileContent = new byte[PsxExeHeader.HeaderSize + words.Length * 4];
        WriteMagic(fileContent);
        WriteU32(fileContent, 0x10, entryPoint);
        WriteU32(fileContent, 0x14, gpInitial);
        WriteU32(fileContent, 0x18, textStart);
        WriteU32(fileContent, 0x1C, (uint)(words.Length * 4));
        WriteU32(fileContent, 0x30, spInitial);
        WriteU32(fileContent, 0x34, spOffset);
        for (var i = 0; i < words.Length; i++)
        {
            WriteU32(fileContent, PsxExeHeader.HeaderSize + i * 4, words[i]);
        }

        return PsxExe.Load(fileContent, "SLUS_00000.00");
    }

    private static void WriteMagic(byte[] buffer) =>
        Buffer.BlockCopy(BitConverter.GetBytes(PsxExeHeader.Magic), 0, buffer, 0, 8);

    private static void WriteU32(byte[] buffer, int offset, uint value) =>
        BitConverter.GetBytes(value).CopyTo(buffer, offset);
}
