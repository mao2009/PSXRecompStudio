using FluentAssertions;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;
using PSXRecomp.Tests.RealRomAnalysis;
using Xunit;

namespace PSXRecomp.Tests.Recompiler;

// Issue #773: indirect-call root discovery. The tests pin the evidence rules (a reachable lui/addiu names a table, the table
// words are candidates, a candidate needs a function-entry shape and a non-reserved first instruction), the chain from a table
// caller to its callee (E-2), the guard against a data base becoming a root (E-3), determinism and the caps (E-8), and the
// opt-in manifest directive (E-7). Synthetic images only; the real-ROM check is recorded separately.
[Test]
public sealed class IndirectRootDiscoveryTests
{
    private const uint Base = 0x80080000u;
    private const byte V0 = 2, Sp = 29, Ra = 31;
    private const uint Reserved = 0xFC000000u; // primary opcode 0x3F: not an R3000A instruction
    private static readonly IReadOnlySet<uint> NoExclusions = new HashSet<uint>();

    private static uint Prologue => MipsEncoding.I(0x09, Sp, Sp, 0xFFF0); // addiu $sp,$sp,-16
    private static uint Return => MipsEncoding.JumpRegister(Ra);

    private static uint[] Image(uint bytes) => new uint[bytes / 4];

    private static void Place(uint[] image, uint address, params uint[] words) =>
        words.CopyTo(image, (int)((address - Base) / 4));

    /// <summary>Entry code: one <c>lui</c>/<c>addiu</c> pair per table base, then a return.</summary>
    private static uint[] Entry(params uint[] tables)
    {
        var code = new List<uint>();
        foreach (var table in tables)
        {
            code.Add(MipsEncoding.I(0x0F, V0, 0, (ushort)(table >> 16)));
            code.Add(MipsEncoding.I(0x09, V0, V0, (ushort)table));
        }

        code.Add(Return);
        code.Add(MipsEncoding.Nop);
        return code.ToArray();
    }

    /// <summary>
    /// The chain of E-2: entry → table at <c>+0x100</c> → caller A (prologue, JAL to target, return) and caller B (prologue,
    /// return). The table ends at a zero word, which is not an image address.
    /// </summary>
    private static (uint[] Image, uint Table, uint CallerA, uint CallerB, uint Target) Layout()
    {
        var image = Image(0x800);
        var table = Base + 0x100u;
        var callerA = Base + 0x200u;
        var callerB = Base + 0x220u;
        var target = Base + 0x300u;
        Place(image, Base, Entry(table));
        Place(image, table, callerA, callerB, 0);
        Place(image, callerA, Prologue, MipsEncoding.JumpAndLink(target), MipsEncoding.Nop, Return, MipsEncoding.Nop);
        Place(image, callerB, Prologue, Return, MipsEncoding.Nop);
        Place(image, target, Prologue, Return, MipsEncoding.Nop);
        return (image, table, callerA, callerB, target);
    }

    private static byte[] ToBytes(uint[] words) => words.SelectMany(BitConverter.GetBytes).ToArray();

    [Fact]
    public void TableAtEndOfAddressSpace_DoesNotWrapTheNextWord()
    {
        const uint load = 0xFFFFFF00u;
        const uint table = 0xFFFFFFFCu;
        const uint callee = 0xFFFFFF40u;
        var words = new uint[64];
        words[0] = MipsEncoding.I(0x0F, V0, 0, 0xFFFF);
        words[1] = MipsEncoding.I(0x0D, V0, V0, 0xFFFC);
        words[2] = Return;
        words[3] = MipsEncoding.Nop;
        words[(int)((callee - load) / 4)] = Prologue;
        words[(int)((callee - load) / 4) + 1] = Return;
        words[(int)((callee - load) / 4) + 2] = MipsEncoding.Nop;
        words[^1] = callee;

        var roots = IndirectRootDiscovery.Find(load, words, [load]);

        roots.Should().ContainSingle().Which.TargetPc.Should().Be(callee);
    }

    [Fact]
    public void TableEntriesNamedByReachableCode_AreFound_WhenDirectDiscoveryMissesThem()
    {
        var (image, table, callerA, callerB, _) = Layout();

        var roots = IndirectRootDiscovery.Find(Base, image, [Base]);

        roots.Should().Equal(
            new IndirectRoot(callerA, table, Base),
            new IndirectRoot(callerB, table + 4, Base));
    }

    [Fact]
    public void CalleeOfATableCaller_IsReachedThroughTheRoot_AndGetsItsOwnBlock()
    {
        var (image, _, callerA, callerB, target) = Layout();

        var roots = IndirectRootDiscovery.Find(Base, image, [Base]).Select(r => r.TargetPc).ToArray();

        // The callee is a direct JAL target of an accepted root, so it is reached and never added as a root of its own.
        roots.Should().Equal(callerA, callerB);
        ReachableProgramBuilder.ReachablePcs(Base, image, [Base]).Should().NotContain(callerA).And.NotContain(target);
        ReachableProgramBuilder.ReachablePcs(Base, image, roots.Append(Base)).Should().Contain(target);

        var direct = ReachableProgramBuilder.BuildLoadedImage(Base, image, [Base], NoExclusions);
        var directEntries = direct.Blocks.Select(b => b.Block.EntryPc).ToArray();
        directEntries.Should().NotContain(callerA).And.NotContain(target);

        var indirect = ReachableProgramBuilder.BuildLoadedImage(Base, image, roots.Append(Base), NoExclusions);
        var indirectEntries = indirect.Blocks.Select(b => b.Block.EntryPc).ToArray();
        indirectEntries.Should().Contain(callerA).And.Contain(target);
    }

    [Fact]
    public void ADataBase_IsNeverItsOwnRoot_AndANonPointerTableRegistersNothing()
    {
        // E-3: the base of a materialized data array looks like a function entry, but it is only ever read as a table.
        // Its words are instructions, not in-image addresses, so the table ends at once and the base is not a root.
        var image = Image(0x800);
        var data = Base + 0x400u;
        Place(image, Base, Entry(data));
        Place(image, data, Prologue, Return, MipsEncoding.Nop, Prologue);

        IndirectRootDiscovery.Find(Base, image, [Base]).Should().BeEmpty();
    }

    [Fact]
    public void AReservedWordAtACandidate_IsRejected_AndFallsBackToTheInterpreter()
    {
        // A table names an entry whose first word is undecodable; the entry looks like a return target, so only the
        // reserved-encoding rule keeps it out.
        var image = Image(0x800);
        var table = Base + 0x100u;
        var candidate = Base + 0x200u;
        Place(image, Base, Entry(table));
        Place(image, table, candidate, 0);
        Place(image, candidate - 8, Return, MipsEncoding.Nop, Reserved);

        IndirectRootDiscovery.Find(Base, image, [Base]).Should().BeEmpty();
    }

    [Fact]
    public void TheResult_IsStableAcrossRuns_AndDuplicatesAndCyclesAreBounded()
    {
        // The two callers call each other and the table lists caller A twice.
        var image = Image(0x800);
        var table = Base + 0x100u;
        var callerA = Base + 0x200u;
        var callerB = Base + 0x220u;
        Place(image, Base, Entry(table));
        Place(image, table, callerA, callerA, callerB, 0);
        Place(image, callerA, Prologue, MipsEncoding.JumpAndLink(callerB), MipsEncoding.Nop, Return, MipsEncoding.Nop);
        Place(image, callerB, Prologue, MipsEncoding.JumpAndLink(callerA), MipsEncoding.Nop, Return, MipsEncoding.Nop);

        var first = IndirectRootDiscovery.Find(Base, image, [Base]);
        var second = IndirectRootDiscovery.Find(Base, image, [Base]);

        first.Select(r => r.TargetPc).Should().Equal(callerA, callerB);
        second.Should().Equal(first);
    }

    [Fact]
    public void TheRootCount_IsCapped_ForAnArbitrarilyLargeTableSet()
    {
        // Ten tables of 32 callers are 320 candidates: more than MaxRoots, so the cap must stop the discovery.
        const int tables = 10, entriesPerTable = 32;
        var image = Image(0x2000);
        var tableBases = Enumerable.Range(0, tables).Select(k => Base + 0x100u + (uint)k * 0x80u).ToArray();
        Place(image, Base, Entry(tableBases));
        for (var k = 0; k < tables; k++)
        {
            for (var j = 0; j < entriesPerTable; j++)
            {
                var caller = Base + 0x800u + (uint)(k * entriesPerTable + j) * 0x10u;
                Place(image, tableBases[k] + (uint)j * 4u, caller);
                Place(image, caller, Prologue, Return, MipsEncoding.Nop, MipsEncoding.Nop);
            }
        }

        var roots = IndirectRootDiscovery.Find(Base, image, [Base]);

        roots.Should().HaveCount(IndirectRootDiscovery.MaxRoots);
        roots.Select(r => r.TargetPc).Should().BeInAscendingOrder();
    }

    [Fact]
    public void TheIndirectRootsDirective_IsOptIn_AndWithoutItTheRootsAreUnchanged()
    {
        // E-7: without the directive a PS-X EXE keeps exactly its entry point; with it, the table callers join the roots.
        var (image, _, callerA, callerB, _) = Layout();
        using var dir = new TempDirectory();
        dir.WriteFile("prog.exe", SyntheticPsxExeBuilder.Build(Base, Base, (uint)image.Length * 4, ToBytes(image)));
        var firmware = OpenBiosFirmware.FromBytes(new byte[OpenBiosFirmware.ImageSize]);

        var plain = LoadImageManifest.Read(
            dir.WriteFile("plain.txt", System.Text.Encoding.ASCII.GetBytes("exe prog prog.exe\n")), firmware, disc: null);
        var opted = LoadImageManifest.Read(
            dir.WriteFile("opted.txt", System.Text.Encoding.ASCII.GetBytes("exe prog prog.exe\nindirect-roots\n")), firmware, disc: null);

        plain.Images.Single().Roots.Should().Equal(Base);
        opted.Images.Single().Roots.Should().Equal(Base, callerA, callerB);
    }
}
