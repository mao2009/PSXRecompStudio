using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

[Test]
// Issue #732: several pre-generated versions of RAM-placed code linked into one guest address space; the version whose
// words guest memory holds is selected, and with none there is no native code at that PC.
public sealed class LoadedCodeTableTests
{
    private const uint Pc = 0x80010000u;

    private static GuardedBlock Version(params uint[] words) =>
        new(new RecompilerIrBlock(Pc, [], new RecompilerIrExit(RecompilerIrTerminationReason.Success, Pc + 4)), words);

    [Fact]
    public void SeveralVersionsAtOneAddress_AreKept_AndAnIdenticalOneOnce()
    {
        var table = new LoadedCodeTable([new GuardedImageProgram([Version(1)], []), new GuardedImageProgram([Version(2), Version(1)], [])]);

        table.VersionsAt(Pc).Select(static v => v.Words[0]).Should().Equal(1u, 2u);
        table.Contains(Pc + 4).Should().BeFalse();
        table.VersionsAt(Pc + 4).Should().BeEmpty();
    }

    [Fact]
    public void Match_SelectsTheVersionMemoryHolds_OrNone()
    {
        var table = new LoadedCodeTable([new GuardedImageProgram([Version(1), Version(2)], [])]);

        table.Match(Pc, _ => 2u)!.Words.Should().Equal(2u);
        table.Match(Pc, _ => 1u)!.Words.Should().Equal(1u);
        table.Match(Pc, _ => 3u).Should().BeNull("code that matches no pre-generated version has no native code");
        table.Match(Pc + 4, _ => 1u).Should().BeNull();
    }
}
