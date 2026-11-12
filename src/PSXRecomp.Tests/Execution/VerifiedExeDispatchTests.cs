using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Execution;

/// <summary>
/// Synthetic-only coverage for the <see cref="VerifiedExeDispatch"/> scaffolding
/// (Issue #734): no production call sites exist yet, so nothing here proves real
/// Persona/interpreter parity. Wiring into the engine is follow-up work.
/// </summary>
[Test]
public sealed class VerifiedExeDispatchTests
{
    private const uint Load = 0x80010000u;
    private static uint Nop => 0x00000000u;

    private static (LoadedCodeTable Table, uint[] Ram) Build(params uint[] words)
    {
        var image = ReachableProgramBuilder.BuildLoadedImage(Load, words, [Load], new HashSet<uint>());
        var table = new LoadedCodeTable([image]);
        var ram = (uint[])words.Clone();
        return (table, ram);
    }

    private static Func<uint, uint> Reader(uint[] ram) => addr => ram[(addr - Load) / 4];

    [Fact]
    public void Dispatch_HitsVerifiedNative_WhenRamEqualsAot()
    {
        var words = new uint[] { Nop, Nop };
        var (table, ram) = Build(words);
        var dispatch = new VerifiedExeDispatch(table, Load, Load, "exe-sha", words);
        var hit = dispatch.TryDispatch(Load, Reader(ram));
        Assert.NotNull(hit);
        Assert.Equal(1ul, dispatch.NativeHits);
        Assert.Equal(0ul, dispatch.Fallbacks);
    }

    [Fact]
    public void Dispatch_ForbidsPcOnlyTransition_WhenBytesDiffer()
    {
        var words = new uint[] { Nop, Nop };
        var (table, ram) = Build(words);
        ram[0] = 0xDEADBEEFu; // address reuse with different bytes
        var dispatch = new VerifiedExeDispatch(table, Load, Load, "exe-sha", words);
        Assert.Null(dispatch.TryDispatch(Load, Reader(ram)));
        Assert.Equal(1ul, dispatch.Fallbacks);
        Assert.Equal(0ul, dispatch.NativeHits);
    }

    [Fact]
    public void Dispatch_RejectsPartialSpanMatch_BeyondFirstBlock()
    {
        // F1 regression: a single fused block match must not prove the full span.
        var words = new uint[] { Nop, Nop };
        var (table, ram) = Build(words);
        ram[1] = 0xDEADBEEFu; // second word of the span differs
        var dispatch = new VerifiedExeDispatch(table, Load, Load, "exe-sha", words);
        Assert.Null(dispatch.TryDispatch(Load, Reader(ram)));
        Assert.Equal(0ul, dispatch.NativeHits);
    }

    [Fact]
    public void Dispatch_ReaderFailure_FallsClosed()
    {
        var words = new uint[] { Nop, Nop };
        var (table, _) = Build(words);
        var dispatch = new VerifiedExeDispatch(table, Load, Load, "exe-sha", words);
        Assert.Null(dispatch.TryDispatch(Load, _ => throw new InvalidOperationException("no RAM")));
        Assert.Equal(1ul, dispatch.Fallbacks);
        Assert.Equal(0ul, dispatch.NativeHits);
    }

    [Fact]
    public void Dispatch_FallsBack_OutsideLoadRange()
    {
        var words = new uint[] { Nop, Nop };
        var (table, ram) = Build(words);
        var dispatch = new VerifiedExeDispatch(table, Load, Load, "exe-sha", words);
        Assert.Null(dispatch.TryDispatch(Load + 0x1000u, Reader(ram)));
        Assert.Null(dispatch.TryDispatch(Load + 1u, Reader(ram))); // unaligned
    }

    [Fact]
    public void RamWrite_InvalidatesMapping_UntilReverified()
    {
        var words = new uint[] { Nop, Nop };
        var (table, ram) = Build(words);
        var dispatch = new VerifiedExeDispatch(table, Load, Load, "exe-sha", words);
        Assert.NotNull(dispatch.TryDispatch(Load, Reader(ram)));
        FallbackReturnCheck.TryRamOffset(Load, out var phys);
        dispatch.NotifyRamWrite(phys, 4); // store into verified span
        Assert.Equal(1ul, dispatch.Invalidations);
        Assert.Null(dispatch.TryDispatch(Load, Reader(ram))); // stale mapping never dispatches
        Assert.NotNull(dispatch.Reverify(Load, Reader(ram))); // RAM still equals AOT: re-verified
        Assert.NotNull(dispatch.TryDispatch(Load, Reader(ram)));
    }

    [Fact]
    public void RamWrite_WraparoundEnd_StillInvalidates()
    {
        var words = new uint[] { Nop, Nop };
        var (table, _) = Build(words);
        var dispatch = new VerifiedExeDispatch(table, Load, Load, "exe-sha", words);
        FallbackReturnCheck.TryRamOffset(Load, out var phys);
        dispatch.NotifyRamWrite(phys, uint.MaxValue); // saturating end covers the span
        Assert.Equal(1ul, dispatch.Invalidations);
        Assert.Equal(0ul, dispatch.NativeHits);
    }

    [Fact]
    public void Ctor_RejectsMisalignedLoadEntryWrapUnmappedAndOversized()
    {
        var words = new uint[] { Nop, Nop };
        var (table, _) = Build(words);
        Assert.Throws<ArgumentOutOfRangeException>(() => new VerifiedExeDispatch(table, Load + 1u, Load + 1u, "exe-sha", words));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VerifiedExeDispatch(table, Load, Load + 1u, "exe-sha", words));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VerifiedExeDispatch(table, Load, Load + 0x1000u, "exe-sha", words));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VerifiedExeDispatch(table, 0xFFFFFFFCu, 0xFFFFFFFCu, "exe-sha", words));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VerifiedExeDispatch(table, 0xBFC00000u, 0xBFC00000u, "exe-sha", words));
        // Span longer than real RAM from its offset fails closed.
        var huge = new uint[(0x200000 / 4) + 1];
        Assert.Throws<ArgumentOutOfRangeException>(() => new VerifiedExeDispatch(table, Load, Load, "exe-sha", huge));
        // Upper RAM boundary still constructs.
        FallbackReturnCheck.TryRamOffset(Load, out var phys);
        var fitCount = (int)((0x200000u - phys) / 4u);
        var fit = new VerifiedExeDispatch(table, Load, Load, "exe-sha", new uint[fitCount]);
        Assert.Equal(fitCount, fit.WordCount);
    }

    [Fact]
    public void Reverify_CountsSingleAttempt()
    {
        var words = new uint[] { Nop, Nop };
        var (table, ram) = Build(words);
        var dispatch = new VerifiedExeDispatch(table, Load, Load, "exe-sha", words);
        Assert.NotNull(dispatch.Reverify(Load, Reader(ram)));
        Assert.Equal(1ul, dispatch.DispatchAttempts);
        Assert.Equal(1ul, dispatch.NativeHits);
        Assert.Equal(0ul, dispatch.Fallbacks);
    }

    [Fact]
    public void Transitions_CountBothDirections()
    {
        var words = new uint[] { Nop, Nop };
        var (table, _) = Build(words);
        var dispatch = new VerifiedExeDispatch(table, Load, Load, "exe-sha", words);
        dispatch.NoteNativeToInterpreter();
        dispatch.NoteNativeToInterpreter();
        dispatch.NoteInterpreterToNative();
        Assert.Equal(2ul, dispatch.NativeToInterpreter);
        Assert.Equal(1ul, dispatch.InterpreterToNative);
    }

    [Fact]
    public void SyntheticOnly_StateShape_MatchesFixture()
    {
        // Synthetic-only: checks the fixture's state shape, not real engine parity.
        var words = new uint[] { Nop };
        var (table, _) = Build(words);
        var ram = (uint[])words.Clone();
        var dispatch = new VerifiedExeDispatch(table, Load, Load, "exe-sha", words);
        var hit = dispatch.TryDispatch(Load, Reader(ram));
        Assert.NotNull(hit);
        Assert.Equal(hit!.Words, ram.AsEnumerable()); // RAM identity == AOT words
        var cpu = new FallbackCpuState(new uint[32], 0, 0, Load, 0, 0, 0);
        Assert.Equal(Load, cpu.Pc);
        Assert.Equal(32, cpu.Gpr.Count);
        // No IRQ modelled in this synthetic slice: Cause/Epc stay zero.
        Assert.Equal(0u, cpu.Cause);
    }
}
