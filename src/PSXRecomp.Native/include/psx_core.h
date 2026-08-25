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

PSX_API uint8_t* PSXCore_GetRAM(PSXCore* core);
PSX_API uint32_t PSXCore_GetRAMSize(void);

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
