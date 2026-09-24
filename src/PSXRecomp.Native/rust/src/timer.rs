//! PS1 timers (Root Counters), migrated from the C++ `PSXTimerController`
//! (Issue #486).
//!
//! Per psx-spx "Timers": three independent counters at
//! `0x1F801100 + timer*0x10`, each with a 16-bit COUNT/MODE/TARGET register
//! triplet. Timer 2 divides the system clock by 8 when its clock-source bits
//! select that source; Timers 0/1 gate counting on an externally driven
//! Hblank/Vblank sync line (see [`psx_timer_set_sync_line`]).
//!
//! Ownership: the state is a plain [`TimerState`] value that the C++
//! `PSXCore` stores inline and passes by value, exactly like
//! [`crate::interrupt::InterruptState`]. Rust allocates nothing, keeps no
//! global state, and takes no pointers, so every export here is infallible,
//! contains no `unsafe`, and has no operation that can panic (timer/IRQ
//! indices are range-checked before use). They return their result directly,
//! as permitted for infallible functions by
//! `docs/development/rust-ffi-contract.md` §5, and are therefore trivially
//! thread-safe for distinct state values.
//!
//! A register read can have a side effect (reading MODE clears its
//! reached-target/reached-overflow flags), so a plain return value is not
//! enough to carry both the queried value and the state's evolution the way
//! every other export here does. [`psx_timer_read_register`] returns
//! [`TimerReadResult`], a `#[repr(C)]` bundle of the two, rather than taking
//! an out-parameter pointer.
//!
//! These symbols are internal to `PSXRecomp.Native`: C++ (`src/psx_api.cpp`,
//! declared in `src/psx_timer.h`) calls them to implement the unchanged
//! `PSXCore_*Timer*` C ABI. They are not P/Invoked by managed code.

/// Absolute address of Timer 0's register block.
const PSX_TIMER_BASE: u32 = 0x1F80_1100;
/// Byte stride between consecutive timers' register blocks.
const PSX_TIMER_STRIDE: u32 = 0x10;
/// Number of timers (Root Counters 0-2).
const PSX_TIMER_COUNT: usize = 3;

const REG_COUNT_OFFSET: u32 = 0x00;
const REG_MODE_OFFSET: u32 = 0x04;
const REG_TARGET_OFFSET: u32 = 0x08;

const MODE_SYNC_ENABLE: u16 = 1 << 0;
const MODE_SYNC_MASK: u16 = 3 << 1;
const MODE_RESET_TARGET: u16 = 1 << 3;
const MODE_IRQ_TARGET: u16 = 1 << 4;
const MODE_IRQ_OVERFLOW: u16 = 1 << 5;
const MODE_IRQ_REPEAT: u16 = 1 << 6;
const MODE_IRQ_TOGGLE: u16 = 1 << 7;
const MODE_CLK_SRC_MASK: u16 = 3 << 8;
const MODE_IRQ_REQUEST: u16 = 1 << 10;
const MODE_TARGET_FLAG: u16 = 1 << 11;
const MODE_OVERFLOW_FLAG: u16 = 1 << 12;
const MODE_WRITE_MASK: u16 = 0x03FF;

/// One timer's register state, owned by the C++ caller.
///
/// Mirrored field-for-field by `PSXTimerChannel` in `src/psx_timer.h`; a
/// layout change there or here is an ABI break. All fields are zero at power
/// on (see [`psx_timer_reset`]).
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct TimerChannel {
    /// Current 16-bit counter value.
    pub counter: u16,
    /// Target value compared against `counter`.
    pub target: u16,
    /// Mode register, bits 0-12 (bits 11/12 are read-and-clear status flags).
    pub mode: u16,
    /// Unacknowledged external IRQ latch (edge to the interrupt controller).
    pub irq_flag: u8,
    /// One-shot arming: IRQs are suppressed after firing until the next mode write re-arms them.
    pub irq_armed: u8,
    /// Toggle-mode output line state.
    pub toggle_line: u8,
    /// Current Hblank/Vblank sync line state.
    pub sync_active: u8,
    /// Previous sync line state, for edge detection.
    pub sync_prev: u8,
    /// Sync mode 3: pauses until the first blank, then free-runs.
    pub sync_armed: u8,
    /// Fractional clock-divisor accumulator.
    pub frac: u32,
}

/// All three timers' state, owned by the C++ caller.
///
/// Mirrored field-for-field by `PSXTimerState` in `src/psx_timer.h`.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct TimerState {
    /// Per-timer register state, indexed by timer number (0-2).
    pub channels: [TimerChannel; PSX_TIMER_COUNT],
}

/// The value and evolved state produced by [`psx_timer_read_register`].
///
/// Mirrored field-for-field by `PSXTimerReadResult` in `src/psx_timer.h`.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct TimerReadResult {
    /// `state` after this read's side effects (if any).
    pub state: TimerState,
    /// The register's value as read.
    pub value: u32,
}

/// Index for `timer`, or `None` outside `0..PSX_TIMER_COUNT`.
fn timer_at(timer: i32) -> Option<usize> {
    usize::try_from(timer).ok().filter(|&t| t < PSX_TIMER_COUNT)
}

/// Decodes `address` into a timer index, or `None` when it is outside every
/// timer's register block.
fn decode_address(address: u32) -> Option<(usize, u32)> {
    if address < PSX_TIMER_BASE
        || address >= PSX_TIMER_BASE + (PSX_TIMER_COUNT as u32) * PSX_TIMER_STRIDE
    {
        return None;
    }
    let offset_from_base = address - PSX_TIMER_BASE;
    let index = (offset_from_base / PSX_TIMER_STRIDE) as usize;
    let reg_offset = offset_from_base % PSX_TIMER_STRIDE;
    Some((index, reg_offset))
}

/// Clock source -> CPU cycles per counter increment (psx-spx).
///
/// Dotclock/Hblank are GPU-generated signals (out of scope here, matching the
/// migrated C++); they default to counting every CPU cycle as a deterministic
/// approximation until a GPU clock source is wired in.
fn clock_divisor(channel: &TimerChannel, timer: usize) -> u32 {
    let src = (channel.mode & MODE_CLK_SRC_MASK) >> 8;
    if timer == 2 && (src == 2 || src == 3) {
        8
    } else {
        1
    }
}

/// Whether the current sync state permits the counter to increment this cycle.
fn sync_allows_count(channel: &TimerChannel, timer: usize) -> bool {
    if channel.mode & MODE_SYNC_ENABLE == 0 {
        return true; // free run
    }

    let sync_mode = (channel.mode & MODE_SYNC_MASK) >> 1;
    if timer == 2 {
        // Modes 0 or 3 = stop counter forever; 1 or 2 = free run (no h/v-blank).
        return sync_mode == 1 || sync_mode == 2;
    }

    // Timer 0/1 use an externally-driven line (Hblank/Vblank).
    let active = channel.sync_active != 0;
    match sync_mode {
        0 => !active,                    // pause during blank
        1 => true,                       // reset at blank (counts)
        2 => active,                     // reset at blank & pause outside
        3 => channel.sync_armed != 0,    // pause until first blank then free run
        _ => true,
    }
}

fn fire_irq(channel: &mut TimerChannel) {
    if channel.mode & MODE_IRQ_TOGGLE != 0 {
        channel.toggle_line ^= 1;
        if channel.toggle_line != 0 {
            channel.mode &= !MODE_IRQ_REQUEST; // bit10 = 0 => IRQ pending
            channel.irq_flag = 1;
        } else {
            channel.mode |= MODE_IRQ_REQUEST;
        }
    } else {
        // Pulse mode: brief low pulse on bit10, raise external IRQ.
        channel.mode &= !MODE_IRQ_REQUEST;
        channel.irq_flag = 1;
        channel.mode |= MODE_IRQ_REQUEST;
    }

    // One-shot: suppress further IRQs until the next mode write re-arms.
    // Interrupt-enable bits are preserved for reads and read-modify-write.
    if channel.mode & MODE_IRQ_REPEAT == 0 {
        channel.irq_armed = 0;
    }
}

fn advance_one(channel: &mut TimerChannel, timer: usize) {
    if !sync_allows_count(channel, timer) {
        return;
    }

    let old = channel.counter;
    channel.counter = channel.counter.wrapping_add(1);

    // Target reached (equality on the 16-bit counter, per psx-spx).
    if channel.counter == channel.target {
        channel.mode |= MODE_TARGET_FLAG;
        if channel.mode & MODE_IRQ_TARGET != 0 && channel.irq_armed != 0 {
            fire_irq(channel);
        }
        if channel.mode & MODE_RESET_TARGET != 0 {
            channel.counter = 0;
        }
    }

    // Overflow (wrapped from FFFFh to 0000h).
    if old == 0xFFFF {
        channel.mode |= MODE_OVERFLOW_FLAG;
        if channel.mode & MODE_IRQ_OVERFLOW != 0 && channel.irq_armed != 0 {
            fire_irq(channel);
        }
    }
}

/// Returns the power-on state (all fields zero). Infallible.
#[no_mangle]
pub extern "C" fn psx_timer_reset() -> TimerState {
    TimerState::default()
}

/// Reads the register at `address` and returns it alongside `state` after any
/// side effect of the read (reading MODE clears its target/overflow flags).
/// An address outside every timer's block reads as 0 with `state` unchanged.
/// Infallible.
#[no_mangle]
pub extern "C" fn psx_timer_read_register(state: TimerState, address: u32) -> TimerReadResult {
    let Some((index, reg_offset)) = decode_address(address) else {
        return TimerReadResult { state, value: 0 };
    };

    let mut state = state;
    let channel = &mut state.channels[index];
    let value = match reg_offset {
        REG_COUNT_OFFSET => u32::from(channel.counter),
        REG_MODE_OFFSET => {
            let value = u32::from(channel.mode);
            channel.mode &= !(MODE_TARGET_FLAG | MODE_OVERFLOW_FLAG);
            value
        }
        REG_TARGET_OFFSET => u32::from(channel.target),
        _ => 0,
    };
    TimerReadResult { state, value }
}

/// Returns `state` after writing `value` to the register at `address`. An
/// address outside every timer's block leaves `state` unchanged.
///
/// Writing MODE resets the counter, forces bit 10 (IRQ_REQUEST) set, and
/// re-arms one-shot/repeat IRQs, per psx-spx. Infallible.
#[no_mangle]
pub extern "C" fn psx_timer_write_register(
    state: TimerState,
    address: u32,
    value: u32,
) -> TimerState {
    let Some((index, reg_offset)) = decode_address(address) else {
        return state;
    };

    let mut state = state;
    let channel = &mut state.channels[index];
    match reg_offset {
        REG_COUNT_OFFSET => channel.counter = value as u16,
        REG_MODE_OFFSET => {
            channel.mode = (value as u16 & MODE_WRITE_MASK) | MODE_IRQ_REQUEST;
            channel.counter = 0;
            channel.toggle_line = 0;
            channel.frac = 0;
            channel.sync_armed = 0;
            channel.irq_flag = 0;
            channel.irq_armed = 1; // re-arm one-shot / repeat IRQs
        }
        REG_TARGET_OFFSET => channel.target = value as u16,
        _ => {}
    }
    state
}

/// Advances every timer by `cycles` CPU cycles, applying each timer's clock
/// divisor, sync gating, and target/overflow IRQ semantics. Infallible.
#[no_mangle]
pub extern "C" fn psx_timer_tick(state: TimerState, cycles: u32) -> TimerState {
    if cycles == 0 {
        return state;
    }

    let mut state = state;
    for timer in 0..PSX_TIMER_COUNT {
        let divisor = clock_divisor(&state.channels[timer], timer);
        state.channels[timer].frac += cycles;
        let ticks = state.channels[timer].frac / divisor;
        state.channels[timer].frac %= divisor;
        for _ in 0..ticks {
            advance_one(&mut state.channels[timer], timer);
        }
    }
    state
}

/// Returns `state` after setting `timer`'s Hblank/Vblank sync line to
/// `active`, applying any edge side effect (sync modes 1/2 reset the counter
/// on a rising edge; sync mode 3 arms free-run on the first rising edge). An
/// out-of-range `timer` leaves `state` unchanged. Infallible.
#[no_mangle]
pub extern "C" fn psx_timer_set_sync_line(
    state: TimerState,
    timer: i32,
    active: i32,
) -> TimerState {
    let mut state = state;
    let Some(index) = timer_at(timer) else {
        return state;
    };

    let channel = &mut state.channels[index];
    channel.sync_prev = channel.sync_active;
    channel.sync_active = u8::from(active != 0);

    // Edge side effects on a rising sync line (Hblank/Vblank) for Timer 0/1.
    let rose = channel.sync_active != 0 && channel.sync_prev == 0;
    if channel.mode & MODE_SYNC_ENABLE != 0 && index != 2 && rose {
        let sync_mode = (channel.mode & MODE_SYNC_MASK) >> 1;
        if sync_mode == 1 || sync_mode == 2 {
            channel.counter = 0; // reset counter at blank edge
        }
        if sync_mode == 3 {
            channel.sync_armed = 1; // pause-until-first-blank -> free run
        }
    }
    state
}

/// Returns 1 when `timer` has an unacknowledged IRQ latched, else 0. An
/// out-of-range `timer` returns 0. Infallible, no side effect (mirrors the
/// migrated C++'s `const` accessor).
#[no_mangle]
pub extern "C" fn psx_timer_get_interrupt_pending(state: TimerState, timer: i32) -> u32 {
    match timer_at(timer) {
        Some(index) => u32::from(state.channels[index].irq_flag != 0),
        None => 0,
    }
}

/// Returns `state` with `timer`'s IRQ latch cleared. An out-of-range `timer`
/// leaves `state` unchanged. Infallible.
#[no_mangle]
pub extern "C" fn psx_timer_clear_interrupt(state: TimerState, timer: i32) -> TimerState {
    let mut state = state;
    if let Some(index) = timer_at(timer) {
        state.channels[index].irq_flag = 0;
    }
    state
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tmr(t: i32, off: u32) -> u32 {
        PSX_TIMER_BASE + (t as u32) * PSX_TIMER_STRIDE + off
    }

    fn read(state: TimerState, address: u32) -> (TimerState, u32) {
        let result = psx_timer_read_register(state, address);
        (result.state, result.value)
    }

    #[test]
    fn reset_is_all_zero() {
        assert_eq!(psx_timer_reset(), TimerState::default());
    }

    #[test]
    fn register_read_write_round_trips() {
        let mut s = psx_timer_reset();
        s = psx_timer_write_register(s, tmr(0, 0x00), 0x1234);
        assert_eq!(read(s, tmr(0, 0x00)).1, 0x1234);

        s = psx_timer_write_register(s, tmr(1, 0x08), 0x8000);
        assert_eq!(read(s, tmr(1, 0x08)).1, 0x8000);

        s = psx_timer_write_register(s, tmr(2, 0x00), 0xABCD);
        s = psx_timer_write_register(s, tmr(2, 0x04), 0xFFFF);
        assert_eq!(read(s, tmr(2, 0x04)).1, 0x03FF | 0x0400);
        // Mode write resets the counter.
        assert_eq!(read(s, tmr(2, 0x00)).1, 0);
    }

    #[test]
    fn address_outside_any_block_is_zero_and_unchanged() {
        let s = psx_timer_reset();
        for addr in [0, PSX_TIMER_BASE - 4, PSX_TIMER_BASE + 3 * PSX_TIMER_STRIDE, u32::MAX] {
            let (next, value) = read(s, addr);
            assert_eq!(value, 0, "addr = {addr:#010X}");
            assert_eq!(next, s, "addr = {addr:#010X}");
            assert_eq!(psx_timer_write_register(s, addr, 0xFFFF_FFFF), s, "addr = {addr:#010X}");
        }
    }

    #[test]
    fn free_run_target_reached_generates_repeat_irq() {
        let mut s = psx_timer_reset();
        s = psx_timer_write_register(s, tmr(0, 0x04), 0x50); // IRQ_TARGET | IRQ_REPEAT
        s = psx_timer_write_register(s, tmr(0, 0x08), 5);
        for _ in 0..5 {
            s = psx_timer_tick(s, 1);
        }
        assert_eq!(read(s, tmr(0, 0x00)).1, 5);
        assert_eq!(psx_timer_get_interrupt_pending(s, 0), 1);

        let (s2, mode) = read(s, tmr(0, 0x04));
        assert_eq!(mode & 0x0800, 0x0800); // target flag set
        let (_, mode2) = read(s2, tmr(0, 0x04));
        assert_eq!(mode2 & 0x0800, 0); // reading MODE clears it
    }

    #[test]
    fn target_reset_bit_resets_counter_to_zero() {
        let mut s = psx_timer_reset();
        s = psx_timer_write_register(s, tmr(0, 0x04), 0x18); // IRQ_TARGET | RESET_TARGET
        s = psx_timer_write_register(s, tmr(0, 0x08), 5);
        for _ in 0..5 {
            s = psx_timer_tick(s, 1);
        }
        assert_eq!(read(s, tmr(0, 0x00)).1, 0);
        assert_eq!(psx_timer_get_interrupt_pending(s, 0), 1);
    }

    #[test]
    fn overflow_at_ffff_generates_irq() {
        let mut s = psx_timer_reset();
        s = psx_timer_write_register(s, tmr(0, 0x04), 0x60); // IRQ_OVERFLOW | IRQ_REPEAT
        s = psx_timer_write_register(s, tmr(0, 0x00), 0xFFFE);
        s = psx_timer_tick(s, 1); // -> FFFF
        assert_eq!(psx_timer_get_interrupt_pending(s, 0), 0);
        s = psx_timer_tick(s, 1); // -> 0000 (overflow)
        assert_eq!(read(s, tmr(0, 0x00)).1, 0);
        assert_eq!(psx_timer_get_interrupt_pending(s, 0), 1);
        assert_eq!(read(s, tmr(0, 0x04)).1 & 0x1000, 0x1000);
    }

    #[test]
    fn one_shot_suppresses_further_irqs_until_mode_rewrite() {
        let mut s = psx_timer_reset();
        s = psx_timer_write_register(s, tmr(0, 0x04), 0x10); // IRQ_TARGET only, no repeat
        s = psx_timer_write_register(s, tmr(0, 0x08), 3);
        for _ in 0..3 {
            s = psx_timer_tick(s, 1);
        }
        assert_eq!(psx_timer_get_interrupt_pending(s, 0), 1);

        s = psx_timer_clear_interrupt(s, 0);
        for _ in 0..3 {
            s = psx_timer_tick(s, 1);
        }
        assert_eq!(psx_timer_get_interrupt_pending(s, 0), 0);
    }

    #[test]
    fn toggle_mode_raises_irq_on_alternating_reaches() {
        let mut s = psx_timer_reset();
        s = psx_timer_write_register(s, tmr(0, 0x04), 0xD8); // TARGET|REPEAT|TOGGLE|RESET_TARGET
        s = psx_timer_write_register(s, tmr(0, 0x08), 2);

        let advance = |mut s: TimerState| {
            for _ in 0..2 {
                s = psx_timer_tick(s, 1);
            }
            s
        };

        s = advance(s);
        assert_eq!(psx_timer_get_interrupt_pending(s, 0), 1);
        s = psx_timer_clear_interrupt(s, 0);

        s = advance(s);
        assert_eq!(psx_timer_get_interrupt_pending(s, 0), 0);
        s = psx_timer_clear_interrupt(s, 0);

        s = advance(s);
        assert_eq!(psx_timer_get_interrupt_pending(s, 0), 1);
    }

    #[test]
    fn timer2_system_clock_div8_counts_one_per_eight_cycles() {
        let mut s = psx_timer_reset();
        s = psx_timer_write_register(s, tmr(2, 0x04), 0x200); // src=2 -> /8
        s = psx_timer_tick(s, 7);
        assert_eq!(read(s, tmr(2, 0x00)).1, 0);
        s = psx_timer_tick(s, 1);
        assert_eq!(read(s, tmr(2, 0x00)).1, 1);

        let mut s2 = psx_timer_reset();
        s2 = psx_timer_write_register(s2, tmr(2, 0x04), 0);
        s2 = psx_timer_tick(s2, 8);
        assert_eq!(read(s2, tmr(2, 0x00)).1, 8);
    }

    #[test]
    fn timer2_sync_mode0_stops_mode1_free_runs() {
        let mut s = psx_timer_reset();
        s = psx_timer_write_register(s, tmr(2, 0x04), 0x01);
        s = psx_timer_tick(s, 10);
        assert_eq!(read(s, tmr(2, 0x00)).1, 0);

        let mut s2 = psx_timer_reset();
        s2 = psx_timer_write_register(s2, tmr(2, 0x04), 0x03);
        s2 = psx_timer_tick(s2, 5);
        assert_eq!(read(s2, tmr(2, 0x00)).1, 5);
    }

    #[test]
    fn timer0_sync_mode0_pauses_during_blank_line() {
        let mut s = psx_timer_reset();
        s = psx_timer_write_register(s, tmr(0, 0x04), 0x01); // sync enable, mode 0
        s = psx_timer_tick(s, 5);
        assert_eq!(read(s, tmr(0, 0x00)).1, 5);

        s = psx_timer_set_sync_line(s, 0, 1); // blank active -> paused
        s = psx_timer_tick(s, 5);
        assert_eq!(read(s, tmr(0, 0x00)).1, 5);

        s = psx_timer_set_sync_line(s, 0, 0); // blank inactive -> resume
        s = psx_timer_tick(s, 3);
        assert_eq!(read(s, tmr(0, 0x00)).1, 8);
    }

    #[test]
    fn timer0_sync_mode1_resets_counter_on_blank_edge() {
        let mut s = psx_timer_reset();
        s = psx_timer_write_register(s, tmr(0, 0x04), 0x03); // sync enable, mode 1
        s = psx_timer_write_register(s, tmr(0, 0x00), 10);
        s = psx_timer_set_sync_line(s, 0, 1); // rising edge
        s = psx_timer_tick(s, 1);
        assert_eq!(read(s, tmr(0, 0x00)).1, 1);
    }

    #[test]
    fn timer0_sync_mode3_pauses_until_first_blank_then_free_runs() {
        let mut s = psx_timer_reset();
        s = psx_timer_write_register(s, tmr(0, 0x04), 0x07);
        s = psx_timer_tick(s, 5);
        assert_eq!(read(s, tmr(0, 0x00)).1, 0); // paused

        s = psx_timer_set_sync_line(s, 0, 1); // first blank -> arm free run
        s = psx_timer_set_sync_line(s, 0, 0);
        s = psx_timer_tick(s, 5);
        assert_eq!(read(s, tmr(0, 0x00)).1, 5); // free run
    }

    #[test]
    fn reset_clears_counters_and_interrupts() {
        let mut s = psx_timer_reset();
        s = psx_timer_write_register(s, tmr(0, 0x00), 0x1234);
        s = psx_timer_write_register(s, tmr(0, 0x04), 0x50);
        s = psx_timer_write_register(s, tmr(0, 0x08), 2);
        for _ in 0..2 {
            s = psx_timer_tick(s, 1);
        }
        assert_eq!(psx_timer_get_interrupt_pending(s, 0), 1);

        s = psx_timer_reset();
        assert_eq!(read(s, tmr(0, 0x00)).1, 0);
        assert_eq!(read(s, tmr(0, 0x04)).1, 0);
        assert_eq!(psx_timer_get_interrupt_pending(s, 0), 0);
    }

    #[test]
    fn out_of_range_timer_index_is_ignored() {
        let s = psx_timer_reset();
        for t in [i32::MIN, -1, 3, 4, 32, i32::MAX] {
            assert_eq!(psx_timer_set_sync_line(s, t, 1), s, "t = {t}");
            assert_eq!(psx_timer_clear_interrupt(s, t), s, "t = {t}");
            assert_eq!(psx_timer_get_interrupt_pending(s, t), 0, "t = {t}");
        }
    }

    #[test]
    fn timers_are_independent() {
        let mut s = psx_timer_reset();
        s = psx_timer_write_register(s, tmr(0, 0x04), 0x50);
        s = psx_timer_write_register(s, tmr(0, 0x08), 10);
        s = psx_timer_write_register(s, tmr(1, 0x04), 0x50);
        s = psx_timer_write_register(s, tmr(1, 0x08), 5);
        s = psx_timer_tick(s, 5);

        assert_eq!(read(s, tmr(0, 0x00)).1, 5);
        assert_eq!(read(s, tmr(1, 0x00)).1, 5);
        assert_eq!(psx_timer_get_interrupt_pending(s, 1), 1);
        assert_eq!(psx_timer_get_interrupt_pending(s, 0), 0);
    }

    #[test]
    fn repeated_operation_sequences_are_deterministic() {
        let run = || {
            let mut s = psx_timer_reset();
            let mut trace = Vec::new();
            for i in 0..64u32 {
                s = psx_timer_write_register(s, tmr((i % 3) as i32, 0x04), i.wrapping_mul(0x9E37_79B9));
                s = psx_timer_tick(s, i % 17);
                if i % 5 == 0 {
                    s = psx_timer_set_sync_line(s, (i % 3) as i32, i32::from(i % 2 == 0));
                }
                if i % 7 == 0 {
                    s = psx_timer_clear_interrupt(s, (i % 3) as i32);
                }
                trace.push((s, psx_timer_get_interrupt_pending(s, (i % 3) as i32)));
            }
            trace
        };
        assert_eq!(run(), run());
    }
}
