//! PS1 SIO0 (controller / memory-card serial port) register-only model
//! (Issue #542), migrated from the managed `PSXRecomp.Core.Runtime.Sio`
//! model (`Sio0State`/`Sio0Device`/`Sio0MmioAdapter`) so the semantics are
//! reachable from the production guest CPU load/store path
//! (`crate::memory`), not only from the managed `MemoryBus` test/BIOS-HLE
//! seam. Fixes a CodeRabbit finding on PR #548: guest `LW/SW/LH/SH/LB/SB` at
//! `0x1F801040-0x1F80105F` reached the native `PSXMemory` flat HW-register
//! fallback store, never the managed device.
//!
//! Register-only, same scope as the managed model it replaces: no serial
//! transfer / controller / memory-card protocol, no IRQ7 (Issue #543 tracks
//! both as follow-up work). [`Sio0State::enqueue_received_byte`] is the seam
//! a future transaction model will drive; nothing calls it in production
//! yet, so it is `pub(crate)`, exercised only by this module's own tests.
//!
//! ## Ownership
//!
//! Unlike [`crate::dma`]/[`crate::timer`]/[`crate::interrupt`], this state is
//! owned inline by [`crate::memory::PsxMemory`] rather than by the C++
//! `PSXCore` via an `AttachControllers`-style borrowed pointer: nothing else
//! in native code (no DMA channel, no interrupt evaluation, no timer) needs
//! to observe or drive SIO0 state in this scope, so the extra indirection
//! and C++-side `PSXSio0State` mirror struct buy nothing here. This keeps
//! the fix entirely inside `PSXRecomp.Native`'s internal Rust/C++ boundary:
//! no `include/psx_core.h` / `NativeInterop.cs` / `ABI_VERSION` change.
//!
//! ## Dispatch
//!
//! Dispatch is by exact register address, not word-realigned like the
//! DMA/Timer/Interrupt controllers: this mirrors the managed
//! `Sio0MmioAdapter`/`Ps1MemoryMap.GetSio0RegisterType` behavior it replaces
//! byte-for-byte (every access reads/writes the full register value at its
//! exact address; a sub-word access that lands on an unnamed offset — e.g.
//! the upper half of SIO_STAT — is Reserved, not part of the named
//! register), so PR #548's already-reviewed guest-visible contract does not
//! change, only where it is reached from.

/// SIO0 register window base (inclusive). Must match `Ps1MemoryMap.Sio0Base`.
pub const PSX_SIO0_BASE: u32 = 0x1F80_1040;
/// SIO0 register window end (exclusive). Must match `Ps1MemoryMap.Sio0End`.
pub const PSX_SIO0_END: u32 = 0x1F80_1060;

const OFFSET_DATA: u32 = 0x00;
const OFFSET_STATUS: u32 = 0x04;
const OFFSET_MODE: u32 = 0x08;
const OFFSET_CONTROL: u32 = 0x0A;
const OFFSET_BAUD: u32 = 0x0E;

/// SIO_MODE writable bits (0-8).
const MODE_WRITE_MASK: u16 = 0x01FF;
/// SIO_CTRL stored bits: 0-13 minus bit4 (acknowledge) and bit6 (reset), which are write-only actions.
const CONTROL_STORE_MASK: u16 = 0x3FAF;
/// SIO_CTRL.6: reset every SIO0 register to zero.
const CONTROL_RESET_BIT: u16 = 0x0040;

/// SIO_STAT.0: TX ready flag 1 (TX latch free).
const STATUS_TX_READY_1: u32 = 1 << 0;
/// SIO_STAT.1: RX FIFO not empty.
const STATUS_RX_NOT_EMPTY: u32 = 1 << 1;
/// SIO_STAT.2: TX ready flag 2 (no transfer in progress).
const STATUS_TX_READY_2: u32 = 1 << 2;

/// Hardware RX FIFO depth in bytes.
pub const RX_FIFO_CAPACITY: usize = 8;

/// SIO0 register file. See the module documentation for the ownership
/// rationale (owned inline by `PsxMemory`, no C++-visible counterpart).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Sio0State {
    mode: u16,
    control: u16,
    baud: u16,
    tx_data: u8,
    rx_fifo: [u8; RX_FIFO_CAPACITY],
    rx_head: u8,
    rx_len: u8,
    last_rx_data: u8,
}

impl Sio0State {
    /// Power-on / SIO_CTRL.6 reset value: every register zero (SIO_STAT's
    /// idle bits are derived, not stored; see [`read_status`]).
    pub const fn power_on() -> Self {
        Self {
            mode: 0,
            control: 0,
            baud: 0,
            tx_data: 0,
            rx_fifo: [0; RX_FIFO_CAPACITY],
            rx_head: 0,
            rx_len: 0,
            last_rx_data: 0,
        }
    }

    /// Resets every register to zero (power-on and SIO_CTRL.6 semantics).
    pub fn reset(&mut self) {
        *self = Self::power_on();
    }

    fn rx_dequeue(&mut self) -> Option<u8> {
        if self.rx_len == 0 {
            return None;
        }
        let value = self.rx_fifo[self.rx_head as usize];
        self.rx_head = (self.rx_head + 1) % RX_FIFO_CAPACITY as u8;
        self.rx_len -= 1;
        Some(value)
    }

    /// Transaction-side seam (Issue #543): appends a received byte to the RX
    /// FIFO. A byte arriving while the 8-byte FIFO is full is dropped.
    /// Nothing calls this in production yet (no controller/memory-card
    /// protocol is modeled); it exists so this module's tests can exercise
    /// RX FIFO ordering/overflow the same way the managed model's own tests
    /// did before this migration.
    #[cfg(test)]
    pub(crate) fn enqueue_received_byte(&mut self, value: u8) {
        if (self.rx_len as usize) < RX_FIFO_CAPACITY {
            let tail = (self.rx_head as usize + self.rx_len as usize) % RX_FIFO_CAPACITY;
            self.rx_fifo[tail] = value;
            self.rx_len += 1;
        }
    }
}

impl Default for Sio0State {
    fn default() -> Self {
        Self::power_on()
    }
}

/// SIO_RX_DATA read: pops the oldest RX FIFO byte. With an empty FIFO it
/// returns the most recently popped byte again (0 after reset).
fn read_data(state: &mut Sio0State) -> u32 {
    if let Some(value) = state.rx_dequeue() {
        state.last_rx_data = value;
    }
    state.last_rx_data as u32
}

/// SIO_STAT read. TX ready 1/2 (bits 0, 2) read 1 because no transfer can be
/// in progress in this model. RX not empty (bit 1) reflects the FIFO. Every
/// other bit (parity error, DSR/ACK, IRQ, baud timer, SIO1-only bits) reads
/// 0: no device, transfer, interrupt source or timer is modeled.
fn read_status(state: &Sio0State) -> u32 {
    let mut status = STATUS_TX_READY_1 | STATUS_TX_READY_2;
    if state.rx_len > 0 {
        status |= STATUS_RX_NOT_EMPTY;
    }
    status
}

/// True when `address` falls inside the SIO0 register window
/// `[PSX_SIO0_BASE, PSX_SIO0_END)`.
pub fn is_sio0_register(address: u32) -> bool {
    (PSX_SIO0_BASE..PSX_SIO0_END).contains(&address)
}

/// Reads the SIO0 register at the exact `address` (must satisfy
/// [`is_sio0_register`]). Any offset other than DATA/STAT/MODE/CTRL/BAUD —
/// including one that only partially overlaps a named register, e.g. the
/// upper half of SIO_STAT — is Reserved and reads a fixed 0, matching the
/// managed model this replaces.
pub fn read_register(state: &mut Sio0State, address: u32) -> u32 {
    match address - PSX_SIO0_BASE {
        OFFSET_DATA => read_data(state),
        OFFSET_STATUS => read_status(state),
        OFFSET_MODE => state.mode as u32,
        OFFSET_CONTROL => state.control as u32,
        OFFSET_BAUD => state.baud as u32,
        _ => 0,
    }
}

/// Writes `value` to the SIO0 register at the exact `address` (must satisfy
/// [`is_sio0_register`]); a Reserved offset accepts and ignores the write,
/// matching the managed model this replaces. `value`'s bits beyond the
/// target register's width are discarded exactly as the managed
/// `(byte)`/`(ushort)` truncations did.
pub fn write_register(state: &mut Sio0State, address: u32, value: u32) {
    match address - PSX_SIO0_BASE {
        OFFSET_DATA => state.tx_data = value as u8,
        OFFSET_STATUS => {}
        OFFSET_MODE => state.mode = (value as u16) & MODE_WRITE_MASK,
        OFFSET_CONTROL => {
            let value = value as u16;
            if value & CONTROL_RESET_BIT != 0 {
                state.reset();
            } else {
                state.control = value & CONTROL_STORE_MASK;
            }
        }
        OFFSET_BAUD => state.baud = value as u16,
        _ => {}
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const DATA: u32 = PSX_SIO0_BASE;
    const STAT: u32 = PSX_SIO0_BASE + OFFSET_STATUS;
    const MODE: u32 = PSX_SIO0_BASE + OFFSET_MODE;
    const CTRL: u32 = PSX_SIO0_BASE + OFFSET_CONTROL;
    const BAUD: u32 = PSX_SIO0_BASE + OFFSET_BAUD;
    const IDLE_STATUS: u32 = 0x0000_0005; // TX ready 1 + TX ready 2

    #[test]
    fn reset_reads_documented_idle_values() {
        let mut s = Sio0State::power_on();
        assert_eq!(read_register(&mut s, DATA), 0);
        assert_eq!(read_register(&mut s, STAT), IDLE_STATUS);
        assert_eq!(read_register(&mut s, MODE), 0);
        assert_eq!(read_register(&mut s, CTRL), 0);
        assert_eq!(read_register(&mut s, BAUD), 0);
    }

    #[test]
    fn mode_round_trip_masks_to_bits_0_to_8() {
        let mut s = Sio0State::power_on();
        write_register(&mut s, MODE, 0xFFFF_FFFF);
        assert_eq!(read_register(&mut s, MODE), 0x01FF);
    }

    #[test]
    fn baud_round_trip_keeps_16_bits() {
        let mut s = Sio0State::power_on();
        write_register(&mut s, BAUD, 0x1234_ABCD);
        assert_eq!(read_register(&mut s, BAUD), 0xABCD);
    }

    #[test]
    fn control_round_trip_drops_write_only_and_unused_bits() {
        let mut s = Sio0State::power_on();
        write_register(&mut s, CTRL, 0xFFBF); // everything but reset
        assert_eq!(read_register(&mut s, CTRL), 0x3FAF); // bit4 (ack), bit6, bits 14-15 not stored
    }

    #[test]
    fn control_reset_bit_zeroes_all_registers() {
        let mut s = Sio0State::power_on();
        write_register(&mut s, MODE, 0x000D);
        write_register(&mut s, BAUD, 0x0088);
        write_register(&mut s, CTRL, 0x1003);
        write_register(&mut s, DATA, 0x42);
        s.enqueue_received_byte(0x99);

        write_register(&mut s, CTRL, 0x1043); // reset wins over the other bits

        assert_eq!(read_register(&mut s, MODE), 0);
        assert_eq!(read_register(&mut s, BAUD), 0);
        assert_eq!(read_register(&mut s, CTRL), 0);
        assert_eq!(read_register(&mut s, STAT), IDLE_STATUS);
        assert_eq!(read_register(&mut s, DATA), 0);
    }

    #[test]
    fn data_write_latches_tx_byte_without_filling_rx() {
        let mut s = Sio0State::power_on();
        write_register(&mut s, DATA, 0xFFFF_FF42);
        assert_eq!(s.tx_data, 0x42);
        assert_eq!(read_register(&mut s, STAT), IDLE_STATUS); // RX stays empty
        assert_eq!(read_register(&mut s, DATA), 0);
    }

    #[test]
    fn data_read_pops_rx_fifo_in_order_and_repeats_last_byte_when_empty() {
        let mut s = Sio0State::power_on();
        s.enqueue_received_byte(0xFF);
        s.enqueue_received_byte(0x41);

        assert_eq!(read_register(&mut s, STAT) & STATUS_RX_NOT_EMPTY, STATUS_RX_NOT_EMPTY);
        assert_eq!(read_register(&mut s, DATA), 0xFF);
        assert_eq!(read_register(&mut s, DATA), 0x41);
        assert_eq!(read_register(&mut s, STAT), IDLE_STATUS);
        assert_eq!(read_register(&mut s, DATA), 0x41);
    }

    #[test]
    fn rx_fifo_drops_bytes_beyond_eight() {
        let mut s = Sio0State::power_on();
        for i in 1u8..=9 {
            s.enqueue_received_byte(i);
        }
        for i in 1u8..=8 {
            assert_eq!(read_register(&mut s, DATA), i as u32);
        }
        assert_eq!(read_register(&mut s, STAT), IDLE_STATUS);
    }

    #[test]
    fn status_is_read_only() {
        let mut s = Sio0State::power_on();
        write_register(&mut s, STAT, 0xFFFF_FFFF);
        assert_eq!(read_register(&mut s, STAT), IDLE_STATUS);
    }

    #[test]
    fn reserved_address_reads_zero_and_ignores_writes() {
        let mut s = Sio0State::power_on();
        write_register(&mut s, MODE, 0x000D);
        write_register(&mut s, CTRL, 0x1003);
        write_register(&mut s, BAUD, 0x0088);

        for address in [
            PSX_SIO0_BASE + 0x01,
            PSX_SIO0_BASE + 0x06,
            PSX_SIO0_BASE + 0x0C,
            PSX_SIO0_BASE + 0x10,
            PSX_SIO0_BASE + 0x1E,
            PSX_SIO0_BASE + 0x1F,
        ] {
            write_register(&mut s, address, 0xFFFF_FFFF);
            assert_eq!(read_register(&mut s, address), 0);
        }

        assert_eq!(read_register(&mut s, MODE), 0x000D);
        assert_eq!(read_register(&mut s, CTRL), 0x1003);
        assert_eq!(read_register(&mut s, BAUD), 0x0088);
        assert_eq!(read_register(&mut s, STAT), IDLE_STATUS);
    }

    #[test]
    fn every_address_in_window_is_deterministic_across_identical_sequences() {
        fn run() -> Vec<u32> {
            let mut s = Sio0State::power_on();
            let mut reads = Vec::new();
            let mut address = PSX_SIO0_BASE;
            while address < PSX_SIO0_END {
                write_register(&mut s, address, address.wrapping_mul(0x9E37_79B1));
                reads.push(read_register(&mut s, address));
                address += 1;
            }
            reads
        }

        assert_eq!(run(), run());
    }

    #[test]
    fn window_boundaries_are_exact() {
        assert!(!is_sio0_register(PSX_SIO0_BASE - 1));
        assert!(is_sio0_register(PSX_SIO0_BASE));
        assert!(is_sio0_register(PSX_SIO0_END - 1));
        assert!(!is_sio0_register(PSX_SIO0_END));
    }
}
