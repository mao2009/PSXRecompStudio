# PSXCpu Rust Migration Slices

**Status:** Stable

**Authority:** Reference

**Related Issues:** #524, #471, #525, #526, #527, #528, #529, #530, #531

**Related Components:** `src/PSXRecomp.Native/src/psx_cpu*.cpp`, `src/PSXRecomp.Native/rust/src/cpu_*.rs`, `src/PSXRecomp.Native/tests/test_psx_cpu_*_rust.cpp`

**Dependencies:** [Rust FFI Safety Contract](../../rust-ffi-contract.md), [ADR-023](../../../adr/023-rust-native-coexistence-substrate.md)

## Purpose

The conflict-isolated PSXCpu migration defined by #524 is complete: all seven
slices #525-#531 have landed. This page is now the maintenance map for those
completed Rust-owned semantic slices: it records which translation unit, Rust
module, test file, and design note own each boundary, and which files remain
shared coordination points.

The per-function FFI rules are in the
[Rust FFI Safety Contract](../../rust-ffi-contract.md). This page covers file
ownership only.

## Slice ownership

Every path is relative to `src/PSXRecomp.Native/`.

| Slice | Issue | Status | C++ translation unit | Rust module | Native test file | Doc |
|---|---|---|---|---|---|---|
| Decode / dispatch | #525 | ✅ Migrated | `src/psx_cpu_decode.cpp` | `rust/src/cpu_decode.rs` | `tests/test_psx_cpu_decode_rust.cpp` | [decode.md](decode.md) |
| Branch / jump | #526 | ✅ Migrated | `src/psx_cpu_control.cpp` | `rust/src/cpu_control.rs` | `tests/test_psx_cpu_control_rust.cpp` | [control.md](control.md) |
| Aligned load/store | #527 | ✅ Migrated | `src/psx_cpu_memory_access.cpp` | `rust/src/cpu_memory_access.rs` | `tests/test_psx_cpu_memory_access_rust.cpp` | [memory-access.md](memory-access.md) |
| LWL/LWR/SWL/SWR | #528 | ✅ Migrated | `src/psx_cpu_unaligned.cpp` | `rust/src/cpu_unaligned.rs` | `tests/test_psx_cpu_unaligned_rust.cpp` | [unaligned.md](unaligned.md) |
| COP0 | #529 | ✅ Migrated | `src/psx_cpu_cop0.cpp` | `rust/src/cpu_cop0.rs` | `tests/test_psx_cpu_cop0_rust.cpp` | [cop0.md](cop0.md) |
| Exception resolution | #530 | ✅ Migrated | `src/psx_cpu_exception.cpp` | `rust/src/cpu_exception.rs` | `tests/test_psx_cpu_exception_rust.cpp` | [exception.md](exception.md) |
| Pipeline / load delay (second wave) | #531 | ✅ Migrated | `src/psx_cpu_pipeline.cpp` | `rust/src/cpu_pipeline.rs` | `tests/test_psx_cpu_pipeline_rust.cpp` | [pipeline.md](pipeline.md) |

Each migrated slice owns its existing internal `src/psx_cpu_<slice>.h`
boundary where present. Future maintenance should keep semantic changes in the
same owned Rust/C++/test/doc surface unless the ABI genuinely requires a shared
file change. Headers are not listed in CMake.

Everything in the table is registered in the build/test graph: each Rust module
is in `rust/src/lib.rs` and cargo's CMake `DEPENDS` list, each C++ file is in
`PSX_CPU_SOURCES`, and each native test runner is wired into the harness.
Ordinary maintenance of one completed slice should therefore avoid those shared
registration files.

## Shared files

These files belong to no slice. A slice PR that has to change one says so in
its PR body.

| File | Why it is shared |
|---|---|
| `src/psx_cpu.h` | The single `PSXCpu` declaration. Changing it is only needed to add, remove or re-sign a private member. |
| `src/psx_cpu.cpp` | Lifecycle and register accessors, `SetGPR`/`RecordGprWrite`, the helpers (`SignExtend16`, `ZeroExtend16`, `ToSigned`), and the already-migrated ALU/logic/shift/HI-LO handlers (#495, #497, #501). |
| `rust/src/lib.rs`, `CMakeLists.txt` | Registration only. Already done for every slice. |
| `tests/test_psx_core.cpp`, `tests/test_harness.h` | `main()`, the counters, and the `TEST`/`PASS`/`ASSERT_EQ`/`ASSERT_EXCEPTION` macros. Also the lifecycle, memory-API, ALU/HI-LO, end-to-end and controller tests, which belong to no slice. |
| `docs/development/rust-ffi-contract.md` | The FFI rules. A slice records its exports in its own doc here, not in the contract. |
| `include/psx_core.h`, `src/psx_rust_abi.cpp`, `src/PSXRecomp.Core/NativeInterop.cs` | The public C ABI. No slice is expected to change it. |

## Cross-slice calls

The translation units call each other through `PSXCpu` private members, so
changing one of these functions affects callers in other slices' files:

- `ExecuteInstruction` (decode) calls every `Exec*` handler and
  `RaiseException`.
- `Step`/`FetchInstruction` (pipeline) call `ExecuteInstruction`,
  `RaiseException`/`RaiseAddressError`, `TranslateAddress`/`IsMapped` and
  `RecordGprWrite`.
- `WriteRegDelayed` (pipeline) is called by the load handlers (memory access,
  unaligned) and by `ExecMfc0` (COP0).
- `SetPendingBranch` (pipeline) is called by every branch/jump handler
  (control).
- `RaiseException`/`RaiseAddressError` (exception) are called by decode,
  memory access, COP0 and pipeline.
- `TranslateAddress`/`IsMapped` (memory access) are also called by unaligned
  and pipeline.

A slice may change what these functions do inside its own file, as long as the
signature in `psx_cpu.h` stays the same.

## Maintenance rules

1. Change the smallest completed slice that owns the behavior. If a shared file
   must change, call that out explicitly in the PR.
2. Do not duplicate Rust-owned semantics back into C++. Observable CPU behavior
   changes require focused tests and an Issue that states the intended semantic change.
3. Put new regression tests in the owning slice's test file when possible.
4. Keep each slice document synchronized when its internal Rust ABI or ownership
   boundary changes; the repository-wide FFI rules remain in the
   [Rust FFI Safety Contract](../../rust-ffi-contract.md).
