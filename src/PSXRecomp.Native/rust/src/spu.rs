//! Minimal PS1 SPU register-store model (Issue #445).
//!
//! This module deliberately models only the guest-visible 0x1F801C00-
//! 0x1F801DFF register window. It does not synthesize audio, decode ADPCM,
//! run ADSR/reverb, move sound RAM, or raise IRQ9. Every halfword in the
//! 512-byte window has deterministic zero-at-reset storage so BIOS/game
//! initialization writes are observable instead of falling through to the
//! generic flat hardware-register buffer.
//!
//! The state is owned inline by `PsxMemory`, exactly like SIO0: the guest CPU
//! reaches it through the production `PSXMemory` read/write path, while the
//! managed `MemoryBus` forwards its SPU route to those same native memory
//! accessors. There is therefore one register-semantics SSOT and no new public
//! C ABI surface.
//!
//! Canonical SPU registers are 16-bit. Byte accesses update/read one lane of
//! the containing halfword, and aligned 32-bit accesses combine two adjacent
//! halfwords in little-endian order. Misaligned 16/32-bit accesses are defined
//! as zero/no-op here; the R3000A normally raises the address exception before
//! such an access reaches memory.

/// First byte of the SPU register window.
pub const SPU_BASE: u32 = 0x1F80_1C00;
/// Exclusive end of the SPU register window.
pub const SPU_END: u32 = 0x1F80_1E00;
/// Size of the guest-visible SPU register window in bytes.
pub const SPU_SIZE: u32 = SPU_END - SPU_BASE;
const REGISTER_COUNT: usize = (SPU_SIZE as usize) / 2;

/// Rust-owned storage for the 256 guest-visible 16-bit SPU registers.
pub(crate) struct SpuState {
    registers: [u16; REGISTER_COUNT],
}

impl SpuState {
    /// Returns deterministic PS1 power-on state for the register-only model.
    pub(crate) const fn power_on() -> Self {
        Self {
            registers: [0; REGISTER_COUNT],
        }
    }

    /// Restores every modeled register to its deterministic reset value.
    pub(crate) fn reset(&mut self) {
        self.registers.fill(0);
    }
}

/// Returns true when `address` lies anywhere inside the SPU register window.
#[must_use]
pub const fn is_spu_register(address: u32) -> bool {
    address >= SPU_BASE && address < SPU_END
}

fn register_index(address: u32) -> Option<usize> {
    if !is_spu_register(address) {
        return None;
    }
    Some(((address - SPU_BASE) >> 1) as usize)
}

/// Reads one byte from the SPU register store. Outside-window reads return 0.
pub(crate) fn read8(state: &SpuState, address: u32) -> u8 {
    let Some(index) = register_index(address) else {
        return 0;
    };
    let shift = ((address - SPU_BASE) & 1) * 8;
    ((state.registers[index] >> shift) & 0xFF) as u8
}

/// Writes one byte to the SPU register store. Outside-window writes are ignored.
pub(crate) fn write8(state: &mut SpuState, address: u32, value: u8) {
    let Some(index) = register_index(address) else {
        return;
    };
    let shift = ((address - SPU_BASE) & 1) * 8;
    let mask = !(0xFFu16 << shift);
    state.registers[index] =
        (state.registers[index] & mask) | (u16::from(value) << shift);
}

/// Reads one aligned 16-bit SPU register. Unsupported/misaligned reads return 0.
pub(crate) fn read16(state: &SpuState, address: u32) -> u16 {
    if (address & 1) != 0 {
        return 0;
    }
    let Some(index) = register_index(address) else {
        return 0;
    };
    state.registers[index]
}

/// Writes one aligned 16-bit SPU register. Unsupported/misaligned writes are ignored.
pub(crate) fn write16(state: &mut SpuState, address: u32, value: u16) {
    if (address & 1) != 0 {
        return;
    }
    let Some(index) = register_index(address) else {
        return;
    };
    state.registers[index] = value;
}

/// Reads two adjacent aligned 16-bit SPU registers as one little-endian word.
pub(crate) fn read32(state: &SpuState, address: u32) -> u32 {
    if (address & 3) != 0 || address > SPU_END - 4 {
        return 0;
    }
    u32::from(read16(state, address)) | (u32::from(read16(state, address + 2)) << 16)
}

/// Writes one little-endian word into two adjacent aligned SPU registers.
pub(crate) fn write32(state: &mut SpuState, address: u32, value: u32) {
    if (address & 3) != 0 || address > SPU_END - 4 {
        return;
    }
    write16(state, address, value as u16);
    write16(state, address + 2, (value >> 16) as u16);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn power_on_state_is_zero_across_the_window() {
        let state = SpuState::power_on();
        for offset in (0..SPU_SIZE).step_by(2) {
            assert_eq!(read16(&state, SPU_BASE + offset), 0);
        }
    }

    #[test]
    fn halfword_registers_round_trip_at_window_boundaries() {
        let mut state = SpuState::power_on();
        write16(&mut state, SPU_BASE, 0x1234);
        write16(&mut state, SPU_END - 2, 0xABCD);

        assert_eq!(read16(&state, SPU_BASE), 0x1234);
        assert_eq!(read16(&state, SPU_END - 2), 0xABCD);
        assert_eq!(read16(&state, SPU_BASE - 2), 0);
        assert_eq!(read16(&state, SPU_END), 0);
    }

    #[test]
    fn byte_accesses_update_only_the_selected_lane() {
        let mut state = SpuState::power_on();
        write16(&mut state, SPU_BASE + 0x180, 0x1234);

        write8(&mut state, SPU_BASE + 0x180, 0xAA);
        assert_eq!(read16(&state, SPU_BASE + 0x180), 0x12AA);

        write8(&mut state, SPU_BASE + 0x181, 0xBB);
        assert_eq!(read16(&state, SPU_BASE + 0x180), 0xBBAA);
        assert_eq!(read8(&state, SPU_BASE + 0x180), 0xAA);
        assert_eq!(read8(&state, SPU_BASE + 0x181), 0xBB);
    }

    #[test]
    fn word_access_combines_two_halfwords_little_endian() {
        let mut state = SpuState::power_on();
        write32(&mut state, SPU_BASE + 0x180, 0xAABB_CCDD);

        assert_eq!(read16(&state, SPU_BASE + 0x180), 0xCCDD);
        assert_eq!(read16(&state, SPU_BASE + 0x182), 0xAABB);
        assert_eq!(read32(&state, SPU_BASE + 0x180), 0xAABB_CCDD);
    }

    #[test]
    fn reset_clears_register_storage() {
        let mut state = SpuState::power_on();
        write16(&mut state, SPU_BASE + 0x180, 0x1111);
        write16(&mut state, SPU_BASE + 0x1AA, 0x2222);
        write16(&mut state, SPU_END - 2, 0x3333);

        state.reset();

        assert_eq!(read16(&state, SPU_BASE + 0x180), 0);
        assert_eq!(read16(&state, SPU_BASE + 0x1AA), 0);
        assert_eq!(read16(&state, SPU_END - 2), 0);
    }

    #[test]
    fn misaligned_multi_byte_access_is_explicitly_zero_or_noop() {
        let mut state = SpuState::power_on();
        write16(&mut state, SPU_BASE, 0xCAFE);

        write16(&mut state, SPU_BASE + 1, 0xFFFF);
        write32(&mut state, SPU_BASE + 2, 0xFFFF_FFFF);

        assert_eq!(read16(&state, SPU_BASE + 1), 0);
        assert_eq!(read32(&state, SPU_BASE + 2), 0);
        assert_eq!(read16(&state, SPU_BASE), 0xCAFE);
    }
}
