#include "psx_cpu.h"
#include "psx_memory.h"
#include "psx_dma.h"
#include "psx_timer.h"

struct PSXCore {
    PSXCpu cpu;
    PSXMemory memory;
    PSXDmaController dma;
    PSXTimerController timers;
};
