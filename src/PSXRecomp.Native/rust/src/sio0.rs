//! PS1 SIO0 (controller / memory-card serial port) model. The register file
//! (Issue #542) was migrated from the managed `PSXRecomp.Core.Runtime.Sio`
//! model (`Sio0State`/`Sio0Device`/`Sio0MmioAdapter`) so the semantics are
//! reachable from the production guest CPU load/store path
//! (`crate::memory`), not only from the managed `MemoryBus` test/BIOS-HLE
//! seam. Fixes a CodeRabbit finding on PR #548: guest `LW/SW/LH/SH/LB/SB` at
//! `0x1F801040-0x1F80105F` reached the native `PSXMemory` flat HW-register
//! fallback store, never the managed device.
//!
//! Issue #543 adds a minimal controller serial protocol on top of that
//! register file: every port this component models is permanently empty
//! (no host input integration, no memory-card protocol — see the module's
//! non-goals below), so [`handle_data_write`] gives every transaction a
//! deterministic "disconnected" response instead of undefined register
//! state. [`Sio0State::enqueue_received_byte`] is the RX-FIFO seam that
//! response is delivered through; it used to be `#[cfg(test)]`-only (Issue
//! #542 modeled no transfer), and is now called from production.
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
/// SIO_CTRL.1: `/JOYn` output (device select line). 1 = a device on this
/// port is selected/clocked; 0 = deselected. Already part of
/// `CONTROL_STORE_MASK` (Issue #542); Issue #543 gives this bit protocol
/// meaning: a transaction only proceeds while it is set, and each edge
/// starts a fresh transaction (see [`handle_control_write`]).
const CONTROL_SELECT_BIT: u16 = 0x0002;

/// The only controller command this component recognizes (`0x42`, "read
/// pad", psx-spx / nocash PSX docs). Any other command byte is classified
/// [`CommandClassification::UnsupportedCommand`]; the response byte is
/// identical either way (see [`handle_data_write`]).
const COMMAND_READ_PAD: u8 = 0x42;

/// The byte a disconnected port's data line reads back for every
/// transaction byte: real SIO0 hardware leaves the line pulled high when no
/// device's `/ACK` drives it, so an empty port reads `0xFF` regardless of
/// position or command (psx-spx "Controller/Memory Card protocol" —
/// no-controller-connected behavior).
const DISCONNECTED_RESPONSE_BYTE: u8 = 0xFF;

/// SIO_STAT.0: TX ready flag 1 (TX latch free).
const STATUS_TX_READY_1: u32 = 1 << 0;
/// SIO_STAT.1: RX FIFO not empty.
const STATUS_RX_NOT_EMPTY: u32 = 1 << 1;
/// SIO_STAT.2: TX ready flag 2 (no transfer in progress).
const STATUS_TX_READY_2: u32 = 1 << 2;

/// Hardware RX FIFO depth in bytes.
pub const RX_FIFO_CAPACITY: usize = 8;

/// Diagnostic classification of the current transaction's command byte
/// (Issue #543 acceptance criteria: an unrecognized command must be
/// classified explicitly — production-visible, not silently treated as
/// known). Never affects the disconnected-slot response byte itself, which
/// is always `0xFF` regardless of this value — see [`handle_data_write`].
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CommandClassification {
    /// No command byte has been seen since the last transaction reset
    /// (selection edge or `SIO_CTRL.6`).
    None,
    /// The command byte was [`COMMAND_READ_PAD`].
    RecognizedReadPad,
    /// The command byte was anything else. Carries the actual byte so a
    /// production caller can log/inspect it, not just the fact of mismatch.
    UnsupportedCommand(u8),
}

impl CommandClassification {
    /// The discriminant (`0`/`1`/`2`) exposed across the native boundary by
    /// [`crate::memory::psx_memory_get_sio0_command_status`].
    fn status_code(self) -> u8 {
        match self {
            Self::None => 0,
            Self::RecognizedReadPad => 1,
            Self::UnsupportedCommand(_) => 2,
        }
    }

    /// The unsupported command byte, or `0` when not
    /// [`Self::UnsupportedCommand`] (callers must check
    /// [`Self::status_code`]/the status accessor first — `0` is also a
    /// legitimate byte value when it *is* unsupported).
    fn unsupported_byte(self) -> u8 {
        match self {
            Self::UnsupportedCommand(byte) => byte,
            Self::None | Self::RecognizedReadPad => 0,
        }
    }
}

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
    /// Bytes sent since the current selection edge (Issue #543); saturates,
    /// since only 0 (address byte) and 1 (command byte) are inspected.
    transfer_byte_index: u8,
    /// Diagnostic classification of the transaction's command byte
    /// (`transfer_byte_index == 1`). See [`CommandClassification`].
    last_command: CommandClassification,
    /// Unacknowledged "byte received" (IRQ7) latch (Issue #543), mirroring
    /// `TimerChannel::irq_flag`'s edge-latch pattern.
    irq_pending: bool,
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
            transfer_byte_index: 0,
            last_command: CommandClassification::None,
            irq_pending: false,
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

    /// Transaction-side seam: appends a received byte to the RX FIFO. A byte
    /// arriving while the 8-byte FIFO is full is dropped. Called from
    /// production by [`handle_data_write`] (Issue #543); also exercised
    /// directly by this module's own RX FIFO ordering/overflow tests.
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
        OFFSET_DATA => handle_data_write(state, value as u8),
        OFFSET_STATUS => {}
        OFFSET_MODE => state.mode = (value as u16) & MODE_WRITE_MASK,
        OFFSET_CONTROL => handle_control_write(state, value as u16),
        OFFSET_BAUD => state.baud = value as u16,
        _ => {}
    }
}

/// SIO_DATA (TX) write: latches the byte (unchanged from Issue #542), then
/// runs the minimal controller protocol (Issue #543) while the port is
/// selected (`SIO_CTRL.1`, [`CONTROL_SELECT_BIT`]). Deselected, this is
/// exactly Issue #542's behavior: a latch with no RX/IRQ side effect.
fn handle_data_write(state: &mut Sio0State, byte: u8) {
    state.tx_data = byte;
    if state.control & CONTROL_SELECT_BIT == 0 {
        return;
    }

    if state.transfer_byte_index == 1 {
        state.last_command = if byte == COMMAND_READ_PAD {
            CommandClassification::RecognizedReadPad
        } else {
            CommandClassification::UnsupportedCommand(byte)
        };
    }
    state.transfer_byte_index = state.transfer_byte_index.saturating_add(1);

    // ponytail: every port this component models is permanently empty (no
    // host input integration, no real pad/memory-card protocol — Issue
    // #543 non-goals), so every transaction byte reads back the same fixed
    // disconnected-slot value, regardless of position or command. Upgrade
    // path: a real device model would branch here on port/command instead
    // of always enqueueing DISCONNECTED_RESPONSE_BYTE.
    state.enqueue_received_byte(DISCONNECTED_RESPONSE_BYTE);

    // ponytail: this simplified model signals "byte received" (IRQ7) for
    // every transaction byte, synchronously, rather than modeling the real
    // `/ACK` pulse a physical device would drive (which, from an empty
    // port, would mean IRQ7 never fires at all). That keeps "transfer
    // complete" deterministic and testable without cycle-exact ACK timing
    // (explicitly out of scope). Upgrade path: gate this on a modeled
    // device's real ACK if/when a real controller protocol is added.
    state.irq_pending = true;
}

/// SIO_CTRL write: the reset bit (unchanged from Issue #542) wins over
/// everything else. Otherwise, stores the masked value and, on either edge
/// of the select bit ([`CONTROL_SELECT_BIT`]), starts a fresh transaction
/// (Issue #543): deselecting mid-transfer abandons it, and (re)selecting
/// always begins counting bytes from 0, so a second transaction cannot
/// observe stale byte-position/command state from the first.
fn handle_control_write(state: &mut Sio0State, value: u16) {
    if value & CONTROL_RESET_BIT != 0 {
        state.reset();
        return;
    }

    let was_selected = state.control & CONTROL_SELECT_BIT != 0;
    state.control = value & CONTROL_STORE_MASK;
    let is_selected = state.control & CONTROL_SELECT_BIT != 0;
    if was_selected != is_selected {
        state.transfer_byte_index = 0;
        state.last_command = CommandClassification::None;
    }
}

/// Returns whether SIO0 has an unacknowledged "byte received" (IRQ7) latch
/// (Issue #543).
pub fn is_interrupt_pending(state: &Sio0State) -> bool {
    state.irq_pending
}

/// Clears SIO0's "byte received" (IRQ7) latch (Issue #543).
pub fn clear_interrupt_pending(state: &mut Sio0State) {
    state.irq_pending = false;
}

/// Production-visible diagnostic accessor (Issue #543 acceptance criteria:
/// an unrecognized command must be classified explicitly, not silently
/// treated as known). See [`CommandClassification`].
pub fn last_command_classification(state: &Sio0State) -> CommandClassification {
    state.last_command
}

/// The discriminant [`crate::memory::psx_memory_get_sio0_command_status`]
/// exposes across the native boundary: `0` = [`CommandClassification::None`],
/// `1` = [`CommandClassification::RecognizedReadPad`], `2` =
/// [`CommandClassification::UnsupportedCommand`].
pub fn command_status_code(state: &Sio0State) -> u8 {
    state.last_command.status_code()
}

/// The command byte [`crate::memory::psx_memory_get_sio0_last_command_byte`]
/// exposes: the actual byte when the classification is
/// [`CommandClassification::UnsupportedCommand`], else `0` (callers must
/// check [`command_status_code`] first: `0` is also a legitimate byte value
/// when it *is* unsupported).
pub fn last_unsupported_command_byte(state: &Sio0State) -> u8 {
    state.last_command.unsupported_byte()
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

    // Issue #543: minimal controller serial protocol (disconnected-pad response).

    const SELECT: u32 = 0x0003; // TXEN | SIO_CTRL.1 (select)
    const DESELECT: u32 = 0x0001; // TXEN only, deselected

    #[test]
    fn unselected_data_write_never_signals_irq_or_fills_rx() {
        // Issue #542's original behavior, unchanged: with nothing selected,
        // a TX write is a bare latch.
        let mut s = Sio0State::power_on();
        write_register(&mut s, DATA, COMMAND_READ_PAD as u32);
        assert!(!is_interrupt_pending(&s));
        assert_eq!(read_register(&mut s, STAT), IDLE_STATUS);
    }

    #[test]
    fn disconnected_exchange_every_byte_reads_0xff_and_signals_irq_once_selected() {
        let mut s = Sio0State::power_on();
        write_register(&mut s, CTRL, SELECT);
        assert!(!is_interrupt_pending(&s), "selecting alone must not signal a byte-received IRQ");

        write_register(&mut s, DATA, 0x01); // address byte
        assert!(is_interrupt_pending(&s));
        assert_eq!(read_register(&mut s, STAT) & STATUS_RX_NOT_EMPTY, STATUS_RX_NOT_EMPTY);
        clear_interrupt_pending(&mut s);
        assert!(!is_interrupt_pending(&s));
        assert_eq!(read_register(&mut s, DATA), DISCONNECTED_RESPONSE_BYTE as u32);
        assert_eq!(read_register(&mut s, STAT), IDLE_STATUS, "RX-ready clears once the byte is read");

        write_register(&mut s, DATA, COMMAND_READ_PAD as u32); // command byte
        assert_eq!(last_command_classification(&s), CommandClassification::RecognizedReadPad);
        assert_eq!(command_status_code(&s), 1);
        assert!(is_interrupt_pending(&s));
        assert_eq!(read_register(&mut s, DATA), DISCONNECTED_RESPONSE_BYTE as u32);
    }

    #[test]
    fn unrecognized_command_is_classified_explicitly_but_response_is_unchanged() {
        let mut s = Sio0State::power_on();
        write_register(&mut s, CTRL, SELECT);

        write_register(&mut s, DATA, 0x01); // address byte, never classified
        assert_eq!(
            last_command_classification(&s),
            CommandClassification::None,
            "the address byte alone must not flip the classification"
        );

        write_register(&mut s, DATA, 0x99); // unrecognized command
        assert_eq!(last_command_classification(&s), CommandClassification::UnsupportedCommand(0x99));
        assert_eq!(command_status_code(&s), 2, "production-visible status code for Unsupported");
        assert_eq!(last_unsupported_command_byte(&s), 0x99, "the actual command byte is retained, not just a flag");

        // Disconnected response never depends on recognition: nothing ever
        // responds, known command or not.
        assert_eq!(read_register(&mut s, DATA), DISCONNECTED_RESPONSE_BYTE as u32);
        assert_eq!(read_register(&mut s, DATA), DISCONNECTED_RESPONSE_BYTE as u32);
        // Deterministic completion: the transaction did not hang, and IRQ7
        // still latched exactly as it does for a recognized command.
        assert!(is_interrupt_pending(&s));
    }

    #[test]
    fn deselecting_mid_transaction_abandons_it_and_a_fresh_selection_starts_over() {
        let mut s = Sio0State::power_on();
        write_register(&mut s, CTRL, SELECT);
        write_register(&mut s, DATA, 0x01); // byte 0 (address)
        write_register(&mut s, DATA, 0x99); // byte 1 (command) -> unrecognized
        assert_eq!(last_command_classification(&s), CommandClassification::UnsupportedCommand(0x99));

        write_register(&mut s, CTRL, DESELECT); // deselect: abandon the transaction
        clear_interrupt_pending(&mut s);

        write_register(&mut s, CTRL, SELECT); // reselect: fresh transaction
        write_register(&mut s, DATA, 0x99); // byte 0 (address) of the NEW transaction
        assert_eq!(
            last_command_classification(&s),
            CommandClassification::None,
            "a fresh transaction must not inherit the previous one's classification"
        );
        write_register(&mut s, DATA, COMMAND_READ_PAD as u32); // byte 1 (command)
        assert_eq!(last_command_classification(&s), CommandClassification::RecognizedReadPad);
    }

    #[test]
    fn repeated_transaction_after_reset_behaves_identically() {
        fn run_transaction(s: &mut Sio0State) -> (u32, u32, CommandClassification) {
            write_register(s, CTRL, SELECT);
            write_register(s, DATA, 0x01);
            let first = read_register(s, DATA);
            write_register(s, DATA, COMMAND_READ_PAD as u32);
            let second = read_register(s, DATA);
            let recognized = last_command_classification(s);
            write_register(s, CTRL, CONTROL_RESET_BIT as u32); // full reset between polls
            (first, second, recognized)
        }

        let mut s = Sio0State::power_on();
        let a = run_transaction(&mut s);
        let b = run_transaction(&mut s);
        assert_eq!(a, b);
        assert_eq!(
            a,
            (
                DISCONNECTED_RESPONSE_BYTE as u32,
                DISCONNECTED_RESPONSE_BYTE as u32,
                CommandClassification::RecognizedReadPad
            )
        );
    }
}
