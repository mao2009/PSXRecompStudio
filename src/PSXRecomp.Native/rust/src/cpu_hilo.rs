//! `PSXCpu` HI/LO multiply/divide arithmetic (`MULT`/`MULTU`/`DIV`/`DIVU`),
//! migrated from the C++ `PSXCpu::ExecMult`/`ExecMultu`/`ExecDiv`/`ExecDivu`
//! (Issue #497).
//!
//! Scope: only the pure 64-bit-product / division computation that produces
//! the new HI/LO pair. The caller (`src/psx_cpu.cpp`) keeps owning GPR reads
//! (`gpr_[rs]`/`gpr_[rt]`), HI/LO storage (`hi_`/`lo_`), and `Reset()`
//! semantics; it just assigns this module's result to `hi_`/`lo_`.
//! `MFHI`/`MFLO`/`MTHI`/`MTLO` are plain HI/LO <-> GPR state moves with no
//! arithmetic and stay entirely in C++: there is nothing for Rust to compute,
//! and crossing the FFI boundary for a copy the caller already owns would add
//! a call with no semantic value.
//!
//! These symbols are internal to `PSXRecomp.Native` (declared in
//! `src/psx_cpu_hilo.h`, called only from `psx_cpu.cpp`), not P/Invoked, so
//! `include/psx_core.h`, `NativeInterop.cs` and `ABI_VERSION` are unaffected
//! by this module. Every export takes only `u32` values, performs no
//! allocation, dereferences no pointer, and contains no operation that can
//! panic: the divisor-zero and `i32::MIN / -1` cases (both of which panic
//! Rust's checked `/`/`%` in every build profile, unlike C++'s well-defined-
//! but-PS1-special-cased behavior) are handled before any native division, so
//! every export is infallible per `docs/development/rust-ffi-contract.md` §5
//! and returns its result directly.

/// A HI/LO register pair, as produced by `MULT`/`MULTU`/`DIV`/`DIVU`.
///
/// Mirrored field-for-field by `PSXMulDivResult` in `src/psx_cpu_hilo.h`; a
/// layout change there or here is an ABI break.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct MulDivResult {
    /// The new value of the HI register.
    pub hi: u32,
    /// The new value of the LO register.
    pub lo: u32,
}

/// `MULT`: the signed 64-bit product of `a` and `b` (each reinterpreted as a
/// two's-complement `i32`), split with HI holding the upper 32 bits and LO
/// the lower 32 bits. Matches `PSXCpu::ExecMult`'s
/// `int64_t(ToSigned(a)) * int64_t(ToSigned(b))` exactly; the product of two
/// `i32` values always fits in `i64` (magnitude at most `2^62`), so this can
/// never overflow.
#[must_use]
pub const fn mult_signed(a: u32, b: u32) -> MulDivResult {
    let product = (a as i32 as i64) * (b as i32 as i64);
    MulDivResult {
        hi: (product >> 32) as u32,
        lo: (product & 0xFFFF_FFFF) as u32,
    }
}

/// `MULTU`: the unsigned 64-bit product of `a` and `b`, split the same way as
/// [`mult_signed`]. The product of two `u32` values always fits in `u64`, so
/// this can never overflow.
#[must_use]
pub const fn mult_unsigned(a: u32, b: u32) -> MulDivResult {
    let product = (a as u64) * (b as u64);
    MulDivResult {
        hi: (product >> 32) as u32,
        lo: (product & 0xFFFF_FFFF) as u32,
    }
}

/// `DIV`: `dividend / divisor` and `dividend % divisor`, both operands
/// reinterpreted as two's-complement `i32`, with the PS1/MIPS-I special
/// cases `PSXCpu::ExecDiv` implements:
///
/// - `divisor == 0`: LO = `0xFFFFFFFF` when `dividend >= 0`, else `1`; HI =
///   `dividend` (reinterpreted as `u32`).
/// - `dividend == i32::MIN && divisor == -1`: LO = `0x80000000`, HI = `0`
///   (this is the PS1-specific result, not a trap — the mathematical
///   quotient `2^31` cannot be represented in 32 bits, and this codebase
///   pins the wrapped result rather than any other convention).
/// - Otherwise: LO = truncating quotient, HI = truncating remainder (Rust's
///   `/`/`%` on `i32` already truncate toward zero and take the dividend's
///   sign, identical to C++'s `/`/`%` on `int32_t`), computed only after the
///   two cases above are ruled out (`i32::MIN / -1` and any `/ 0` panic
///   Rust's checked division otherwise).
#[must_use]
pub const fn div_signed(dividend: u32, divisor: u32) -> MulDivResult {
    let dividend_s = dividend as i32;
    let divisor_s = divisor as i32;
    if divisor_s == 0 {
        let lo = if dividend_s >= 0 { u32::MAX } else { 1 };
        return MulDivResult { hi: dividend_s as u32, lo };
    }
    if dividend_s == i32::MIN && divisor_s == -1 {
        return MulDivResult { hi: 0, lo: 0x8000_0000 };
    }
    MulDivResult {
        hi: (dividend_s % divisor_s) as u32,
        lo: (dividend_s / divisor_s) as u32,
    }
}

/// `DIVU`: unsigned `dividend / divisor` and `dividend % divisor`, with
/// `PSXCpu::ExecDivu`'s divide-by-zero special case: `divisor == 0` gives
/// LO = `0xFFFFFFFF`, HI = `dividend`, checked before any native division
/// (which would otherwise panic on divide-by-zero).
#[must_use]
pub const fn div_unsigned(dividend: u32, divisor: u32) -> MulDivResult {
    if divisor == 0 {
        return MulDivResult { hi: dividend, lo: u32::MAX };
    }
    MulDivResult {
        hi: dividend % divisor,
        lo: dividend / divisor,
    }
}

/// Returns [`mult_signed`]. Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_hilo_mult(a: u32, b: u32) -> MulDivResult {
    mult_signed(a, b)
}

/// Returns [`mult_unsigned`]. Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_hilo_multu(a: u32, b: u32) -> MulDivResult {
    mult_unsigned(a, b)
}

/// Returns [`div_signed`]. Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_hilo_div(dividend: u32, divisor: u32) -> MulDivResult {
    div_signed(dividend, divisor)
}

/// Returns [`div_unsigned`]. Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_hilo_divu(dividend: u32, divisor: u32) -> MulDivResult {
    div_unsigned(dividend, divisor)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn r(hi: u32, lo: u32) -> MulDivResult {
        MulDivResult { hi, lo }
    }

    // --- MULT --------------------------------------------------------------

    #[test]
    fn mult_zero_times_zero() {
        assert_eq!(mult_signed(0, 0), r(0, 0));
        assert_eq!(psx_cpu_hilo_mult(0, 0), r(0, 0));
    }

    #[test]
    fn mult_positive_times_positive() {
        // 1000 * 2000 = 2_000_000, fits entirely in LO.
        assert_eq!(mult_signed(1000, 2000), r(0, 2_000_000));
    }

    #[test]
    fn mult_positive_times_negative() {
        // 5 * -3 = -15.
        assert_eq!(mult_signed(5, 0xFFFF_FFFDu32), r(0xFFFF_FFFF, (-15i32) as u32));
    }

    #[test]
    fn mult_negative_times_negative() {
        // -5 * -3 = 15.
        assert_eq!(mult_signed(0xFFFF_FFFBu32, 0xFFFF_FFFDu32), r(0, 15));
    }

    #[test]
    fn mult_int32_max_squared() {
        let expected = (i32::MAX as i64) * (i32::MAX as i64);
        let got = mult_signed(i32::MAX as u32, i32::MAX as u32);
        assert_eq!(got, r((expected >> 32) as u32, (expected & 0xFFFF_FFFF) as u32));
        assert_eq!(got, r(0x3FFF_FFFF, 0x0000_0001));
    }

    #[test]
    fn mult_int32_min_squared() {
        // i32::MIN * i32::MIN = 2^62, entirely in HI's top region.
        let got = mult_signed(i32::MIN as u32, i32::MIN as u32);
        let expected = (i32::MIN as i64) * (i32::MIN as i64);
        assert_eq!(got, r((expected >> 32) as u32, (expected & 0xFFFF_FFFF) as u32));
    }

    #[test]
    fn mult_upper_lower_split_is_a_true_64_bit_product() {
        // i32::MIN * -1 = 2^31, which does NOT fit in a signed/unsigned 32-bit
        // LO alone: HI must carry the overflow bit.
        let got = mult_signed(i32::MIN as u32, 0xFFFF_FFFFu32 /* -1 */);
        assert_eq!(got, r(0, 0x8000_0000));
    }

    // --- MULTU ---------------------------------------------------------

    #[test]
    fn multu_zero() {
        assert_eq!(mult_unsigned(0, 12345), r(0, 0));
        assert_eq!(psx_cpu_hilo_multu(0, 12345), r(0, 0));
    }

    #[test]
    fn multu_uint32_max_squared() {
        let expected = (u32::MAX as u64) * (u32::MAX as u64);
        let got = mult_unsigned(u32::MAX, u32::MAX);
        assert_eq!(got, r((expected >> 32) as u32, expected as u32));
        assert_eq!(got, r(0xFFFF_FFFE, 0x0000_0001));
    }

    #[test]
    fn multu_large_times_large() {
        let a = 0xABCD_1234u32;
        let b = 0x1234_ABCDu32;
        let expected = (a as u64) * (b as u64);
        assert_eq!(mult_unsigned(a, b), r((expected >> 32) as u32, expected as u32));
    }

    #[test]
    fn multu_upper_lower_split() {
        // 0x1_0000_0000 requires two multiplicands whose product exceeds 32 bits.
        let got = mult_unsigned(0x0001_0000, 0x0001_0000);
        assert_eq!(got, r(0x0000_0001, 0x0000_0000));
    }

    // --- DIV -------------------------------------------------------------

    #[test]
    fn div_normal_positive() {
        // 42 / 5 = 8 rem 2.
        assert_eq!(div_signed(42, 5), r(2, 8));
        assert_eq!(psx_cpu_hilo_div(42, 5), r(2, 8));
    }

    #[test]
    fn div_positive_by_negative() {
        // 42 / -5 = -8 rem 2 (remainder takes dividend's sign).
        let got = div_signed(42, (-5i32) as u32);
        assert_eq!(got, r(2, (-8i32) as u32));
    }

    #[test]
    fn div_negative_by_positive() {
        // -42 / 5 = -8 rem -2.
        let got = div_signed((-42i32) as u32, 5);
        assert_eq!(got, r((-2i32) as u32, (-8i32) as u32));
    }

    #[test]
    fn div_negative_by_negative() {
        // -42 / -5 = 8 rem -2.
        let got = div_signed((-42i32) as u32, (-5i32) as u32);
        assert_eq!(got, r((-2i32) as u32, 8));
    }

    #[test]
    fn div_dividend_zero() {
        assert_eq!(div_signed(0, 7), r(0, 0));
        assert_eq!(div_signed(0, (-7i32) as u32), r(0, 0));
    }

    #[test]
    fn div_by_zero_positive_dividend() {
        // PS1/MIPS-I: LO = -1, HI = dividend.
        assert_eq!(div_signed(42, 0), r(42, u32::MAX));
        assert_eq!(psx_cpu_hilo_div(42, 0), r(42, u32::MAX));
    }

    #[test]
    fn div_by_zero_negative_dividend() {
        // PS1/MIPS-I: LO = 1, HI = dividend.
        let dividend = (-42i32) as u32;
        assert_eq!(div_signed(dividend, 0), r(dividend, 1));
    }

    #[test]
    fn div_by_zero_dividend_zero() {
        // dividend >= 0 branch: LO = -1, HI = 0.
        assert_eq!(div_signed(0, 0), r(0, u32::MAX));
    }

    #[test]
    fn div_int32_min_by_minus_one_is_the_pinned_ps1_result_not_a_panic() {
        let got = div_signed(i32::MIN as u32, (-1i32) as u32);
        assert_eq!(got, r(0, 0x8000_0000));
        assert_eq!(psx_cpu_hilo_div(i32::MIN as u32, (-1i32) as u32), r(0, 0x8000_0000));
    }

    #[test]
    fn div_int32_min_by_one_is_unaffected_by_the_minus_one_special_case() {
        assert_eq!(div_signed(i32::MIN as u32, 1), r(0, i32::MIN as u32));
    }

    #[test]
    fn div_quotient_and_remainder_signs_match_native_truncating_division() {
        for (dividend, divisor) in [
            (7, 2), (-7, 2), (7, -2), (-7, -2),
            (1, 3), (-1, 3), (1, -3), (-1, -3),
        ] {
            let got = div_signed(dividend as u32, divisor as u32);
            assert_eq!(got.lo as i32, dividend / divisor, "quotient({dividend}, {divisor})");
            assert_eq!(got.hi as i32, dividend % divisor, "remainder({dividend}, {divisor})");
        }
    }

    // --- DIVU ------------------------------------------------------------

    #[test]
    fn divu_normal() {
        assert_eq!(div_unsigned(42, 5), r(2, 8));
        assert_eq!(psx_cpu_hilo_divu(42, 5), r(2, 8));
    }

    #[test]
    fn divu_dividend_zero() {
        assert_eq!(div_unsigned(0, 7), r(0, 0));
    }

    #[test]
    fn divu_by_zero() {
        assert_eq!(div_unsigned(42, 0), r(42, u32::MAX));
        assert_eq!(psx_cpu_hilo_divu(42, 0), r(42, u32::MAX));
    }

    #[test]
    fn divu_by_zero_dividend_zero() {
        assert_eq!(div_unsigned(0, 0), r(0, u32::MAX));
    }

    #[test]
    fn divu_uint32_max() {
        assert_eq!(div_unsigned(u32::MAX, 1), r(0, u32::MAX));
        assert_eq!(div_unsigned(u32::MAX, u32::MAX), r(0, 1));
        // No sign extension: treated as the largest possible unsigned value.
        let got = div_unsigned(u32::MAX, 2);
        assert_eq!(got, r(1, u32::MAX / 2));
    }

    #[test]
    fn divu_quotient_and_remainder_match_native_unsigned_division() {
        for (dividend, divisor) in [(100u32, 7u32), (1, 1), (u32::MAX, 3), (0x8000_0000, 3)] {
            let got = div_unsigned(dividend, divisor);
            assert_eq!(got.lo, dividend / divisor);
            assert_eq!(got.hi, dividend % divisor);
        }
    }
}
