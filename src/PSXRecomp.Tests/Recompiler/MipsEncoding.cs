using PSXRecomp.Core.Cpu;

namespace PSXRecomp.Tests.Recompiler;

/// <summary>
/// Encodes R3000A instruction words for the lowering tests, so a test states the
/// instruction it means rather than a hexadecimal literal. Field placement
/// follows <c>docs/cpu/instruction-format.md</c>.
/// </summary>
[Test]
internal static class MipsEncoding
{
    public const uint Nop = 0x00000000u;

    public static uint R(byte funct, byte rd, byte rs, byte rt, byte shamt) =>
        ((uint)rs << 21) | ((uint)rt << 16) | ((uint)rd << 11) | ((uint)shamt << 6) | funct;

    public static uint I(byte opcode, byte rt, byte rs, ushort immediate) =>
        ((uint)opcode << 26) | ((uint)rs << 21) | ((uint)rt << 16) | immediate;

    public static uint J(byte opcode, uint target) =>
        ((uint)opcode << 26) | ((target & 0x0FFFFFFCu) >> 2);

    /// <summary>Encodes a base+offset load or store (LB/LBU/LH/LHU/LW/LWL, SB/SH/SW).</summary>
    public static uint Load(R3000aOpcode opcode, byte rt, byte baseRegister, ushort offset) =>
        I(MemoryOpcodeField(opcode), rt, baseRegister, offset);

    /// <summary>Encodes BEQ / BNE with a target address, converting it to the encoded word offset.</summary>
    public static uint Branch(byte opcode, byte rs, byte rt, uint pc, uint target)
    {
        var wordOffset = (int)(unchecked(target - (pc + 4u))) >> 2;
        return I(opcode, rt, rs, unchecked((ushort)(short)wordOffset));
    }

    public static uint Jump(uint target) => J(0x02, target);

    public static uint JumpAndLink(uint target) => J(0x03, target);

    public static uint JumpRegister(byte rs) => R(0x08, rd: 0, rs: rs, rt: 0, shamt: 0);

    public static uint JumpAndLinkRegister(byte rd, byte rs) => R(0x09, rd: rd, rs: rs, rt: 0, shamt: 0);

    private static byte MemoryOpcodeField(R3000aOpcode opcode) => opcode switch
    {
        R3000aOpcode.Lb => 0x20,
        R3000aOpcode.Lh => 0x21,
        R3000aOpcode.Lwl => 0x22,
        R3000aOpcode.Lw => 0x23,
        R3000aOpcode.Lbu => 0x24,
        R3000aOpcode.Lhu => 0x25,
        R3000aOpcode.Lwr => 0x26,
        R3000aOpcode.Sb => 0x28,
        R3000aOpcode.Sh => 0x29,
        R3000aOpcode.Sw => 0x2B,
        _ => throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Not a base+offset memory opcode."),
    };
}
