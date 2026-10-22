using PSXRecomp.Architecture;

namespace PSXRecomp.Infrastructure.Standalone;

/// <summary>
/// A finite synthetic PS-X program for the live frame path (Issue #630). It fills the screen with one color at a time through
/// GP0, the way a game would, and waits about two VBlank intervals between fills, so each color reaches the display as its own
/// frame. It is a verification program only: it is not a title and contains no title data.
/// </summary>
[Infrastructure]
public static class SyntheticLiveProgram
{
    /// <summary>The program's entry PC, in KSEG0 RAM.</summary>
    public const uint EntryPc = 0x80001000u;

    /// <summary>The fills in order, as GP0 colors (0x00BBGGRR): red, green, blue.</summary>
    public static IReadOnlyList<uint> FillColors { get; } = [0x000000FFu, 0x0000FF00u, 0x00FF0000u];

    /// <summary>The loop count of each wait: about 1.2M guest cycles, roughly two VBlank intervals.</summary>
    private const uint WaitIterations = 400_000;

    /// <summary>The instruction words of the program, placed at <see cref="EntryPc"/>. The program ends in a self-branch, so the
    /// run ends only when its segment budget does.</summary>
    public static uint[] Words()
    {
        var words = new List<uint>();
        uint Here() => EntryPc + (uint)words.Count * 4u;
        void Emit(params uint[] emitted) => words.AddRange(emitted);
        void Li(uint register, uint value)
        {
            Emit(I(0x0F, 0, register, value >> 16), I(0x0D, register, register, value & 0xFFFFu));
        }

        const uint T0 = 8, T1 = 9, T9 = 25;
        const uint Gp0Offset = 0x1810u;
        Li(T9, 0x1F800000u); // the I/O base: GP0 is at 0x1F801810

        foreach (var color in FillColors)
        {
            Li(T0, 0x02000000u | color); // GP0 quick fill, then its position and size
            Emit(I(0x2B, T9, T0, Gp0Offset));
            Li(T0, 0u);
            Emit(I(0x2B, T9, T0, Gp0Offset));
            Li(T0, (240u << 16) | 256u);
            Emit(I(0x2B, T9, T0, Gp0Offset));

            Li(T1, WaitIterations);
            var loop = Here();
            Emit(I(0x09, T1, T1, 0xFFFFu)); // addiu $t1, $t1, -1
            Emit(Branch(0x05, T1, 0, Here(), loop)); // bne $t1, $zero, loop
            Emit(0u); // delay slot
        }

        var self = Here();
        Emit(Branch(0x04, 0, 0, self, self)); // beq $zero, $zero, self
        Emit(0u);
        return [.. words];
    }

    private static uint I(uint opcode, uint rs, uint rt, uint immediate) =>
        (opcode << 26) | (rs << 21) | (rt << 16) | (immediate & 0xFFFFu);

    private static uint Branch(uint opcode, uint rs, uint rt, uint pc, uint target)
    {
        var wordOffset = (int)unchecked(target - (pc + 4u)) >> 2;
        return I(opcode, rs, rt, unchecked((uint)(ushort)(short)wordOffset));
    }
}
