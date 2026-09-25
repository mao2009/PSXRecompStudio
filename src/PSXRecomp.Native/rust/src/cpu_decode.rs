//! `PSXCpu` instruction decode / classification, migrated from the C++
//! `PSXCpu::ExecuteInstruction` opcode/funct/REGIMM/COP0 switch (Issue #525).
//!
//! Scope: only classifying a 32-bit instruction word into the `Exec*` handler
//! that runs it (or [`DecodeOp::Reserved`] / [`DecodeOp::CopUnusable`]) and
//! extracting its operand fields. The caller (`src/psx_cpu_decode.cpp`) keeps
//! owning the dispatch to `PSXCpu::Exec*`, raising RI (Excode 0x0A) and CpU
//! (Excode 0x0B, `CAUSE.CE = cop`), the 16-bit immediate's sign/zero
//! extension (by the handler's parameter type), and all GPR / COP0 / memory /
//! PC / pipeline state.
//!
//! The classification is the one the C++ switch had on `main`, not a MIPS
//! reference: REGIMM matches the full 5-bit `rt` (only 0x00/0x01/0x10/0x11),
//! COP0 recognises MFC0 (`rs == 0`), MTC0 (`rs == 4`) and RFE (`rs == 0x10 &&
//! funct == 0x10`) and nothing else, COP1/2/3 and LWC1-3/SWC1-3 are CpU, and
//! every other encoding (including LWC0/SWC0) is RI.
//!
//! The symbol is internal to `PSXRecomp.Native` (declared in
//! `src/psx_cpu_decode.h`, called only from `psx_cpu_decode.cpp`), not
//! P/Invoked, so `include/psx_core.h`, `NativeInterop.cs` and `ABI_VERSION`
//! are unaffected. The export takes a `u32`, performs no allocation,
//! dereferences no pointer, and contains only shifts by constants, masks and
//! integer matches, none of which can panic, so it is infallible per
//! `docs/development/rust-ffi-contract.md` §5 and returns its result directly.

/// The handler an instruction word dispatches to.
///
/// Mirrored value-for-value by `PSXDecodeOp` in `src/psx_cpu_decode.h`; the
/// values are ABI. `Reserved` is `0` so a zeroed result fails closed (RI).
#[repr(u32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[allow(missing_docs)] // Each variant is the `PSXCpu::Exec*` handler of the same name.
pub enum DecodeOp {
    /// Reserved/undefined encoding: the caller raises RI (Excode 0x0A).
    Reserved = 0,
    /// COP1/COP2/COP3, LWC1-3, SWC1-3: the caller raises CpU (Excode 0x0B)
    /// with `CAUSE.CE = cop`.
    CopUnusable = 1,
    // SPECIAL (opcode 0x00), by funct.
    Sll = 2,
    Srl = 3,
    Sra = 4,
    Sllv = 5,
    Srlv = 6,
    Srav = 7,
    Jr = 8,
    Jalr = 9,
    Syscall = 10,
    Break = 11,
    Mfhi = 12,
    Mthi = 13,
    Mflo = 14,
    Mtlo = 15,
    Mult = 16,
    Multu = 17,
    Div = 18,
    Divu = 19,
    Add = 20,
    Addu = 21,
    Sub = 22,
    Subu = 23,
    And = 24,
    Or = 25,
    Xor = 26,
    Nor = 27,
    Slt = 28,
    Sltu = 29,
    // REGIMM (opcode 0x01), by rt.
    Bltz = 30,
    Bgez = 31,
    Bltzal = 32,
    Bgezal = 33,
    // Primary opcodes.
    J = 34,
    Jal = 35,
    Beq = 36,
    Bne = 37,
    Blez = 38,
    Bgtz = 39,
    Addi = 40,
    Addiu = 41,
    Slti = 42,
    Sltiu = 43,
    Andi = 44,
    Ori = 45,
    Xori = 46,
    Lui = 47,
    Lb = 48,
    Lh = 49,
    Lwl = 50,
    Lw = 51,
    Lbu = 52,
    Lhu = 53,
    Lwr = 54,
    Sb = 55,
    Sh = 56,
    Swl = 57,
    Sw = 58,
    Swr = 59,
    // COP0 (opcode 0x10).
    Mfc0 = 60,
    Mtc0 = 61,
    Rfe = 62,
}

/// A classified instruction word and its operand fields.
///
/// Mirrored field-for-field by `PSXDecodedInstruction` in
/// `src/psx_cpu_decode.h`; a layout change there or here is an ABI break.
/// Every field is extracted from every word regardless of `op`; the caller
/// passes only the ones the handler for `op` takes.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct DecodedInstruction {
    /// The handler to dispatch to.
    pub op: DecodeOp,
    /// Bits 21-25.
    pub rs: u32,
    /// Bits 16-20.
    pub rt: u32,
    /// Bits 11-15.
    pub rd: u32,
    /// Bits 6-10.
    pub shamt: u32,
    /// Bits 0-15, zero-extended. The caller narrows it to the handler's
    /// `int16_t`/`uint16_t` parameter, which selects sign or zero extension.
    pub imm: u32,
    /// Bits 0-25 (J/JAL jump target).
    pub target: u32,
    /// Bits 26-27: the coprocessor number, used as `CAUSE.CE` for
    /// [`DecodeOp::CopUnusable`].
    pub cop: u32,
}

/// Classifies `instruction` exactly as the pre-#525 C++ `ExecuteInstruction`
/// switch did.
#[must_use]
pub const fn decode(instruction: u32) -> DecodedInstruction {
    let opcode = instruction >> 26;
    let rs = (instruction >> 21) & 0x1F;
    let rt = (instruction >> 16) & 0x1F;
    let funct = instruction & 0x3F;

    let op = match opcode {
        0x00 => match funct {
            0x00 => DecodeOp::Sll,
            0x02 => DecodeOp::Srl,
            0x03 => DecodeOp::Sra,
            0x04 => DecodeOp::Sllv,
            0x06 => DecodeOp::Srlv,
            0x07 => DecodeOp::Srav,
            0x08 => DecodeOp::Jr,
            0x09 => DecodeOp::Jalr,
            0x0C => DecodeOp::Syscall,
            0x0D => DecodeOp::Break,
            0x10 => DecodeOp::Mfhi,
            0x11 => DecodeOp::Mthi,
            0x12 => DecodeOp::Mflo,
            0x13 => DecodeOp::Mtlo,
            0x18 => DecodeOp::Mult,
            0x19 => DecodeOp::Multu,
            0x1A => DecodeOp::Div,
            0x1B => DecodeOp::Divu,
            0x20 => DecodeOp::Add,
            0x21 => DecodeOp::Addu,
            0x22 => DecodeOp::Sub,
            0x23 => DecodeOp::Subu,
            0x24 => DecodeOp::And,
            0x25 => DecodeOp::Or,
            0x26 => DecodeOp::Xor,
            0x27 => DecodeOp::Nor,
            0x2A => DecodeOp::Slt,
            0x2B => DecodeOp::Sltu,
            _ => DecodeOp::Reserved,
        },
        0x01 => match rt {
            0x00 => DecodeOp::Bltz,
            0x01 => DecodeOp::Bgez,
            0x10 => DecodeOp::Bltzal,
            0x11 => DecodeOp::Bgezal,
            _ => DecodeOp::Reserved,
        },
        0x02 => DecodeOp::J,
        0x03 => DecodeOp::Jal,
        0x04 => DecodeOp::Beq,
        0x05 => DecodeOp::Bne,
        0x06 => DecodeOp::Blez,
        0x07 => DecodeOp::Bgtz,
        0x08 => DecodeOp::Addi,
        0x09 => DecodeOp::Addiu,
        0x0A => DecodeOp::Slti,
        0x0B => DecodeOp::Sltiu,
        0x0C => DecodeOp::Andi,
        0x0D => DecodeOp::Ori,
        0x0E => DecodeOp::Xori,
        0x0F => DecodeOp::Lui,
        // COP0 is usable, so an unrecognised COP0 form (CFC0/CTC0, the TLB
        // ops the PSX lacks) is RI, not CpU.
        0x10 => match rs {
            0x00 => DecodeOp::Mfc0,
            0x04 => DecodeOp::Mtc0,
            0x10 if funct == 0x10 => DecodeOp::Rfe,
            _ => DecodeOp::Reserved,
        },
        // COP1-3 and LWC1-3/SWC1-3 are unusable. LWC0/SWC0 (0x30/0x38) are
        // deliberately absent: COP0 has no load/store forms, so they are RI.
        0x11..=0x13 | 0x31..=0x33 | 0x39..=0x3B => DecodeOp::CopUnusable,
        0x20 => DecodeOp::Lb,
        0x21 => DecodeOp::Lh,
        0x22 => DecodeOp::Lwl,
        0x23 => DecodeOp::Lw,
        0x24 => DecodeOp::Lbu,
        0x25 => DecodeOp::Lhu,
        0x26 => DecodeOp::Lwr,
        0x28 => DecodeOp::Sb,
        0x29 => DecodeOp::Sh,
        0x2A => DecodeOp::Swl,
        0x2B => DecodeOp::Sw,
        0x2E => DecodeOp::Swr,
        _ => DecodeOp::Reserved,
    };

    DecodedInstruction {
        op,
        rs,
        rt,
        rd: (instruction >> 11) & 0x1F,
        shamt: (instruction >> 6) & 0x1F,
        imm: instruction & 0xFFFF,
        target: instruction & 0x03FF_FFFF,
        cop: opcode & 3,
    }
}

/// Returns [`decode`]`(instruction)`. Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_decode(instruction: u32) -> DecodedInstruction {
    decode(instruction)
}

#[cfg(test)]
mod tests {
    use super::*;
    use DecodeOp::*;

    fn op(instruction: u32) -> DecodeOp {
        psx_cpu_decode(instruction).op
    }

    const fn special(funct: u32) -> u32 {
        funct
    }
    const fn primary(opcode: u32) -> u32 {
        opcode << 26
    }
    const fn regimm(rt: u32) -> u32 {
        primary(0x01) | (rt << 16)
    }
    const fn cop0(rs: u32, funct: u32) -> u32 {
        primary(0x10) | (rs << 21) | funct
    }

    const SPECIAL: [(u32, DecodeOp); 28] = [
        (0x00, Sll), (0x02, Srl), (0x03, Sra), (0x04, Sllv), (0x06, Srlv),
        (0x07, Srav), (0x08, Jr), (0x09, Jalr), (0x0C, Syscall), (0x0D, Break),
        (0x10, Mfhi), (0x11, Mthi), (0x12, Mflo), (0x13, Mtlo), (0x18, Mult),
        (0x19, Multu), (0x1A, Div), (0x1B, Divu), (0x20, Add), (0x21, Addu),
        (0x22, Sub), (0x23, Subu), (0x24, And), (0x25, Or), (0x26, Xor),
        (0x27, Nor), (0x2A, Slt), (0x2B, Sltu),
    ];

    const PRIMARY: [(u32, DecodeOp); 26] = [
        (0x02, J), (0x03, Jal), (0x04, Beq), (0x05, Bne), (0x06, Blez),
        (0x07, Bgtz), (0x08, Addi), (0x09, Addiu), (0x0A, Slti), (0x0B, Sltiu),
        (0x0C, Andi), (0x0D, Ori), (0x0E, Xori), (0x0F, Lui), (0x20, Lb),
        (0x21, Lh), (0x22, Lwl), (0x23, Lw), (0x24, Lbu), (0x25, Lhu),
        (0x26, Lwr), (0x28, Sb), (0x29, Sh), (0x2A, Swl), (0x2B, Sw), (0x2E, Swr),
    ];

    const COP_UNUSABLE: [u32; 9] = [0x11, 0x12, 0x13, 0x31, 0x32, 0x33, 0x39, 0x3A, 0x3B];

    #[test]
    fn special_functs_classify_and_every_other_funct_is_reserved() {
        for funct in 0..64u32 {
            let expected = SPECIAL.iter().find(|(f, _)| *f == funct).map_or(Reserved, |(_, o)| *o);
            assert_eq!(op(special(funct)), expected, "funct {funct:#x}");
            // Operand bits never change the SPECIAL classification.
            assert_eq!(op(special(funct) | 0x03FF_FFC0), expected, "funct {funct:#x}");
        }
    }

    #[test]
    fn regimm_matches_full_rt_only() {
        for rt in 0..32u32 {
            let expected = match rt {
                0x00 => Bltz,
                0x01 => Bgez,
                0x10 => Bltzal,
                0x11 => Bgezal,
                _ => Reserved,
            };
            assert_eq!(op(regimm(rt)), expected, "rt {rt:#x}");
            assert_eq!(op(regimm(rt) | (0x1F << 21) | 0xFFFF), expected, "rt {rt:#x}");
        }
    }

    #[test]
    fn every_primary_opcode_classifies_as_before() {
        for opcode in 0..64u32 {
            let expected = if let Some((_, o)) = PRIMARY.iter().find(|(p, _)| *p == opcode) {
                *o
            } else if COP_UNUSABLE.contains(&opcode) {
                CopUnusable
            } else if opcode <= 0x01 || opcode == 0x10 {
                continue; // SPECIAL/REGIMM/COP0 are sub-decoded; covered elsewhere.
            } else {
                Reserved
            };
            assert_eq!(op(primary(opcode)), expected, "opcode {opcode:#x}");
            assert_eq!(op(primary(opcode) | 0x03FF_FFFF), expected, "opcode {opcode:#x}");
        }
    }

    #[test]
    fn lwc0_swc0_and_unused_load_store_slots_are_reserved() {
        for opcode in [0x27u32, 0x2C, 0x2D, 0x2F, 0x30, 0x34, 0x38, 0x3C, 0x3F, 0x14, 0x1E] {
            assert_eq!(op(primary(opcode)), Reserved, "opcode {opcode:#x}");
        }
    }

    #[test]
    fn cop_unusable_reports_the_coprocessor_number() {
        for opcode in COP_UNUSABLE {
            let d = psx_cpu_decode(primary(opcode) | 0x03FF_FFFF);
            assert_eq!(d.op, CopUnusable);
            assert_eq!(d.cop, opcode & 3);
        }
        assert_eq!(psx_cpu_decode(0x4A18_0001).cop, 2); // GTE RTPS
        assert_eq!(psx_cpu_decode(0xC801_0000).cop, 2); // LWC2
        assert_eq!(psx_cpu_decode(0xE801_0000).cop, 2); // SWC2
    }

    #[test]
    fn cop0_forms() {
        for rs in 0..32u32 {
            for funct in [0x00u32, 0x01, 0x02, 0x06, 0x08, 0x10, 0x3F] {
                let expected = match (rs, funct) {
                    (0x00, _) => Mfc0,
                    (0x04, _) => Mtc0,
                    (0x10, 0x10) => Rfe,
                    _ => Reserved,
                };
                assert_eq!(op(cop0(rs, funct)), expected, "rs {rs:#x} funct {funct:#x}");
            }
        }
        assert_eq!(op(0x4041_0000), Reserved); // CFC0 $1, $0
        assert_eq!(op(0x4200_0010), Rfe);
    }

    #[test]
    fn operand_fields_are_extracted_at_their_boundaries() {
        let zero = psx_cpu_decode(0);
        assert_eq!((zero.op, zero.rs, zero.rt, zero.rd, zero.shamt), (Sll, 0, 0, 0, 0));
        assert_eq!((zero.imm, zero.target, zero.cop), (0, 0, 0));

        let ones = psx_cpu_decode(u32::MAX);
        assert_eq!((ones.rs, ones.rt, ones.rd, ones.shamt), (31, 31, 31, 31));
        assert_eq!((ones.imm, ones.target, ones.cop), (0xFFFF, 0x03FF_FFFF, 3));
        assert_eq!(ones.op, Reserved); // opcode 0x3F

        // Each field in isolation: set only its bits, all others stay zero.
        let rs = psx_cpu_decode(0x1F << 21);
        assert_eq!((rs.rs, rs.rt, rs.rd, rs.shamt, rs.imm), (31, 0, 0, 0, 0));
        let rt = psx_cpu_decode(0x1F << 16);
        assert_eq!((rt.rs, rt.rt, rt.rd, rt.shamt, rt.imm), (0, 31, 0, 0, 0));
        let rd = psx_cpu_decode(0x1F << 11);
        assert_eq!((rd.rs, rd.rt, rd.rd, rd.shamt), (0, 0, 31, 0));
        let sh = psx_cpu_decode(0x1F << 6);
        assert_eq!((sh.rd, sh.shamt, sh.op), (0, 31, Sll));

        // Immediate sign boundary is left to the caller's int16_t narrowing.
        assert_eq!(psx_cpu_decode(0x2000_7FFF).imm, 0x7FFF);
        assert_eq!(psx_cpu_decode(0x2000_8000).imm, 0x8000);
        assert_eq!(psx_cpu_decode(0x2000_8000).imm as u16 as i16, i16::MIN);

        // Jump target is exactly 26 bits.
        assert_eq!(psx_cpu_decode(0x0800_0000).target, 0);
        assert_eq!(psx_cpu_decode(0x0BFF_FFFF).target, 0x03FF_FFFF);
        assert_eq!(psx_cpu_decode(0x0C12_3456).target, 0x0012_3456);
    }

    #[test]
    fn real_encodings() {
        // ADDU $3, $1, $2
        let d = psx_cpu_decode(0x0022_1821);
        assert_eq!((d.op, d.rs, d.rt, d.rd), (Addu, 1, 2, 3));
        // SRA $5, $6, 31
        let d = psx_cpu_decode(0x0006_2FC3);
        assert_eq!((d.op, d.rt, d.rd, d.shamt), (Sra, 6, 5, 31));
        // LW $8, -4($29)
        let d = psx_cpu_decode(0x8FA8_FFFC);
        assert_eq!((d.op, d.rs, d.rt, d.imm), (Lw, 29, 8, 0xFFFC));
        // BGEZAL $4, +2
        let d = psx_cpu_decode(0x0491_0002);
        assert_eq!((d.op, d.rs, d.imm), (Bgezal, 4, 2));
        // MTC0 $12, $12 (SR)
        let d = psx_cpu_decode(0x408C_6000);
        assert_eq!((d.op, d.rt, d.rd), (Mtc0, 12, 12));
    }

    #[test]
    fn layout_is_eight_u32() {
        assert_eq!(std::mem::size_of::<DecodedInstruction>(), 32);
        assert_eq!(std::mem::size_of::<DecodeOp>(), 4);
    }
}
