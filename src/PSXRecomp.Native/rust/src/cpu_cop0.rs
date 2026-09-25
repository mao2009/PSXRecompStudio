//! `PSXCpu` COP0 register bit transformations, migrated from the C++
//! `PSXCpu::ExecMtc0` (CAUSE write mask) and `PSXCpu::ExecRfe` (SR stack pop)
//! handlers (Issue #529).
//!
//! Scope: only the pure `u32 -> u32` computation of the new register value.
//! The caller (`src/psx_cpu_cop0.cpp`) keeps owning the COP0 register array
//! (`cop0_`), GPR reads, the `rd` range check, the plain full-value MTC0 write
//! to every register other than CAUSE, MFC0 and its load delay, and
//! SYSCALL/BREAK exception raising.
//!
//! These symbols are internal to `PSXRecomp.Native` (declared in
//! `src/psx_cpu_cop0.h`, called only from `psx_cpu_cop0.cpp`), not P/Invoked,
//! so `include/psx_core.h`, `NativeInterop.cs` and `ABI_VERSION` are
//! unaffected. Every export takes only `u32` values, performs no allocation,
//! dereferences no pointer, and contains only masks and shifts by constants,
//! which cannot panic. So every export is infallible per
//! `docs/development/rust-ffi-contract.md` §5 and returns its result directly.

/// CAUSE bits writable by `MTC0`: IP[1:0], the software interrupt-pending
/// bits 8-9 (`docs/cpu/cop0.md`).
const CAUSE_SW_IP_MASK: u32 = 0x300;

/// `MTC0 rt, CAUSE` (`rd == 13`): the new CAUSE value. Bits 8-9 come from
/// `written`; every other bit (BD, Excode, hardware IP[7:2], ...) is kept from
/// `cause`.
#[no_mangle]
pub extern "C" fn psx_cpu_cop0_write_cause(cause: u32, written: u32) -> u32 {
    (cause & !CAUSE_SW_IP_MASK) | (written & CAUSE_SW_IP_MASK)
}

/// `RFE`: the new SR value after popping the 3-level KU/IE stack
/// (`KUc<-KUp, IEc<-IEp, KUp<-KUo, IEp<-IEo`). KUo/IEo (bits 4-5) and every
/// bit above 5 are left unchanged, matching PSX hardware (`docs/cpu/cop0.md`,
/// ADR-005).
#[no_mangle]
pub extern "C" fn psx_cpu_cop0_rfe(sr: u32) -> u32 {
    (sr & !0x0F) | ((sr >> 2) & 0x0F)
}

#[cfg(test)]
mod tests {
    use super::*;

    const PATTERNS: [u32; 8] = [
        0,
        u32::MAX,
        0xAAAA_AAAA,
        0x5555_5555,
        0x0000_0300,
        0xFFFF_FCFF,
        0x8000_047C,
        0x1234_5678,
    ];

    /// The pre-migration C++ `ExecMtc0` CAUSE branch, verbatim.
    fn cause_reference(cause: u32, written: u32) -> u32 {
        let ip = written & 0x300;
        (cause & !0x300u32) | ip
    }

    /// The pre-migration C++ `ExecRfe`, verbatim.
    fn rfe_reference(mut sr: u32) -> u32 {
        let kup = (sr >> 2) & 1;
        let iep = (sr >> 3) & 1;
        let kuo = (sr >> 4) & 1;
        let ieo = (sr >> 5) & 1;
        sr &= !0x0Fu32;
        sr |= kup | (iep << 1) | (kuo << 2) | (ieo << 3);
        sr
    }

    #[test]
    fn write_cause_updates_only_software_ip() {
        assert_eq!(psx_cpu_cop0_write_cause(0, 0), 0);
        assert_eq!(psx_cpu_cop0_write_cause(0, u32::MAX), 0x300);
        assert_eq!(psx_cpu_cop0_write_cause(u32::MAX, 0), 0xFFFF_FCFF);
        assert_eq!(psx_cpu_cop0_write_cause(u32::MAX, u32::MAX), u32::MAX);
        // Excode (bits 2-6), BD (31) and IP2 (10) preserved; IP[1:0] replaced.
        assert_eq!(psx_cpu_cop0_write_cause(0x8000_047C, 0x100), 0x8000_057C);
        assert_eq!(psx_cpu_cop0_write_cause(0x8000_077C, 0x37C), 0x8000_077C);
        assert_eq!(psx_cpu_cop0_write_cause(0x8000_077C, 0x200), 0x8000_067C);
        // Written bits outside 8-9 are ignored.
        assert_eq!(psx_cpu_cop0_write_cause(0, 0xFFFF_FCFF), 0);
        assert_eq!(psx_cpu_cop0_write_cause(0xAAAA_AAAA, 0x5555_5555), 0xAAAA_A9AA);
    }

    #[test]
    fn write_cause_matches_cpp_reference() {
        for cause in PATTERNS {
            for written in PATTERNS {
                assert_eq!(
                    psx_cpu_cop0_write_cause(cause, written),
                    cause_reference(cause, written),
                    "cause={cause:#x} written={written:#x}"
                );
            }
        }
    }

    #[test]
    fn rfe_pops_stack_and_keeps_old_level() {
        assert_eq!(psx_cpu_cop0_rfe(0), 0);
        assert_eq!(psx_cpu_cop0_rfe(u32::MAX), u32::MAX);
        // Post-exception 0x3C (KUp=IEp=KUo=IEo=1) -> 0x3F.
        assert_eq!(psx_cpu_cop0_rfe(0x3C), 0x3F);
        // KUc/IEc discarded; KUp/IEp come from KUo/IEo; KUo/IEo unchanged.
        assert_eq!(psx_cpu_cop0_rfe(0x03), 0x00);
        assert_eq!(psx_cpu_cop0_rfe(0x0C), 0x03);
        assert_eq!(psx_cpu_cop0_rfe(0x30), 0x3C);
        assert_eq!(psx_cpu_cop0_rfe(0x14), 0x15); // KUp=1, KUo=1
        assert_eq!(psx_cpu_cop0_rfe(0x28), 0x2A); // IEp=1, IEo=1
        // Bits above 5 (IM, BEV, CU, ...) preserved.
        assert_eq!(psx_cpu_cop0_rfe(0xFFFF_FFC0), 0xFFFF_FFC0);
        assert_eq!(psx_cpu_cop0_rfe(0x1040_040F), 0x1040_0403);
        assert_eq!(psx_cpu_cop0_rfe(0xAAAA_AAAA), 0xAAAA_AAAA);
        assert_eq!(psx_cpu_cop0_rfe(0x5555_5555), 0x5555_5555);
    }

    #[test]
    fn rfe_matches_cpp_reference() {
        // Every combination of the six stack bits, with low and high upper bits.
        for low in 0..0x40u32 {
            for high in [0, 0xFFFF_FFC0, 0x1040_0400] {
                let sr = high | low;
                assert_eq!(psx_cpu_cop0_rfe(sr), rfe_reference(sr), "sr={sr:#x}");
            }
        }
        for sr in PATTERNS {
            assert_eq!(psx_cpu_cop0_rfe(sr), rfe_reference(sr), "sr={sr:#x}");
        }
    }
}
