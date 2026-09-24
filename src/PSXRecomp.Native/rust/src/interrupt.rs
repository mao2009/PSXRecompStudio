//! PS1 interrupt controller (I_STAT / I_MASK), migrated from the C++
//! `PSXInterruptController` (Issue #484).
//!
//! Ownership: the state is a plain [`InterruptState`] value that the C++
//! `PSXCore` stores inline and passes by value. Rust allocates nothing, keeps
//! no global state, and takes no pointers, so every export here is infallible,
//! contains no `unsafe`, and has no operation that can panic (IRQ numbers are
//! range-checked before shifting). They return their result directly, as
//! permitted for infallible functions by `docs/development/rust-ffi-contract.md`
//! §5, and are therefore trivially thread-safe for distinct state values.
//!
//! These symbols are internal to `PSXRecomp.Native`: C++ (`src/psx_api.cpp`,
//! declared in `src/psx_interrupt.h`) calls them to implement the unchanged
//! `PSXCore_*Interrupt*` C ABI. They are not P/Invoked by managed code.

/// Absolute address of I_STAT (interrupt status, write-0-to-clear).
pub const PSX_INT_STAT: u32 = 0x1F80_1070;

/// Absolute address of I_MASK (interrupt mask, plain read/write).
pub const PSX_INT_MASK: u32 = 0x1F80_1074;

/// Number of IRQ lines (IRQ0..IRQ10) the controller latches.
pub const PSX_INT_IRQ_COUNT: i32 = 11;

/// Interrupt-controller register state, owned by the C++ caller.
///
/// Mirrored field-for-field by `PSXInterruptState` in `src/psx_interrupt.h`;
/// a layout change there or here is an ABI break.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct InterruptState {
    /// I_STAT: latched interrupt requests.
    pub i_stat: u32,
    /// I_MASK: enabled interrupt lines.
    pub i_mask: u32,
}

/// Returns the power-on state (both registers zero). Infallible.
#[no_mangle]
pub extern "C" fn psx_interrupt_reset() -> InterruptState {
    InterruptState { i_stat: 0, i_mask: 0 }
}

/// Reads the register at `address`: I_STAT, I_MASK, or 0 for any other
/// address. Infallible.
#[no_mangle]
pub extern "C" fn psx_interrupt_read_register(state: InterruptState, address: u32) -> u32 {
    match address {
        PSX_INT_STAT => state.i_stat,
        PSX_INT_MASK => state.i_mask,
        _ => 0,
    }
}

/// Returns `state` after writing `value` to the register at `address`.
///
/// I_STAT is write-0-to-clear (psx-spx "Interrupt Acknowledge"): a 0 bit
/// clears the latched request, a 1 bit leaves it unchanged. I_MASK is replaced.
/// Any other address leaves the state unchanged. Infallible.
#[no_mangle]
pub extern "C" fn psx_interrupt_write_register(
    state: InterruptState,
    address: u32,
    value: u32,
) -> InterruptState {
    match address {
        PSX_INT_STAT => InterruptState { i_stat: state.i_stat & value, ..state },
        PSX_INT_MASK => InterruptState { i_mask: value, ..state },
        _ => state,
    }
}

/// Bit for `irq`, or `None` outside `0..PSX_INT_IRQ_COUNT`.
fn irq_bit(irq: i32) -> Option<u32> {
    if (0..PSX_INT_IRQ_COUNT).contains(&irq) {
        1u32.checked_shl(irq.unsigned_abs())
    } else {
        None
    }
}

/// Returns `state` with IRQ line `irq` latched in I_STAT. An out-of-range
/// `irq` is ignored. Infallible.
#[no_mangle]
pub extern "C" fn psx_interrupt_raise(state: InterruptState, irq: i32) -> InterruptState {
    match irq_bit(irq) {
        Some(bit) => InterruptState { i_stat: state.i_stat | bit, ..state },
        None => state,
    }
}

/// Returns `state` with IRQ line `irq` cleared in I_STAT. An out-of-range
/// `irq` is ignored. Infallible.
#[no_mangle]
pub extern "C" fn psx_interrupt_clear(state: InterruptState, irq: i32) -> InterruptState {
    match irq_bit(irq) {
        Some(bit) => InterruptState { i_stat: state.i_stat & !bit, ..state },
        None => state,
    }
}

/// Returns 1 when any unmasked request is pending (`I_STAT & I_MASK != 0`),
/// otherwise 0. This is the aggregate line fed to CPU CAUSE.IP2. Infallible.
#[no_mangle]
pub extern "C" fn psx_interrupt_pending(state: InterruptState) -> u32 {
    u32::from(state.i_stat & state.i_mask != 0)
}

#[cfg(test)]
mod tests {
    use super::*;

    const RESET: InterruptState = InterruptState { i_stat: 0, i_mask: 0 };

    fn st(i_stat: u32, i_mask: u32) -> InterruptState {
        InterruptState { i_stat, i_mask }
    }

    #[test]
    fn reset_is_all_zero_and_not_pending() {
        let s = psx_interrupt_reset();
        assert_eq!(s, RESET);
        assert_eq!(psx_interrupt_read_register(s, PSX_INT_STAT), 0);
        assert_eq!(psx_interrupt_read_register(s, PSX_INT_MASK), 0);
        assert_eq!(psx_interrupt_pending(s), 0);
    }

    #[test]
    fn read_returns_each_register_and_zero_elsewhere() {
        let s = st(0x1234_5678, 0x8765_4321);
        assert_eq!(psx_interrupt_read_register(s, PSX_INT_STAT), 0x1234_5678);
        assert_eq!(psx_interrupt_read_register(s, PSX_INT_MASK), 0x8765_4321);
        for addr in [0, PSX_INT_STAT - 4, PSX_INT_STAT + 1, PSX_INT_MASK + 4, u32::MAX] {
            assert_eq!(psx_interrupt_read_register(s, addr), 0, "addr = {addr:#010X}");
        }
    }

    #[test]
    fn status_write_is_write_zero_to_clear() {
        let s = st(0x7FF, 0x5A5);
        assert_eq!(psx_interrupt_write_register(s, PSX_INT_STAT, 0x7FE), st(0x7FE, 0x5A5));
        assert_eq!(psx_interrupt_write_register(s, PSX_INT_STAT, 0), st(0, 0x5A5));
        assert_eq!(psx_interrupt_write_register(s, PSX_INT_STAT, u32::MAX), s);
        // Writing 1 never sets a bit that was not latched.
        assert_eq!(psx_interrupt_write_register(RESET, PSX_INT_STAT, u32::MAX), RESET);
    }

    #[test]
    fn mask_write_replaces_mask_only() {
        let s = st(0x3, 0x7FF);
        assert_eq!(psx_interrupt_write_register(s, PSX_INT_MASK, 0x1), st(0x3, 0x1));
        assert_eq!(psx_interrupt_write_register(s, PSX_INT_MASK, u32::MAX), st(0x3, u32::MAX));
        assert_eq!(psx_interrupt_write_register(s, PSX_INT_MASK, 0), st(0x3, 0));
    }

    #[test]
    fn write_to_other_address_is_ignored() {
        let s = st(0x7FF, 0x7FF);
        for addr in [0, PSX_INT_STAT + 2, PSX_INT_MASK + 4, u32::MAX] {
            assert_eq!(psx_interrupt_write_register(s, addr, 0), s, "addr = {addr:#010X}");
        }
    }

    #[test]
    fn raise_sets_each_line_and_all_lines_give_0x7ff() {
        let mut s = RESET;
        for irq in 0..PSX_INT_IRQ_COUNT {
            assert_eq!(psx_interrupt_raise(RESET, irq).i_stat, 1 << irq);
            s = psx_interrupt_raise(s, irq);
        }
        assert_eq!(s, st(0x7FF, 0));
        // Raising an already-latched line is idempotent.
        assert_eq!(psx_interrupt_raise(s, 4), s);
    }

    #[test]
    fn clear_removes_only_the_requested_line() {
        let s = st(0x3, 0x7FF);
        assert_eq!(psx_interrupt_clear(s, 0), st(0x2, 0x7FF));
        assert_eq!(psx_interrupt_clear(s, 5), s);
        assert_eq!(psx_interrupt_clear(st(u32::MAX, 0), 10).i_stat, u32::MAX & !(1 << 10));
    }

    #[test]
    fn out_of_range_irq_is_ignored() {
        let s = st(0x1, 0x1);
        for irq in [i32::MIN, -1, PSX_INT_IRQ_COUNT, 31, 32, 64, i32::MAX] {
            assert_eq!(psx_interrupt_raise(s, irq), s, "irq = {irq}");
            assert_eq!(psx_interrupt_clear(s, irq), s, "irq = {irq}");
        }
    }

    #[test]
    fn pending_requires_an_unmasked_latched_bit() {
        assert_eq!(psx_interrupt_pending(st(0x1, 0x0)), 0);
        assert_eq!(psx_interrupt_pending(st(0x0, 0x7FF)), 0);
        assert_eq!(psx_interrupt_pending(st(0x2, 0x1)), 0);
        assert_eq!(psx_interrupt_pending(st(0x2, 0x3)), 1);
        assert_eq!(psx_interrupt_pending(st(0x7FF, 0x400)), 1);
        assert_eq!(psx_interrupt_pending(st(u32::MAX, u32::MAX)), 1);
        assert_eq!(psx_interrupt_pending(st(0x8000_0000, 0x8000_0000)), 1);
    }

    #[test]
    fn acknowledge_sequence_drops_pending() {
        let mut s = psx_interrupt_write_register(RESET, PSX_INT_MASK, 0x1);
        s = psx_interrupt_raise(s, 0);
        s = psx_interrupt_raise(s, 1);
        assert_eq!(psx_interrupt_pending(s), 1);
        s = psx_interrupt_write_register(s, PSX_INT_STAT, !1u32);
        assert_eq!(s, st(0x2, 0x1));
        assert_eq!(psx_interrupt_pending(s), 0);
    }

    #[test]
    fn repeated_operation_sequences_are_deterministic() {
        let run = || {
            let mut s = psx_interrupt_reset();
            let mut trace = Vec::new();
            for i in 0..64i32 {
                s = psx_interrupt_raise(s, i % 13 - 1);
                s = psx_interrupt_write_register(s, PSX_INT_MASK, (i as u32).wrapping_mul(0x9E37_79B9));
                if i % 3 == 0 {
                    s = psx_interrupt_clear(s, i % 11);
                }
                if i % 7 == 0 {
                    s = psx_interrupt_write_register(s, PSX_INT_STAT, !(1u32 << (i % 11)));
                }
                trace.push((s, psx_interrupt_pending(s)));
            }
            trace
        };
        assert_eq!(run(), run());
    }
}
