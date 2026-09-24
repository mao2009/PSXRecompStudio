//! PS1 DMA controller registers (per-channel MADR/BCR/CHCR, DPCR, DICR),
//! migrated from the C++ `PSXDmaController` (Issue #488).
//!
//! Ownership: the state is a plain [`DmaState`] value that the C++ `PSXCore`
//! stores inline and passes by value. Rust allocates nothing, keeps no global
//! state, and takes no pointers, so every export here is infallible, contains
//! no `unsafe`, and has no operation that can panic (address arithmetic is
//! checked and channel access uses `get`). They return their result directly,
//! as permitted for infallible functions by
//! `docs/development/rust-ffi-contract.md` §5.
//!
//! These symbols are internal to `PSXRecomp.Native`: C++ (`src/psx_api.cpp`,
//! declared in `src/psx_dma.h`) calls them to implement the unchanged
//! `PSXCore_*Dma*` C ABI. They are not P/Invoked by managed code.
//!
//! No data is ever moved: no device backs a channel yet. [`psx_dma_tick`]
//! models only a started channel's *duration* and completion (Issue #442), so
//! a transfer finishes after a deterministic cycle count, clears its CHCR
//! start bits, and sets its DICR flag when that channel's enable bit is set.

/// Number of DMA channels (0..6).
pub const PSX_DMA_CHANNEL_COUNT: usize = 7;

/// Absolute address of channel 0's MADR; channel `n` starts at
/// `PSX_DMA_BASE + n * PSX_DMA_CHANNEL_STRIDE`.
pub const PSX_DMA_BASE: u32 = 0x1F80_1080;

/// Address distance between consecutive channels' register blocks.
pub const PSX_DMA_CHANNEL_STRIDE: u32 = 0x10;

/// Absolute address of DPCR (channel priority/enable, plain read/write).
pub const PSX_DMA_DPCR: u32 = 0x1F80_10F0;

/// Absolute address of DICR (DMA interrupt control).
pub const PSX_DMA_DICR: u32 = 0x1F80_10F4;

/// DPCR power-on value.
pub const PSX_DMA_DPCR_RESET: u32 = 0x0765_4321;

/// CPU cycles a modelled transfer spends per word. Not cycle-exact (Issue
/// #442): only a deterministic, non-zero duration is required.
pub const PSX_DMA_CYCLES_PER_WORD: u64 = 1;

/// CHCR bits 9-10: sync mode (0 manual/burst, 1 block, 2 linked list).
const CHCR_SYNC_SHIFT: u32 = 9;
/// CHCR bit 24: start/busy.
const CHCR_START_BUSY: u32 = 1 << 24;
/// CHCR bit 28: start/trigger (required to start sync mode 0).
const CHCR_START_TRIGGER: u32 = 1 << 28;

/// DICR bits 0-6: per-channel interrupt flags (write-1-to-clear).
const DICR_FLAGS_MASK: u32 = 0x0000_007F;
/// DICR bit 15: force IRQ.
const DICR_FORCE_IRQ: u32 = 1 << 15;
/// DICR bit 23: master interrupt enable.
const DICR_MASTER_EN: u32 = 1 << 23;
/// DICR bits 24-30: per-channel interrupt enables.
const DICR_ENABLES_MASK: u32 = 0x7F00_0000;
/// DICR bit 31: aggregate IRQ status (read-only, computed on read).
const DICR_IRQ_STATUS: u32 = 1 << 31;
/// DICR bits a write replaces.
const DICR_CONTROL_MASK: u32 = DICR_FORCE_IRQ | DICR_MASTER_EN | DICR_ENABLES_MASK;

/// One channel's register block.
///
/// Mirrored field-for-field by `PSXDmaChannelState` in `src/psx_dma.h`.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct DmaChannelState {
    /// MADR: base address.
    pub madr: u32,
    /// BCR: block control.
    pub bcr: u32,
    /// CHCR: channel control.
    pub chcr: u32,
}

/// DMA controller register state, owned by the C++ caller.
///
/// Mirrored field-for-field by `PSXDmaState` in `src/psx_dma.h`; a layout
/// change there or here is an ABI break.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct DmaState {
    /// Channels 0..6.
    pub channels: [DmaChannelState; PSX_DMA_CHANNEL_COUNT],
    /// DPCR.
    pub dpcr: u32,
    /// DICR as stored (bit 31 is computed on read, never stored).
    pub dicr: u32,
    /// Cycles left on each channel's in-flight transfer; 0 means the current
    /// start has not been costed yet. Not register-visible.
    pub remaining: [u32; PSX_DMA_CHANNEL_COUNT],
}

/// Returns the power-on state: channels zero, DPCR `0x07654321`, DICR zero.
/// Infallible.
#[no_mangle]
pub extern "C" fn psx_dma_reset() -> DmaState {
    DmaState {
        channels: [DmaChannelState::default(); PSX_DMA_CHANNEL_COUNT],
        dpcr: PSX_DMA_DPCR_RESET,
        dicr: 0,
        remaining: [0; PSX_DMA_CHANNEL_COUNT],
    }
}

/// Aggregate DMA IRQ line: master enable with any enabled flag, or force IRQ.
fn irq_line(dicr: u32) -> bool {
    let flags = dicr & DICR_FLAGS_MASK;
    let enables = (dicr & DICR_ENABLES_MASK) >> 24;
    let master = dicr & DICR_MASTER_EN != 0;
    (master && flags & enables != 0) || dicr & DICR_FORCE_IRQ != 0
}

/// Splits `address` into (channel index, offset within the channel block).
/// The index is unbounded; callers range-check it via `channels.get`.
fn decode_channel(address: u32) -> Option<(usize, u32)> {
    let rel = address.checked_sub(PSX_DMA_BASE)?;
    let channel = usize::try_from(rel / PSX_DMA_CHANNEL_STRIDE).ok()?;
    Some((channel, rel % PSX_DMA_CHANNEL_STRIDE))
}

/// Reads the register at `address`. DPCR as stored; DICR with bit 31 set to
/// the aggregate IRQ line; a channel's MADR/BCR/CHCR at offsets 0/4/8; 0 for
/// any other address. Infallible.
#[no_mangle]
pub extern "C" fn psx_dma_read_register(state: DmaState, address: u32) -> u32 {
    match address {
        PSX_DMA_DPCR => state.dpcr,
        PSX_DMA_DICR => {
            let stored = state.dicr & (DICR_FLAGS_MASK | DICR_CONTROL_MASK);
            stored | if irq_line(state.dicr) { DICR_IRQ_STATUS } else { 0 }
        }
        _ => decode_channel(address)
            .and_then(|(ch, offset)| {
                let c = state.channels.get(ch)?;
                match offset {
                    0 => Some(c.madr),
                    4 => Some(c.bcr),
                    8 => Some(c.chcr),
                    _ => None,
                }
            })
            .unwrap_or(0),
    }
}

/// Returns `state` after writing `value` to the register at `address`.
///
/// DPCR is replaced. DICR flags (bits 0-6) are write-1-to-clear and its
/// force-IRQ, master-enable and per-channel-enable bits are replaced. A
/// channel's MADR/BCR/CHCR at offsets 0/4/8 is replaced. Any other address
/// leaves the state unchanged. Infallible.
#[no_mangle]
pub extern "C" fn psx_dma_write_register(state: DmaState, address: u32, value: u32) -> DmaState {
    let mut s = state;
    match address {
        PSX_DMA_DPCR => s.dpcr = value,
        PSX_DMA_DICR => {
            let flags = s.dicr & DICR_FLAGS_MASK & !(value & DICR_FLAGS_MASK);
            s.dicr = (s.dicr & !(DICR_FLAGS_MASK | DICR_CONTROL_MASK))
                | flags
                | (value & DICR_CONTROL_MASK);
        }
        _ => {
            if let Some((ch, offset)) = decode_channel(address) {
                if let Some(c) = s.channels.get_mut(ch) {
                    match offset {
                        0 => c.madr = value,
                        4 => c.bcr = value,
                        8 => {
                            c.chcr = value;
                            // A CHCR write (re)starts or stops the channel, so
                            // any in-flight duration is re-costed.
                            if let Some(r) = s.remaining.get_mut(ch) {
                                *r = 0;
                            }
                        }
                        _ => {}
                    }
                }
            }
        }
    }
    s
}

/// Whether channel `ch` is started: CHCR start/busy set, its DPCR enable bit
/// set, and, for sync mode 0, its start/trigger bit set.
fn channel_started(dpcr: u32, ch: u32, chcr: u32) -> bool {
    let dpcr_enable = 1u32.checked_shl(3 + 4 * ch).unwrap_or(0);
    let sync = (chcr >> CHCR_SYNC_SHIFT) & 3;
    chcr & CHCR_START_BUSY != 0
        && dpcr & dpcr_enable != 0
        && (sync != 0 || chcr & CHCR_START_TRIGGER != 0)
}

/// Modelled duration of `channel`'s transfer in cycles, at least 1.
///
/// Word count per psx-spx: sync 0 uses BCR bits 0-15, sync 1 multiplies block
/// size (bits 0-15) by block count (bits 16-31), a zero field meaning 0x10000.
/// A linked list's length lives in guest RAM, which this state cannot see, so
/// sync 2/3 is costed as one word (GPU DMA2 is Issue #440's remainder).
fn transfer_cycles(channel: &DmaChannelState) -> u32 {
    let field = |v: u32| if v == 0 { 0x1_0000u64 } else { u64::from(v) };
    let words = match (channel.chcr >> CHCR_SYNC_SHIFT) & 3 {
        0 => field(channel.bcr & 0xFFFF),
        1 => field(channel.bcr & 0xFFFF) * field(channel.bcr >> 16),
        _ => 1,
    };
    u32::try_from(words.saturating_mul(PSX_DMA_CYCLES_PER_WORD))
        .unwrap_or(u32::MAX)
        .max(1)
}

/// Returns `state` after `cycles` CPU cycles of DMA progress (Issue #442).
///
/// Every started channel counts down its modelled duration; on reaching zero
/// it completes: CHCR start/busy (bit 24) and start/trigger (bit 28) clear,
/// and its DICR flag (bit `ch`) is set when its DICR enable (bit 24 + `ch`) is
/// set. Cycles past completion are discarded and no data is transferred. A
/// channel whose DPCR enable is cleared mid-transfer pauses. Infallible.
#[no_mangle]
pub extern "C" fn psx_dma_tick(state: DmaState, cycles: u32) -> DmaState {
    let mut s = state;
    if cycles == 0 {
        return s;
    }
    for (ch, (channel, remaining)) in s.channels.iter_mut().zip(s.remaining.iter_mut()).enumerate() {
        let bit = 1u32 << ch;
        if !channel_started(s.dpcr, ch as u32, channel.chcr) {
            continue;
        }
        if *remaining == 0 {
            *remaining = transfer_cycles(channel);
        }
        if cycles < *remaining {
            *remaining -= cycles;
            continue;
        }
        *remaining = 0;
        channel.chcr &= !(CHCR_START_BUSY | CHCR_START_TRIGGER);
        if s.dicr & (bit << 24) != 0 {
            s.dicr |= bit;
        }
    }
    s
}

/// Returns 1 when the aggregate DMA IRQ line is asserted (the DICR bit-31
/// condition), otherwise 0. Infallible.
#[no_mangle]
pub extern "C" fn psx_dma_get_interrupt_pending(state: DmaState) -> u32 {
    u32::from(irq_line(state.dicr))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn with_dicr(dicr: u32) -> DmaState {
        DmaState { dicr, ..psx_dma_reset() }
    }

    fn chan_addr(ch: u32, offset: u32) -> u32 {
        PSX_DMA_BASE + ch * PSX_DMA_CHANNEL_STRIDE + offset
    }

    #[test]
    fn layout_matches_cpp_mirror() {
        assert_eq!(std::mem::size_of::<DmaChannelState>(), 12);
        assert_eq!(std::mem::size_of::<DmaState>(), 120);
    }

    #[test]
    fn reset_state() {
        let s = psx_dma_reset();
        assert_eq!(psx_dma_read_register(s, PSX_DMA_DPCR), 0x0765_4321);
        assert_eq!(psx_dma_read_register(s, PSX_DMA_DICR), 0);
        assert_eq!(psx_dma_get_interrupt_pending(s), 0);
        for ch in 0..7 {
            for offset in [0, 4, 8] {
                assert_eq!(psx_dma_read_register(s, chan_addr(ch, offset)), 0);
            }
        }
    }

    #[test]
    fn dpcr_is_plain_read_write() {
        let s = psx_dma_write_register(psx_dma_reset(), PSX_DMA_DPCR, 0xDEAD_BEEF);
        assert_eq!(psx_dma_read_register(s, PSX_DMA_DPCR), 0xDEAD_BEEF);
        assert_eq!(s.dicr, 0);
        assert_eq!(s.channels, psx_dma_reset().channels);
    }

    #[test]
    fn every_channel_register_is_independent_read_write() {
        let mut s = psx_dma_reset();
        for ch in 0..7u32 {
            for (i, offset) in [0u32, 4, 8].into_iter().enumerate() {
                s = psx_dma_write_register(s, chan_addr(ch, offset), 0x1000 * (ch + 1) + i as u32);
            }
        }
        for ch in 0..7u32 {
            let c = s.channels[ch as usize];
            assert_eq!(c, DmaChannelState {
                madr: 0x1000 * (ch + 1),
                bcr: 0x1000 * (ch + 1) + 1,
                chcr: 0x1000 * (ch + 1) + 2,
            });
            for (i, offset) in [0u32, 4, 8].into_iter().enumerate() {
                assert_eq!(psx_dma_read_register(s, chan_addr(ch, offset)), 0x1000 * (ch + 1) + i as u32);
            }
        }
        assert_eq!(s.dpcr, PSX_DMA_DPCR_RESET);
        assert_eq!(s.dicr, 0);
    }

    #[test]
    fn unmapped_offsets_and_addresses_read_zero_and_ignore_writes() {
        let mut full = psx_dma_reset();
        for c in full.channels.iter_mut() {
            *c = DmaChannelState { madr: u32::MAX, bcr: u32::MAX, chcr: u32::MAX };
        }
        let addrs = [
            0,
            PSX_DMA_BASE - 4,
            PSX_DMA_BASE + 1,
            chan_addr(0, 0xC),
            chan_addr(6, 0xC),
            chan_addr(3, 2),
            PSX_DMA_DPCR + 1,
            PSX_DMA_DICR + 4,
            u32::MAX,
        ];
        for addr in addrs {
            assert_eq!(psx_dma_read_register(full, addr), 0, "addr = {addr:#010X}");
            assert_eq!(psx_dma_write_register(full, addr, 0), full, "addr = {addr:#010X}");
        }
    }

    #[test]
    fn dicr_write_clears_flags_on_one_and_keeps_on_zero() {
        let s = with_dicr(0x7F);
        assert_eq!(psx_dma_write_register(s, PSX_DMA_DICR, 0x01).dicr, 0x7E);
        assert_eq!(psx_dma_write_register(s, PSX_DMA_DICR, 0x00).dicr, 0x7F);
        assert_eq!(psx_dma_write_register(s, PSX_DMA_DICR, 0x7F).dicr, 0);
        // Writing 1 never sets a flag.
        assert_eq!(psx_dma_write_register(with_dicr(0), PSX_DMA_DICR, 0x7F).dicr, 0);
    }

    #[test]
    fn dicr_write_replaces_control_bits_and_drops_others() {
        let s = with_dicr(0x7F | DICR_CONTROL_MASK);
        let w = psx_dma_write_register(s, PSX_DMA_DICR, DICR_MASTER_EN | (0x05 << 24));
        assert_eq!(w.dicr, 0x7F | DICR_MASTER_EN | (0x05 << 24));
        // Bits outside flags/control (7-14, 16-22, 31) are never stored.
        let w = psx_dma_write_register(psx_dma_reset(), PSX_DMA_DICR, 0x807F_7F80);
        assert_eq!(w.dicr, 0);
        let w = psx_dma_write_register(psx_dma_reset(), PSX_DMA_DICR, u32::MAX);
        assert_eq!(w.dicr, DICR_CONTROL_MASK);
    }

    #[test]
    fn dicr_read_reports_stored_bits_plus_irq_status() {
        assert_eq!(psx_dma_read_register(with_dicr(DICR_MASTER_EN), PSX_DMA_DICR), DICR_MASTER_EN);
        let active = 0x04 | (0x04 << 24) | DICR_MASTER_EN;
        assert_eq!(psx_dma_read_register(with_dicr(active), PSX_DMA_DICR), active | DICR_IRQ_STATUS);
    }

    #[test]
    fn irq_requires_master_enable_and_matching_flag() {
        let cases = [
            (0x01 | (0x01 << 24) | DICR_MASTER_EN, 1),
            (0x01 | (0x01 << 24), 0),             // master off
            (0x01 | DICR_MASTER_EN, 0),           // channel not enabled
            ((0x01 << 24) | DICR_MASTER_EN, 0),   // no flag
            (0x02 | (0x01 << 24) | DICR_MASTER_EN, 0), // flag/enable mismatch
            (0x40 | (0x40 << 24) | DICR_MASTER_EN, 1), // channel 6
        ];
        for (dicr, pending) in cases {
            let s = with_dicr(dicr);
            assert_eq!(psx_dma_get_interrupt_pending(s), pending, "dicr = {dicr:#010X}");
            assert_eq!(
                psx_dma_read_register(s, PSX_DMA_DICR) & DICR_IRQ_STATUS != 0,
                pending == 1,
                "dicr = {dicr:#010X}"
            );
        }
    }

    #[test]
    fn force_irq_asserts_line_without_master_or_flags() {
        let s = psx_dma_write_register(psx_dma_reset(), PSX_DMA_DICR, DICR_FORCE_IRQ);
        assert_eq!(psx_dma_get_interrupt_pending(s), 1);
        assert_eq!(psx_dma_read_register(s, PSX_DMA_DICR), DICR_FORCE_IRQ | DICR_IRQ_STATUS);
        let s = psx_dma_write_register(s, PSX_DMA_DICR, 0);
        assert_eq!(psx_dma_get_interrupt_pending(s), 0);
        assert_eq!(psx_dma_read_register(s, PSX_DMA_DICR), 0);
    }

    #[test]
    fn acknowledging_the_flag_drops_the_line() {
        let s = with_dicr(0x01 | (0x01 << 24) | DICR_MASTER_EN);
        assert_eq!(psx_dma_get_interrupt_pending(s), 1);
        let s = psx_dma_write_register(s, PSX_DMA_DICR, 0x01 | (0x01 << 24) | DICR_MASTER_EN);
        assert_eq!(psx_dma_get_interrupt_pending(s), 0);
        assert_eq!(s.dicr, (0x01 << 24) | DICR_MASTER_EN);
    }

    /// Channel 6 (OTC) armed for a manual (sync 0) transfer of `words` words,
    /// with DPCR enable and, when `irq`, DICR master + channel 6 enable.
    fn armed_otc(words: u32, irq: bool) -> DmaState {
        let mut s = psx_dma_reset();
        s = psx_dma_write_register(s, PSX_DMA_DPCR, PSX_DMA_DPCR_RESET | (1 << (3 + 4 * 6)));
        if irq {
            s = psx_dma_write_register(s, PSX_DMA_DICR, DICR_MASTER_EN | (0x40 << 24));
        }
        s = psx_dma_write_register(s, chan_addr(6, 4), words);
        psx_dma_write_register(s, chan_addr(6, 8), CHCR_START_BUSY | CHCR_START_TRIGGER | 0x2)
    }

    #[test]
    fn tick_completes_after_modelled_word_count_and_flags_enabled_channel() {
        let mut s = psx_dma_tick(armed_otc(8, true), 7);
        assert_ne!(s.channels[6].chcr & CHCR_START_BUSY, 0, "one cycle early");
        assert_eq!(psx_dma_get_interrupt_pending(s), 0);
        s = psx_dma_tick(s, 1);
        assert_eq!(s.channels[6].chcr & (CHCR_START_BUSY | CHCR_START_TRIGGER), 0);
        assert_eq!(s.channels[6].chcr & 0x2, 0x2, "other CHCR bits untouched");
        assert_eq!(s.dicr & DICR_FLAGS_MASK, 0x40);
        assert_eq!(psx_dma_get_interrupt_pending(s), 1);
        assert_eq!(s.remaining[6], 0);
        // A completed channel stays idle.
        assert_eq!(psx_dma_tick(s, 100), s);
    }

    #[test]
    fn tick_completion_without_enable_sets_no_flag() {
        let s = psx_dma_tick(armed_otc(4, false), 4);
        assert_eq!(s.channels[6].chcr & CHCR_START_BUSY, 0);
        assert_eq!(s.dicr, 0);
        assert_eq!(psx_dma_get_interrupt_pending(s), 0);
    }

    #[test]
    fn tick_ignores_unstarted_channels() {
        let no_dpcr = psx_dma_write_register(armed_otc(1, true), PSX_DMA_DPCR, PSX_DMA_DPCR_RESET);
        assert_eq!(psx_dma_tick(no_dpcr, 100), no_dpcr);
        let no_trigger = psx_dma_write_register(armed_otc(1, true), chan_addr(6, 8), CHCR_START_BUSY);
        assert_eq!(psx_dma_tick(no_trigger, 100), no_trigger);
        let armed = armed_otc(1, true);
        assert_eq!(psx_dma_tick(armed, 0), armed);
    }

    #[test]
    fn tick_costs_block_mode_as_size_times_count_and_zero_as_0x10000() {
        let mut s = psx_dma_write_register(psx_dma_reset(), PSX_DMA_DPCR, 1 << 3);
        s = psx_dma_write_register(s, chan_addr(0, 4), (3 << 16) | 4);
        s = psx_dma_write_register(s, chan_addr(0, 8), CHCR_START_BUSY | (1 << CHCR_SYNC_SHIFT));
        s = psx_dma_tick(s, 11);
        assert_ne!(s.channels[0].chcr & CHCR_START_BUSY, 0);
        s = psx_dma_tick(s, 1);
        assert_eq!(s.channels[0].chcr & CHCR_START_BUSY, 0);

        let s = armed_otc(0, false);
        assert_ne!(psx_dma_tick(s, 0xFFFF).channels[6].chcr & CHCR_START_BUSY, 0);
        assert_eq!(psx_dma_tick(s, 0x1_0000).channels[6].chcr & CHCR_START_BUSY, 0);
    }

    #[test]
    fn tick_costs_linked_list_as_one_word() {
        let mut s = psx_dma_write_register(psx_dma_reset(), PSX_DMA_DPCR, 1 << (3 + 4 * 2));
        s = psx_dma_write_register(s, chan_addr(2, 8), CHCR_START_BUSY | (2 << CHCR_SYNC_SHIFT));
        assert_eq!(psx_dma_tick(s, 1).channels[2].chcr & CHCR_START_BUSY, 0);
    }

    #[test]
    fn chcr_rewrite_recosts_an_in_flight_transfer() {
        let mut s = psx_dma_tick(armed_otc(8, true), 5);
        assert_eq!(s.remaining[6], 3);
        s = psx_dma_write_register(s, chan_addr(6, 8), CHCR_START_BUSY | CHCR_START_TRIGGER);
        assert_eq!(s.remaining[6], 0);
        s = psx_dma_tick(s, 7);
        assert_ne!(s.channels[6].chcr & CHCR_START_BUSY, 0);
        s = psx_dma_tick(s, 1);
        assert_eq!(s.channels[6].chcr & CHCR_START_BUSY, 0);
    }

    #[test]
    fn repeated_tick_sequences_are_deterministic() {
        let run = || {
            let mut s = armed_otc(37, true);
            let mut trace = Vec::new();
            for i in 0..16u32 {
                s = psx_dma_tick(s, i % 5);
                trace.push((s, psx_dma_get_interrupt_pending(s)));
            }
            trace
        };
        assert_eq!(run(), run());
    }
}
