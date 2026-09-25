//! `PSXCpu` branch / jump decision and target arithmetic, migrated from the
//! C++ `PSXCpu::ExecBeq`..`ExecJalr` handlers (Issue #526).
//!
//! Scope: only the pure computation — whether a branch is taken, where it
//! goes, and the `PC + 8` return address. The caller
//! (`src/psx_cpu_control.cpp`) keeps owning GPR reads, the `$ra`/`rd` link
//! write (`SetGPR`), `SetPendingBranch` (branch-delay state, ADR-004/005), and
//! the real `pc_`. The linking forms reuse the non-linking export
//! (`BLTZAL` = [`psx_cpu_control_bltz`], `BGEZAL` = [`psx_cpu_control_bgez`],
//! `JAL` = [`psx_cpu_control_j`], `JALR` = [`psx_cpu_control_jr`]); the
//! caller writes [`ControlResult::link`] for those, always, whether or not the
//! branch is taken.
//!
//! Every export receives register *values*, never register numbers, so the
//! caller reads `rs` before writing the link: `JALR rd, rs` with `rd == rs`
//! jumps to the old `rs`, and `BLTZAL`/`BGEZAL` on `$ra` test the old `$ra`.
//!
//! These symbols are internal to `PSXRecomp.Native` (declared in
//! `src/psx_cpu_control.h`, called only from `psx_cpu_control.cpp`), not
//! P/Invoked, so `include/psx_core.h`, `NativeInterop.cs` and `ABI_VERSION` are
//! unaffected. Every export takes only integer values, performs no
//! allocation, dereferences no pointer, retains no state, and contains no
//! operation that can panic (all additions are `wrapping_*`; `<< 2` by a
//! constant amount never panics), so every export is infallible per
//! `docs/development/rust-ffi-contract.md` §5 and returns its result directly.

/// Outcome of one branch/jump instruction.
///
/// Mirrored field-for-field by `PSXControlResult` in `src/psx_cpu_control.h`;
/// a layout change there or here is an ABI break. `taken` is `0`/`1`, not
/// `bool`, per the FFI contract.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ControlResult {
    /// Destination address, valid whether or not the branch is taken.
    pub target: u32,
    /// `1` when control transfers to `target` after the delay slot, else `0`.
    pub taken: u32,
    /// Return address `PC + 8` (wrapping). Only the linking forms write it,
    /// and they write it even when the branch is not taken.
    pub link: u32,
}

/// Conditional-branch target: `PC + 4 + (sign_extend(offset) << 2)`, wrapping
/// in 32 bits.
#[must_use]
pub const fn branch_target(pc: u32, offset: i16) -> u32 {
    pc.wrapping_add(4).wrapping_add(((offset as i32) << 2) as u32)
}

/// `J`/`JAL` target: the upper 4 bits of `PC + 4` (the delay slot's region)
/// OR `index << 2`. The decoder passes the 26-bit `instr_index`; the value is
/// used unmasked, exactly as the C++ did.
#[must_use]
pub const fn jump_target(pc: u32, index: u32) -> u32 {
    (pc.wrapping_add(4) & 0xF000_0000) | (index << 2)
}

const fn result(pc: u32, target: u32, taken: bool) -> ControlResult {
    ControlResult { target, taken: taken as u32, link: pc.wrapping_add(8) }
}

/// `BEQ`: taken when `rs == rt`. Infallible.
#[no_mangle]
pub extern "C" fn psx_cpu_control_beq(pc: u32, rs: u32, rt: u32, offset: i16) -> ControlResult {
    result(pc, branch_target(pc, offset), rs == rt)
}

/// `BNE`: taken when `rs != rt`. Infallible.
#[no_mangle]
pub extern "C" fn psx_cpu_control_bne(pc: u32, rs: u32, rt: u32, offset: i16) -> ControlResult {
    result(pc, branch_target(pc, offset), rs != rt)
}

/// `BLEZ`: taken when `rs <= 0` as `i32`. Infallible.
#[no_mangle]
pub extern "C" fn psx_cpu_control_blez(pc: u32, rs: u32, offset: i16) -> ControlResult {
    result(pc, branch_target(pc, offset), rs as i32 <= 0)
}

/// `BGTZ`: taken when `rs > 0` as `i32`. Infallible.
#[no_mangle]
pub extern "C" fn psx_cpu_control_bgtz(pc: u32, rs: u32, offset: i16) -> ControlResult {
    result(pc, branch_target(pc, offset), rs as i32 > 0)
}

/// `BLTZ` and `BLTZAL`: taken when `rs < 0` as `i32`. Infallible.
#[no_mangle]
pub extern "C" fn psx_cpu_control_bltz(pc: u32, rs: u32, offset: i16) -> ControlResult {
    result(pc, branch_target(pc, offset), (rs as i32) < 0)
}

/// `BGEZ` and `BGEZAL`: taken when `rs >= 0` as `i32`. Infallible.
#[no_mangle]
pub extern "C" fn psx_cpu_control_bgez(pc: u32, rs: u32, offset: i16) -> ControlResult {
    result(pc, branch_target(pc, offset), rs as i32 >= 0)
}

/// `J` and `JAL`: always taken, to [`jump_target`]. Infallible.
#[no_mangle]
pub extern "C" fn psx_cpu_control_j(pc: u32, index: u32) -> ControlResult {
    result(pc, jump_target(pc, index), true)
}

/// `JR` and `JALR`: always taken, to the `rs` value as-is (no alignment
/// masking; a misaligned target faults at fetch). Infallible.
#[no_mangle]
pub extern "C" fn psx_cpu_control_jr(pc: u32, rs: u32) -> ControlResult {
    result(pc, rs, true)
}

#[cfg(test)]
mod tests {
    use super::*;

    const MIN: u32 = 0x8000_0000; // i32::MIN bit pattern
    const MAX: u32 = 0x7FFF_FFFF; // i32::MAX bit pattern
    const NEG1: u32 = u32::MAX;

    #[test]
    fn branch_target_sign_extends_offset() {
        assert_eq!(branch_target(0, 2), 12);
        assert_eq!(branch_target(0x1000, 0), 0x1004);
        assert_eq!(branch_target(0x1000, -1), 0x1000); // branch to self
        assert_eq!(branch_target(0x1000, -2), 0x0FFC);
        assert_eq!(branch_target(0x8000_0000, 0x7FFF), 0x8002_0000);
        assert_eq!(branch_target(0x8002_0000, i16::MIN), 0x8000_0004);
    }

    #[test]
    fn branch_target_wraps_32_bits() {
        assert_eq!(branch_target(0xFFFF_FFFC, 0), 0);
        assert_eq!(branch_target(0xFFFF_FFFC, 1), 4);
        assert_eq!(branch_target(0, -2), 0xFFFF_FFFC);
        assert_eq!(branch_target(0, i16::MIN), 0xFFFE_0004);
    }

    #[test]
    fn branch_target_matches_wide_reference() {
        for pc in [0u32, 4, 0x1000, 0x7FFF_FFFC, MIN, 0xBFC0_0000, 0xFFFF_FFFC] {
            for off in [i16::MIN, -1, 0, 1, i16::MAX] {
                let wide = (pc as i64 + 4 + (off as i64) * 4) as u32; // truncates mod 2^32
                assert_eq!(branch_target(pc, off), wide, "pc={pc:#x} off={off}");
            }
        }
    }

    #[test]
    fn jump_target_keeps_region_of_delay_slot() {
        assert_eq!(jump_target(0, 3), 12);
        assert_eq!(jump_target(0x8001_0000, 0x0000_0400), 0x8000_1000);
        assert_eq!(jump_target(0xBFC0_0000, 0x03FF_FFFF), 0xBFFF_FFFC);
        // The region comes from PC + 4, not PC.
        assert_eq!(jump_target(0x0FFF_FFFC, 1), 0x1000_0004);
        assert_eq!(jump_target(0xFFFF_FFFC, 1), 0x0000_0004); // PC + 4 wraps
    }

    #[test]
    fn beq_bne_compare_raw_bits() {
        assert_eq!(psx_cpu_control_beq(0, 10, 10, 2), ControlResult { target: 12, taken: 1, link: 8 });
        assert_eq!(psx_cpu_control_beq(0, 10, 20, 2), ControlResult { target: 12, taken: 0, link: 8 });
        assert_eq!(psx_cpu_control_bne(0, 10, 20, 2).taken, 1);
        assert_eq!(psx_cpu_control_bne(0, NEG1, NEG1, 2).taken, 0);
        assert_eq!(psx_cpu_control_beq(0, MIN, MAX, 2).taken, 0);
        assert_eq!(psx_cpu_control_bne(0, MIN, MAX, 2).taken, 1);
    }

    #[test]
    fn zero_compares_are_signed() {
        // (value, <= 0, > 0, < 0, >= 0)
        for (v, lez, gtz, ltz, gez) in [
            (0u32, 1, 0, 0, 1),
            (1, 0, 1, 0, 1),
            (MAX, 0, 1, 0, 1),
            (MIN, 1, 0, 1, 0),
            (NEG1, 1, 0, 1, 0),
        ] {
            assert_eq!(psx_cpu_control_blez(0, v, 2).taken, lez, "blez {v:#x}");
            assert_eq!(psx_cpu_control_bgtz(0, v, 2).taken, gtz, "bgtz {v:#x}");
            assert_eq!(psx_cpu_control_bltz(0, v, 2).taken, ltz, "bltz {v:#x}");
            assert_eq!(psx_cpu_control_bgez(0, v, 2).taken, gez, "bgez {v:#x}");
        }
    }

    #[test]
    fn link_is_pc_plus_8_whether_or_not_taken() {
        assert_eq!(psx_cpu_control_bltz(0x1000, 5, 2).link, 0x1008); // not taken
        assert_eq!(psx_cpu_control_bltz(0x1000, NEG1, 2).link, 0x1008); // taken
        assert_eq!(psx_cpu_control_bgez(0x1000, NEG1, 2).link, 0x1008); // not taken
        assert_eq!(psx_cpu_control_j(0x1000, 0).link, 0x1008);
        assert_eq!(psx_cpu_control_jr(0x1000, 0x8000_1234).link, 0x1008);
        assert_eq!(psx_cpu_control_jr(0xFFFF_FFF8, 0).link, 0); // wraps
    }

    #[test]
    fn jumps_are_always_taken() {
        assert_eq!(psx_cpu_control_j(0, 3), ControlResult { target: 12, taken: 1, link: 8 });
        // JALR-001 (docs/cpu/test-specification.md).
        assert_eq!(
            psx_cpu_control_jr(0x1000, 0x8000_1234),
            ControlResult { target: 0x8000_1234, taken: 1, link: 0x1008 }
        );
        // Target is not masked or aligned.
        assert_eq!(psx_cpu_control_jr(0, 0x8000_0003).target, 0x8000_0003);
    }
}
