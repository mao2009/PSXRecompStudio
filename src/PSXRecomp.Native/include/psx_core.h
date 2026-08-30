#ifndef PSX_CORE_H
#define PSX_CORE_H

#include <stdint.h>

#ifdef _WIN32
    #ifdef PSX_RECOMP_NATIVE_EXPORTS
        #define PSX_API __declspec(dllexport)
    #else
        #define PSX_API __declspec(dllimport)
    #endif
#else
    #define PSX_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct PSXCore PSXCore;

PSX_API PSXCore* PSXCore_Create(void);
PSX_API void     PSXCore_Destroy(PSXCore* core);
PSX_API void     PSXCore_Reset(PSXCore* core);

PSX_API uint32_t PSXCore_GetGPR(PSXCore* core, int index);
PSX_API void     PSXCore_SetGPR(PSXCore* core, int index, uint32_t value);

PSX_API uint32_t PSXCore_GetPC(PSXCore* core);
PSX_API void     PSXCore_SetPC(PSXCore* core, uint32_t value);

PSX_API uint32_t PSXCore_GetHI(PSXCore* core);
PSX_API void     PSXCore_SetHI(PSXCore* core, uint32_t value);

PSX_API uint32_t PSXCore_GetLO(PSXCore* core);
PSX_API void     PSXCore_SetLO(PSXCore* core, uint32_t value);

PSX_API uint32_t PSXCore_GetCop0(PSXCore* core, int index);
PSX_API void     PSXCore_SetCop0(PSXCore* core, int index, uint32_t value);

PSX_API uint8_t* PSXCore_GetRAM(PSXCore* core);
PSX_API uint32_t PSXCore_GetRAMSize(void);

PSX_API uint32_t PSXCore_ReadDmaRegister(PSXCore* core, uint32_t address);
PSX_API void     PSXCore_WriteDmaRegister(PSXCore* core, uint32_t address, uint32_t value);
PSX_API int      PSXCore_GetDmaInterruptPending(PSXCore* core);

PSX_API uint32_t PSXCore_ReadTimerRegister(PSXCore* core, uint32_t address);
PSX_API void     PSXCore_WriteTimerRegister(PSXCore* core, uint32_t address, uint32_t value);
PSX_API void     PSXCore_TickTimers(PSXCore* core, uint32_t cycles);
PSX_API int      PSXCore_GetTimerInterruptPending(PSXCore* core, int timer);
PSX_API void     PSXCore_ClearTimerInterrupt(PSXCore* core, int timer);
PSX_API void     PSXCore_SetTimerSync(PSXCore* core, int timer, int active);
PSX_API void     PSXCore_ResetTimers(PSXCore* core);

PSX_API uint32_t PSXCore_ReadInterruptControllerRegister(PSXCore* core, uint32_t address);
PSX_API void     PSXCore_WriteInterruptControllerRegister(PSXCore* core, uint32_t address, uint32_t value);
PSX_API int      PSXCore_GetInterruptPending(PSXCore* core);
PSX_API void     PSXCore_RaiseInterrupt(PSXCore* core, int irq);
PSX_API void     PSXCore_ClearInterrupt(PSXCore* core, int irq);
PSX_API void     PSXCore_ResetInterruptController(PSXCore* core);

PSX_API int PSXCore_Step(PSXCore* core);
PSX_API int PSXCore_Run(PSXCore* core, uint32_t maxInstructions);

PSX_API uint32_t PSXCore_ReadMemory32(PSXCore* core, uint32_t address);
PSX_API void PSXCore_WriteMemory32(PSXCore* core, uint32_t address, uint32_t value);
PSX_API uint16_t PSXCore_ReadMemory16(PSXCore* core, uint32_t address);
PSX_API void PSXCore_WriteMemory16(PSXCore* core, uint32_t address, uint16_t value);
PSX_API uint8_t PSXCore_ReadMemory8(PSXCore* core, uint32_t address);
PSX_API void PSXCore_WriteMemory8(PSXCore* core, uint32_t address, uint8_t value);

#ifdef __cplusplus
}
#endif

#endif
