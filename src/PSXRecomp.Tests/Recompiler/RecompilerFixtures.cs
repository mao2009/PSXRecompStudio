using PSXRecomp.Core.Recompiler;

namespace PSXRecomp.Tests.Recompiler;

[Test]
// Common synthetic fixtures shared by the #211 differential harness and the
// #209 vertical-slice tests, so both sides of the comparison consume the same
// input (Issue #211 "common test fixture").
internal static class RecompilerFixtures
{
    private const uint EntryPc = 0x80000000u;

    // ADDIU $t0, $zero, 5   ($t0 = GPR8  = 5)
    // ADDIU $t1, $zero, 7   ($t1 = GPR9  = 7)
    // ADDU  $t3, $t0, $t1   ($t3 = GPR11 = 12)
    public static readonly uint[] AddThreeWords =
    {
        0x24080005u,
        0x24090007u,
        0x01095821u,
    };

    public static RecompilerDifferentialFixture AddThree() =>
        new("add-three", AddThreeWords, EntryPc, stepBudget: 3);

    // The Issue #209 example: ADDIU t0,zero,1; ADDIU t1,zero,2; ADDU t2,t0,t1 → t2=3.
    public static readonly uint[] Issue209Words =
    {
        0x24080001u,
        0x24090002u,
        0x01095021u,
    };

    public static RecompilerDifferentialFixture Issue209Add() =>
        new("issue-209-add", Issue209Words, EntryPc, stepBudget: 3);
}
