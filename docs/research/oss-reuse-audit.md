# OSS Reuse Audit for v0.1 (Issue #740)

Status: Draft (research record; no dependency, product code, or licensing change)
Authority: Reference
Related Issues: #740 (this audit), #730 (OpenBIOS license audit), #732 (OpenBIOS backend), #661 / #712 (PAD/Card IRQ, `_bu_init`), #443 / #454 / #541 / #543 (SIO0 protocol), #734 / #739 (v0.1 execution gates), #773 (AOT root discovery), #774 (standalone vs interpreter divergence)
Related PRs: #731 (merged; reuse-first OpenBIOS audit and gates), #733 (merged; replaceable BIOS backend seam), #765 (merged; OpenBIOS-booted standalone distribution), #435 (merged; input mapping layer)
Baseline: `origin/main` at `19507d1` (2026-10-15)

This document is a research record. It does not grant a license, approve distribution, or select a dependency. Per [References and Prior Art](../REFERENCES.md), a file-level audit is required before any third-party source is incorporated. The license observations below are first-party readings of upstream files and are not legal advice.

Evidence labels: **[FACT]** = read directly from a repository file or API result at the SHA listed. **[INFERRED]** = derived from code reading, not confirmed by a test. **[HYPOTHESIS]** = an effort estimate with no measurement behind it. **[UNVERIFIED]** = not checked.

---

## 1. Executive Summary

- **No candidate is a v0.1 critical-path item.** The v0.1 gate in the repository is the Persona title-screen end-to-end path ([#351](https://github.com/mao2009/PSXRecompStudio/issues/351), [#734](https://github.com/mao2009/PSXRecompStudio/issues/734), [#739](https://github.com/mao2009/PSXRecompStudio/issues/739)), with the title screen not yet reached [FACT: `docs/index.html:106`]. The remaining blockers are runtime and BIOS-boundary work (PAD/Card IRQ, `_bu_init`, SIO0 protocol, execution parity). None of the audited OSS libraries replaces that work.
- **Already reused or already in main, so not new work:** OpenBIOS (fetched and built locally, not vendored, via PR #765 and #733); the input mapping layer (PR #435, which deliberately excludes an OS backend); PNG output (hand-written `PngWriter` on BCL `ZLibStream`); the CHD reader (hand-written, `cdlz` and `cdzl` only).
- **Recommendation:** adopt nothing into the codebase now. Keep the current hand-written and BCL-based implementations. Record the following as decisions:
  - **SDL3:** hold for v0.1. If an interactive standalone input backend is scheduled later, SDL3 is the leading candidate for the host-input adapter only. Its zlib license is permissive, but its bundled third-party code has its own terms.
  - **Virtual PAD adapter:** build it as internal test code, not as a library. It needs the SIO0 protocol first, so design it after #661 / #712 / #443 move.
  - **libcue:** do not adopt (GPLv2, whole project). If v0.1 needs BIN/CUE input, write a small spec-based parser instead. This is a real gap, see §3.
  - **libchdr:** hold. Adopt only if real target discs use `cdfl` or `cdzs`, which the current reader rejects.
  - **Beetle PSX:** reference only. Do not import code (GPLv2; its bundled `lightning` is GPLv3 and `lightrec` is LGPL-2.0).
  - **pcsx-redux (emulator):** reference only (GPLv2). Not the same project as Nugget OpenBIOS.
  - **miniaudio:** hold. No audio output exists yet, so this is Future work.
  - **Dear ImGui:** defer. It is not needed for v0.1 and would add a second UI stack beside Avalonia.
  - **Catch2:** do not adopt. The existing xunit and custom native harness cover the need.
- **Effort reduction:** no reduction figure is claimed here. No measurement exists for any candidate, so any percentage would be a fabricated number, which the issue prohibits.
- **Documentation discrepancies (§11)**: REFERENCES.md marked the Nugget files as direct reuse PENDING while `docs/legal/openbios-license-audit.md` marked the same files APPROVED. This branch reconciled the legal audit to PENDING; the `statemachine.c` blob difference is only pinned-vs-HEAD, not a recording error. Any APPROVED decision remains a human decision (#794).

---

## 2. Current Implementation Findings

All paths are relative to the repository root at `19507d1`.

| Area | Status | Evidence | Existing dependency | Gap / debt | v0.1 relevance |
|---|---|---|---|---|---|
| Language split and FFI | C++ shared library `PSXRecomp.Native` with a Rust staticlib, managed binding via `LibraryImport`. Hand-written header. | `src/PSXRecomp.Native/include/psx_core.h`; `src/PSXRecomp.Core/NativeInterop.cs`; ADR-023 | Rust: no third-party crates (`src/PSXRecomp.Native/rust/Cargo.toml`). C++: none. | ABI version declared in Rust (`ABI_VERSION = 1`, `rust/src/lib.rs:57`) and in the header and managed side under different names. Only an export-name test (`tests/test_psx_abi_contract.cpp`) links them. | Yes (existing substrate) |
| RuntimeHost | Implemented for Phase 2 Step C (#739). `--frame-out` writes one end-of-run PNG. | `src/PSXRecomp.RuntimeHost/Program.cs:61`; `src/PSXRecomp.Infrastructure/Standalone/RuntimeHostFrameOutput.cs:38`; `docs/runtime/runtime-host.md` | .NET 10 | Frame output is a single snapshot, not a stream. | Yes |
| Recompiler | AOT emits C# source; interpreter fallback via `MixedFallback`. | `src/PSXRecomp.Core/Recompiler/`; `src/PSXRecomp.Core/Execution/MixedFallback.cs`; `src/PSXRecomp.Infrastructure/RecompiledHostExecutionEngine.cs` | None | Known native/interpreter divergence (#774). | Yes (#734, #739) |
| OpenBIOS | Integrated as a replaceable backend. The ROM is built locally from pinned Nugget `c950e18` and is not vendored. Persona opening imagery is reached on the interpreter path only. | PR #765 (merge `7704e84`), PR #733; `docs/runtime/openbios-firmware-backend.md`; `src/PSXRecomp.Cli/OpenBiosProbeCommand.cs`; `docs/index.html:106` | Build-time only; no runtime dependency | Title screen not reached. The two documentation sources were reconciled to PENDING (§11); the human decision is tracked in #794. | Yes (#732 is still Open) |
| GPU and frame output | Pure managed software GPU (VRAM 1024x512 halfword buffer, rasterizer, GP0 decoder). PNG output is hand-written. No GPU backend. | `src/PSXRecomp.Core/Runtime/Gpu/GpuDevice.cs`, `GpuVram.cs`, `GpuRasterizer.cs`, `Gp0CommandDecoder.cs`; `src/PSXRecomp.Infrastructure/Runtime/Gpu/PngWriter.cs` (BCL `ZLibStream`); ADR-022 | None | No hardware-accelerated presentation (not needed for the headless path). | Yes (for frame output); No (for presentation) |
| SPU and audio | **No audio output.** `ISpu` has no implementation. Rust `spu.rs` is register-only. | `src/PSXRecomp.Core/Runtime/ISpu.cs`; `src/PSXRecomp.Native/rust/src/spu.rs` | None | Total absence of synthesis and output. | Unknown (see §8 open question) |
| CD-ROM and disc image | ISO9660 reader, PS-X EXE parser. CHD reader supports `cdlz` and `cdzl` plus uncompressed hunks. Any other codec tag throws. | `src/PSXRecomp.Core/DiscImage/Iso9660Reader.cs`, `PsxExe.cs`, `ChdReader.cs`, `ChdCdCodec.cs` (lines 18-19, 69) | SharpCompress (LZMA for `cdlz`); BCL `DeflateStream` for `cdzl` | `cdfl` and `cdzs` unsupported. | Depends on target disc images |
| BIN/CUE | **No production parser found.** `src/` has no CUE/BIN parser; one test file contains a `.cue` string. | grep over `src/**/*.cs` | None | Gap if the v0.1 input is BIN/CUE. | Unknown (open question) |
| SIO0 / PAD / Memory card | SIO0 register file in Rust, every port reports "disconnected." Managed BIOS pad and card state exist. Memory card as a 128 KiB file. No controller or card wire protocol. | `src/PSXRecomp.Native/rust/src/sio0.rs` (module header); `src/PSXRecomp.Core/Runtime/BiosPadState.cs`, `BiosPadCardIrqHandler.cs`, `BiosCardState.cs`; `src/PSXRecomp.Core/MemoryCard/MemoryCardImage.cs`; `docs/runtime/memory-card.md` | None | Protocol and IRQ path are the open blockers (#443 family, #661, #712). | Yes (blocker) |
| Host input | Console-agnostic mapping and PS1 device policy. Explicitly excludes any OS/device backend (SDL, XInput, DirectInput, evdev). | `src/PSXRecomp.Core/Runtime/Input/` (`InputBindingMap.cs`, `Ps1ControllerState.cs`, etc.); `docs/runtime/input.md` | None | No physical input acquisition. | Not for headless v0.1 |
| CLI and Studio | CLI commands: run, recompile, package, screenshot, openbios-probe. Studio is Avalonia MVVM with no keyboard or gamepad handling. | `src/PSXRecomp.Cli/`; `src/PSXRecompStudio/` | Avalonia 11.3.6, CommunityToolkit.Mvvm 8.2.1 | No interactive input. | Partly (CLI yes, Studio input no) |
| Native and .NET tests, CI | Native: custom `tests/test_harness.h` (not Catch2), CTest targets. .NET: xunit 2.9.3, FluentAssertions, Avalonia.Headless. CI: `.github/workflows/ci.yml` with native, dotnet, gui-tests, and a gate job (line 225). | `src/PSXRecomp.Native/tests/test_harness.h`; `src/PSXRecomp.Tests/`; `.github/workflows/ci.yml` | As listed | No deterministic input script or replay facility. A golden trace exists (`tests/golden_trace.h`). | Yes (test infrastructure) |
| Third-party inventory (whole repo) | No `.gitmodules`, no vendored directories. No code references to SDL, miniaudio, libchdr, libcue, ImGui, Catch2, or zlib libraries. | Repository-wide grep (excluding `out/`, `artifacts/`, `build/`, `rom/`, `logs/`, `reports/`) | `loach.PureSharp 0.1.6`, `loach.ArchitectureAnalyzer 0.1.0`, `SharpCompress 0.50.4`, Avalonia, CommunityToolkit.Mvvm, Tmds.DBus.Protocol | None | — |

Already-implemented items the issue body could be read as calling missing: OpenBIOS boot path (#765), frame PNG output (`--frame-out`), input mapping layer (#435), memory-card file storage, CHD reading (for the codecs above).

---

## 3. OSS Candidate Comparison

Commit SHAs are the upstream default-branch HEAD read via `gh api` on 2026-10-15. "Top-level license" means the repository's `LICENSE` file, not a README badge.

| Candidate | Upstream (SHA read) | Top-level license [FACT] | Mixed or bundled licenses [FACT] | Possible role in PSXRecompStudio | Decision | v0.1 impact |
|---|---|---|---|---|---|---|
| **SDL3** | https://github.com/libsdl-org/SDL @ `6defbc0129b5ba90bab3f6c67bbc1e9329e24ff1` | zlib (`LICENSE.txt`, blob `e9adee44…`) | `src/hidapi/` is triple-licensed (GPL-3.0 / BSD / original; user chooses). `src/libm/` is fdlibm-derived. `src/video/khronos/` is Khronos MIT-style. No submodules. | Host Input Adapter, window, and audio backend. Does **not** provide PS1 PAD, SIO0, IRQ, or BIOS behavior. | 現時点では保留 (hold). Revisit when interactive standalone input is scheduled. | **Indirect/Future.** Not on the headless critical path. |
| **Virtual PAD adapter** (internal, no library) | Reuses existing `golden_trace.h` and `Ps1ControllerState` concepts. | N/A | N/A | Deterministic, SDL-free input script feeding the Guest PAD State. Needs the SIO0 protocol before it has any effect. | 限定的な採用 as internal test code, after #443 / #661 / #712. Design only; no implementation in this audit. | **Indirect.** Improves reproducibility for CI and E2E. |
| **miniaudio** | https://github.com/mackron/miniaudio @ `9634bedb5b5a2ca38c1ee7108a9358a4e233f14d` | Dual: Public Domain (Unlicense) **or** MIT-No-Attribution (user chooses) | `external/fs/` vendored with **no license text found** [UNVERIFIED]. `.gitmodules` is empty. | PCM output backend. No audio exists to back. | 現時点では保留 | **Future.** |
| **libchdr** | https://github.com/rtissera/libchdr @ `47d794151b2b64f6aba60304e6480f4f5a2bc24b` | BSD-3-Clause (`LICENSE.txt`, blob `1c36e5b5…`) | `deps/` contains `lzma`, `miniz`, `zstd`; their per-library licenses were not individually verified [UNVERIFIED]. | Full CHD codec set (adds `cdfl`, `cdzs`). Replaces hand-written `ChdReader`, which only covers `cdlz` and `cdzl`. | 現時点では保留. Decide after checking which codecs the target disc images use (local check, no image committed). | **Future** unless target discs need `cdfl` or `cdzs`. |
| **libcue** | https://github.com/lipnitsk/libcue @ `f35fd675703d8984c2ea26cca7cb403493368fd5` | **GPLv2** for the project (`LICENSE`, blob `60874f76…`, "There are two licences involved"); cuetools-derived files are GPLv2 | README and source headers not read [UNVERIFIED]. | CUE parsing. | **不採用** (do not link or copy). A small spec-based parser written in-tree would avoid GPL coupling. | **Direct if BIN/CUE is a v0.1 input**, otherwise None. |
| **Dear ImGui** | https://github.com/ocornut/imgui @ `c1aaac65e896a45f81f47a4d68f9959e0f86c859` | MIT (`LICENSE.txt`, blob `d5ba3155…`) | README license line not read [UNVERIFIED]. | Debug GUI, VRAM and register viewers. Would duplicate Avalonia. | **延期** | **None** for v0.1. |
| **Catch2** | https://github.com/catchorg/Catch2 @ `08092139210881f01a34e60c5f2fd6afa0ea4024` | BSL-1.0 (`LICENSE.txt`, blob `36b7cd93…`) | `third_party/clara.hpp` is BSL-1.0 (header read). | C++ test framework. | **不採用** — the existing native harness and xunit cover the same need; a new framework adds cost with no unmet requirement. | **None.** |
| **Beetle PSX** | https://github.com/libretro/beetle-psx-libretro @ `e57860d82faa953dc0fc10f201fd583739bc2f7d` | **GPL-2.0** (`COPYING`, blob `afd5a947…`) | `deps/lightning` is GPLv3. `deps/lightrec` is LGPL-2.0. `deps/openbios` is MIT (PCSX-Redux authors). `deps/mman`, `deps/wiiu` [UNVERIFIED]. | GPU, SPU, CD-ROM, and compatibility behavior reference. | **参照実装のみ** (reference only). No code import. | **None** (reference value only). |
| **PCSX-Redux emulator** | https://github.com/grumpycoders/pcsx-redux @ `db9bc8af44e3c77424127b573c17f9583ee2ab95` | **GPL-2.0** (`LICENSE`, blob `d1591691…`). Source headers say "version 2 ... or later." | `LICENSES.md` enumerates bundled libraries. ~50 submodules; only some named ones verified [UNVERIFIED]. | Reference for hardware behavior. Not the same project as Nugget. | **参照実装のみ**. Do not copy (GPL-2.0). | **None.** |
| **PCSX-Redux Nugget OpenBIOS** | https://github.com/pcsx-redux/nugget. Pinned `c950e18a168944ec2d4e6d3c408fc224317483a7`; HEAD read `51ee9abf6e2fb4176e5daa2734569c428835cfa9` | MIT (`LICENSE`, blob `490e7cb3…`, unchanged between pinned and HEAD) | `third_party/` (EABase, EASTL, snitch, psxlua, xmake-psx, libcester) licenses [UNVERIFIED]. | Already the pinned build-time BIOS source (see §6). | 既存採用 (already adopted as a build-time source). Further direct source reuse 保留 pending #730 file-level audit. | **Direct** (already in place for #732). |

### Nugget per-file status at pinned vs HEAD

| File | Pinned `c950e18` blob (per REFERENCES.md) | Upstream HEAD blob | Match |
|---|---|---|---|
| `openbios/card/backupunit.c` | `5c5a7391…` | `5c5a7391…` | yes |
| `openbios/sio0/driver.c` | `b846e440…` | `b846e440…` | yes |
| `openbios/sio0/card.c` | `70f664e6…` | `70f664e6…` | yes |
| `openbios/kernel/events.c` | `9c693c4d…` | `9c693c4d…` | yes |
| `openbios/cdrom/statemachine.c` | `c6ee3bdf…` | `577eb186…` | **no** (HEAD moved on after the pinned commit) |

The pinned revision in `docs/runtime/openbios-firmware-backend.md` is unchanged and is the revision used. Verified on 2026-10-16 with the GitHub contents API: `c6ee3bdf…` is the blob at the pinned commit `c950e18…`, and `577eb186…` is the blob at upstream HEAD `51ee9ab…`. The column compares the pinned blob with HEAD, so the mismatch means only that HEAD has moved on for that file. REFERENCES.md's value is correct for the pin, and only its column label needed clarifying. The file-level reuse decision is still PENDING.

---

## 4. License Audit

Per-candidate summary. Items marked [UNVERIFIED] must be closed before any source is incorporated (the audit gate requires an immutable path, blob hash, and file-level text per file).

| Candidate | Source checked | License type | File-level differences | Use forms available | Redistribution obligations (if used) | Uncertainties |
|---|---|---|---|---|---|---|
| SDL3 | `LICENSE.txt` (zlib), `src/hidapi/LICENSE*.txt`, `src/libm/`, `src/video/khronos/` headers | zlib (core); tri-licence (hidapi); Sun-style (libm); Khronos MIT (headers) | Yes | Link (dynamic or static), or copy with notice | Keep zlib notice; if hidapi used, pick one of its licenses and keep its notice | Full Khronos header license text [UNVERIFIED] |
| miniaudio | `LICENSE` (Unlicense / MIT-0 dual) | Public domain or MIT-No-Attribution | Vendored `external/fs/` | Single-header include | Minimal; MIT-0 needs no attribution | `external/fs/` license text not found [UNVERIFIED] |
| libchdr | `LICENSE.txt` (BSD-3) | BSD-3-Clause | `deps/` third-party under their own terms | Link or copy | BSD-3 notice; bundled deps' notices | Per-dep license files for `lzma`, `miniz`, `zstd` [UNVERIFIED] |
| libcue | `LICENSE` (GPLv2 with cuetools files) | GPL-2.0 (project-wide, per its own statement) | Two-license wording in file | Link or copy | GPL-2.0 applies to the combined work if linked or copied; **incompatible with a MIT-distributed core** unless the whole product became GPL | README and headers [UNVERIFIED] |
| Dear ImGui | `LICENSE.txt` (MIT) | MIT | None observed in header sample | Copy or submodule | Retain MIT notice | README line [UNVERIFIED] |
| Catch2 | `LICENSE.txt` (BSL-1.0), `third_party/clara.hpp` | BSL-1.0 | None observed | Link or header | Retain BSL notice | None material |
| Beetle PSX | `COPYING` (GPL-2.0), `deps/*` | GPL-2.0 (project); GPLv3 (`lightning`); LGPL-2.0 (`lightrec`); MIT (`openbios`) | Yes | Reference only | Any copy would impose GPL/LGPL terms | `mman`, `wiiu` [UNVERIFIED] |
| PCSX-Redux emulator | `LICENSE` (GPL-2.0), `LICENSES.md`, sampled headers | GPL-2.0 (or later) | Yes (third_party varies) | Reference only | GPL-2.0 for any copy | Most submodules [UNVERIFIED] |
| PCSX-Redux Nugget | `LICENSE` (MIT), `README.md` line 21, sampled `openbios/` blobs | MIT; third_party carries own licenses | `third_party/` differs | Build-time use (current); copy with notice | Retain MIT copyright/permission notice in NOTICE | `third_party/` licenses [UNVERIFIED]; `statemachine.c` blob drift (§3) |

**Gate outcome:** no candidate is cleared for source incorporation by this audit. Nugget OpenBIOS has an MIT top-level license, but its build-time use is governed by #730 / #732 and `docs/legal/openbios-license-audit.md`, and its file-level status is in conflict (§11).

**Caution on non-commercial and GPL material:** the `mstan/psxrecomp` reference (PolyForm Noncommercial, per REFERENCES.md) and every GPL-2.0 candidate here must remain reference-only.

---

## 5. FFI, ABI, and Cross-Platform Impact

- **SDL3** would add a C dependency. It crosses the C++ native library boundary (not the managed boundary) and would need a new `LibraryImport` surface or a managed P/Invoke binding plus Windows, Linux, and macOS binaries in CI. Cost: new native packaging and a test matrix. Not free.
- **libchdr** would add a C dependency plus three compression libraries (`lzma`, `miniz`, `zstd`) at the same boundary. It would also replace the managed `ChdReader` and change error behavior that the current tests check.
- **miniaudio** is single-header C, so the integration cost is in the audio thread and the managed/native callback boundary, not in linking.
- **libcue** and **Beetle PSX** are not recommended for linking. Beetle would need an FFI wrapper around a large GPL codebase.
- **Dear ImGui** would need a second native rendering path beside Avalonia. This would be a second UI stack beside Avalonia Studio; no architecture document was checked for or against this.
- **Catch2** would add a second native test framework beside `test_harness.h`, which is already wired into CTest.
- **Cross-platform:** the current native build and CI run on Linux; `docs/development/ci-policy.md` (lines 13-18) requires Windows and macOS coverage only for release candidates, not ordinary PRs. Any candidate that adds a platform backend (SDL3, miniaudio) must add verification for each platform it targets before it can count as done.

---

## 6. Effort Reduction Assessment

Each estimate is labeled. No measured values exist.

| Candidate | Self-built scope | Already done | OSS covers | OSS does not cover | Integration | Maintenance | v0.1 effect |
|---|---|---|---|---|---|---|---|
| SDL3 | Host input acquisition (not built) | Input mapping layer | Device polling, window, event loop | PAD/SIO0/IRQ/BIOS behavior | [HYPOTHESIS] moderate: native build plus managed binding plus CI matrix | Upstream version tracking | None on the headless path |
| Virtual PAD adapter | Test input script (not built) | Golden trace, input model | Nothing external | Everything, as it is internal | [HYPOTHESIS] small once SIO0 exists | Low | Indirect |
| miniaudio | Audio output (not built, absent entirely) | Register-only SPU | PCM device abstraction | SPU synthesis, timing, IRQ9 | [HYPOTHESIS] moderate | Low to moderate | Future |
| libchdr | CHD codecs `cdfl`, `cdzs` (not built) | `cdlz`, `cdzl`, raw hunks | Full CHD V5 decode | Disc parsing semantics, which the repo owns | [HYPOTHESIS] moderate (three native deps) | Moderate | Future unless target discs need them |
| libcue | CUE/BIN parsing (not built, absent) | ISO9660 reader | CUE tokenization | Nothing the repo cannot write itself | [HYPOTHESIS] small to write in-tree | Low | Direct if BIN/CUE is in v0.1 scope |
| Dear ImGui | Debug UI (not built) | Avalonia Studio | Immediate-mode widgets | Would duplicate Avalonia | [HYPOTHESIS] large | Moderate | None |
| Catch2 | Nothing missing | xunit, native harness | Test framework | Nothing needed | Adds cost, no gain | Low | None |
| Beetle PSX | Behavior verification | Own tests and fixtures | Reference behavior only | Code copy forbidden | N/A | N/A | None (reference) |
| Nugget OpenBIOS | BIOS (provided by the Nugget build) | PR #765 | BIOS image | Title screen progress | Done for interpreter path | Revision pin | Direct (already used) |

**Effort-reduction figure:** none is given. The issue prohibits unsupported percentages, and no candidate has a measured effort saving.

---

## 7. SDL3 / Virtual PAD Responsibility Boundary

```text
Host Controller (OS device, via SDL3 if adopted)
  → Host Input Adapter (SDL3 ends here)
  → Physical controller state (PhysicalControllerState / InputBindingMap, already exists)
  → Console-agnostic snapshot (ControllerInputSnapshot, already exists)
  → Guest PAD State (Ps1ControllerState, already exists; device policy)
  → SIO0 Protocol (NOT implemented: #443 family)
  → IRQ / Scheduler (NOT implemented: #661)
  → BIOS / Game (OpenBIOS or HLE; #732, #712)
```

- **SDL3's responsibility ends at the Host Input Adapter.** It does not produce a PS1 pad response, perform SIO0 timing, raise IRQ7, or implement BIOS pad or card services. Treating SDL3 as the fix for #661 / #712 is wrong and is explicitly rejected here.
- **The virtual PAD adapter** sits beside the host adapter, feeding the same Guest PAD State from a deterministic script instead of hardware. It is not an SDL3 substitute. It can be built only after the SIO0 protocol exists; before then, a script can only assert the logical snapshot, which the existing `docs/runtime/input.md` contract already covers.
- **New design needed?** Not now. The existing layers (mapping, console snapshot, device policy) already define the boundary. The one missing piece is the SIO0 protocol, which is an existing blocker. A new interface would add a parallel state model. Recommendation: no new interface until #443 family and #661 land. If one is needed later, it should consume `Ps1ControllerState` directly.

---

## 8. Impact on v0.1

| Candidate | Classification | v0.1 impact | Reason |
|---|---|---|---|
| Nugget OpenBIOS | 既存採用 / 参照 | Direct (already applied) | Already the BIOS source for the interpreter path (#765) |
| SDL3 | 現時点では保留 | Indirect / Future | Headless v0.1 needs no host input |
| Virtual PAD adapter | 限定的な採用 (internal, after SIO0) | Indirect | Better CI reproducibility once SIO0 exists |
| libcue | 不採用 (write in-tree if needed) | Direct only if BIN/CUE is a v0.1 input | GPL coupling makes it unsuitable |
| libchdr | 現時点では保留 | Future (or Direct if target codecs demand it) | Current reader covers the codecs in use, as far as checked |
| miniaudio | 現時点では保留 | Future | No audio backend exists |
| Dear ImGui | 延期 | None | Not needed, duplicates Avalonia |
| Catch2 | 不採用 | None | Existing harnesses suffice |
| Beetle PSX | 参照実装のみ | None | GPL; reference only |
| PCSX-Redux emulator | 参照実装のみ | None | GPL-2.0 |

**Open question:** the v0.1 scope is not documented as a single scope file. It is inferred from `docs/v0.1.0/persona-e2e-status.md` and the Issue #9 milestone [INFERRED]. If v0.1 requires BIN/CUE input or audible output, the Direct/Future classification for libcue and miniaudio changes.

---

## 9. Recommended Adoption Order

1. **Nugget OpenBIOS (pinned): continue as-is.** Complete the #730 file-level audit (including the `statemachine.c` blob) before any further source reuse.
2. **In-tree BIN/CUE parser (if in v0.1 scope):** write a small spec-based parser. Do not use libcue.
3. **Virtual PAD adapter:** design after #443 family, #661, and #712 are resolved. Internal code only.
4. **SDL3 (host input):** revisit when interactive standalone input is scheduled. Limit the dependency to the host adapter.
5. **libchdr:** only if target disc images require `cdfl` or `cdzs`, and only after confirming the three bundled compressors' licenses.
6. **miniaudio:** only after an SPU output path exists.
7. **Dear ImGui, Catch2, Beetle PSX code, libcue, PCSX-Redux emulator code:** not adopted (see §10).

---

## 10. Rejected and Deferred Candidates

- **libcue (不採用):** GPL-2.0 project-wide. Linking or copying would impose GPL on the combined work, which conflicts with the MIT project license adopted in [#92](https://github.com/mao2009/PSXRecompStudio/issues/92). Replace with an in-tree parser if needed.
- **Catch2 (不採用):** no gap that the native harness and xunit leave open.
- **Dear ImGui (延期):** not needed for v0.1. Would duplicate the Avalonia Studio UI.
- **Beetle PSX (reference only):** GPL-2.0 with GPLv3 and LGPL-2.0 deps. Behavior and test reference only.
- **PCSX-Redux emulator (reference only):** GPL-2.0. Not the same as Nugget.
- **mstan/psxrecomp (reference only):** PolyForm Noncommercial, per REFERENCES.md. Not imported.

---

## 11. Unverified Items and Risks

**Documentation discrepancies found (items 1 and 2 reconciled in this PR; item 3 remains a follow-up):**

1. `docs/REFERENCES.md` marks `openbios/card/backupunit.c`, `sio0/driver.c`, `sio0/card.c`, `kernel/events.c`, and `cdrom/statemachine.c` as "REFERENCE_ONLY; direct/ported use PENDING." `docs/legal/openbios-license-audit.md` marked the same files "APPROVED". Reconciled toward PENDING in this branch: the file-level rule in REFERENCES.md requires a reviewer and a decision date, and none is recorded. The legal audit now records the per-file MIT header and leaves direct reuse PENDING. Whether to record any file-level APPROVED is a human decision.
2. Resolved on 2026-10-16: the `statemachine.c` blob recorded in `docs/REFERENCES.md` (`c6ee3bdf…`) is the blob at the pinned commit `c950e18…`. The differing value `577eb186…` is the blob at upstream HEAD `51ee9ab…`. No recording error was found.
3. `docs/development/ci-policy.md` requires Windows and macOS for release candidates. The `ci` gate in `.github/workflows/ci.yml` (line 225) covers Linux native, .NET, and GUI tests. These are consistent for ordinary PRs, but a reader could misread the policy. Clarify when next editing that file.

**Unverified upstream items:**

- miniaudio `external/fs/` license.
- libchdr `deps/` per-library licenses (`lzma`, `miniz`, `zstd`).
- libcue README and source-file headers.
- Dear ImGui README license line.
- Beetle PSX `deps/mman`, `deps/wiiu`, and source headers.
- PCSX-Redux `third_party/` licenses (ELFIO, ImFileDialog, PEGTL, clip, cq, arith64, ftxui, and submodules).
- Nugget `third_party/` licenses (EABase, EASTL, snitch, psxlua, xmake-psx, libcester).
- Full Khronos header license text bundled with SDL.

**Open technical questions:**

- Which codecs and container formats do the target disc images use? (Requires a local check; no disc image is committed or distributed.)
- Does v0.1 require BIN/CUE input?
- Does v0.1 require audio output?

**Risks:**

- Relying on the zlib, BSD, MIT, or public-domain status of a top-level file without checking bundled code (the SDL, libchdr, and miniaudio cases above) would be a provenance error.
- The Nugget `statemachine.c` drift means the recorded file-level evidence is stale.

**Not performed:** no build, no test run, no ROM run. No product code, dependency, or license file was changed.

---

## 12. Recommended Next Issues

No new issue is opened by this audit. Existing issues cover the blockers. Suggested follow-ups, each to be checked for duplicates first:

1. **Reconcile the OpenBIOS license status** between `docs/REFERENCES.md` and `docs/legal/openbios-license-audit.md`, and re-verify the `statemachine.c` blob. Likely belongs under #730.
2. **Decide the BIN/CUE input requirement for v0.1.** If required, open one Issue for an in-tree CUE parser (no libcue). Check for an existing disc-input Issue first.
3. **Decide whether target discs need `cdfl` / `cdzs`.** If yes, open a libchdr evaluation Issue with per-dependency license audit.
4. **Schedule host-input design (SDL3 as adapter only)** after #661 / #712 / #443 family, and reference #47 and #435 (the existing input contract).

Comment for Issue #740 (draft): link this PR, state that no candidate is on the v0.1 critical path, list the four follow-ups, and note that #730, #732, #661, #712 remain the owning Issues.
