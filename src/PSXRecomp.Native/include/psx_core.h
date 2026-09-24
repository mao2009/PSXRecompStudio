/**
 * @file psx_core.h
 * @brief Public C ABI for the PSXRecomp native core.
 *
 * This header is the interop boundary named by the project documentation
 * policy (docs/development/documentation-policy.md): most functions here
 * are mirrored one-to-one by a P/Invoke declaration in
 * `src/PSXRecomp.Core/NativeInterop.cs`. The one exception is
 * PSXCore_SetCop0, which is exercised only from the native test suite and
 * has no C# binding yet; add one there when a managed caller needs it
 * (PSXCore_GetCop0 gained one for Issue #499). Keep the mirrored subset in lockstep
 * with the C# side when changing a signature or its documented semantics.
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
/**
 * Advances started DMA transfers by `cycles` CPU clock cycles (Issue #442). A
 * transfer completes after its modelled duration (one cycle per word; no data
 * moves): CHCR bits 24/28 clear and its DICR flag sets when that channel's
 * DICR enable is set.
 */
PSX_API void     PSXCore_TickDma(PSXCore* core, uint32_t cycles);

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

/**
 * Executes a single instruction, honoring branch/load-delay slot semantics.
 * Returns zero when the step was taken, or a negative status when `core` is NULL.
 *
 * A guest exception is NOT reported here: an architectural exception (INT,
 * SYSCALL, RI/CpU/AdEL/AdES) is a normal, continuable hardware event, and the
 * step that takes it succeeds — it simply lands the PC on the exception vector.
 * Use PSXCore_GetExceptionRaised() to ask whether the step faulted (Issue #377).
 */
PSX_API int PSXCore_Step(PSXCore* core);
/**
 * PSXCore_Step() with the CPU's hardware interrupt input held low: I_STAT and
 * I_MASK are untouched, but no INT exception is taken for them and CAUSE.IP2
 * reads 0. For callers that cannot continue into an exception handler (PR #493).
 */
PSX_API int PSXCore_StepWithoutInterrupts(PSXCore* core);
/** Returns non-zero when the most recent PSXCore_Step() raised a guest exception. Reset by every step. */
PSX_API int PSXCore_GetExceptionRaised(PSXCore* core);
/** Returns the CAUSE Excode of the exception the most recent PSXCore_Step() raised (Issue #481). Meaningful only when PSXCore_GetExceptionRaised() is non-zero. */
PSX_API uint32_t PSXCore_GetExceptionCode(PSXCore* core);
/** Returns the faulting PC (EPC) of the exception the most recent PSXCore_Step() raised: the branch PC when the faulting instruction was in a branch delay slot, else the faulting instruction's own PC. Meaningful only when PSXCore_GetExceptionRaised() is non-zero. */
PSX_API uint32_t PSXCore_GetExceptionFaultPc(PSXCore* core);
/** Returns non-zero when the faulting instruction of the most recent exception was in a branch delay slot (BD, CAUSE bit 31). Meaningful only when PSXCore_GetExceptionRaised() is non-zero. */
PSX_API int PSXCore_GetExceptionInDelaySlot(PSXCore* core);
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

/*
 * Rust coexistence substrate (Issue #473).
 *
 * Infrastructure only: these two functions prove that Rust code linked into
 * this library is reachable across the existing C ABI. They carry no emulator
 * state, are unrelated to PSXCore, and must not grow into a runtime
 * abstraction. Their implementation is a thin re-export of the Rust crate in
 * `rust/` (see `src/psx_rust_abi.cpp`); the FFI rules every migration must
 * follow are in `docs/development/rust-ffi-contract.md` (ADR-023).
 */

/** Status: the Rust call succeeded and wrote its out-parameter. */
#define PSX_RUST_OK 0
/** Status: a required out-parameter pointer was NULL; nothing was written. */
#define PSX_RUST_ERR_NULL_ARGUMENT (-1)
/** Status: a Rust panic was contained at the boundary; the out-parameter is unspecified. */
#define PSX_RUST_ERR_PANIC (-2)

/** Returns the Rust substrate's C ABI contract version (currently 1). Infallible. */
PSX_API uint32_t PSXRecompRust_AbiVersion(void);
/**
 * Writes `value ^ 0x5A5A5A5A` to `out_result`.
 *
 * Returns PSX_RUST_OK, PSX_RUST_ERR_NULL_ARGUMENT when `out_result` is NULL, or
 * PSX_RUST_ERR_PANIC when a panic was contained. `out_result` stays owned by
 * the caller and is borrowed only for the duration of the call.
 */
PSX_API int32_t PSXRecompRust_RoundTrip(uint32_t value, uint32_t* out_result);

#ifdef __cplusplus
}
#endif

#endif
