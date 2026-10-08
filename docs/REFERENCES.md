# References and Prior Art

Status: Stable
Authority: Reference
Related Issues: #248, #249, #250, #279

PSXRecompStudio is informed by publicly available research and reference implementations. A project appearing in this document does not by itself mean that its source code has been copied, incorporated, or redistributed by PSXRecompStudio.

This document records architectural and behavioral prior art. It is separate from third-party notices for code, libraries, assets, or other material that may actually be incorporated or redistributed.

## Reference policy

For every reference implementation, record:

- repository and project identity;
- reference date and immutable upstream revision;
- license observed at the time of reference;
- technical areas consulted;
- whether source code was directly reused;
- the PSXRecompStudio adoption policy;
- related Issues, ADRs, or implementation work.

When source code is not directly reused, implementation should be derived independently from PSXRecompStudio contracts, tests, specifications, and observable behavior rather than by mechanical porting or superficial rewriting of another project's source.

If third-party source or other material is later incorporated, its applicable license and notice obligations must be handled separately, for example through `THIRD_PARTY_NOTICES.md` or an equivalent mechanism. This file is not a substitute for those notices.

## Reuse-first license and provenance gate (Issue #730)

**Decision order:** (1) directly reuse a technically appropriate, licensed OSS implementation; (2) adapt/port licensed code into the existing C#/Rust/C++ Runtime; (3) use public behavior, specifications and contract tests as prior art; (4) write new code only when the earlier options are unsuitable. Preserve an existing Runtime model instead of introducing parallel state.

Before directly copying, translating, porting or adapting substantial external source, create a **file-by-file audit** containing: immutable upstream commit, path and blob hash; rightsholders and exact file-level license text; included/generated dependencies and their licenses; host/R3000A ABI and backend fit; modifications; source/binary distribution and notice obligations; test evidence; reviewer, decision date and `APPROVED`/`PENDING`/`RESTRICTED`. Unknown or unclear cases remain PENDING and **cannot be incorporated**. An upstream repository's overall badge alone is not file-level clearance.

For MIT-licensed copied/modified files or portions, retain the upstream copyright and permission notice in the source or an applicable third-party notice distributed with the software. Do not silently relicense them as PSXRecompStudio-owned code. GPL-, LGPL-, MPL- and license-exception-bearing source require individual integration/distribution analysis. A source that is publicly readable but lacks a reuse license is not a copy permission. A non-commercial reference is not imported into the MIT project.

If only *observable behavior* is studied and independently implemented, record the reference and evidence; do not claim third-party code is incorporated without proof. Conversely, do not classify a line-by-line translation as independent work merely because its programming language differs.

The commercial Sony BIOS and game disc/asset contents are forbidden in tracked files, release bundles and CI. Cross-check new implementation behavior with repository-owned synthetic fixtures and actual Runtime execution, not the apparent completeness of upstream code. This research record is **not** a legal warranty.

## PCSX-Redux Nugget OpenBIOS — licensed reuse candidate

- Repo: https://github.com/pcsx-redux/nugget
- Reference snapshot: `c950e18a168944ec2d4e6d3c408fc224317483a7` (reviewed 2026-10-09).
- License observations: repository `LICENSE` contains MIT and the candidate files below each have an explicit MIT license header plus PCSX-Redux authors' copyright. **First-party observations only; all transitive dependencies and actual integration requirements remain unapproved.**
- CAUTION: the **separate** `grumpycoders/pcsx-redux` emulator repository uses GPL-2.0. Its code cannot be treated as Nugget MIT source.
- Origin: OpenBIOS README says its implementation was developed using analysis/reverse engineering of commercial PS1 BIOS. Its MIT grant applies to its own code, and does not imply permission to distribute protected Sony ROM bytes or assets.

| File (relative to Nugget root) | Observed blob SHA | Relevant services | Current decision |
| --- | --- | --- | --- |
| `openbios/card/backupunit.c` | `5c5a739186a3848c603e7221a5ec9c478e744e36` | A0:55/A0:70 `initBackupUnit`; `buInit`, card directory state, error delivery | REFERENCE_ONLY; direct/ported use PENDING |
| `openbios/sio0/driver.c` | `b846e440f98f89f55cd8eecd93fa815e424c4099` | Pad/Card IRQ, VBlank, `firstStageCardAction`, `mcReadSector` | REFERENCE_ONLY; direct/ported use PENDING |
| `openbios/sio0/card.c` | `70f664e6af75553031cfd00bd2297dcc4a54a197` | `mcWaitForStatus`, `mcReadHandler` | REFERENCE_ONLY; direct/ported use PENDING |
| `openbios/kernel/events.c` | `9c693c4db5f18c67c99bca091d7fa515b04e233e` | `OpenEvent`, `DeliverEvent`, `UndeliverEvent`, callback mode | REFERENCE_ONLY; C# implementation already exists |
| `openbios/cdrom/statemachine.c` | `c6ee3bdf657a1e0fe2eeff7b4e1e299085d40f91` | BIOS-driven CD response/state handling | REFERENCE_ONLY; don't replace PSXRecompStudio's CD hardware model |

**Usage to date:** existing BIOS/Card/Pad/Event C# and Rust implementations cite OpenBIOS for behavior, but this audit has not established that licensed source text was directly incorporated. Do not add third-party copyright to an unrelated independent C# file without evidence. If future work copies/ports code, a human must approve the pinned files/dependencies, record altered code paths and preserve the exact MIT license/copyright in an appropriate NOTICE before merge. Current #712 first blocker remains unresolved by documentation alone. Links: [#730](https://github.com/mao2009/PSXRecompStudio/issues/730), [#712](https://github.com/mao2009/PSXRecompStudio/issues/712), [#661](https://github.com/mao2009/PSXRecompStudio/issues/661).

## PSn00bSDK — alternative reference, different obligations

- Source: https://github.com/Lameguy64/PSn00bSDK — snapshot `5d9aa2d3dfc7d6e51c2eb942ab4cdbae5571a40a` (2026-10-09).
- The root `LICENSE.md` documents **MPL-2.0** for core SDK, separate copyleft obligations for `mkpsxiso` and other tools. The inspected `libpsn00b/psxcd/cdread.c` file identifies itself as MPL.
- Relevant: CD command retry/cooldown, post-Pause waiting, independently testable edge cases. **REFERENCE_ONLY / no code imported**. Direct inclusion would require a new file/dependency and distribution-format license audit, not an assumed MIT grant.

## mstan/psxrecomp

Repository: https://github.com/mstan/psxrecomp

Reference date: 2026-09-04.

Upstream revision observed: `15822694c65463e78477774bd2d5783fdd24cfac`.

License observed when referenced: PolyForm Noncommercial License 1.0.0.

Referenced areas include:

- PlayStation static recompilation architecture;
- function discovery and control-flow recovery;
- runtime-loaded overlay and dynamic-code handling;
- native dispatch and interpreter-fallback concepts;
- BIOS/runtime boundaries;
- differential/oracle validation approaches.

Usage in PSXRecompStudio:

- Direct source reuse: No.
- architectural and behavioral prior art only;
- no direct source copying or mechanical porting is intended under the current policy;
- implementation is performed independently against PSXRecompStudio SSOTs, semantic contracts, tests, and other appropriate specifications;
- concepts that are useful but not required for the current vertical slice are tracked separately rather than imported wholesale.

Related work: #248, #249.

## N64Recomp

Repository: https://github.com/N64Recomp/N64Recomp

Reference date: 2026-09-04.

Upstream revision observed: `ffb39cdad1da5de07eaaa48bd1db4a89a7986771`.

License observed when referenced: MIT License.

Referenced areas include:

- MIPS-to-C lowering patterns;
- delay-slot and control-flow treatment;
- jump-table / switch lowering;
- indirect-call runtime lookup;
- overlay and relocation concepts;
- generated-code/runtime boundaries.

Usage in PSXRecompStudio:

- Direct source reuse: No.
- reference implementation and prior art;
- concepts are evaluated against PlayStation/R3000A requirements and the existing PSXRecompStudio IR/analysis architecture rather than copied as an N64-specific design;
- any future direct source reuse must explicitly preserve the applicable MIT copyright and permission notice requirements.

Related work: #248.

## psx-spx / Nocash PlayStation Specifications

Project: psx-spx (PlayStation Specifications), originally authored by Martin
Korth (Nocash).

Repository: https://github.com/psx-spx/psx-spx.github.io

Reference date: 2026-09-09; re-consulted 2026-09-11.

Upstream revision observed: not pinned for the 2026-09-09 consultation, which
used the published renderings at
`https://psx-spx.consoledev.net/kernelbios/` and
`https://problemkaputt.de/psxspx-bios-tty-console-std-io.htm`. The 2026-09-11
identity verification below read the source of the same document,
`docs/kernelbios.md`, at commit `ecd6f794f459ab5f72feb88d46df8d23b3c413e0`.

License observed when referenced: the published document does not state a
license grant for its prose; it is treated here as behavioral documentation
consulted for reading only.

Referenced areas include:

- BIOS kernel A0/B0/C0 jump-table function identity and numbering;
- the TTY console (`std_io`) function ABIs — `A(3Ch) or B(3Dh)
  std_out_putchar(char)` and `A(3Eh) or B(3Fh) std_out_puts(src)`, including the
  documented behavior that `std_out_puts` returns its incoming string-pointer
  argument;
- the identities of the jump-table function numbers real-ROM analysis observed
  most frequently (Issue #11). Each is documented for exactly one family, and is
  not an alias of any other entry:
  - `A(39h) InitHeap(addr, size)` — initializes the address and size of the heap
    used by `malloc`/`realloc`/`calloc`/`free` and `qsort`; also deallocates all
    memory handles. The BIOS never calls it automatically, so software must.
  - `A(ABh) _card_info(port)` — checks whether the most recent `_card_write`
    completed, by issuing an incomplete dummy read command that is aborted once
    the memory card's status byte arrives. `B(4Dh) _card_info_subfunc(port)` is
    documented as its subfunction, not as an alias of it.
  - `A(ACh) _card_load(port)` — invokes asynchronous reading of the memory card
    directory.
  - `B(4Eh) _card_write(port, sector, src)` — invokes asynchronous writing of a
    single memory card sector; returns 1 on success, 0 on an invalid sector
    number. The actual I/O completes later, on IRQ level.
  - `B(50h) _new_card()` — tells the BIOS to ignore the card-changed flag on the
    next read/write operation. No arguments.
  - `B(56h) GetC0Table` and `B(57h) GetB0Table` — retrieve the address of the
    jump list for the `C(NNh)` and `B(NNh)` functions respectively, allowing
    entries in those lists to be patched (the source adds: "the BIOS does often
    jump directly to the function addresses, rather than indirectly via the
    list, so patching may have little effect in such cases" — a fact about real
    hardware's internal call graph, not a statement that the returned list is
    unused). No arguments; the address is the return
    value. The source documents both under one shared description, which does not
    restate per-function which list each returns; the split above follows the two
    function names it gives them. There is no equivalent function for the
    `A(NNh)` list.
  - The "BIOS Patches" section documents reading an *existing* jump-list entry
    as ordinary, common practice, not merely a write target: real commercial
    titles (e.g. Ridge Racer, Metal Gear Solid) call `B(56h) GetC0Table`, then
    read table entry `C(06h)` (annotated in the source as
    `;=00000C80h = exception_handler = C(06h)`) and inspect the bytes at that
    address before conditionally patching them.
  - Function-number range documentation for B0/C0 (`docs/kernelbios.md`'s
    B-Functions/C-Functions tables): `B(5Eh..FFh) N/A ;jump_to_00000000h` and
    `B(100h....) N/A ;garbage`; `C(1Eh..7Fh) N/A ;jump_to_00000000h` and
    `C(80h.....) N/A ;mirrors to B(00h.....)` — i.e. C-function numbers 0x80 and
    up are documented as dispatching through the same jump-list memory as
    B-function numbers 0x00 and up.

Verifying an identity records what a title asks the BIOS for. It is not a
decision to implement any of these as an HLE service, which ADR-014 gates
separately on evidence, criticality and Runtime prerequisites.

Usage in PSXRecompStudio:

- Direct source reuse: No.
- behavioral documentation only; no code, tables, or text were copied;
- BIOS HLE services are implemented independently against PSXRecompStudio's own
  Runtime contracts and tests from the documented behavior, not by porting any
  reference implementation;
- the documentation is used to establish BIOS function identity so that function
  numbers are verified rather than guessed; no BIOS ROM image is obtained,
  distributed, or required by this repository.

Related work: #11, #279, [ADR-014](adr/014-bios-hle-runtime-contract.md).

## Prior art vs. incorporated third-party material

The distinction is intentional:

```text
docs/REFERENCES.md
  -> research, prior art, architectural references, behavioral references

THIRD_PARTY_NOTICES.md (if/when required)
  -> third-party source, libraries, assets, or other material actually incorporated or redistributed
  -> applicable copyright and license notices
```

A reference entry must not be interpreted as evidence that third-party source code is present in the repository.

## Adding a reference

Use the following structure for future additions:

```text
Project:
Repository:
Reference date:
Upstream revision observed:
License observed when referenced:
Referenced areas:
Direct source reuse: Yes / No
Usage / adoption policy:
License handling if reused:
Related Issues / ADRs / implementation:
```

License descriptions in this document should state what was observed in the referenced repository at the recorded upstream revision and reference date, and should avoid presenting this document as legal advice.
