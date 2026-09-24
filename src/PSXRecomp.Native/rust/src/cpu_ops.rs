//! `PSXCpu` non-trapping ALU / logic / compare / shift arithmetic, migrated
//! from the C++ `PSXCpu::ExecAddu`..`ExecSrav` handlers (Issue #501).
//!
//! Scope: only the pure `u32 -> u32` computation. The caller
//! (`src/psx_cpu.cpp`) keeps owning instruction decode, GPR reads, `SetGPR`
//! (including `$zero` protection), PC/pipeline/delay-slot state, and the
//! 16-bit immediate extension of the I-type forms: `ADDIU`/`SLTI`/`SLTIU`
//! pass `SignExtend16(imm)`, `ANDI`/`ORI`/`XORI` pass `ZeroExtend16(imm)`, and
//! `LUI` is [`psx_cpu_ops_sll`]`(imm, 16)` — the same split as `ADDI` in
//! `cpu_alu.rs` (Issue #495).
//!
//! These symbols are internal to `PSXRecomp.Native` (declared in
//! `src/psx_cpu_ops.h`, called only from `psx_cpu.cpp`), not P/Invoked, so
//! `include/psx_core.h`, `NativeInterop.cs` and `ABI_VERSION` are unaffected.
//! Every export takes only `u32` values, performs no allocation, dereferences
//! no pointer, and contains no operation that can panic: add/subtract use
//! `wrapping_*`, and shifts use `wrapping_shl`/`wrapping_shr`, which mask the
//! amount to its low 5 bits instead of panicking on `>= 32` — exactly the
//! MIPS `& 0x1F` the C++ applied. So every export is infallible per
//! `docs/development/rust-ffi-contract.md` §5 and returns its result directly.

/// `ADDU` (and `ADDIU` with a sign-extended immediate): `a + b`, wrapping.
#[no_mangle]
pub extern "C" fn psx_cpu_ops_addu(a: u32, b: u32) -> u32 {
    a.wrapping_add(b)
}

/// `SUBU`: `a - b`, wrapping.
#[no_mangle]
pub extern "C" fn psx_cpu_ops_subu(a: u32, b: u32) -> u32 {
    a.wrapping_sub(b)
}

/// `AND` (and `ANDI` with a zero-extended immediate).
#[no_mangle]
pub extern "C" fn psx_cpu_ops_and(a: u32, b: u32) -> u32 {
    a & b
}

/// `OR` (and `ORI` with a zero-extended immediate).
#[no_mangle]
pub extern "C" fn psx_cpu_ops_or(a: u32, b: u32) -> u32 {
    a | b
}

/// `XOR` (and `XORI` with a zero-extended immediate).
#[no_mangle]
pub extern "C" fn psx_cpu_ops_xor(a: u32, b: u32) -> u32 {
    a ^ b
}

/// `NOR`: `!(a | b)`.
#[no_mangle]
pub extern "C" fn psx_cpu_ops_nor(a: u32, b: u32) -> u32 {
    !(a | b)
}

/// `SLT` (and `SLTI` with a sign-extended immediate): `1` when `a < b` as
/// two's-complement `i32`, else `0`.
#[no_mangle]
pub extern "C" fn psx_cpu_ops_slt(a: u32, b: u32) -> u32 {
    ((a as i32) < (b as i32)) as u32
}

/// `SLTU` (and `SLTIU` with a *sign*-extended immediate, MIPS I): `1` when
/// `a < b` unsigned, else `0`.
#[no_mangle]
pub extern "C" fn psx_cpu_ops_sltu(a: u32, b: u32) -> u32 {
    (a < b) as u32
}

/// `SLL`/`SLLV` (and `LUI` as `sll(imm, 16)`): logical left shift by
/// `amount & 0x1F`.
#[no_mangle]
pub extern "C" fn psx_cpu_ops_sll(value: u32, amount: u32) -> u32 {
    value.wrapping_shl(amount)
}

/// `SRL`/`SRLV`: logical right shift by `amount & 0x1F`.
#[no_mangle]
pub extern "C" fn psx_cpu_ops_srl(value: u32, amount: u32) -> u32 {
    value.wrapping_shr(amount)
}

/// `SRA`/`SRAV`: arithmetic (sign-filling) right shift by `amount & 0x1F`.
/// Rust defines `>>` on `i32` as arithmetic, which is what every compiler the
/// C++ `int32_t >> n` was built with produced (implementation-defined in C++17).
#[no_mangle]
pub extern "C" fn psx_cpu_ops_sra(value: u32, amount: u32) -> u32 {
    (value as i32).wrapping_shr(amount) as u32
}

#[cfg(test)]
mod tests {
    use super::*;

    const MIN: u32 = 0x8000_0000; // i32::MIN bit pattern
    const MAX: u32 = 0x7FFF_FFFF; // i32::MAX bit pattern

    // What the C++ caller passes for a 16-bit immediate.
    fn sext16(imm: u16) -> u32 {
        imm as i16 as i32 as u32
    }
    fn zext16(imm: u16) -> u32 {
        imm as u32
    }

    #[test]
    fn addu_wraps() {
        assert_eq!(psx_cpu_ops_addu(0, 0), 0);
        assert_eq!(psx_cpu_ops_addu(10, 20), 30);
        assert_eq!(psx_cpu_ops_addu(u32::MAX, 1), 0);
        assert_eq!(psx_cpu_ops_addu(u32::MAX, u32::MAX), 0xFFFF_FFFE);
        // Signed overflow does not trap and just wraps.
        assert_eq!(psx_cpu_ops_addu(MAX, 1), MIN);
        assert_eq!(psx_cpu_ops_addu(MIN, MIN), 0);
    }

    #[test]
    fn subu_wraps() {
        assert_eq!(psx_cpu_ops_subu(0, 0), 0);
        assert_eq!(psx_cpu_ops_subu(50, 30), 20);
        assert_eq!(psx_cpu_ops_subu(0, 1), u32::MAX);
        assert_eq!(psx_cpu_ops_subu(MIN, 1), MAX);
        assert_eq!(psx_cpu_ops_subu(MAX, u32::MAX), MIN);
        assert_eq!(psx_cpu_ops_subu(u32::MAX, u32::MAX), 0);
    }

    #[test]
    fn addiu_uses_sign_extended_immediate() {
        assert_eq!(psx_cpu_ops_addu(10, sext16(0xFFFF)), 9); // + (-1)
        assert_eq!(psx_cpu_ops_addu(0, sext16(0x8000)), 0xFFFF_8000);
        assert_eq!(psx_cpu_ops_addu(0, sext16(0x7FFF)), 0x0000_7FFF);
        assert_eq!(psx_cpu_ops_addu(0, sext16(0)), 0);
    }

    #[test]
    fn logic_ops() {
        let alt = 0xAAAA_AAAA;
        let inv = 0x5555_5555;
        for (a, b) in [(0, 0), (u32::MAX, u32::MAX), (0, u32::MAX), (alt, inv), (alt, alt)] {
            assert_eq!(psx_cpu_ops_and(a, b), a & b);
            assert_eq!(psx_cpu_ops_or(a, b), a | b);
            assert_eq!(psx_cpu_ops_xor(a, b), a ^ b);
            assert_eq!(psx_cpu_ops_nor(a, b), !(a | b));
        }
        assert_eq!(psx_cpu_ops_and(alt, inv), 0);
        assert_eq!(psx_cpu_ops_or(alt, inv), u32::MAX);
        assert_eq!(psx_cpu_ops_xor(alt, alt), 0);
        assert_eq!(psx_cpu_ops_nor(0, 0), u32::MAX);
        assert_eq!(psx_cpu_ops_nor(alt, inv), 0);
        assert_eq!(psx_cpu_ops_nor(0xF0F0, 0x0F0F), 0xFFFF_0000);
    }

    #[test]
    fn logic_immediates_are_zero_extended() {
        assert_eq!(psx_cpu_ops_and(u32::MAX, zext16(0xFFFF)), 0x0000_FFFF);
        assert_eq!(psx_cpu_ops_and(u32::MAX, zext16(0x8000)), 0x0000_8000);
        assert_eq!(psx_cpu_ops_or(0, zext16(0x8000)), 0x0000_8000);
        assert_eq!(psx_cpu_ops_or(0x1234_0000, zext16(0x7FFF)), 0x1234_7FFF);
        assert_eq!(psx_cpu_ops_xor(u32::MAX, zext16(0xFFFF)), 0xFFFF_0000);
        assert_eq!(psx_cpu_ops_xor(5, zext16(0)), 5);
    }

    #[test]
    fn slt_is_signed() {
        assert_eq!(psx_cpu_ops_slt(0, 0), 0);
        assert_eq!(psx_cpu_ops_slt(10, 20), 1);
        assert_eq!(psx_cpu_ops_slt(20, 10), 0);
        assert_eq!(psx_cpu_ops_slt(u32::MAX, 0), 1); // -1 < 0
        assert_eq!(psx_cpu_ops_slt(0, u32::MAX), 0);
        assert_eq!(psx_cpu_ops_slt(MIN, MAX), 1);
        assert_eq!(psx_cpu_ops_slt(MAX, MIN), 0);
        assert_eq!(psx_cpu_ops_slt(MIN, MIN), 0);
        assert_eq!(psx_cpu_ops_slt(MAX, MAX), 0);
    }

    #[test]
    fn sltu_is_unsigned() {
        assert_eq!(psx_cpu_ops_sltu(0, 0), 0);
        assert_eq!(psx_cpu_ops_sltu(10, 20), 1);
        assert_eq!(psx_cpu_ops_sltu(u32::MAX, 0), 0);
        assert_eq!(psx_cpu_ops_sltu(0, u32::MAX), 1);
        assert_eq!(psx_cpu_ops_sltu(MAX, MIN), 1);
        assert_eq!(psx_cpu_ops_sltu(MIN, MAX), 0);
        assert_eq!(psx_cpu_ops_sltu(u32::MAX, u32::MAX), 0);
    }

    #[test]
    fn slti_uses_sign_extended_immediate() {
        assert_eq!(psx_cpu_ops_slt(0, sext16(0x0000)), 0);
        assert_eq!(psx_cpu_ops_slt(0, sext16(0x7FFF)), 1);
        assert_eq!(psx_cpu_ops_slt(0, sext16(0x8000)), 0); // 0 < -32768 false
        assert_eq!(psx_cpu_ops_slt(0, sext16(0xFFFF)), 0); // 0 < -1 false
        assert_eq!(psx_cpu_ops_slt(MIN, sext16(0x8000)), 1);
        assert_eq!(psx_cpu_ops_slt(0xFFFF_8000, sext16(0x8000)), 0); // equal
    }

    #[test]
    fn sltiu_sign_extends_then_compares_unsigned() {
        // Issue #306: 0xFFFF becomes 0xFFFFFFFF, not 0x0000FFFF.
        assert_eq!(psx_cpu_ops_sltu(0x0001_0000, sext16(0xFFFF)), 1);
        assert_eq!(psx_cpu_ops_sltu(0x0000_FFFF, sext16(0xFFFF)), 1);
        assert_eq!(psx_cpu_ops_sltu(u32::MAX, sext16(0xFFFF)), 0);
        assert_eq!(psx_cpu_ops_sltu(MAX, sext16(0x8000)), 1); // vs 0xFFFF8000
        assert_eq!(psx_cpu_ops_sltu(3, sext16(0x0005)), 1);
        assert_eq!(psx_cpu_ops_sltu(0, sext16(0x0000)), 0);
        assert_eq!(psx_cpu_ops_sltu(0x7FFF, sext16(0x7FFF)), 0);
    }

    #[test]
    fn lui_is_sll_by_16() {
        assert_eq!(psx_cpu_ops_sll(zext16(0x0000), 16), 0);
        assert_eq!(psx_cpu_ops_sll(zext16(0x7FFF), 16), 0x7FFF_0000);
        assert_eq!(psx_cpu_ops_sll(zext16(0x8000), 16), 0x8000_0000);
        assert_eq!(psx_cpu_ops_sll(zext16(0xFFFF), 16), 0xFFFF_0000);
    }

    #[test]
    fn fixed_shift_amounts() {
        assert_eq!(psx_cpu_ops_sll(1, 0), 1);
        assert_eq!(psx_cpu_ops_sll(1, 1), 2);
        assert_eq!(psx_cpu_ops_sll(1, 31), MIN);
        assert_eq!(psx_cpu_ops_sll(u32::MAX, 31), MIN);
        assert_eq!(psx_cpu_ops_srl(MIN, 0), MIN);
        assert_eq!(psx_cpu_ops_srl(MIN, 1), 0x4000_0000);
        assert_eq!(psx_cpu_ops_srl(MIN, 31), 1);
        assert_eq!(psx_cpu_ops_srl(u32::MAX, 31), 1);
    }

    #[test]
    fn sra_fills_with_sign_bit() {
        assert_eq!(psx_cpu_ops_sra(MIN, 0), MIN);
        assert_eq!(psx_cpu_ops_sra(MIN, 1), 0xC000_0000);
        assert_eq!(psx_cpu_ops_sra(MIN, 31), u32::MAX);
        assert_eq!(psx_cpu_ops_sra(u32::MAX, 1), u32::MAX);
        assert_eq!(psx_cpu_ops_sra(u32::MAX, 31), u32::MAX);
        assert_eq!(psx_cpu_ops_sra(0xFFFF_FF00, 4), 0xFFFF_FFF0);
        // Positive values shift in zeros.
        assert_eq!(psx_cpu_ops_sra(MAX, 1), 0x3FFF_FFFF);
        assert_eq!(psx_cpu_ops_sra(MAX, 31), 0);
        assert_eq!(psx_cpu_ops_sra(0x40, 3), 0x8);
    }

    #[test]
    fn variable_shift_uses_low_five_bits_only() {
        // 32 -> 0, 33 -> 1, 63 -> 31, 0xFFFFFFFF -> 31: matches C++ `& 0x1F`.
        for (amount, masked) in [(32u32, 0u32), (33, 1), (63, 31), (64, 0), (u32::MAX, 31)] {
            for v in [1u32, MIN, u32::MAX, 0x1234_5678] {
                assert_eq!(psx_cpu_ops_sll(v, amount), v << masked);
                assert_eq!(psx_cpu_ops_srl(v, amount), v >> masked);
                assert_eq!(psx_cpu_ops_sra(v, amount), ((v as i32) >> masked) as u32);
            }
        }
        assert_eq!(psx_cpu_ops_sll(1, 33), 2);
        assert_eq!(psx_cpu_ops_srl(MIN, 63), 1);
        assert_eq!(psx_cpu_ops_sra(MIN, 63), u32::MAX);
        assert_eq!(psx_cpu_ops_sra(MIN, 32), MIN);
    }

    #[test]
    fn sra_matches_explicit_sign_fill_reference() {
        // Independent of `>>` on i32: shift logically, then OR in the sign fill.
        for v in [0u32, 1, MAX, MIN, u32::MAX, 0x8000_0001, 0xDEAD_BEEF, 0x1234_5678] {
            for s in 0..32u32 {
                let fill = if v & MIN != 0 && s != 0 { !(u32::MAX >> s) } else { 0 };
                assert_eq!(psx_cpu_ops_sra(v, s), (v >> s) | fill, "v={v:#x} s={s}");
            }
        }
    }
}
