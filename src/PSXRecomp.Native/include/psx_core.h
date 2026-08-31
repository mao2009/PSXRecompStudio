/**
 * @file psx_core.h
 * @brief Public C ABI for the PSXRecomp native core.
 *
 * This header is the interop boundary named by the project documentation
 * policy (docs/development/documentation-policy.md): every function here is
 * mirrored one-to-one by a P/Invoke declaration in
 * `src/PSXRecomp.Core/NativeInterop.cs`. Keep both sides in lockstep when
 * changing a signature or its documented semantics.
 *
 * Ownership: `PSXCore_Create` returns an opaque handle owned by the caller;
 * it must be released exactly once via `PSXCore_Destroy`. No other function
 * in this header transfers ownership of the handle.
 */

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

/** Opaque handle to a native PSX core instance; fields are not part of the ABI. */
typedef struct PSXCore PSXCore;

/** Allocates a new core instance. Ownership transfers to the caller; returns NULL on allocation failure. */
PSX_API PSXCore* PSXCore_Create(void);
/** Releases a core handle previously returned by PSXCore_Create. Must be called exactly once. */
PSX_API void     PSXCore_Destroy(PSXCore* core);
/** Resets CPU registers, COP0 state, and memory-mapped subsystems to their power-on state. */
PSX_API void     PSXCore_Reset(PSXCore* core);

/** Reads general-purpose register `index` (0-31; R0 always reads as 0). */
PSX_API uint32_t PSXCore_GetGPR(PSXCore* core, int index);
/** Writes general-purpose register `index` (0-31; writes to R0 are silently ignored). */
PSX_API void     PSXCore_SetGPR(PSXCore* core, int index, uint32_t value);

/** Reads the current program counter. */
PSX_API uint32_t PSXCore_GetPC(PSXCore* core);
/** Sets the program counter and flushes pending branch/load-delay pipeline state (see ADR-004/005). */
PSX_API void     PSXCore_SetPC(PSXCore* core, uint32_t value);

/** Reads the HI register of the multiply/divide unit. */
PSX_API uint32_t PSXCore_GetHI(PSXCore* core);
/** Writes the HI register of the multiply/divide unit. */
PSX_API void     PSXCore_SetHI(PSXCore* core, uint32_t value);

/** Reads the LO register of the multiply/divide unit. */
PSX_API uint32_t PSXCore_GetLO(PSXCore* core);
/** Writes the LO register of the multiply/divide unit. */
PSX_API void     PSXCore_SetLO(PSXCore* core, uint32_t value);

/** Reads COP0 register `index` (see docs/cpu/cop0.md for the register map). */
PSX_API uint32_t PSXCore_GetCop0(PSXCore* core, int index);
/** Writes COP0 register `index`. */
PSX_API void     PSXCore_SetCop0(PSXCore* core, int index, uint32_t value);

/** Returns a pointer to the native 2 MiB main-RAM buffer owned by `core`. Valid only until the core is destroyed. */
PSX_API uint8_t* PSXCore_GetRAM(PSXCore* core);
/** Returns the fixed PS1 main-RAM size in bytes. Does not require a live instance. */
PSX_API uint32_t PSXCore_GetRAMSize(void);

/** Reads a DMA controller register at the given absolute address. */
PSX_API uint32_t PSXCore_ReadDmaRegister(PSXCore* core, uint32_t address);
/** Writes a DMA controller register at the given absolute address. */
PSX_API void     PSXCore_WriteDmaRegister(PSXCore* core, uint32_t address, uint32_t value);
/** Returns non-zero when a DMA-triggered interrupt is pending. */
PSX_API int      PSXCore_GetDmaInterruptPending(PSXCore* core);

/** Reads a timer (0-2) register at the given absolute address. */
PSX_API uint32_t PSXCore_ReadTimerRegister(PSXCore* core, uint32_t address);
/** Writes a timer (0-2) register at the given absolute address. */
PSX_API void     PSXCore_WriteTimerRegister(PSXCore* core, uint32_t address, uint32_t value);
/** Advances all timer counters by `cycles` CPU clock cycles, evaluating targets/overflow/sync per timer mode. */
PSX_API void     PSXCore_TickTimers(PSXCore* core, uint32_t cycles);
/** Returns non-zero when the given timer (0-2) has a pending interrupt. */
PSX_API int      PSXCore_GetTimerInterruptPending(PSXCore* core, int timer);
/** Acknowledges/clears the pending interrupt for the given timer (0-2). */
PSX_API void     PSXCore_ClearTimerInterrupt(PSXCore* core, int timer);
/** Sets whether the given timer (0-2) is currently synchronized/paused by its configured sync source. */
PSX_API void     PSXCore_SetTimerSync(PSXCore* core, int timer, int active);
/** Resets all timer counters, modes, and pending interrupts to their power-on state. */
PSX_API void     PSXCore_ResetTimers(PSXCore* core);

/** Reads an interrupt controller (I_STAT/I_MASK) register at the given absolute address. */
PSX_API uint32_t PSXCore_ReadInterruptControllerRegister(PSXCore* core, uint32_t address);
/** Writes an interrupt controller (I_STAT/I_MASK) register at the given absolute address. */
PSX_API void     PSXCore_WriteInterruptControllerRegister(PSXCore* core, uint32_t address, uint32_t value);
/** Returns non-zero when any unmasked interrupt is pending (I_STAT and I_MASK combined). */
PSX_API int      PSXCore_GetInterruptPending(PSXCore* core);
/** Raises (sets pending) the given IRQ line (see docs/cpu/exceptions.md for the IRQ numbering). */
PSX_API void     PSXCore_RaiseInterrupt(PSXCore* core, int irq);
/** Clears the pending state of the given IRQ line. */
PSX_API void     PSXCore_ClearInterrupt(PSXCore* core, int irq);
/** Resets the interrupt controller (I_STAT/I_MASK) to its power-on state. */
PSX_API void     PSXCore_ResetInterruptController(PSXCore* core);

/** Executes a single instruction, honoring branch/load-delay slot semantics. Returns zero on success, non-zero native status/exception code otherwise. */
PSX_API int PSXCore_Step(PSXCore* core);
/** Executes up to `maxInstructions` instructions, stopping early on a native exception/halt condition. Returns the number of instructions actually executed, or a negative status on error. */
PSX_API int PSXCore_Run(PSXCore* core, uint32_t maxInstructions);

/** Reads a 32-bit little-endian value from the CPU address space (RAM or MMIO). */
PSX_API uint32_t PSXCore_ReadMemory32(PSXCore* core, uint32_t address);
/** Writes a 32-bit little-endian value to the CPU address space (RAM or MMIO). */
PSX_API void PSXCore_WriteMemory32(PSXCore* core, uint32_t address, uint32_t value);
/** Reads a 16-bit little-endian value from the CPU address space. */
PSX_API uint16_t PSXCore_ReadMemory16(PSXCore* core, uint32_t address);
/** Writes a 16-bit little-endian value to the CPU address space. */
PSX_API void PSXCore_WriteMemory16(PSXCore* core, uint32_t address, uint16_t value);
/** Reads an 8-bit value from the CPU address space. */
PSX_API uint8_t PSXCore_ReadMemory8(PSXCore* core, uint32_t address);
/** Writes an 8-bit value to the CPU address space. */
PSX_API void PSXCore_WriteMemory8(PSXCore* core, uint32_t address, uint8_t value);

#ifdef __cplusplus
}
#endif

#endif
