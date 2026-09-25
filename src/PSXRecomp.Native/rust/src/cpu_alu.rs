//! `PSXCpu` overflow-checked arithmetic (`ADD`/`ADDI`/`SUB`), migrated from
//! the C++ `PSXCpu::ExecAdd`/`ExecAddi`/`ExecSub` (Issue #495).
//!
//! Scope: only the pure overflow-detecting sum/difference computation. The
//! caller (`src/psx_cpu.cpp`) keeps owning GPR reads/writes, sign-extending
//! `ADDI`'s immediate before calling [`psx_cpu_alu_add`], and raising the
//! `Ov` exception (CAUSE Excode 0x0C) when `overflow != 0`.
//!
//! These symbols are internal to `PSXRecomp.Native` (declared in
//! `src/psx_cpu_alu.h`, called only from `psx_cpu.cpp`), not P/Invoked, so
//! `include/psx_core.h`, `NativeInterop.cs` and `ABI_VERSION` are unaffected
//! by this module. Every export takes only `u32` values, performs no
//! allocation, dereferences no pointer, and contains no operation that can
//! panic (addition/subtraction on `u32` never panics; overflow is detected,
//! not trapped), so every export is infallible per
//! `docs/development/rust-ffi-contract.md` §5 and returns its result
//! directly.

/// Result of an overflow-checked 32-bit add/subtract: the wrapped result plus
/// whether the operation signed-overflowed.
///
/// Mirrored field-for-field by `PSXAluResult` in `src/psx_cpu_alu.h`; a
/// layout change there or here is an ABI break. `overflow` is `0`/`1`, not
/// `bool`, per the FFI contract (`bool` is not an ABI-visible struct field).
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct AluResult {
    /// The 32-bit wrapped result of the operation, valid regardless of
    /// `overflow`. The caller only writes it to a GPR when `overflow == 0`
    /// (MIPS I: the destination register is unmodified on a trapped `ADD`/
    /// `ADDI`/`SUB`).
    pub result: u32,
    /// `1` when the operation signed-overflowed (MIPS I: operands' signs
    /// equal and differ from the result's sign for `ADD`/`ADDI`; operands'
    /// signs differ and the result's sign differs from the minuend's for
    /// `SUB`), else `0`.
    pub overflow: u32,
}

/// `a + b` interpreted as two's-complement 32-bit signed integers, with MIPS
/// I `ADD`/`ADDI` signed-overflow detection.
///
/// `ADDI` sign-extends its 16-bit immediate to 32 bits before calling this
/// (equivalent to `ADD` with that sign-extended value as `b`).
#[must_use]
pub const fn add_overflowing(a: u32, b: u32) -> AluResult {
    let sum = a.wrapping_add(b);
    // Signed overflow (MIPS I): sign of a and b equal and differ from sign of sum.
    let overflow = ((a ^ sum) & (b ^ sum)) & 0x8000_0000 != 0;
    AluResult { result: sum, overflow: overflow as u32 }
}

/// `a - b` interpreted as two's-complement 32-bit signed integers, with MIPS
/// I `SUB` signed-overflow detection.
#[must_use]
pub const fn sub_overflowing(a: u32, b: u32) -> AluResult {
    let diff = a.wrapping_sub(b);
    // Signed overflow (MIPS I): signs differ and result sign differs from minuend.
    let overflow = ((a ^ b) & (a ^ diff)) & 0x8000_0000 != 0;
    AluResult { result: diff, overflow: overflow as u32 }
}

/// Returns `add_overflowing(a, b)`. Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_alu_add(a: u32, b: u32) -> AluResult {
    add_overflowing(a, b)
}

/// Returns `sub_overflowing(a, b)`. Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_alu_sub(a: u32, b: u32) -> AluResult {
    sub_overflowing(a, b)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ok(result: u32) -> AluResult {
        AluResult { result, overflow: 0 }
    }

    fn ov(result: u32) -> AluResult {
        AluResult { result, overflow: 1 }
    }

    #[test]
    fn add_normal_case() {
        assert_eq!(add_overflowing(10, 20), ok(30));
        assert_eq!(psx_cpu_alu_add(10, 20), ok(30));
    }

    #[test]
    fn add_zero_is_identity() {
        assert_eq!(add_overflowing(0, 0), ok(0));
        assert_eq!(add_overflowing(0x1234_5678, 0), ok(0x1234_5678));
        assert_eq!(add_overflowing(0, 0x1234_5678), ok(0x1234_5678));
    }

    #[test]
    fn add_negative_operands_via_two_complement() {
        // -1 + -1 = -2, no overflow.
        assert_eq!(add_overflowing(u32::MAX, u32::MAX), ok(0xFFFF_FFFE));
        // -1 + 1 = 0, no overflow.
        assert_eq!(add_overflowing(u32::MAX, 1), ok(0));
    }

    #[test]
    fn add_signed_boundary_no_overflow() {
        // i32::MAX + (-1) = i32::MAX - 1: same sign as neither operand's
        // extreme, no overflow.
        assert_eq!(add_overflowing(0x7FFF_FFFF, u32::MAX), ok(0x7FFF_FFFE));
        // i32::MIN + 1: no overflow (result sign matches operand signs' mix).
        assert_eq!(add_overflowing(0x8000_0000, 1), ok(0x8000_0001));
    }

    #[test]
    fn add_overflow_positive() {
        // i32::MAX + 1 overflows into negative range.
        assert_eq!(add_overflowing(0x7FFF_FFFF, 1), ov(0x8000_0000));
        assert_eq!(psx_cpu_alu_add(0x7FFF_FFFF, 1), ov(0x8000_0000));
        // i32::MAX + i32::MAX overflows.
        assert_eq!(add_overflowing(0x7FFF_FFFF, 0x7FFF_FFFF).overflow, 1);
    }

    #[test]
    fn add_overflow_negative() {
        // i32::MIN + i32::MIN overflows into positive range.
        assert_eq!(add_overflowing(0x8000_0000, 0x8000_0000), ov(0));
        assert_eq!(add_overflowing(0x8000_0000, 0x8000_0000).overflow, 1);
    }

    #[test]
    fn add_wraps_on_overflow_result_bits() {
        // Result bits still follow wrapping addition even when overflow is set.
        let r = add_overflowing(0x7FFF_FFFF, 0x7FFF_FFFF);
        assert_eq!(r.result, 0x7FFF_FFFF_u32.wrapping_add(0x7FFF_FFFF));
        assert_eq!(r.overflow, 1);
    }

    #[test]
    fn sub_normal_case() {
        assert_eq!(sub_overflowing(50, 30), ok(20));
        assert_eq!(psx_cpu_alu_sub(50, 30), ok(20));
    }

    #[test]
    fn sub_zero_cases() {
        assert_eq!(sub_overflowing(0, 0), ok(0));
        assert_eq!(sub_overflowing(0x1234_5678, 0), ok(0x1234_5678));
        assert_eq!(sub_overflowing(0, 1), ok(u32::MAX)); // 0 - 1 = -1, no overflow
    }

    #[test]
    fn sub_signed_boundary_no_overflow() {
        // i32::MIN - i32::MIN = 0, no overflow.
        assert_eq!(sub_overflowing(0x8000_0000, 0x8000_0000), ok(0));
        // i32::MAX - i32::MAX = 0, no overflow.
        assert_eq!(sub_overflowing(0x7FFF_FFFF, 0x7FFF_FFFF), ok(0));
    }

    #[test]
    fn sub_overflow_positive_minus_negative() {
        // i32::MAX - (-1) overflows (would be i32::MAX + 1).
        assert_eq!(sub_overflowing(0x7FFF_FFFF, u32::MAX), ov(0x8000_0000));
        assert_eq!(psx_cpu_alu_sub(0x7FFF_FFFF, u32::MAX), ov(0x8000_0000));
    }

    #[test]
    fn sub_overflow_negative_minus_positive() {
        // i32::MIN - 1 overflows (would be i32::MIN - 1, wraps past MIN).
        assert_eq!(sub_overflowing(0x8000_0000, 1), ov(0x7FFF_FFFF));
    }

    #[test]
    fn sub_wraps_on_overflow_result_bits() {
        let r = sub_overflowing(0x8000_0000, 1);
        assert_eq!(r.result, 0x8000_0000_u32.wrapping_sub(1));
        assert_eq!(r.overflow, 1);
    }

    #[test]
    fn add_sub_are_inverse_when_no_overflow() {
        for (a, b) in [(10u32, 20u32), (0, 0), (0x1234, 0x5678), (100, 1)] {
            let sum = add_overflowing(a, b);
            if sum.overflow == 0 {
                assert_eq!(sub_overflowing(sum.result, b).result, a);
            }
        }
    }
}
