//! `PSXCpu` overflow-checked arithmetic (`ADD`/`ADDI`/`SUB`), migrated from
//! the C++ `PSXCpu::ExecAdd`/`ExecAddi`/`ExecSub` (Issue #495).
//!
//! Scope: only the pure overflow-detecting sum/difference computation. The
//! caller (`src/psx_cpu.cpp`) keeps owning GPR reads/writes, sign-extending
//! `ADDI`'s immediate, and raising the `Ov` exception (CAUSE Excode 0x0C) on
//! overflow. See `docs/development/rust-ffi-contract.md` and Issue #495 for
//! the full contract; the exported functions land in a follow-up commit on
//! this Draft PR, not in this scaffold.
//!
//! These symbols are internal to `PSXRecomp.Native` (declared in
//! `src/psx_cpu_alu.h`, called only from `psx_cpu.cpp`), not P/Invoked, so
//! `include/psx_core.h`, `NativeInterop.cs` and `ABI_VERSION` are unaffected
//! by this module.
