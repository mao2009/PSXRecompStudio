# ADR-023: Rust Coexists Inside the Existing Native Shared Library

- **Status**: Accepted
- **Date**: 2026-09-24
- **Issue**: #473 (parent: #471)

## Context

`ARCHITECTURE.md` records that Rust was considered and C++ chosen for the native
core, primarily because of existing PSX-emulator knowledge and C# P/Invoke
compatibility. Issue #471 revisits that for *incremental* migration rather than
a rewrite: parts of the native core would move to Rust one subsystem at a time,
while the rest stays C++.

That plan needs a decision before any subsystem moves: **where Rust lives in the
build and how managed code reaches it**. Getting this wrong is expensive to
undo, because every later migration inherits it. The existing boundary is a
single artifact — `PSXRecomp.Native` (`.dll`/`.so`/`.dylib`), built by CMake,
loaded through `[LibraryImport("PSXRecomp.Native")]` with a deterministic
fallback resolver, staged into each test project's output by MSBuild targets,
and produced per OS by three `native*` CI jobs whose artifacts the matching
`dotnet*` jobs download.

Issue #473 is deliberately behaviour-neutral: it must not migrate a subsystem,
must not change emulator behaviour, and must not block the v0.1.0 bring-up
(#351) or the runnable-artifact path (#456 / #461).

## Decision

1. **One native artifact.** The Rust crate
   (`src/PSXRecomp.Native/rust/`) is built by cargo as a `staticlib` and linked
   into the existing `PSXRecomp.Native` shared library. No second native library
   and no second native-loading path is introduced.
2. **The existing managed boundary is reused unchanged.** Rust entry points are
   declared in `src/PSXRecomp.Core/NativeInterop.cs` next to the `PSXCore_*`
   imports, against the same library name. Artifact naming, the MSBuild staging
   targets, `NativeLibraryResolver`, and the CI artifact flow are untouched.
3. **`include/psx_core.h` stays the single C ABI header.** Rust exports are
   declared there, and a thin `extern "C"` re-export layer
   (`src/psx_rust_abi.cpp`) defines the exported names. It exists for two
   mechanical reasons: a static archive member is linked in only when
   referenced, and on Windows only a `dllexport`ed definition reaches the import
   library — a static library can supply neither.
4. **cargo runs from CMake.** A custom command builds the crate before the
   shared library links, so the normal repository build produces and stages
   everything with no manual copy step. The `release` cargo profile is always
   used, so the artifact path is identical under single- and multi-config
   generators.
5. **The toolchain is pinned** in `rust/rust-toolchain.toml`, and the crate
   takes no third-party dependencies, so the unsafe surface stays auditable.
6. **The FFI rules are a separate, binding document**:
   [`docs/development/rust-ffi-contract.md`](../development/rust-ffi-contract.md).
   Every migration PR under #471 is reviewed against it.

## Alternatives Considered

- **`cdylib` as a second native library.** Rejected. It would add a second
  artifact to name, stage, resolve, upload and download per OS — duplicating
  every mechanism Issue #100 already had to get right once — for no benefit at
  this size. The `staticlib` reuses all of it.
- **Exporting Rust symbols directly with per-platform linker flags**
  (`/EXPORT:` on MSVC, `-Wl,-u,` on GNU/Mach-O) instead of the C++ re-export
  layer. Rejected: three toolchain-specific branches that only fail at link
  time, versus one translation unit that behaves identically everywhere. The
  thunks are mechanical and covered by the managed smoke test.
- **A repository-root Cargo workspace.** Rejected for now: it would place Rust
  outside the Native/runtime boundary and invite managed-adjacent crates. The
  crate is its own workspace root so a future root manifest cannot absorb it
  silently.
- **`panic = "abort"` in the release profile.** Rejected: it does contain
  panics, but by killing the managed host. Keeping `unwind` lets `catch_unwind`
  convert a panic into an ABI-safe error code, which is the pattern migrations
  must follow.
- **Deferring the decision until the first real migration.** Rejected: the
  build/link/CI mechanics are exactly the risky part, and proving them on a
  trivial function keeps that risk out of the PR that also changes behaviour.

## Consequences

- Rust code can be built, linked, called from C#, unit-tested and CI-validated
  on Linux, Windows and macOS without changing observable runtime behaviour.
- A migration PR replaces a C++ implementation in place: the exported symbol and
  the managed declaration stay where they are, so the blast radius is the
  subsystem, not the boundary.
- `cargo` becomes a build prerequisite for the native library on every platform.
  `find_program(... REQUIRED)` makes a missing toolchain a configure-time error
  rather than a confusing link failure. Where the C++ toolchain is a MinGW/GCC
  front-end on Windows — including `windows-latest` CI runners, which resolve
  `cmake -G Ninja` to MinGW — CMake selects the `x86_64-pc-windows-gnu` Rust
  target, because rustc's Windows host default is the MSVC triple and MinGW
  cannot link an MSVC static library. Since that triple is not the host, CMake
  also installs it via `rustup` at configure time; the alternative was leaving
  every MinGW user and two CI jobs with a `can't find crate for 'std'` error and
  a manual setup step.
- Rust and C++ objects share one address space and one process-wide allocator
  contract, which is why the FFI contract's "the allocator frees" rule is not
  optional.
- The `ARCHITECTURE.md` rationale for C++ is no longer the whole picture and is
  updated to record that Rust now coexists inside the native boundary.
