#pragma once

#include "psx_core.h"

#ifdef __cplusplus
extern "C" {
#endif

// Internal test/trace hook -- NOT part of the stable public ABI contract.
// The public PSXCore_* API intentionally keeps the core opaque, so the Golden
// Trace harness cannot reach the embedded PSXCpu from a PSXCore*. This accessor
// exists only so the native test binary can attach the GPR write-event recorder
// (see psx_cpu.h / golden_trace.h); a recompiler backend participating in a
// golden-trace comparison would surface its per-step write events to the
// harness through its own channel.
//
// Returns the core's embedded PSXCpu as an opaque void* so this header remains
// valid C (the class type is C++-only); C++ callers cast it back.
PSX_API void* PSXCoreGetCpuForTrace(PSXCore* core);

#ifdef __cplusplus
}
#endif