#pragma once

#include <cstdint>

/*
 * DMA controller registers (MADR/BCR/CHCR x 7, DPCR, DICR). Implemented in
 * Rust (`../rust/src/dma.rs`, Issue #488); this header only declares that
 * crate's internal C ABI for use by psx_api.cpp.
 *
 * The state is a plain value owned by PSXCore and passed by value; Rust never
 * allocates or retains it. Every function is infallible and cannot panic.
 * Must match `DmaChannelState` / `DmaState` in dma.rs field-for-field.
 */
struct PSXDmaChannelState {
    uint32_t madr;
    uint32_t bcr;
    uint32_t chcr;
};

struct PSXDmaState {
    PSXDmaChannelState channels[7];
    uint32_t dpcr;
    uint32_t dicr;
    uint32_t remaining[7]; // Modelled cycles left per in-flight transfer (Issue #442).
};

static_assert(sizeof(PSXDmaState) == 120, "PSXDmaState must match DmaState in dma.rs");

extern "C" {
PSXDmaState psx_dma_reset(void);
uint32_t psx_dma_read_register(PSXDmaState state, uint32_t address);
PSXDmaState psx_dma_write_register(PSXDmaState state, uint32_t address, uint32_t value);
uint32_t psx_dma_get_interrupt_pending(PSXDmaState state);
PSXDmaState psx_dma_tick(PSXDmaState state, uint32_t cycles);
}
