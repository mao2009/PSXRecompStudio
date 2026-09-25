//! PS1 guest memory (2 MiB RAM + its low-8-MiB mirror, the 1 KiB scratchpad,
//! the BIOS backing store, and the HW-register fallback store), migrated
//! from the C++ `PSXMemory` (Issue #492).
//!
//! Semantics are pinned to the `main` state established by Issue #386 / PR
//! #491: the low-8-MiB RAM mirror, the independent (non-RAM-aliased)
//! scratchpad, MMIO routing to the Rust [`crate::dma`]/[`crate::timer`]/
//! [`crate::interrupt`] controllers, and the DICR sub-word write-1-to-clear
//! preservation fix (see [`write8`](PsxMemory::write8)/
//! [`write16`](PsxMemory::write16)).
//!
//! ## Ownership
//!
//! RAM+scratchpad+BIOS+HW-register storage is ~2.6 MiB: far too large to
//! pass by value on every call the way the DMA/Timer/Interrupt PODs are
//! (`docs/development/rust-ffi-contract.md` §3). This module instead follows
//! the `PSXCore_Create`/`PSXCore_Destroy` opaque-handle pattern: C++'s
//! `PSXMemory` (`src/psx_memory.h`) holds a `PsxMemory*` handle created by
//! [`psx_memory_create`] and released exactly once via [`psx_memory_destroy`].
//! The handle's fields are not part of the ABI.
//!
//! ## Controller routing
//!
//! DMA/Timer/Interrupt register semantics already live in [`crate::dma`],
//! [`crate::timer`], and [`crate::interrupt`] (Issues #488/#486/#484). This
//! module does not reimplement them: [`read_controller32`]/
//! [`write_controller32`] call those modules' existing pure functions
//! directly (same crate, no FFI hop) and never duplicate their register
//! semantics. The controller state itself stays owned by the C++ `PSXCore`
//! (unchanged); it is borrowed here only for the duration of each call via
//! the same three independently-nullable pointers `PSXMemory::AttachControllers`
//! has always taken, matching the "unattached controller falls back to the
//! flat HW-register store" behavior.
//!
//! These exported symbols are internal to `PSXRecomp.Native`: `src/psx_memory.h`
//! declares them for `PSXMemory`'s own use. They are not P/Invoked by managed
//! code and are not part of `include/psx_core.h` / `ABI_VERSION`.
//!
//! ## Panic-freedom
//!
//! Every address range check below is a saturating/inclusive comparison
//! against compile-time constants, and every buffer index used afterward is
//! proven in range by that check before it is used, so no slice index here
//! can panic (matching the panic-free style already established by
//! [`crate::dma`]/[`crate::timer`]/[`crate::interrupt`], which need no
//! `catch_unwind` for the same reason). Allocation uses the global allocator
//! through two small fallible helpers so `psx_memory_create` can honor its
//! documented null-on-allocation-failure contract instead of invoking Rust's
//! infallible allocation path. Other unsafety is limited to dereferencing the
//! caller-supplied handle/controller pointers in the thin `unsafe extern "C"`
//! wrappers at the bottom of this file; address decoding and memory semantics
//! remain plain safe Rust.

use std::alloc::{alloc, alloc_zeroed, Layout};
use std::ptr;

use crate::dma::{psx_dma_read_register, psx_dma_write_register, DmaState};
use crate::interrupt::{psx_interrupt_read_register, psx_interrupt_write_register, InterruptState};
use crate::sio0::{is_sio0_register, read_register as sio0_read_register, write_register as sio0_write_register, Sio0State};
use crate::timer::{psx_timer_read_register, psx_timer_write_register, TimerState};

/// PS1 main RAM size (2 MiB). Must match `PSX_RAM_SIZE` in `psx_cpu.h`.
pub const PSX_RAM_SIZE: u32 = 2 * 1024 * 1024;
/// PS1 BIOS ROM size (512 KiB). Must match `PSX_BIOS_SIZE` in `psx_cpu.h`.
pub const PSX_BIOS_SIZE: u32 = 512 * 1024;
/// Fallback HW-register window size (8 KiB). Must match `PSX_HW_REG_SIZE` in `psx_cpu.h`.
pub const PSX_HW_REG_SIZE: u32 = 8 * 1024;
/// Scratchpad size (1 KiB). Must match `PSX_SCRATCHPAD_SIZE` in `psx_memory.h`.
pub const PSX_SCRATCHPAD_SIZE: u32 = 0x0000_0400;

/// BIOS ROM base address. Must match `PSX_BIOS_BASE` in `psx_memory.h`.
pub const PSX_BIOS_BASE: u32 = 0x1FC0_0000;
/// HW-register window base address. Must match `PSX_HW_REG_BASE` in `psx_memory.h`.
pub const PSX_HW_REG_BASE: u32 = 0x1F80_1000;
/// Exclusive end of the low-8-MiB RAM mirror window (Issue #386). Must match
/// `PSX_RAM_MIRROR_END` in `psx_memory.h`.
pub const PSX_RAM_MIRROR_END: u32 = 0x0080_0000;
/// Scratchpad base address. Must match `PSX_SCRATCHPAD_BASE` in `psx_memory.h`.
pub const PSX_SCRATCHPAD_BASE: u32 = 0x1F80_0000;

/// I_STAT absolute address. Must match `PSX_INTR_I_STAT` in `psx_memory.h`.
const PSX_INTR_I_STAT: u32 = 0x1F80_1070;
/// I_MASK absolute address. Must match `PSX_INTR_I_MASK` in `psx_memory.h`.
const PSX_INTR_I_MASK: u32 = 0x1F80_1074;
/// DMA register window base. Must match `PSX_DMA_BASE` in `psx_memory.h`.
const PSX_DMA_BASE: u32 = 0x1F80_1080;
/// DICR absolute address (inclusive end of the DMA register window). Must
/// match `PSX_DMA_REGION_END` in `psx_memory.h`.
const PSX_DMA_REGION_END: u32 = 0x1F80_10F4;
/// Timer register window base. Must match `PSX_TIMER_BASE` in `psx_memory.h`.
const PSX_TIMER_BASE: u32 = 0x1F80_1100;
/// Inclusive end of the timer register window. Must match
/// `PSX_TIMER_REGION_END` in `psx_memory.h`.
const PSX_TIMER_REGION_END: u32 = PSX_TIMER_BASE + 3 * 0x10 - 1;

/// DICR bits 0-6: per-channel write-1-to-clear interrupt flags. Mirrors the
/// private constant of the same shape in [`crate::dma`].
const DICR_FLAGS_MASK: u32 = 0x0000_007F;

/// Where an address decoded to, with the byte offset into that region's
/// backing buffer (already proven in range for the access width requested).
enum Region {
    Ram(usize),
    Scratchpad(usize),
    Bios(usize),
    HwReg(usize),
    Unmapped,
}

/// Decodes `address` for an access of `width` bytes (1, 2, or 4), returning
/// the region and offset if the full `[address, address + width)` span is
/// in range. RAM decodes through the low-8-MiB mirror (`address &
/// (PSX_RAM_SIZE - 1)`) before the width check, matching `PSXMemory`'s mirror
/// semantics: a mirrored access that would run off the end of the physical
/// 2 MiB buffer is unmapped rather than wrapping.
fn locate(address: u32, width: u32) -> Region {
    if address < PSX_RAM_MIRROR_END {
        let idx = address & (PSX_RAM_SIZE - 1);
        return if idx <= PSX_RAM_SIZE - width {
            Region::Ram(idx as usize)
        } else {
            Region::Unmapped
        };
    }
    if in_range(address, PSX_SCRATCHPAD_BASE, PSX_SCRATCHPAD_SIZE, width) {
        return Region::Scratchpad((address - PSX_SCRATCHPAD_BASE) as usize);
    }
    if in_range(address, PSX_BIOS_BASE, PSX_BIOS_SIZE, width) {
        return Region::Bios((address - PSX_BIOS_BASE) as usize);
    }
    if in_range(address, PSX_HW_REG_BASE, PSX_HW_REG_SIZE, width) {
        return Region::HwReg((address - PSX_HW_REG_BASE) as usize);
    }
    Region::Unmapped
}

/// True when `[address, address + width)` fits entirely inside
/// `[base, base + size)`.
fn in_range(address: u32, base: u32, size: u32, width: u32) -> bool {
    address >= base && address <= base + size - width
}

fn load16_le(bytes: &[u8]) -> u16 {
    u16::from_le_bytes([bytes[0], bytes[1]])
}

fn store16_le(bytes: &mut [u8], value: u16) {
    bytes.copy_from_slice(&value.to_le_bytes());
}

fn load32_le(bytes: &[u8]) -> u32 {
    u32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]])
}

fn store32_le(bytes: &mut [u8], value: u32) {
    bytes.copy_from_slice(&value.to_le_bytes());
}

/// Reads the 32-bit controller register at `address`, or `None` when
/// `address` is not a DMA/Timer/Interrupt register, or when it is but the
/// matching controller pointer is `None` (unattached). A timer read can
/// mutate `*timers` (reading MODE clears its target/overflow flags), exactly
/// mirroring `PSXMemory::ReadController32`.
fn read_controller32(
    address: u32,
    dma: Option<&mut DmaState>,
    timers: Option<&mut TimerState>,
    interrupts: Option<&mut InterruptState>,
) -> Option<u32> {
    if address == PSX_INTR_I_STAT || address == PSX_INTR_I_MASK {
        let interrupts = interrupts?;
        return Some(psx_interrupt_read_register(*interrupts, address));
    }
    if (PSX_DMA_BASE..=PSX_DMA_REGION_END).contains(&address) {
        let dma = dma?;
        return Some(psx_dma_read_register(*dma, address));
    }
    if (PSX_TIMER_BASE..=PSX_TIMER_REGION_END).contains(&address) {
        let timers = timers?;
        let result = psx_timer_read_register(*timers, address);
        *timers = result.state;
        return Some(result.value);
    }
    None
}

/// Writes `value` to the 32-bit controller register at `address`. Returns
/// `false` (and writes nothing) when `address` is not a DMA/Timer/Interrupt
/// register, or when it is but the matching controller pointer is `None`
/// (unattached), exactly mirroring `PSXMemory::WriteController32`.
fn write_controller32(
    address: u32,
    value: u32,
    dma: Option<&mut DmaState>,
    timers: Option<&mut TimerState>,
    interrupts: Option<&mut InterruptState>,
) -> bool {
    if address == PSX_INTR_I_STAT || address == PSX_INTR_I_MASK {
        let Some(interrupts) = interrupts else {
            return false;
        };
        *interrupts = psx_interrupt_write_register(*interrupts, address, value);
        return true;
    }
    if (PSX_DMA_BASE..=PSX_DMA_REGION_END).contains(&address) {
        let Some(dma) = dma else {
            return false;
        };
        *dma = psx_dma_write_register(*dma, address, value);
        return true;
    }
    if (PSX_TIMER_BASE..=PSX_TIMER_REGION_END).contains(&address) {
        let Some(timers) = timers else {
            return false;
        };
        *timers = psx_timer_write_register(*timers, address, value);
        return true;
    }
    false
}

/// The Rust-owned guest-memory backing store behind the opaque
/// `PsxMemory*` handle. See the module documentation for the ownership
/// rationale.
pub struct PsxMemory {
    ram: Box<[u8]>,
    scratchpad: Box<[u8]>,
    bios: Box<[u8]>,
    hw_regs: Box<[u8]>,
    /// SIO0 register-only model (Issue #542). Owned here rather than by the
    /// C++ `PSXCore` — see `crate::sio0`'s module documentation for why.
    sio0: Sio0State,
}

fn try_zeroed_bytes(len: usize) -> Option<Box<[u8]>> {
    let layout = Layout::array::<u8>(len).ok()?;

    // SAFETY: `layout` is a valid non-zero byte-array layout. The returned
    // allocation comes from the global allocator, which is exactly what
    // Box<[u8]> expects when it later deallocates the slice.
    let raw = unsafe { alloc_zeroed(layout) };
    if raw.is_null() {
        return None;
    }

    // SAFETY: `raw` points to `len` initialized zero bytes allocated with
    // the global allocator using the matching array layout above.
    Some(unsafe { Box::from_raw(ptr::slice_from_raw_parts_mut(raw, len)) })
}

impl PsxMemory {
    fn try_new() -> Option<*mut Self> {
        let value = Self {
            ram: try_zeroed_bytes(PSX_RAM_SIZE as usize)?,
            scratchpad: try_zeroed_bytes(PSX_SCRATCHPAD_SIZE as usize)?,
            bios: try_zeroed_bytes(PSX_BIOS_SIZE as usize)?,
            hw_regs: try_zeroed_bytes(PSX_HW_REG_SIZE as usize)?,
            sio0: Sio0State::power_on(),
        };

        let layout = Layout::new::<Self>();

        // SAFETY: `layout` is the exact layout Box<Self> uses with the global
        // allocator. A null result is reported to the C++ caller as allocation
        // failure instead of going through Rust's infallible Box::new path.
        let raw = unsafe { alloc(layout) }.cast::<Self>();
        if raw.is_null() {
            return None;
        }

        // SAFETY: `raw` is valid, properly aligned storage for one Self and
        // is currently uninitialized. Ownership of `value` moves into it.
        unsafe { ptr::write(raw, value) };
        Some(raw)
    }

    /// Zeroes RAM, scratchpad, BIOS, and the HW-register fallback store.
    fn reset(&mut self) {
        self.ram.fill(0);
        self.scratchpad.fill(0);
        self.bios.fill(0);
        self.hw_regs.fill(0);
        self.sio0.reset();
    }

    /// Reads a little-endian 32-bit value; 0 outside every mapped region.
    fn read32(
        &mut self,
        address: u32,
        dma: Option<&mut DmaState>,
        timers: Option<&mut TimerState>,
        interrupts: Option<&mut InterruptState>,
    ) -> u32 {
        match locate(address, 4) {
            Region::Ram(i) => load32_le(&self.ram[i..i + 4]),
            Region::Scratchpad(i) => load32_le(&self.scratchpad[i..i + 4]),
            Region::Bios(i) => load32_le(&self.bios[i..i + 4]),
            Region::HwReg(i) => {
                if is_sio0_register(address) {
                    sio0_read_register(&mut self.sio0, address)
                } else {
                    match read_controller32(address, dma, timers, interrupts) {
                        Some(value) => value,
                        None => load32_le(&self.hw_regs[i..i + 4]),
                    }
                }
            }
            Region::Unmapped => 0,
        }
    }

    /// Writes a little-endian 32-bit value; a no-op outside every mapped region.
    fn write32(
        &mut self,
        address: u32,
        value: u32,
        dma: Option<&mut DmaState>,
        timers: Option<&mut TimerState>,
        interrupts: Option<&mut InterruptState>,
    ) {
        match locate(address, 4) {
            Region::Ram(i) => store32_le(&mut self.ram[i..i + 4], value),
            Region::Scratchpad(i) => store32_le(&mut self.scratchpad[i..i + 4], value),
            Region::Bios(i) => store32_le(&mut self.bios[i..i + 4], value),
            Region::HwReg(i) => {
                if is_sio0_register(address) {
                    sio0_write_register(&mut self.sio0, address, value);
                } else if !write_controller32(address, value, dma, timers, interrupts) {
                    store32_le(&mut self.hw_regs[i..i + 4], value);
                }
            }
            Region::Unmapped => {}
        }
    }

    /// Reads a little-endian 16-bit value; 0 outside every mapped region. A
    /// HW-register access covers any halfword inside the containing 32-bit
    /// controller register. A SIO0 access dispatches at its exact address
    /// instead (see `crate::sio0`'s module documentation).
    fn read16(
        &mut self,
        address: u32,
        dma: Option<&mut DmaState>,
        timers: Option<&mut TimerState>,
        interrupts: Option<&mut InterruptState>,
    ) -> u16 {
        match locate(address, 2) {
            Region::Ram(i) => load16_le(&self.ram[i..i + 2]),
            Region::Scratchpad(i) => load16_le(&self.scratchpad[i..i + 2]),
            Region::Bios(i) => load16_le(&self.bios[i..i + 2]),
            Region::HwReg(i) => {
                if is_sio0_register(address) {
                    (sio0_read_register(&mut self.sio0, address) & 0xFFFF) as u16
                } else {
                    let word_addr = address & !2u32;
                    match read_controller32(word_addr, dma, timers, interrupts) {
                        Some(value) => ((value >> (8 * (address & 2))) & 0xFFFF) as u16,
                        None => load16_le(&self.hw_regs[i..i + 2]),
                    }
                }
            }
            Region::Unmapped => 0,
        }
    }

    /// Writes a little-endian 16-bit value; a no-op outside every mapped
    /// region. A sub-word write to a controller register first reads the
    /// full 32-bit register back (a timer read can have side effects, so
    /// this reuses the same read path as [`Self::read16`]), masks DICR's
    /// write-1-to-clear flag bits (0-6) out of that echoed value so an
    /// untouched flag is not reinterpreted as "clear" by the merge below,
    /// merges in the target halfword, and writes the full word back
    /// (CodeRabbit PR #491; see the DICR regression tests).
    fn write16(
        &mut self,
        address: u32,
        value: u16,
        mut dma: Option<&mut DmaState>,
        mut timers: Option<&mut TimerState>,
        mut interrupts: Option<&mut InterruptState>,
    ) {
        match locate(address, 2) {
            Region::Ram(i) => store16_le(&mut self.ram[i..i + 2], value),
            Region::Scratchpad(i) => store16_le(&mut self.scratchpad[i..i + 2], value),
            Region::Bios(i) => store16_le(&mut self.bios[i..i + 2], value),
            Region::HwReg(i) => {
                if is_sio0_register(address) {
                    sio0_write_register(&mut self.sio0, address, value as u32);
                } else {
                    let word_addr = address & !2u32;
                    match read_controller32(word_addr, dma.as_deref_mut(), timers.as_deref_mut(), interrupts.as_deref_mut()) {
                        Some(mut value32) => {
                            if word_addr == PSX_DMA_REGION_END {
                                value32 &= !DICR_FLAGS_MASK;
                            }
                            let shift = 8 * (address & 2);
                            let merged = (value32 & !(0xFFFFu32 << shift)) | (u32::from(value) << shift);
                            write_controller32(word_addr, merged, dma, timers, interrupts);
                        }
                        None => store16_le(&mut self.hw_regs[i..i + 2], value),
                    }
                }
            }
            Region::Unmapped => {}
        }
    }

    /// Reads a byte; 0 outside every mapped region. A HW-register access
    /// covers any byte inside the containing 32-bit controller register. A
    /// SIO0 access dispatches at its exact address instead (see
    /// `crate::sio0`'s module documentation).
    fn read8(
        &mut self,
        address: u32,
        dma: Option<&mut DmaState>,
        timers: Option<&mut TimerState>,
        interrupts: Option<&mut InterruptState>,
    ) -> u8 {
        match locate(address, 1) {
            Region::Ram(i) => self.ram[i],
            Region::Scratchpad(i) => self.scratchpad[i],
            Region::Bios(i) => self.bios[i],
            Region::HwReg(i) => {
                if is_sio0_register(address) {
                    (sio0_read_register(&mut self.sio0, address) & 0xFF) as u8
                } else {
                    let word_addr = address & !3u32;
                    match read_controller32(word_addr, dma, timers, interrupts) {
                        Some(value) => ((value >> (8 * (address & 3))) & 0xFF) as u8,
                        None => self.hw_regs[i],
                    }
                }
            }
            Region::Unmapped => 0,
        }
    }

    /// Writes a byte; a no-op outside every mapped region. See
    /// [`Self::write16`] for the sub-word controller-register merge and the
    /// DICR write-1-to-clear preservation it shares with this method.
    fn write8(
        &mut self,
        address: u32,
        value: u8,
        mut dma: Option<&mut DmaState>,
        mut timers: Option<&mut TimerState>,
        mut interrupts: Option<&mut InterruptState>,
    ) {
        match locate(address, 1) {
            Region::Ram(i) => self.ram[i] = value,
            Region::Scratchpad(i) => self.scratchpad[i] = value,
            Region::Bios(i) => self.bios[i] = value,
            Region::HwReg(i) => {
                if is_sio0_register(address) {
                    sio0_write_register(&mut self.sio0, address, value as u32);
                } else {
                    let word_addr = address & !3u32;
                    match read_controller32(word_addr, dma.as_deref_mut(), timers.as_deref_mut(), interrupts.as_deref_mut()) {
                        Some(mut value32) => {
                            if word_addr == PSX_DMA_REGION_END {
                                value32 &= !DICR_FLAGS_MASK;
                            }
                            let shift = 8 * (address & 3);
                            let merged = (value32 & !(0xFFu32 << shift)) | (u32::from(value) << shift);
                            write_controller32(word_addr, merged, dma, timers, interrupts);
                        }
                        None => self.hw_regs[i] = value,
                    }
                }
            }
            Region::Unmapped => {}
        }
    }
}

/// Allocates a new, zeroed guest-memory backing store.
///
/// Mirrors the `PSXCore_Create` handle pattern
/// (`docs/development/rust-ffi-contract.md` §3): returns an opaque handle
/// owned by the caller, or null on allocation failure. Must be released
/// exactly once via [`psx_memory_destroy`].
#[no_mangle]
pub extern "C" fn psx_memory_create() -> *mut PsxMemory {
    PsxMemory::try_new().unwrap_or(ptr::null_mut())
}

/// Releases a handle previously returned by [`psx_memory_create`]. A null
/// `mem` is a documented no-op.
///
/// # Safety
///
/// `mem` must be either null or a handle returned by [`psx_memory_create`]
/// that has not already been destroyed.
#[no_mangle]
pub unsafe extern "C" fn psx_memory_destroy(mem: *mut PsxMemory) {
    if mem.is_null() {
        return;
    }
    // SAFETY: caller's documented contract above.
    drop(unsafe { Box::from_raw(mem) });
}

/// Zeroes RAM, scratchpad, BIOS, and the HW-register fallback store. A null
/// `mem` is a documented no-op.
///
/// # Safety
///
/// `mem` must be either null or a valid, live handle for the duration of the call.
#[no_mangle]
pub unsafe extern "C" fn psx_memory_reset(mem: *mut PsxMemory) {
    // SAFETY: caller's documented contract above.
    let Some(mem) = (unsafe { mem.as_mut() }) else {
        return;
    };
    mem.reset();
}

/// Returns non-zero when SIO0 has an unacknowledged "byte received" (IRQ7)
/// latch (Issue #543); 0 when `mem` is null. Not P/Invoked directly: called
/// only from `PSXMemory::GetSio0InterruptPending` (`psx_memory.h`), the same
/// polling boundary `psx_dma_get_interrupt_pending`/
/// `psx_timer_get_interrupt_pending` already use.
///
/// # Safety
///
/// `mem` must be either null or a valid, live handle for the duration of the call.
#[no_mangle]
pub unsafe extern "C" fn psx_memory_get_sio0_interrupt_pending(mem: *const PsxMemory) -> u8 {
    // SAFETY: caller's documented contract above.
    match unsafe { mem.as_ref() } {
        Some(mem) => crate::sio0::is_interrupt_pending(&mem.sio0) as u8,
        None => 0,
    }
}

/// Clears SIO0's "byte received" (IRQ7) latch (Issue #543). A null `mem` is
/// a documented no-op.
///
/// # Safety
///
/// `mem` must be either null or a valid, live handle for the duration of the call.
#[no_mangle]
pub unsafe extern "C" fn psx_memory_clear_sio0_interrupt(mem: *mut PsxMemory) {
    // SAFETY: caller's documented contract above.
    let Some(mem) = (unsafe { mem.as_mut() }) else {
        return;
    };
    crate::sio0::clear_interrupt_pending(&mut mem.sio0);
}

/// Returns SIO0's last-transaction command classification (Issue #543
/// diagnostic observability): `0` = no command byte seen since the last
/// transaction reset, `1` = recognized (the one supported command), `2` =
/// unsupported/unrecognized — see
/// [`crate::sio0::CommandClassification`]/[`crate::sio0::command_status_code`].
/// `0` when `mem` is null. Not P/Invoked directly: called only from
/// `PSXMemory::GetSio0CommandStatus` (`psx_memory.h`).
///
/// # Safety
///
/// `mem` must be either null or a valid, live handle for the duration of the call.
#[no_mangle]
pub unsafe extern "C" fn psx_memory_get_sio0_command_status(mem: *const PsxMemory) -> u8 {
    // SAFETY: caller's documented contract above.
    match unsafe { mem.as_ref() } {
        Some(mem) => crate::sio0::command_status_code(&mem.sio0),
        None => 0,
    }
}

/// Returns the command byte last classified unsupported (Issue #543); only
/// meaningful when [`psx_memory_get_sio0_command_status`] reports `2` (`0`
/// is also a legitimate byte value when it *is* unsupported, so callers must
/// check the status first). `0` when `mem` is null.
///
/// # Safety
///
/// `mem` must be either null or a valid, live handle for the duration of the call.
#[no_mangle]
pub unsafe extern "C" fn psx_memory_get_sio0_last_command_byte(mem: *const PsxMemory) -> u8 {
    // SAFETY: caller's documented contract above.
    match unsafe { mem.as_ref() } {
        Some(mem) => crate::sio0::last_unsupported_command_byte(&mem.sio0),
        None => 0,
    }
}

/// Returns a pointer to the RAM backing store, stable for the handle's
/// lifetime (until it is destroyed), or null when `mem` is null.
///
/// # Safety
///
/// `mem` must be either null or a valid, live handle for the duration of the call.
#[no_mangle]
pub unsafe extern "C" fn psx_memory_ram_ptr(mem: *mut PsxMemory) -> *mut u8 {
    // SAFETY: caller's documented contract above.
    match unsafe { mem.as_mut() } {
        Some(mem) => mem.ram.as_mut_ptr(),
        None => std::ptr::null_mut(),
    }
}

/// Reads a little-endian 32-bit value at `address`; 0 when `mem` is null or
/// `address` is outside every mapped region.
///
/// `dma`/`timers`/`interrupts` are the controller states the HW-register
/// window's DMA/Timer/Interrupt sub-ranges route to; each is independently
/// nullable (an unattached controller falls back to the flat HW-register
/// store, matching `PSXMemory::AttachControllers`). A non-null pointer is
/// borrowed only for the duration of this call and may be mutated (a timer
/// read can have side effects); it is never retained.
///
/// # Safety
///
/// `mem` must be either null or a valid, live handle from
/// [`psx_memory_create`]. Each of `dma`/`timers`/`interrupts` must be either
/// null or a valid, aligned pointer, exclusively borrowed for the duration
/// of this call, to its respective state type.
#[no_mangle]
pub unsafe extern "C" fn psx_memory_read32(
    mem: *mut PsxMemory,
    address: u32,
    dma: *mut DmaState,
    timers: *mut TimerState,
    interrupts: *mut InterruptState,
) -> u32 {
    // SAFETY: caller's documented contract above.
    let Some(mem) = (unsafe { mem.as_mut() }) else {
        return 0;
    };
    // SAFETY: caller's documented contract above.
    unsafe { mem.read32(address, dma.as_mut(), timers.as_mut(), interrupts.as_mut()) }
}

/// Writes a little-endian 32-bit value at `address`; a no-op when `mem` is
/// null or `address` is outside every mapped region. See
/// [`psx_memory_read32`] for the controller-pointer contract.
///
/// # Safety
///
/// Same as [`psx_memory_read32`].
#[no_mangle]
pub unsafe extern "C" fn psx_memory_write32(
    mem: *mut PsxMemory,
    address: u32,
    value: u32,
    dma: *mut DmaState,
    timers: *mut TimerState,
    interrupts: *mut InterruptState,
) {
    // SAFETY: caller's documented contract above.
    let Some(mem) = (unsafe { mem.as_mut() }) else {
        return;
    };
    // SAFETY: caller's documented contract above.
    unsafe { mem.write32(address, value, dma.as_mut(), timers.as_mut(), interrupts.as_mut()) }
}

/// Reads a little-endian 16-bit value at `address`; 0 when `mem` is null or
/// `address` is outside every mapped region. See [`psx_memory_read32`] for
/// the controller-pointer contract.
///
/// # Safety
///
/// Same as [`psx_memory_read32`].
#[no_mangle]
pub unsafe extern "C" fn psx_memory_read16(
    mem: *mut PsxMemory,
    address: u32,
    dma: *mut DmaState,
    timers: *mut TimerState,
    interrupts: *mut InterruptState,
) -> u16 {
    // SAFETY: caller's documented contract above.
    let Some(mem) = (unsafe { mem.as_mut() }) else {
        return 0;
    };
    // SAFETY: caller's documented contract above.
    unsafe { mem.read16(address, dma.as_mut(), timers.as_mut(), interrupts.as_mut()) }
}

/// Writes a little-endian 16-bit value at `address`; a no-op when `mem` is
/// null or `address` is outside every mapped region. See
/// [`psx_memory_read32`] for the controller-pointer contract and
/// [`PsxMemory::write16`] for the DICR write-1-to-clear preservation this
/// performs on a sub-word controller-register write.
///
/// # Safety
///
/// Same as [`psx_memory_read32`].
#[no_mangle]
pub unsafe extern "C" fn psx_memory_write16(
    mem: *mut PsxMemory,
    address: u32,
    value: u16,
    dma: *mut DmaState,
    timers: *mut TimerState,
    interrupts: *mut InterruptState,
) {
    // SAFETY: caller's documented contract above.
    let Some(mem) = (unsafe { mem.as_mut() }) else {
        return;
    };
    // SAFETY: caller's documented contract above.
    unsafe { mem.write16(address, value, dma.as_mut(), timers.as_mut(), interrupts.as_mut()) }
}

/// Reads a byte at `address`; 0 when `mem` is null or `address` is outside
/// every mapped region. See [`psx_memory_read32`] for the controller-pointer
/// contract.
///
/// # Safety
///
/// Same as [`psx_memory_read32`].
#[no_mangle]
pub unsafe extern "C" fn psx_memory_read8(
    mem: *mut PsxMemory,
    address: u32,
    dma: *mut DmaState,
    timers: *mut TimerState,
    interrupts: *mut InterruptState,
) -> u8 {
    // SAFETY: caller's documented contract above.
    let Some(mem) = (unsafe { mem.as_mut() }) else {
        return 0;
    };
    // SAFETY: caller's documented contract above.
    unsafe { mem.read8(address, dma.as_mut(), timers.as_mut(), interrupts.as_mut()) }
}

/// Writes a byte at `address`; a no-op when `mem` is null or `address` is
/// outside every mapped region. See [`psx_memory_read32`] for the
/// controller-pointer contract and [`PsxMemory::write16`] for the DICR
/// write-1-to-clear preservation this performs on a sub-word
/// controller-register write.
///
/// # Safety
///
/// Same as [`psx_memory_read32`].
#[no_mangle]
pub unsafe extern "C" fn psx_memory_write8(
    mem: *mut PsxMemory,
    address: u32,
    value: u8,
    dma: *mut DmaState,
    timers: *mut TimerState,
    interrupts: *mut InterruptState,
) {
    // SAFETY: caller's documented contract above.
    let Some(mem) = (unsafe { mem.as_mut() }) else {
        return;
    };
    // SAFETY: caller's documented contract above.
    unsafe { mem.write8(address, value, dma.as_mut(), timers.as_mut(), interrupts.as_mut()) }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::dma::psx_dma_reset;
    use crate::interrupt::psx_interrupt_reset;
    use crate::timer::psx_timer_reset;

    unsafe fn create() -> *mut PsxMemory {
        psx_memory_create()
    }

    // --- create/destroy/null-safety -----------------------------------

    #[test]
    fn create_destroy_roundtrip() {
        let mem = unsafe { create() };
        assert!(!mem.is_null());
        unsafe { psx_memory_destroy(mem) };
    }

    #[test]
    fn null_handle_is_safe_for_every_export() {
        unsafe {
            psx_memory_destroy(std::ptr::null_mut());
            psx_memory_reset(std::ptr::null_mut());
            assert!(psx_memory_ram_ptr(std::ptr::null_mut()).is_null());
            assert_eq!(psx_memory_read32(std::ptr::null_mut(), 0, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0);
            psx_memory_write32(std::ptr::null_mut(), 0, 1, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());
            assert_eq!(psx_memory_read16(std::ptr::null_mut(), 0, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0);
            psx_memory_write16(std::ptr::null_mut(), 0, 1, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());
            assert_eq!(psx_memory_read8(std::ptr::null_mut(), 0, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0);
            psx_memory_write8(std::ptr::null_mut(), 0, 1, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());
        }
    }

    // --- RAM -------------------------------------------------------------

    #[test]
    fn ram_read_write_round_trip_all_widths_little_endian() {
        let mem = unsafe { create() };
        unsafe {
            psx_memory_write8(mem, 0, 0x42, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());
            assert_eq!(psx_memory_read8(mem, 0, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0x42);

            psx_memory_write16(mem, 4, 0xBEEF, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());
            assert_eq!(psx_memory_read16(mem, 4, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0xBEEF);
            assert_eq!(psx_memory_read8(mem, 4, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0xEF);
            assert_eq!(psx_memory_read8(mem, 5, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0xBE);

            psx_memory_write32(mem, 8, 0xDEADBEEF, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());
            assert_eq!(psx_memory_read32(mem, 8, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0xDEADBEEF);
            assert_eq!(psx_memory_read8(mem, 8, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0xEF);
            assert_eq!(psx_memory_read8(mem, 11, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0xDE);

            psx_memory_destroy(mem);
        }
    }

    #[test]
    fn ram_mirror_alias_reads_the_same_physical_byte() {
        let mem = unsafe { create() };
        unsafe {
            psx_memory_write8(mem, 0x1234, 0x77, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());
            // 0x1234 + N*2MiB aliases the same physical byte for N = 1, 2, 3.
            for mirror in 1..4u32 {
                let aliased = 0x1234 + mirror * PSX_RAM_SIZE;
                assert!(aliased < PSX_RAM_MIRROR_END);
                assert_eq!(psx_memory_read8(mem, aliased, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0x77);
            }
            psx_memory_destroy(mem);
        }
    }

    #[test]
    fn ram_final_valid_and_first_invalid_boundary() {
        let mem = unsafe { create() };
        unsafe {
            let last_byte = PSX_RAM_SIZE - 1;
            psx_memory_write8(mem, last_byte, 0xAB, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());
            assert_eq!(psx_memory_read8(mem, last_byte, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0xAB);

            // A 32-bit access starting at the last valid word (RAM_SIZE - 4) succeeds...
            let last_word = PSX_RAM_SIZE - 4;
            psx_memory_write32(mem, last_word, 0x11223344, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());
            assert_eq!(psx_memory_read32(mem, last_word, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0x11223344);

            // ...but one starting one byte later would run off the masked buffer end
            // and is unmapped (reads 0, ignores the write) rather than wrapping.
            let overrun = PSX_RAM_SIZE - 3;
            psx_memory_write32(mem, overrun, 0xFFFFFFFF, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());
            assert_eq!(psx_memory_read32(mem, overrun, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0);

            // The mirror window's exclusive end (0x00800000) is unmapped.
            assert_eq!(psx_memory_read8(mem, PSX_RAM_MIRROR_END, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0);

            psx_memory_destroy(mem);
        }
    }

    // --- Scratchpad --------------------------------------------------------

    #[test]
    fn scratchpad_round_trip_all_widths_and_no_ram_alias() {
        let mem = unsafe { create() };
        unsafe {
            psx_memory_write8(mem, PSX_SCRATCHPAD_BASE, 0x11, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());
            psx_memory_write16(mem, PSX_SCRATCHPAD_BASE + 4, 0x2233, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());
            psx_memory_write32(mem, PSX_SCRATCHPAD_BASE + 8, 0x44556677, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());

            assert_eq!(psx_memory_read8(mem, PSX_SCRATCHPAD_BASE, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0x11);
            assert_eq!(psx_memory_read16(mem, PSX_SCRATCHPAD_BASE + 4, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0x2233);
            assert_eq!(psx_memory_read32(mem, PSX_SCRATCHPAD_BASE + 8, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0x44556677);

            // The scratchpad is its own storage: RAM (address 0, which the
            // scratchpad base would alias to under RAM's own mirror mask) sees
            // nothing of it.
            assert_eq!(psx_memory_read8(mem, 0, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0);

            psx_memory_destroy(mem);
        }
    }

    #[test]
    fn scratchpad_first_last_and_out_of_range() {
        let mem = unsafe { create() };
        unsafe {
            let first = PSX_SCRATCHPAD_BASE;
            let last = PSX_SCRATCHPAD_BASE + PSX_SCRATCHPAD_SIZE - 1;
            psx_memory_write8(mem, first, 0xAA, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());
            psx_memory_write8(mem, last, 0xBB, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());
            assert_eq!(psx_memory_read8(mem, first, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0xAA);
            assert_eq!(psx_memory_read8(mem, last, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0xBB);

            // One byte past the scratchpad is unmapped, not aliased back in.
            let past_end = PSX_SCRATCHPAD_BASE + PSX_SCRATCHPAD_SIZE;
            psx_memory_write8(mem, past_end, 0x21, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());
            assert_eq!(psx_memory_read8(mem, past_end, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0);

            psx_memory_destroy(mem);
        }
    }

    // --- BIOS ----------------------------------------------------------

    #[test]
    fn bios_read_write_parity_and_boundaries() {
        let mem = unsafe { create() };
        unsafe {
            psx_memory_write32(mem, PSX_BIOS_BASE, 0x1234_5678, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());
            assert_eq!(psx_memory_read32(mem, PSX_BIOS_BASE, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0x1234_5678);

            let last_byte = PSX_BIOS_BASE + PSX_BIOS_SIZE - 1;
            psx_memory_write8(mem, last_byte, 0x9A, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());
            assert_eq!(psx_memory_read8(mem, last_byte, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0x9A);

            // Past the BIOS window is unmapped.
            let past_end = PSX_BIOS_BASE + PSX_BIOS_SIZE;
            assert_eq!(psx_memory_read8(mem, past_end, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0);

            psx_memory_destroy(mem);
        }
    }

    // --- MMIO routing ----------------------------------------------------

    #[test]
    fn dma_register_routes_through_the_attached_controller() {
        let mem = unsafe { create() };
        let mut dma = psx_dma_reset();
        unsafe {
            psx_memory_write32(mem, 0x1F80_10F0, 0xDEAD_BEEF, &raw mut dma, std::ptr::null_mut(), std::ptr::null_mut());
            assert_eq!(dma.dpcr, 0xDEAD_BEEF);
            assert_eq!(
                psx_memory_read32(mem, 0x1F80_10F0, &raw mut dma, std::ptr::null_mut(), std::ptr::null_mut()),
                0xDEAD_BEEF
            );
            psx_memory_destroy(mem);
        }
    }

    #[test]
    fn timer_register_routes_through_the_attached_controller() {
        let mem = unsafe { create() };
        let mut timers = psx_timer_reset();
        unsafe {
            psx_memory_write16(mem, 0x1F80_1108, 0x0064, std::ptr::null_mut(), &raw mut timers, std::ptr::null_mut());
            assert_eq!(
                psx_memory_read32(mem, 0x1F80_1108, std::ptr::null_mut(), &raw mut timers, std::ptr::null_mut()) & 0xFFFF,
                0x0064
            );
            psx_memory_destroy(mem);
        }
    }

    #[test]
    fn interrupt_register_routes_through_the_attached_controller() {
        let mem = unsafe { create() };
        let mut interrupts = psx_interrupt_reset();
        unsafe {
            psx_memory_write32(mem, 0x1F80_1074, 0x0000_0007, std::ptr::null_mut(), std::ptr::null_mut(), &raw mut interrupts);
            assert_eq!(interrupts.i_mask, 0x0000_0007);
            assert_eq!(
                psx_memory_read32(mem, 0x1F80_1074, std::ptr::null_mut(), std::ptr::null_mut(), &raw mut interrupts),
                0x0000_0007
            );
            psx_memory_destroy(mem);
        }
    }

    #[test]
    fn unattached_controller_falls_back_to_the_flat_hw_register_store() {
        let mem = unsafe { create() };
        unsafe {
            // No dma pointer attached: DPCR's address is treated as a plain
            // fallback register instead of being dropped.
            psx_memory_write32(mem, 0x1F80_10F0, 0x1234, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());
            assert_eq!(
                psx_memory_read32(mem, 0x1F80_10F0, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()),
                0x1234
            );
            psx_memory_destroy(mem);
        }
    }

    // --- DICR sub-word write-1-to-clear preservation (PR #491 regression) --

    #[test]
    fn dicr_write8_preserves_pending_w1c_flags() {
        let mem = unsafe { create() };
        let mut dma = psx_dma_reset();
        dma.dicr = 0x7F; // All 7 channel flags pending.
        unsafe {
            // Byte 2 (bits 16-23) carries master-enable (bit 23) but no flag bits.
            psx_memory_write8(mem, 0x1F80_10F6, 0x80, &raw mut dma, std::ptr::null_mut(), std::ptr::null_mut());
            let after = psx_dma_read_register(dma, 0x1F80_10F4);
            assert_eq!(after & 0x7F, 0x7F, "flags must still be pending");
            assert_eq!(after & (1 << 23), 1 << 23, "master enable must be applied");

            // An intentional W1C write through the same byte path still works.
            psx_memory_write8(mem, 0x1F80_10F4, 0x01, &raw mut dma, std::ptr::null_mut(), std::ptr::null_mut());
            let after_clear = psx_dma_read_register(dma, 0x1F80_10F4);
            assert_eq!(after_clear & 0x7F, 0x7E, "only flag 0 must clear");

            psx_memory_destroy(mem);
        }
    }

    #[test]
    fn dicr_write16_preserves_pending_w1c_flags() {
        let mem = unsafe { create() };
        let mut dma = psx_dma_reset();
        dma.dicr = 0x7F;
        unsafe {
            // High halfword: master-enable (bit 23) + channel 0/2 enables (bits 24/26).
            psx_memory_write16(mem, 0x1F80_10F6, 0x0580, &raw mut dma, std::ptr::null_mut(), std::ptr::null_mut());
            let after = psx_dma_read_register(dma, 0x1F80_10F4);
            assert_eq!(after & 0x7F, 0x7F, "flags must still be pending");
            assert_eq!(after & (1 << 23), 1 << 23);
            assert_eq!((after >> 24) & 0x7F, 0x05);

            // An intentional W1C write through the low halfword still works.
            psx_memory_write16(mem, 0x1F80_10F4, 0x0003, &raw mut dma, std::ptr::null_mut(), std::ptr::null_mut());
            let after_clear = psx_dma_read_register(dma, 0x1F80_10F4);
            assert_eq!(after_clear & 0x7F, 0x7C, "flags 0 and 1 must clear");

            psx_memory_destroy(mem);
        }
    }

    // --- Reset -------------------------------------------------------------

    #[test]
    fn reset_zeroes_ram_scratchpad_and_bios() {
        let mem = unsafe { create() };
        unsafe {
            psx_memory_write8(mem, 0, 0xFF, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());
            psx_memory_write8(mem, PSX_SCRATCHPAD_BASE, 0xFF, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());
            psx_memory_write8(mem, PSX_BIOS_BASE, 0xFF, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());
            psx_memory_write32(mem, 0x1F80_10F0, 0xFFFF_FFFF, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());

            psx_memory_reset(mem);

            assert_eq!(psx_memory_read8(mem, 0, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0);
            assert_eq!(psx_memory_read8(mem, PSX_SCRATCHPAD_BASE, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0);
            assert_eq!(psx_memory_read8(mem, PSX_BIOS_BASE, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0);
            assert_eq!(psx_memory_read32(mem, 0x1F80_10F0, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0);

            psx_memory_destroy(mem);
        }
    }

    // --- ram_ptr stability / RAM-buffer aliasing ----------------------------

    #[test]
    fn ram_ptr_aliases_the_same_buffer_read_and_written_through() {
        let mem = unsafe { create() };
        unsafe {
            let ptr = psx_memory_ram_ptr(mem);
            assert!(!ptr.is_null());
            *ptr.add(10) = 0x5A;
            assert_eq!(psx_memory_read8(mem, 10, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut()), 0x5A);

            psx_memory_write8(mem, 20, 0xA5, std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut());
            assert_eq!(*ptr.add(20), 0xA5);

            psx_memory_destroy(mem);
        }
    }
}
