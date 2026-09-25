//! `PSXCpu` unaligned word loads/stores (`LWL`/`LWR`/`SWL`/`SWR`), migrated
//! from the C++ `PSXCpu::ExecLwl`/`ExecLwr`/`ExecSwl`/`ExecSwr` (Issue #528).
//!
//! Scope: only the aligned-base address and the little-endian byte merge.
//! The caller (`src/psx_cpu_unaligned.cpp`) keeps owning address translation,
//! the mapped check, the memory read/write, choosing the register value to
//! merge with (including the pending load-delay value) and the delayed
//! register write.
//!
//! PS1 is little-endian. With `n = addr & 3` and `mem` the aligned word at
//! `addr & !3`:
//!
//! | n | LWL result                 | LWR result                 |
//! |---|----------------------------|----------------------------|
//! | 0 | `reg & 0x00FFFFFF \| mem << 24` | `mem`                 |
//! | 1 | `reg & 0x0000FFFF \| mem << 16` | `reg & 0xFF000000 \| mem >> 8`  |
//! | 2 | `reg & 0x000000FF \| mem << 8`  | `reg & 0xFFFF0000 \| mem >> 16` |
//! | 3 | `mem`                      | `reg & 0xFFFFFF00 \| mem >> 24` |
//!
//! | n | SWL stored word            | SWR stored word            |
//! |---|----------------------------|----------------------------|
//! | 0 | `mem & 0xFFFFFF00 \| reg >> 24` | `reg`                 |
//! | 1 | `mem & 0xFFFF0000 \| reg >> 16` | `mem & 0x000000FF \| reg << 8`  |
//! | 2 | `mem & 0xFF000000 \| reg >> 8`  | `mem & 0x0000FFFF \| reg << 16` |
//! | 3 | `reg`                      | `mem & 0x00FFFFFF \| reg << 24` |
//!
//! These symbols are internal to `PSXRecomp.Native` (declared in
//! `src/psx_cpu_unaligned.h`), not P/Invoked, so `include/psx_core.h`,
//! `NativeInterop.cs` and `ABI_VERSION` are unaffected. Every export takes
//! only `u32` values and returns a `u32`; shifts are by `(n * 8) <= 24`, so
//! nothing can panic, allocate or dereference a pointer. Every export is
//! infallible per `docs/development/rust-ffi-contract.md` §5.

/// Aligned word address containing `addr` (`addr & !3`).
#[must_use]
pub const fn aligned_base(addr: u32) -> u32 {
    addr & !3
}

const fn byte_shift(addr: u32) -> u32 {
    (addr & 3) * 8
}

/// `LWL`: loads bytes `addr` down to the aligned base into the high end of `reg`.
#[must_use]
pub const fn lwl(addr: u32, reg: u32, mem: u32) -> u32 {
    let s = byte_shift(addr);
    (reg & (0x00FF_FFFF >> s)) | (mem << (24 - s))
}

/// `LWR`: loads bytes `addr` up to the aligned word's end into the low end of `reg`.
#[must_use]
pub const fn lwr(addr: u32, reg: u32, mem: u32) -> u32 {
    let s = byte_shift(addr);
    (reg & !(0xFFFF_FFFF >> s)) | (mem >> s)
}

/// `SWL`: the aligned word after storing the high bytes of `reg` at `addr` downward.
#[must_use]
pub const fn swl(addr: u32, reg: u32, mem: u32) -> u32 {
    let s = byte_shift(addr);
    (mem & !(0xFFFF_FFFF >> (24 - s))) | (reg >> (24 - s))
}

/// `SWR`: the aligned word after storing the low bytes of `reg` at `addr` upward.
#[must_use]
pub const fn swr(addr: u32, reg: u32, mem: u32) -> u32 {
    let s = byte_shift(addr);
    (mem & !(0xFFFF_FFFF << s)) | (reg << s)
}

/// Returns `aligned_base(addr)`. Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_unaligned_base(addr: u32) -> u32 {
    aligned_base(addr)
}

/// Returns `lwl(addr, reg, mem)`. Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_unaligned_lwl(addr: u32, reg: u32, mem: u32) -> u32 {
    lwl(addr, reg, mem)
}

/// Returns `lwr(addr, reg, mem)`. Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_unaligned_lwr(addr: u32, reg: u32, mem: u32) -> u32 {
    lwr(addr, reg, mem)
}

/// Returns `swl(addr, reg, mem)`. Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_unaligned_swl(addr: u32, reg: u32, mem: u32) -> u32 {
    swl(addr, reg, mem)
}

/// Returns `swr(addr, reg, mem)`. Infallible, no `unsafe`.
#[no_mangle]
pub extern "C" fn psx_cpu_unaligned_swr(addr: u32, reg: u32, mem: u32) -> u32 {
    swr(addr, reg, mem)
}

#[cfg(test)]
mod tests {
    use super::*;

    // Memory bytes at 0x1000..0x1004 are 11 22 33 44 (little-endian word).
    const MEM: u32 = 0x4433_2211;
    const REG: u32 = 0xAABB_CCDD;

    #[test]
    fn lwl_all_offsets() {
        assert_eq!(psx_cpu_unaligned_lwl(0x1000, REG, MEM), 0x11BB_CCDD);
        assert_eq!(psx_cpu_unaligned_lwl(0x1001, REG, MEM), 0x2211_CCDD);
        assert_eq!(psx_cpu_unaligned_lwl(0x1002, REG, MEM), 0x3322_11DD);
        assert_eq!(psx_cpu_unaligned_lwl(0x1003, REG, MEM), 0x4433_2211);
    }

    #[test]
    fn lwr_all_offsets() {
        assert_eq!(psx_cpu_unaligned_lwr(0x1000, REG, MEM), 0x4433_2211);
        assert_eq!(psx_cpu_unaligned_lwr(0x1001, REG, MEM), 0xAA44_3322);
        assert_eq!(psx_cpu_unaligned_lwr(0x1002, REG, MEM), 0xAABB_4433);
        assert_eq!(psx_cpu_unaligned_lwr(0x1003, REG, MEM), 0xAABB_CC44);
    }

    #[test]
    fn swl_all_offsets() {
        assert_eq!(psx_cpu_unaligned_swl(0x1000, REG, MEM), 0x4433_22AA);
        assert_eq!(psx_cpu_unaligned_swl(0x1001, REG, MEM), 0x4433_AABB);
        assert_eq!(psx_cpu_unaligned_swl(0x1002, REG, MEM), 0x44AA_BBCC);
        assert_eq!(psx_cpu_unaligned_swl(0x1003, REG, MEM), 0xAABB_CCDD);
    }

    #[test]
    fn swr_all_offsets() {
        assert_eq!(psx_cpu_unaligned_swr(0x1000, REG, MEM), 0xAABB_CCDD);
        assert_eq!(psx_cpu_unaligned_swr(0x1001, REG, MEM), 0xBBCC_DD11);
        assert_eq!(psx_cpu_unaligned_swr(0x1002, REG, MEM), 0xCCDD_2211);
        assert_eq!(psx_cpu_unaligned_swr(0x1003, REG, MEM), 0xDD33_2211);
    }

    #[test]
    fn aligned_base_clears_low_bits() {
        for n in 0..4 {
            assert_eq!(psx_cpu_unaligned_base(0x8000_1000 + n), 0x8000_1000);
        }
        assert_eq!(psx_cpu_unaligned_base(0xFFFF_FFFF), 0xFFFF_FFFC);
    }

    /// Byte-level reference: the value any unaligned word at `addr` reads as,
    /// given a little-endian byte buffer starting at address 0.
    fn read_le(bytes: &[u8], addr: usize) -> u32 {
        u32::from_le_bytes([bytes[addr], bytes[addr + 1], bytes[addr + 2], bytes[addr + 3]])
    }

    fn word(bytes: &[u8], addr: u32) -> u32 {
        read_le(bytes, aligned_base(addr) as usize)
    }

    #[test]
    fn lwl_plus3_lwr_reconstructs_every_unaligned_word() {
        let bytes: [u8; 12] = [0x10, 0x21, 0x32, 0x43, 0x54, 0x65, 0x76, 0x87, 0x98, 0xA9, 0xBA, 0xCB];
        for addr in 0u32..=4 {
            for reg in [0u32, 0xFFFF_FFFF, REG] {
                let r = lwr(addr, reg, word(&bytes, addr));
                let r = lwl(addr + 3, r, word(&bytes, addr + 3));
                assert_eq!(r, read_le(&bytes, addr as usize), "LWR/LWL addr={addr}");
                // Order does not matter: each touches disjoint bytes.
                let l = lwl(addr + 3, reg, word(&bytes, addr + 3));
                let l = lwr(addr, l, word(&bytes, addr));
                assert_eq!(l, read_le(&bytes, addr as usize), "LWL/LWR addr={addr}");
            }
        }
    }

    #[test]
    fn swl_plus3_swr_stores_every_unaligned_word_and_preserves_neighbours() {
        for addr in 0u32..=4 {
            let mut bytes: [u8; 12] = [0xEE; 12];
            let w0 = aligned_base(addr) as usize;
            let v = swr(addr, REG, word(&bytes, addr));
            bytes[w0..w0 + 4].copy_from_slice(&v.to_le_bytes());
            let w1 = aligned_base(addr + 3) as usize;
            let v = swl(addr + 3, REG, word(&bytes, addr + 3));
            bytes[w1..w1 + 4].copy_from_slice(&v.to_le_bytes());
            assert_eq!(read_le(&bytes, addr as usize), REG, "addr={addr}");
            for (i, b) in bytes.iter().enumerate() {
                if i < addr as usize || i >= addr as usize + 4 {
                    assert_eq!(*b, 0xEE, "addr={addr} byte {i} clobbered");
                }
            }
        }
    }
}
