#include "psx_cpu.h"
#include "psx_memory.h"
#include "psx_dma.h"
#include "psx_timer.h"
#include "psx_interrupt.h"

struct PSXCore {
    PSXCpu cpu;
    PSXMemory memory;
    PSXDmaState dma = psx_dma_reset();
    PSXTimerState timers = psx_timer_reset();
    PSXInterruptState interrupts = psx_interrupt_reset();
};
