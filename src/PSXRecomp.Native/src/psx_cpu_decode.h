#pragma once

#include <cstdint>

/*
 * PSXCpu instruction decode / classification. Implemented in Rust
 * (`../rust/src/cpu_decode.rs`, Issue #525); this header only declares that
 * crate's internal C ABI for use by psx_cpu_decode.cpp.
 *
 * psx_cpu_decode takes the instruction word and returns the handler to
 * dispatch to plus every operand field, by value; Rust never allocates or
 * retains state, and the function is infallible and cannot panic. The caller
 * keeps owning dispatch, exception raising and all CPU state.
 *
 * Must match `DecodeOp` / `DecodedInstruction` in cpu_decode.rs
 * value-for-value and field-for-field.
 */
enum class PSXDecodeOp : uint32_t {
    Reserved = 0,    // raise RI (0x0A)
    CopUnusable = 1, // raise CpU (0x0B) with CAUSE.CE = cop
    Sll = 2, Srl = 3, Sra = 4, Sllv = 5, Srlv = 6, Srav = 7,
    Jr = 8, Jalr = 9, Syscall = 10, Break = 11,
    Mfhi = 12, Mthi = 13, Mflo = 14, Mtlo = 15,
    Mult = 16, Multu = 17, Div = 18, Divu = 19,
    Add = 20, Addu = 21, Sub = 22, Subu = 23,
    And = 24, Or = 25, Xor = 26, Nor = 27, Slt = 28, Sltu = 29,
    Bltz = 30, Bgez = 31, Bltzal = 32, Bgezal = 33,
    J = 34, Jal = 35, Beq = 36, Bne = 37, Blez = 38, Bgtz = 39,
    Addi = 40, Addiu = 41, Slti = 42, Sltiu = 43,
    Andi = 44, Ori = 45, Xori = 46, Lui = 47,
    Lb = 48, Lh = 49, Lwl = 50, Lw = 51, Lbu = 52, Lhu = 53, Lwr = 54,
    Sb = 55, Sh = 56, Swl = 57, Sw = 58, Swr = 59,
    Mfc0 = 60, Mtc0 = 61, Rfe = 62,
};

struct PSXDecodedInstruction {
    PSXDecodeOp op;
    uint32_t rs;     // bits 21-25
    uint32_t rt;     // bits 16-20
    uint32_t rd;     // bits 11-15
    uint32_t shamt;  // bits 6-10
    uint32_t imm;    // bits 0-15, zero-extended
    uint32_t target; // bits 0-25
    uint32_t cop;    // bits 26-27 (CAUSE.CE for CopUnusable)
};
static_assert(sizeof(PSXDecodedInstruction) == 32, "must match cpu_decode.rs");

extern "C" {
PSXDecodedInstruction psx_cpu_decode(uint32_t instruction);
}
