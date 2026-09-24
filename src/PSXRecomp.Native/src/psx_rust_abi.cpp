/**
 * @file psx_rust_abi.cpp
 * @brief Re-exports the Rust coexistence substrate's C ABI from this library.
 *
 * The Rust crate in `../rust` is compiled by cargo as a `staticlib` and linked
 * into `PSXRecomp.Native` (see `../CMakeLists.txt`). A static archive member is
 * only pulled in when something references it, and on Windows a symbol is only
 * placed in the import library when its *definition* carries `dllexport` — a
 * static library cannot supply either. This translation unit provides both with
 * one mechanism that behaves identically on every supported toolchain: it
 * references the Rust symbols (forcing archive extraction) and defines the
 * `PSX_API`-exported names that managed callers P/Invoke.
 *
 * These thunks add no logic; they must stay one-to-one with the Rust exports.
 * The rules they and every future migration follow live in
 * `docs/development/rust-ffi-contract.md` (decision: ADR-023).
 */

#include "psx_core.h"

/*
 * Rust side of the boundary. Declared here rather than in a public header
 * because these symbols are an internal implementation detail of this shared
 * library: nothing outside it links against the Rust archive directly.
 */
extern "C" {
uint32_t psx_rust_abi_version(void);
int32_t psx_rust_round_trip(uint32_t value, uint32_t* out_result);
}

PSX_API uint32_t PSXRecompRust_AbiVersion(void)
{
    return psx_rust_abi_version();
}

PSX_API int32_t PSXRecompRust_RoundTrip(uint32_t value, uint32_t* out_result)
{
    return psx_rust_round_trip(value, out_result);
}
