//! `PSXCpu` exception resolution arithmetic, migrated from the C++
//! `PSXCpu::RaiseException` (Issue #530).
//!
//! Scope: only the pure computation of the new EPC, CAUSE, SR and exception
//! vector from the current CPU inputs. The caller (`src/psx_cpu_exception.cpp`)
//! keeps owning every state write: applying the result to `cop0_`/`pc_`,
//! BadVaddr (`RaiseAddressError` stores the faulting address unchanged), the
//! `last_exception_*` snapshot, `exception_raised_`, and clearing the
//! branch/pipeline flags.
//!
//! These symbols are internal to `PSXRecomp.Native` (declared in
//! `src/psx_cpu_exception.h`, called only from `psx_cpu_exception.cpp`), not
//! P/Invoked, so `include/psx_core.h`, `NativeInterop.cs` and `ABI_VERSION` are
//! unaffected. The export takes only `u32` values, performs no allocation,
//! dereferences no pointer, and contains no operation that can panic (shifts
//! are by constants below 32, the EPC subtraction wraps), so it is infallible
//! per `docs/development/rust-ffi-contract.md` §5 and returns its result
//! directly.

/// CAUSE.Excode, bits 6:2.
const CAUSE_EXCODE_MASK: u32 = 0x7C;
/// CAUSE.CE, bits 29:28.
const CAUSE_CE_MASK: u32 = 0x3000_0000;
/// CAUSE.BD, bit 31.
const CAUSE_BD: u32 = 0x8000_0000;
/// SR.BEV, bit 22.
const SR_BEV: u32 = 1 << 22;
/// Exception vector when SR.BEV = 1 (BIOS ROM).
const VECTOR_BEV1: u32 = 0xBFC0_0180;
/// Exception vector when SR.BEV = 0 (KSEG0 RAM).
const VECTOR_BEV0: u32 = 0x8000_0080;

/// The resolved exception: the values the caller writes back to COP0 and PC.
///
/// Mirrored field-for-field by `PSXExceptionResolution` in
/// `src/psx_cpu_exception.h`; a layout change there or here is an ABI break.
/// `in_delay_slot` is `0`/`1`, not `bool`, per the FFI contract.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ExceptionResolution {
    /// New EPC (cop0r14): the owning branch's address in a delay slot, else
    /// the faulting instruction's own address.
    pub epc: u32,
    /// New CAUSE (cop0r13): Excode, CE and BD replaced, every other bit
    /// (including the software IP bits 9:8) preserved.
    pub cause: u32,
    /// New SR (cop0r12) after the KU/IE 3-level stack push.
    pub sr: u32,
    /// Exception vector the caller loads into PC.
    pub vector: u32,
    /// `1` when the faulting instruction was in a branch delay slot (CAUSE.BD),
    /// else `0`.
    pub in_delay_slot: u32,
}

/// Resolves an exception from the current CPU inputs.
///
/// - `in_delay_slot`: nonzero when the faulting instruction is in a delay slot.
/// - `delay_slot_pc`: that delay slot's address (branch address + 4).
/// - `instr_addr`: the faulting instruction's address.
/// - `cause`/`sr`: the current CAUSE and SR.
/// - `excode`: the exception code; shifted into bits 6:2 unmasked, exactly as
///   the C++ it replaces (every caller passes a 5-bit code).
/// - `ce`: coprocessor number for CpU; only bits 1:0 are used. Non-CpU callers
///   pass `0`, which clears a stale CE left by an earlier CpU.
#[must_use]
pub const fn resolve(
    in_delay_slot: bool,
    delay_slot_pc: u32,
    instr_addr: u32,
    cause: u32,
    sr: u32,
    excode: u32,
    ce: u32,
) -> ExceptionResolution {
    let epc = if in_delay_slot { delay_slot_pc.wrapping_sub(4) } else { instr_addr };

    let mut new_cause = cause & !(CAUSE_EXCODE_MASK | CAUSE_CE_MASK | CAUSE_BD);
    new_cause |= (excode << 2) | ((ce & 3) << 28);
    if in_delay_slot {
        new_cause |= CAUSE_BD;
    }

    // KUo<-KUp, IEo<-IEp; KUp<-KUc, IEp<-IEc; KUc<-0, IEc<-0 (docs/cpu/cop0.md):
    // bits 3:0 move up to 5:2, bits 1:0 become 0, bits above 5 are untouched.
    let new_sr = (sr & !0x3F) | ((sr & 0x0F) << 2);

    let vector = if sr & SR_BEV != 0 { VECTOR_BEV1 } else { VECTOR_BEV0 };

    ExceptionResolution {
        epc,
        cause: new_cause,
        sr: new_sr,
        vector,
        in_delay_slot: in_delay_slot as u32,
    }
}

/// Returns `resolve(in_delay_slot != 0, ...)`. Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_exception_resolve(
    in_delay_slot: u32,
    delay_slot_pc: u32,
    instr_addr: u32,
    cause: u32,
    sr: u32,
    excode: u32,
    ce: u32,
) -> ExceptionResolution {
    resolve(in_delay_slot != 0, delay_slot_pc, instr_addr, cause, sr, excode, ce)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn normal(cause: u32, sr: u32, excode: u32, ce: u32) -> ExceptionResolution {
        resolve(false, 0, 0x1000, cause, sr, excode, ce)
    }

    #[test]
    fn epc_is_instruction_address_outside_delay_slot() {
        let r = resolve(false, 0x2004, 0x1000, 0, 0, 0x08, 0);
        assert_eq!(r.epc, 0x1000);
        assert_eq!(r.in_delay_slot, 0);
        assert_eq!(r.cause & CAUSE_BD, 0);
    }

    #[test]
    fn epc_is_branch_address_in_delay_slot() {
        let r = resolve(true, 0x2004, 0x2004, 0, 0, 0x08, 0);
        assert_eq!(r.epc, 0x2000);
        assert_eq!(r.in_delay_slot, 1);
        assert_eq!(r.cause & CAUSE_BD, CAUSE_BD);
    }

    #[test]
    fn delay_slot_epc_wraps_like_the_cpp_unsigned_subtraction() {
        assert_eq!(resolve(true, 0, 0, 0, 0, 0, 0).epc, 0xFFFF_FFFC);
        assert_eq!(resolve(true, 3, 0, 0, 0, 0, 0).epc, 0xFFFF_FFFF);
    }

    #[test]
    fn exported_symbol_treats_any_nonzero_as_delay_slot() {
        assert_eq!(
            psx_cpu_exception_resolve(2, 0x10, 0x40, 0, 0, 0, 0),
            resolve(true, 0x10, 0x40, 0, 0, 0, 0)
        );
        assert_eq!(
            psx_cpu_exception_resolve(0, 0x10, 0x40, 0, 0, 0, 0),
            resolve(false, 0x10, 0x40, 0, 0, 0, 0)
        );
    }

    #[test]
    fn excode_lands_in_bits_6_to_2_for_every_5_bit_code() {
        for excode in 0..32u32 {
            let r = normal(0, 0, excode, 0);
            assert_eq!((r.cause & CAUSE_EXCODE_MASK) >> 2, excode);
            assert_eq!(r.cause & !CAUSE_EXCODE_MASK, 0, "excode {excode:#x}");
        }
    }

    #[test]
    fn excode_replaces_previous_excode_and_bd() {
        let r = normal(CAUSE_EXCODE_MASK | CAUSE_BD, 0, 0x04, 0);
        assert_eq!(r.cause, 0x04 << 2);
    }

    #[test]
    fn ce_uses_low_two_bits_only() {
        for ce in 0..8u32 {
            assert_eq!((normal(0, 0, 0x0B, ce).cause & CAUSE_CE_MASK) >> 28, ce & 3);
        }
    }

    #[test]
    fn non_cpu_exception_clears_stale_ce() {
        let r = normal(CAUSE_CE_MASK, 0, 0x08, 0);
        assert_eq!(r.cause & CAUSE_CE_MASK, 0);
    }

    #[test]
    fn software_ip_bits_and_other_cause_bits_are_preserved() {
        // IP1:IP0 (bits 9:8), IP7..IP2 (15:10) and every bit outside
        // Excode/CE/BD survive untouched.
        let keep = !(CAUSE_EXCODE_MASK | CAUSE_CE_MASK | CAUSE_BD);
        for ip in [0x100u32, 0x200, 0x300, 0xFF00] {
            assert_eq!(normal(ip, 0, 0x08, 0).cause, ip | (0x08 << 2));
        }
        assert_eq!(normal(u32::MAX, 0, 0, 0).cause, keep);
    }

    #[test]
    fn sr_stack_push_every_low_six_bit_pattern() {
        for low in 0..64u32 {
            let (kuc, iec, kup, iep) = (low & 1, (low >> 1) & 1, (low >> 2) & 1, (low >> 3) & 1);
            let expected = (kup << 4) | (iep << 5) | (kuc << 2) | (iec << 3);
            assert_eq!(normal(0, low, 0, 0).sr, expected, "sr low {low:#04x}");
        }
    }

    #[test]
    fn sr_stack_push_preserves_bits_above_5() {
        let upper = !0x3Fu32;
        assert_eq!(normal(0, upper, 0, 0).sr, upper);
        assert_eq!(normal(0, u32::MAX, 0, 0).sr, upper | 0x3C);
    }

    #[test]
    fn known_sr_stack_vectors_match_the_native_tests() {
        assert_eq!(normal(0, 0x3B, 0, 0).sr & 0x3F, 0x2C);
        assert_eq!(normal(0, 0x3F, 0, 0).sr & 0x3F, 0x3C);
        assert_eq!(normal(0, 0x3C, 0, 0).sr & 0x3F, 0x30);
    }

    #[test]
    fn bev_selects_the_vector() {
        assert_eq!(normal(0, 0, 0, 0).vector, VECTOR_BEV0);
        assert_eq!(normal(0, SR_BEV, 0, 0).vector, VECTOR_BEV1);
        assert_eq!(normal(0, !SR_BEV, 0, 0).vector, VECTOR_BEV0);
        assert_eq!(normal(0, u32::MAX, 0, 0).vector, VECTOR_BEV1);
        assert_eq!(VECTOR_BEV0, 0x8000_0080);
        assert_eq!(VECTOR_BEV1, 0xBFC0_0180);
    }
}
