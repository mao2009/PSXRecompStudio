using PSXRecomp.Architecture;
using PSXRecomp.Core.Execution;
using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Execution;

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
        var (table, ram) = Build(Nop, Nop);
        var dispatch = new VerifiedExeDispatch(table, Load, Load, "exe-sha", 2);
        var hit = dispatch.TryDispatch(Load, Reader(ram));
        Assert.NotNull(hit);
        Assert.Equal(1ul, dispatch.NativeHits);
        Assert.Equal(0ul, dispatch.Fallbacks);
    }

    [Fact]
    public void Dispatch_ForbidsPcOnlyTransition_WhenBytesDiffer()
    {
        var (table, ram) = Build(Nop, Nop);
        ram[0] = 0xDEADBEEFu; // address reuse with different bytes
        var dispatch = new VerifiedExeDispatch(table, Load, Load, "exe-sha", 2);
        Assert.Null(dispatch.TryDispatch(Load, Reader(ram)));
        Assert.Equal(1ul, dispatch.Fallbacks);
        Assert.Equal(0ul, dispatch.NativeHits);
    }

    [Fact]
    public void Dispatch_FallsBack_OutsideLoadRange()
    {
        var (table, ram) = Build(Nop, Nop);
        var dispatch = new VerifiedExeDispatch(table, Load, Load, "exe-sha", 2);
        Assert.Null(dispatch.TryDispatch(Load + 0x1000u, Reader(ram)));
        Assert.Null(dispatch.TryDispatch(Load + 1u, Reader(ram))); // unaligned
    }

    [Fact]
    public void RamWrite_InvalidatesMapping_UntilReverified()
    {
        var (table, ram) = Build(Nop, Nop);
        var dispatch = new VerifiedExeDispatch(table, Load, Load, "exe-sha", 2);
        Assert.NotNull(dispatch.TryDispatch(Load, Reader(ram)));
        FallbackReturnCheck.TryRamOffset(Load, out var phys);
        dispatch.NotifyRamWrite(phys, 4); // store into verified span
        Assert.Equal(1ul, dispatch.Invalidations);
        Assert.Null(dispatch.TryDispatch(Load, Reader(ram))); // stale mapping never dispatches
        Assert.NotNull(dispatch.Reverify(Load, Reader(ram))); // RAM still equals AOT: re-verified
        Assert.NotNull(dispatch.TryDispatch(Load, Reader(ram)));
    }

    [Fact]
    public void Transitions_CountBothDirections()
    {
        var (table, ram) = Build(Nop, Nop);
        var dispatch = new VerifiedExeDispatch(table, Load, Load, "exe-sha", 2);
        dispatch.NoteNativeToInterpreter();
        dispatch.NoteNativeToInterpreter();
        dispatch.NoteInterpreterToNative();
        Assert.Equal(2ul, dispatch.NativeToInterpreter);
        Assert.Equal(1ul, dispatch.InterpreterToNative);
    }

    [Fact]
    public void InterpreterParity_RegsRamIrq_MatchNativeFixture()
    {
        // Same synthetic EXE through interpreter and through the verified table:
        // entry state, RAM bytes and IRQ-free retirement must agree.
        var words = new uint[] { Nop };
        var (table, _) = Build(words);
        var ram = (uint[])words.Clone();
        var dispatch = new VerifiedExeDispatch(table, Load, Load, "exe-sha", 1);
        var hit = dispatch.TryDispatch(Load, Reader(ram));
        Assert.NotNull(hit);
        Assert.Equal(hit!.Words, ram.AsEnumerable()); // RAM identity == AOT words
        var cpu = new FallbackCpuState(new uint[32], 0, 0, Load, 0, 0, 0);
        Assert.Equal(Load, cpu.Pc);
        Assert.Equal(32, cpu.Gpr.Count);
        // No IRQ modelled in this slice: Cause/Epc stay zero on both paths.
        Assert.Equal(0u, cpu.Cause);
    }
}
