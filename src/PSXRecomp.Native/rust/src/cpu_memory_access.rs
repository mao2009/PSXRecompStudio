//! `PSXCpu` aligned load/store address semantics and virtual -> physical
//! translation, migrated from the C++ `PSXCpu::TranslateAddress`/`IsMapped`
//! and `ExecLb`..`ExecSw` (Issue #527).
//!
//! Scope: only the pure computation — effective address (base + sign-extended
//! 16-bit offset, 32-bit wrapping), the LH/LHU/SH 2-byte and LW/SW 4-byte
//! alignment check, KUSEG/KSEG0/KSEG1 translation with KSEG2 and above
//! unmapped, and the LB/LBU/LH/LHU sign/zero extension of a loaded value. The
//! caller (`src/psx_cpu_memory_access.cpp`) keeps owning GPR reads,
//! `PSXMemory::Read*`/`Write*`, `WriteRegDelayed` (so an unmapped load still
//! queues a zero), silently dropping an unmapped store, and raising AdEL/AdES
//! (CAUSE Excode 0x04/0x05) with BadVaddr on [`MEM_ACCESS_MISALIGNED`].
//!
//! These symbols are internal to `PSXRecomp.Native` (declared in
//! `src/psx_cpu_memory_access.h`), not P/Invoked, so `include/psx_core.h`,
//! `NativeInterop.cs` and `ABI_VERSION` are unaffected. Every export takes and
//! returns plain integers or a `#[repr(C)]` POD by value, performs no
//! allocation, dereferences no pointer, uses no `unsafe`, retains no state,
//! and contains no operation that can panic (`wrapping_add`, masks, and
//! `as` casts only), so every export is infallible per
//! `docs/development/rust-ffi-contract.md` §5 and returns its result directly.

/// Physical-address sentinel for an unmapped virtual address. Never a real
/// physical address in this model (KUSEG tops out at `0x7FFF_FFFF` and
/// KSEG0/KSEG1 at `0x1FFF_FFFF`).
pub const UNMAPPED_PHYSICAL: u32 = 0xFFFF_FFFF;

/// [`MemAccess::status`]: aligned and mapped; the caller performs the access.
pub const MEM_ACCESS_OK: u32 = 0;
/// [`MemAccess::status`]: misaligned for its width; the caller raises
/// AdEL (load) / AdES (store) with `vaddr` as BadVaddr and does not access
/// memory. Takes precedence over [`MEM_ACCESS_UNMAPPED`].
pub const MEM_ACCESS_MISALIGNED: u32 = 1;
/// [`MemAccess::status`]: aligned but unmapped; a load queues zero, a store
/// is dropped. Neither raises an exception.
pub const MEM_ACCESS_UNMAPPED: u32 = 2;

/// Classification of one aligned load/store.
///
/// Mirrored field-for-field by `PSXMemAccess` in
/// `src/psx_cpu_memory_access.h`; a layout change there or here is an ABI
/// break.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct MemAccess {
    /// Effective virtual address, `base + sign_extend(offset)` wrapping.
    pub vaddr: u32,
    /// [`translate`]`(vaddr)`; [`UNMAPPED_PHYSICAL`] when unmapped. Only
    /// meaningful to the caller when `status == MEM_ACCESS_OK`.
    pub phys: u32,
    /// One of `MEM_ACCESS_OK`, `MEM_ACCESS_MISALIGNED`, `MEM_ACCESS_UNMAPPED`.
    pub status: u32,
}

/// KUSEG (`0x0000_0000..=0x7FFF_FFFF`) maps directly; KSEG0/KSEG1
/// (`0x8000_0000..=0xBFFF_FFFF`) map to `virt & 0x1FFF_FFFF`; KSEG2 and above
/// are unmapped in this model.
#[must_use]
pub const fn translate(virt: u32) -> u32 {
    if virt <= 0x7FFF_FFFF {
        virt
    } else if virt <= 0xBFFF_FFFF {
        virt & 0x1FFF_FFFF
    } else {
        UNMAPPED_PHYSICAL
    }
}

/// Classifies a `width`-byte access at `base + offset`. `width` is 1, 2 or 4;
/// the alignment mask is `width - 1`, so width 1 is never misaligned.
#[must_use]
pub const fn classify(base: u32, offset: i16, width: u32) -> MemAccess {
    let vaddr = base.wrapping_add(offset as i32 as u32);
    let phys = translate(vaddr);
    let status = if vaddr & width.wrapping_sub(1) != 0 {
        MEM_ACCESS_MISALIGNED
    } else if phys == UNMAPPED_PHYSICAL {
        MEM_ACCESS_UNMAPPED
    } else {
        MEM_ACCESS_OK
    };
    MemAccess { vaddr, phys, status }
}

/// Extends the low `width` bytes of a loaded `raw` value to 32 bits: sign
/// extension when `is_signed != 0` (LB/LH), zero extension otherwise
/// (LBU/LHU). Width 4 (LW) returns `raw` unchanged.
#[must_use]
pub const fn extend_load(raw: u32, width: u32, is_signed: u32) -> u32 {
    match (width, is_signed != 0) {
        (1, true) => raw as u8 as i8 as i32 as u32,
        (1, false) => raw as u8 as u32,
        (2, true) => raw as u16 as i16 as i32 as u32,
        (2, false) => raw as u16 as u32,
        _ => raw,
    }
}

/// Returns [`translate`]`(virt)`. Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_mem_translate(virt: u32) -> u32 {
    translate(virt)
}

/// Returns `1` when `phys` is not [`UNMAPPED_PHYSICAL`], else `0`.
/// Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_mem_is_mapped(phys: u32) -> u32 {
    (phys != UNMAPPED_PHYSICAL) as u32
}

/// Returns [`classify`]`(base, offset, width)`. Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_mem_classify(base: u32, offset: i16, width: u32) -> MemAccess {
    classify(base, offset, width)
}

/// Returns [`extend_load`]`(raw, width, is_signed)`. Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_mem_extend_load(raw: u32, width: u32, is_signed: u32) -> u32 {
    extend_load(raw, width, is_signed)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn access(vaddr: u32, phys: u32, status: u32) -> MemAccess {
        MemAccess { vaddr, phys, status }
    }

    #[test]
    fn translate_kuseg_is_identity() {
        for v in [0u32, 0x1000, 0x001F_FFFF, 0x1FC0_0000, 0x7FFF_FFFF] {
            assert_eq!(translate(v), v, "v = {v:#010X}");
        }
    }

    #[test]
    fn translate_kseg0_kseg1_masks_to_29_bits() {
        assert_eq!(translate(0x8000_0000), 0);
        assert_eq!(translate(0x801F_FFFF), 0x001F_FFFF);
        assert_eq!(translate(0x9FFF_FFFF), 0x1FFF_FFFF);
        assert_eq!(translate(0xA000_0000), 0);
        assert_eq!(translate(0xBF80_0000), 0x1F80_0000);
        assert_eq!(translate(0xBFC0_0000), 0x1FC0_0000);
        assert_eq!(translate(0xBFFF_FFFF), 0x1FFF_FFFF);
    }

    #[test]
    fn translate_kseg2_and_above_is_unmapped() {
        for v in [0xC000_0000u32, 0xFFFD_FFFF, 0xFFFE_0000, 0xFFFF_FFFF] {
            assert_eq!(translate(v), UNMAPPED_PHYSICAL, "v = {v:#010X}");
        }
        assert_eq!(psx_cpu_mem_translate(0xC000_0000), UNMAPPED_PHYSICAL);
    }

    #[test]
    fn is_mapped_only_rejects_the_sentinel() {
        assert_eq!(psx_cpu_mem_is_mapped(UNMAPPED_PHYSICAL), 0);
        assert_eq!(psx_cpu_mem_is_mapped(0), 1);
        assert_eq!(psx_cpu_mem_is_mapped(0x1FFF_FFFF), 1);
        assert_eq!(psx_cpu_mem_is_mapped(0x7FFF_FFFF), 1);
    }

    #[test]
    fn effective_address_sign_extends_and_wraps() {
        assert_eq!(classify(0x1000, 4, 4).vaddr, 0x1004);
        assert_eq!(classify(0x1000, -4, 4).vaddr, 0x0FFC);
        assert_eq!(classify(0x1000, i16::MIN, 1).vaddr, 0x1000u32.wrapping_sub(0x8000));
        assert_eq!(classify(0x1000, i16::MAX, 1).vaddr, 0x1000 + 0x7FFF);
        // Wraps below zero into KSEG2 (unmapped) and past 0xFFFFFFFF to KUSEG.
        assert_eq!(classify(0, -1, 1), access(0xFFFF_FFFF, UNMAPPED_PHYSICAL, MEM_ACCESS_UNMAPPED));
        assert_eq!(classify(0xFFFF_FFFF, 1, 1), access(0, 0, MEM_ACCESS_OK));
        assert_eq!(classify(0xFFFF_FFFC, 8, 4), access(4, 4, MEM_ACCESS_OK));
    }

    #[test]
    fn byte_access_is_never_misaligned() {
        for v in 0x1000u32..0x1004 {
            assert_eq!(psx_cpu_mem_classify(v, 0, 1), access(v, v, MEM_ACCESS_OK));
        }
    }

    #[test]
    fn halfword_requires_2_byte_alignment() {
        assert_eq!(classify(0x1000, 0, 2).status, MEM_ACCESS_OK);
        assert_eq!(classify(0x1000, 1, 2), access(0x1001, 0x1001, MEM_ACCESS_MISALIGNED));
        assert_eq!(classify(0x1000, 2, 2).status, MEM_ACCESS_OK);
        assert_eq!(classify(0x1000, 3, 2).status, MEM_ACCESS_MISALIGNED);
    }

    #[test]
    fn word_requires_4_byte_alignment() {
        assert_eq!(classify(0x1000, 0, 4).status, MEM_ACCESS_OK);
        for off in 1..4 {
            assert_eq!(classify(0x1000, off, 4).status, MEM_ACCESS_MISALIGNED, "off = {off}");
        }
        assert_eq!(classify(0x1000, 4, 4).status, MEM_ACCESS_OK);
    }

    #[test]
    fn misaligned_takes_precedence_over_unmapped() {
        assert_eq!(
            classify(0xC000_0000, 1, 2),
            access(0xC000_0001, UNMAPPED_PHYSICAL, MEM_ACCESS_MISALIGNED)
        );
        assert_eq!(classify(0xC000_0000, 2, 4).status, MEM_ACCESS_MISALIGNED);
    }

    #[test]
    fn aligned_unmapped_is_classified_unmapped() {
        for width in [1u32, 2, 4] {
            assert_eq!(
                classify(0xC000_0000, 0, width),
                access(0xC000_0000, UNMAPPED_PHYSICAL, MEM_ACCESS_UNMAPPED)
            );
        }
    }

    #[test]
    fn classify_translates_kseg_addresses() {
        assert_eq!(classify(0x8000_0000, 0, 4), access(0x8000_0000, 0, MEM_ACCESS_OK));
        assert_eq!(classify(0xA000_0000, 0, 4), access(0xA000_0000, 0, MEM_ACCESS_OK));
        assert_eq!(classify(0xBFC0_0000, 0, 1), access(0xBFC0_0000, 0x1FC0_0000, MEM_ACCESS_OK));
    }

    #[test]
    fn extend_load_per_opcode() {
        // LB / LBU
        assert_eq!(psx_cpu_mem_extend_load(0xEF, 1, 1), 0xFFFF_FFEF);
        assert_eq!(psx_cpu_mem_extend_load(0xEF, 1, 0), 0x0000_00EF);
        assert_eq!(extend_load(0x7F, 1, 1), 0x7F);
        // Only the low byte is significant.
        assert_eq!(extend_load(0xFFFF_FF12, 1, 0), 0x12);
        // LH / LHU
        assert_eq!(psx_cpu_mem_extend_load(0xABCD, 2, 1), 0xFFFF_ABCD);
        assert_eq!(psx_cpu_mem_extend_load(0xABCD, 2, 0), 0x0000_ABCD);
        assert_eq!(extend_load(0x7FFF, 2, 1), 0x7FFF);
        assert_eq!(extend_load(0xFFFF_1234, 2, 0), 0x1234);
        // LW is identity regardless of the sign flag.
        assert_eq!(extend_load(0x8765_4321, 4, 0), 0x8765_4321);
        assert_eq!(extend_load(0x8765_4321, 4, 1), 0x8765_4321);
    }
}
