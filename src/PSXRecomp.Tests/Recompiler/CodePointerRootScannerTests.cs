using System.Text;
using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

// Issue #773: a runtime-loaded PS-X EXE calls through pointer tables and callback registrations. Direct reachability from
// the entry cannot see those callees, so the scanner can add their targets as roots. Each fixture is synthetic: a few words
// with the pointer shape under test, and nothing title-specific.
[Test]
public sealed class CodePointerRootScannerTests
{
    private const uint Dest = 0x80010000u;
    private const byte T0 = 8, Sp = 29, Ra = 31;

    // A callee's first word: addiu $sp,$sp,-16; then jr $ra; nop.
    private static uint[] Callee => [MipsEncoding.I(0x09, Sp, Sp, 0xFFF0), MipsEncoding.JumpRegister(Ra), MipsEncoding.Nop];

    [Fact]
    public void DataWordPointer_ToAnUnreachedFunction_IsARootWithItsSourceWord()
    {
        // Entry: jr ra; nop. Word 2 is a pointer table entry naming the callee at word 4.
        var words = new uint[] { MipsEncoding.JumpRegister(Ra), MipsEncoding.Nop, Dest + 16, MipsEncoding.Nop }
            .Concat(Callee).ToArray();

        var roots = CodePointerRootScanner.Find(Dest, words, [Dest]);

        roots.Should().ContainSingle()
            .Which.Should().Be(new CodePointerRoot(Dest + 16, Dest + 8, CodePointerRootKind.DataWord));
    }

    [Theory]
    [InlineData(0x0D)] // ori: zero-extended low half
    [InlineData(0x09)] // addiu: sign-extended low half, here positive
    public void LuiPair_MaterializingAnUnreachedFunction_IsARootWithTheLuiAsSource(int lowOpcode)
    {
        // Entry: jr ra; nop. Words 2-3 materialize the callee at word 5 (Dest+0x14) into $t0.
        var words = new uint[]
        {
            MipsEncoding.JumpRegister(Ra), MipsEncoding.Nop,
            MipsEncoding.I(0x0F, T0, 0, 0x8001), MipsEncoding.I((byte)lowOpcode, T0, T0, 0x14),
            MipsEncoding.Nop,
        }.Concat(Callee).ToArray();

        var roots = CodePointerRootScanner.Find(Dest, words, [Dest]);

        roots.Should().ContainSingle()
            .Which.Should().Be(new CodePointerRoot(Dest + 0x14, Dest + 8, CodePointerRootKind.LuiAddiuPair));
    }

    [Fact]
    public void TargetAlreadyReachableFromTheEntry_IsNotReported()
    {
        // The pointer names the entry itself, which direct reachability already covers.
        var words = new uint[] { MipsEncoding.JumpRegister(Ra), MipsEncoding.Nop, Dest };

        CodePointerRootScanner.Find(Dest, words, [Dest]).Should().BeEmpty();
    }

    [Fact]
    public void AnIntegerWithoutAFunctionShape_IsRejected()
    {
        // Word 2 holds an in-image address, but the word there is a nop and nothing before it is a return:
        // it is data, not an entry, and must not become a root.
        var words = new uint[] { MipsEncoding.JumpRegister(Ra), MipsEncoding.Nop, Dest + 12, MipsEncoding.Nop, MipsEncoding.Nop };

        CodePointerRootScanner.Find(Dest, words, [Dest]).Should().BeEmpty();
    }

    [Fact]
    public void AnUndiscoverableCandidate_IsRejected_WithoutFailingTheScan()
    {
        // The callee's last word is a branch whose delay slot lies outside the image. Its shape looks like a function,
        // but discovery from it alone is a structural error, so it is dropped and the entry still builds.
        var words = new uint[]
        {
            MipsEncoding.JumpRegister(Ra), MipsEncoding.Nop, Dest + 12,
            MipsEncoding.I(0x09, Sp, Sp, 0xFFF0), MipsEncoding.Branch(0x04, 0, 0, Dest + 12, Dest + 16),
        };

        var roots = CodePointerRootScanner.Find(Dest, words, [Dest]);

        roots.Should().BeEmpty();
        ReachableProgramBuilder.BuildLoadedImage(Dest, words, [Dest], new HashSet<uint>()).Blocks.Should().NotBeEmpty();
    }

    [Fact]
    public void DiscoveredRoot_GetsANativeBlock_OnlyWhenTheRootsAreExtended()
    {
        var words = new uint[] { MipsEncoding.JumpRegister(Ra), MipsEncoding.Nop, Dest + 16, MipsEncoding.Nop }
            .Concat(Callee).ToArray();
        var none = new HashSet<uint>();

        var entryOnly = ReachableProgramBuilder.BuildLoadedImage(Dest, words, [Dest], none);
        var extended = ReachableProgramBuilder.BuildLoadedImage(Dest, words, [Dest, Dest + 16], none);

        entryOnly.Blocks.Select(static b => b.Block.EntryPc).Should().NotContain(Dest + 16);
        extended.Blocks.Select(static b => b.Block.EntryPc).Should().Contain(Dest + 16);
    }
}

// Issue #773: the opt-in manifest switch. Without it, every EXE image keeps its entry-only roots; with it, the scan adds
// roots and records each one's source word. Both paths are deterministic.
[Test]
public sealed class CodePointerManifestTests
{
    private const uint Dest = 0x80010000u;

    // Entry at Dest; a pointer table at word 2 names the callee at word 4 (see CodePointerRootScannerTests).
    private static readonly uint[] Text =
    [
        MipsEncoding.JumpRegister(31), MipsEncoding.Nop, Dest + 16, MipsEncoding.Nop,
        MipsEncoding.I(0x09, 29, 29, 0xFFF0), MipsEncoding.JumpRegister(31), MipsEncoding.Nop,
    ];

    private static byte[] Exe()
    {
        var bytes = new byte[2048 + Text.Length * 4];
        Encoding.ASCII.GetBytes("PS-X EXE").CopyTo(bytes, 0);
        BitConverter.GetBytes(Dest).CopyTo(bytes, 0x10);
        BitConverter.GetBytes(Dest).CopyTo(bytes, 0x18);
        BitConverter.GetBytes((uint)Text.Length * 4).CopyTo(bytes, 0x1C);
        for (var i = 0; i < Text.Length; i++)
        {
            BitConverter.GetBytes(Text[i]).CopyTo(bytes, 2048 + i * 4);
        }

        return bytes;
    }

    private static LoadImageManifest Read(TempDirectory dir, string manifest)
    {
        dir.WriteFile("test.exe", Exe());
        var path = dir.WriteFile("images.txt", Encoding.ASCII.GetBytes(manifest));
        return LoadImageManifest.Read(path, OpenBiosFirmware.FromBytes(new byte[OpenBiosFirmware.ImageSize]), disc: null);
    }

    [Fact]
    public void WithoutTheSwitch_ExeImageKeepsEntryOnlyRoots()
    {
        using var dir = new TempDirectory();
        var image = Read(dir, "exe test test.exe\n").Images.Single();

        image.Roots.Should().Equal(Dest);
        image.DiscoveredRoots.Should().BeEmpty();
    }

    [Theory]
    [InlineData("scan-code-pointers\nexe test test.exe\n")]
    [InlineData("exe test test.exe\nscan-code-pointers\n")]
    public void WithTheSwitch_AddsTheRoot_WhereverTheSwitchStands(string manifest)
    {
        using var dir = new TempDirectory();
        var image = Read(dir, manifest).Images.Single();

        image.Roots.Should().Equal(Dest, Dest + 16);
        image.DiscoveredRoots.Should().ContainSingle()
            .Which.Should().Be(new CodePointerRoot(Dest + 16, Dest + 8, CodePointerRootKind.DataWord));
    }

    [Fact]
    public void WithTheSwitch_TwoReadsOfTheSameManifest_AreIdentical()
    {
        using var dir = new TempDirectory();
        var first = Read(dir, "scan-code-pointers\nexe test test.exe\n").Images.Single();
        var second = Read(dir, "scan-code-pointers\nexe test test.exe\n").Images.Single();

        second.Roots.Should().Equal(first.Roots);
        second.DiscoveredRoots.Should().Equal(first.DiscoveredRoots);
    }

    [Fact]
    public void TheSwitch_TakesNoArguments()
    {
        using var dir = new TempDirectory();

        var act = () => Read(dir, "scan-code-pointers yes\nexe test test.exe\n");

        act.Should().Throw<InvalidDataException>().WithMessage("*takes no arguments*");
    }

    // Review (#782): the scan's cost and its output are bounded. A scan that would exceed the root cap fails closed, naming the cap,
    // instead of returning a set that looks complete.
    [Fact]
    public void A_Scan_That_Would_Exceed_The_Root_Cap_Fails_Closed_Naming_The_Cap()
    {
        var callees = CodePointerRootScanner.MaxRoots + 44;
        var calleeStart = Dest + (uint)(2 + callees) * 4u;
        // Each callee: addiu $sp,$sp,-16; jr $ra; nop, with the same shape the other fixtures use.
        var callee = new[] { MipsEncoding.I(0x09, 29, 29, 0xFFF0), MipsEncoding.JumpRegister(31), MipsEncoding.Nop };
        var words = new List<uint> { MipsEncoding.JumpRegister(31), MipsEncoding.Nop };
        for (var i = 0; i < callees; i++) words.Add(calleeStart + (uint)i * 12u);
        for (var i = 0; i < callees; i++) words.AddRange(callee);

        var act = () => CodePointerRootScanner.Find(Dest, words.ToArray(), [Dest]);

        act.Should().Throw<InvalidDataException>().WithMessage($"*{CodePointerRootScanner.MaxRoots}*");
    }
}
