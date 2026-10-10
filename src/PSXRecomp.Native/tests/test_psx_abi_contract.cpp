#include "psx_core.h"

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <type_traits>

#ifdef _WIN32
#include <windows.h>
#else
#include <dlfcn.h>
#endif

static_assert(sizeof(std::uint8_t) == 1);
static_assert(sizeof(std::uint16_t) == 2);
static_assert(sizeof(std::uint32_t) == 4);
static_assert(sizeof(std::int32_t) == 4);

static_assert(std::is_same_v<PSXGpuMmioRead32, std::uint32_t (*)(void*, std::uint32_t)>);
static_assert(std::is_same_v<PSXGpuMmioWrite32, void (*)(void*, std::uint32_t, std::uint32_t)>);
static_assert(std::is_same_v<decltype(&PSXCore_Create), PSXCore* (*)(void)>);
static_assert(std::is_same_v<decltype(&PSXCore_Destroy), void (*)(PSXCore*)>);
static_assert(std::is_same_v<decltype(&PSXCore_ReadMemory32), std::uint32_t (*)(PSXCore*, std::uint32_t)>);
static_assert(std::is_same_v<decltype(&PSXCore_WriteMemory32), void (*)(PSXCore*, std::uint32_t, std::uint32_t)>);
static_assert(std::is_same_v<decltype(&PSXRecompRust_AbiVersion), std::uint32_t (*)(void)>);
static_assert(std::is_same_v<decltype(&PSXRecompRust_RoundTrip), std::int32_t (*)(std::uint32_t, std::uint32_t*)>);
static_assert(PSX_RUST_OK == 0);
static_assert(PSX_RUST_ERR_NULL_ARGUMENT == -1);
static_assert(PSX_RUST_ERR_PANIC == -2);

namespace
{
constexpr const char* RequiredSymbols[] = {
    "PSXCore_Create",
    "PSXCore_Destroy",
    "PSXCore_Reset",
    "PSXCore_GetGPR",
    "PSXCore_SetGPR",
    "PSXCore_GetPC",
    "PSXCore_SetPC",
    "PSXCore_GetHI",
    "PSXCore_SetHI",
    "PSXCore_GetLO",
    "PSXCore_SetLO",
    "PSXCore_GetCop0",
    "PSXCore_SetCop0",
    "PSXCore_GetRAM",
    "PSXCore_GetRAMSize",
    "PSXCore_SetGpuMmioCallbacks",
    "PSXCore_ReadDmaRegister",
    "PSXCore_WriteDmaRegister",
    "PSXCore_GetDmaInterruptPending",
    "PSXCore_TickDma",
    "PSXCore_TickDmaExcludingChannel",
    "PSXCore_CompleteDmaChannel",
    "PSXCore_SetCdRomMmioCallbacks",
    "PSXCore_SetGteCallbacks",
    "PSXCore_ReadTimerRegister",
    "PSXCore_WriteTimerRegister",
    "PSXCore_TickTimers",
    "PSXCore_GetTimerInterruptPending",
    "PSXCore_ClearTimerInterrupt",
    "PSXCore_SetTimerSync",
    "PSXCore_ResetTimers",
    "PSXCore_ReadInterruptControllerRegister",
    "PSXCore_WriteInterruptControllerRegister",
    "PSXCore_GetInterruptPending",
    "PSXCore_RaiseInterrupt",
    "PSXCore_ClearInterrupt",
    "PSXCore_ResetInterruptController",
    "PSXCore_GetSio0InterruptPending",
    "PSXCore_ClearSio0Interrupt",
    "PSXCore_GetSio0CommandStatus",
    "PSXCore_GetSio0LastCommandByte",
    "PSXCore_Step",
    "PSXCore_StepWithoutInterrupts",
    "PSXCore_GetExceptionRaised",
    "PSXCore_GetExceptionCode",
    "PSXCore_GetExceptionFaultPc",
    "PSXCore_GetExceptionInDelaySlot",
    "PSXCore_GetRfeExecuted",
    "PSXCore_GetPipelineState",
    "PSXCore_PopExceptionSrStack",
    "PSXCore_Run",
    "PSXCore_ReadMemory32",
    "PSXCore_WriteMemory32",
    "PSXCore_ReadMemory16",
    "PSXCore_WriteMemory16",
    "PSXCore_ReadMemory8",
    "PSXCore_WriteMemory8",
    "PSXRecompRust_AbiVersion",
    "PSXRecompRust_RoundTrip",
};

#ifdef _WIN32
using LibraryHandle = HMODULE;

LibraryHandle OpenLibrary(const char* path)
{
    return LoadLibraryA(path);
}

bool HasSymbol(LibraryHandle handle, const char* name)
{
    return GetProcAddress(handle, name) != nullptr;
}

void CloseLibrary(LibraryHandle handle)
{
    if (handle != nullptr)
        FreeLibrary(handle);
}

const char* LastLibraryError()
{
    static char buffer[64];
    std::snprintf(buffer, sizeof(buffer), "Win32 error %lu", static_cast<unsigned long>(GetLastError()));
    return buffer;
}
#else
using LibraryHandle = void*;

LibraryHandle OpenLibrary(const char* path)
{
    dlerror();
    return dlopen(path, RTLD_NOW | RTLD_LOCAL);
}

bool HasSymbol(LibraryHandle handle, const char* name)
{
    dlerror();
    return dlsym(handle, name) != nullptr;
}

void CloseLibrary(LibraryHandle handle)
{
    if (handle != nullptr)
        dlclose(handle);
}

const char* LastLibraryError()
{
    const char* error = dlerror();
    return error != nullptr ? error : "unknown dynamic-loader error";
}
#endif
} // namespace

int main(int argc, char** argv)
{
    if (argc < 2)
    {
        std::fprintf(stderr, "usage: psx_abi_contract_tests <native-library> [extra-required-symbol ...]\n");
        return 2;
    }

    LibraryHandle library = OpenLibrary(argv[1]);
    if (library == nullptr)
    {
        std::fprintf(stderr, "failed to open native library '%s': %s\n", argv[1], LastLibraryError());
        return 2;
    }

    int missing = 0;
    for (const char* symbol : RequiredSymbols)
    {
        if (!HasSymbol(library, symbol))
        {
            std::fprintf(stderr, "missing required ABI symbol: %s\n", symbol);
            ++missing;
        }
    }

    // Extra symbols make the checker itself testable. CMake registers one
    // WILL_FAIL case with a deliberately nonexistent symbol; if this loop ever
    // stops detecting missing exports, that negative contract test fails.
    for (int i = 2; i < argc; ++i)
    {
        if (!HasSymbol(library, argv[i]))
        {
            std::fprintf(stderr, "missing required ABI symbol: %s\n", argv[i]);
            ++missing;
        }
    }

    CloseLibrary(library);
    return missing == 0 ? 0 : 1;
}
