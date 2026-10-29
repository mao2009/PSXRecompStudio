# Issue #727: How a generated-host artifact obtains disc sectors at run time (ADR candidate)

Status: **candidate, not an accepted ADR.** This is not a numbered ADR and is not placed in `docs/adr/`. It is an input to the owner decision in [Section 7](#7-owner-decision-required). Implementation is out of scope.

Issue: [#727](https://github.com/mao2009/PSXRecompStudio/issues/727) (`[CD-ROM][Predicted]`, child of #14). Related: #10, #13, #14, #726, #730, #732, #736, #775.

Base: `origin/main` at `2e3ecad`. Verification was by reading code and docs at that commit. **No tests were run and no legal review was performed.**

## Labels

- **CONFIRMED**: stated in a repository file read at this commit, or in the Issue #727 body. Where the claim rests only on the Issue body, it says so.
- **OBSERVED**: read from the code path at this commit (static reading, not executed).
- **PREDICTED**: a consequence or future state that is not yet observed in a run.
- **UNVERIFIED**: not checked here (not run, not traced in full, or needs a legal/owner judgement).

## 1. Problem

The Issue asks where a generated-host artifact gets disc sectors when it runs, given two constraints:

- #13 (as quoted in the Issue body): the artifact produced by Recomp does not require the input CHD/BIN/CUE at run time. **UNVERIFIED**: #13 itself was not opened; this relies on the Issue quotation.
- #14 (as quoted in the Issue body): sectors come from a user-owned disc input.

Those two statements pull against each other whenever the guest reads CD sectors. The Issue is **PREDICTED**, not a current blocker. The current first blocker is `A0:70 _bu_init` (#712), per the Issue body. **CONFIRMED** (Issue body).

## 2. Current state (code at 2e3ecad)

### 2.1 The disc is a sector source injected into the device graph

- `PsxDeviceGraph` takes `ICdSectorSource? disc` in its constructor. **CONFIRMED** (`src/PSXRecomp.Core/Runtime/PsxDeviceGraph.cs`, line 52-53).
- `ICdSectorSource` is an interface with a TOC and `TryReadSector(lba, ...)`. Absent sectors are reported, never zero-filled. **CONFIRMED** (`src/PSXRecomp.Core/Runtime/CdRom/ICdSectorSource.cs`).
- Two implementations exist: `ChdCdSectorSource` (CHD, `src/PSXRecomp.Core/DiscImage/ChdCdSectorSource.cs`) and `RawCdSectorSource` (a single-track raw image). **CONFIRMED**.
- `CdRomDevice` with no disc takes the untimed/legacy path, and ReadN queues an INT1 "data ready" with no payload. **OBSERVED** (`CdRomDevice.cs` ~lines 555-559). If a generated host reached ReadN with no disc, it would get a completed-looking read with no data. This is the silent-failure risk described in Section 3.

### 2.2 Which entry points pass a disc (Issue #727 claim from W04, checked here)

| Path | Disc passed to the device graph? | Evidence |
|---|---|---|
| `RecompiledHostExecutionEngine` (generated host) | Yes, as a constructor argument `disc`, stored and used to build `PsxDeviceGraph` | **CONFIRMED** (`src/PSXRecomp.Infrastructure/RecompiledHostExecutionEngine.cs` lines 454, 639, 2012) |
| `InterpreterTitleExecutionEngine` | Yes, as an optional `disc` parameter | **CONFIRMED** (`src/PSXRecomp.Core/Execution/InterpreterTitleExecutionEngine.cs` line 256) |
| OpenBIOS interpreter/probe (`openbios-probe --disc`, `screenshot --disc`) | Yes | **CONFIRMED** (`OpenBiosProbeCommand.cs`, `ScreenshotCommand.cs`, `docs/runtime/openbios-firmware-backend.md` lines 115-123) |
| Desktop `TitleExecutionService` | No `disc` reference in the file | **OBSERVED** (grep of `src/PSXRecompStudio/Services/TitleExecutionService.cs`) |
| `RecompiledArtifactLauncher` | No `disc` reference in the file | **OBSERVED** (grep) |
| `LiveGameRunner` (standalone HLE) | No `disc` reference in the file | **OBSERVED** (grep) |

W04's summary holds for the three files checked. **UNVERIFIED**: I did not trace every caller of `RecompiledHostExecutionEngine.ForPrebuiltArtifact` or every `ScreenshotCommand` caller, so "no disc on the title/HLE/launcher paths" is confirmed only for those three files.

### 2.3 The OpenBIOS standalone distribution already needs the disc at run time

`psxrecomp package --bios openbios` (`FirmwareDistribution.cs`, `FirmwareBootManifest.cs`):

- The disc is copied to `runtime/disc/disc.{chd,bin}` in the distribution. Its SHA-256 is pinned in `firmware-boot.json` and re-verified on every run. **CONFIRMED** (`FirmwareDistribution.cs` lines ~386-451, ~666-683; `docs/runtime/openbios-firmware-backend.md` line 505).
- The native artifact holds the disc's boot executable as compiled code. OpenBIOS runs from the BIOS image and loads the rest of the game from `runtime/disc/` through its own CD driver. **CONFIRMED** (the distribution README text in `FirmwareDistribution.cs` lines ~565-580).
- The distribution README says this is a **local build** and that redistribution is pending #730. **CONFIRMED** (same file, line ~579).

So today's firmware path is CHD-dependent at run time, in a local build. It does not satisfy the #13 quotation.

### 2.4 The CHD-independent (HLE) path reads no input files

ADR-027 states that the standalone artifact's embedded image "reads no input files" and that the embedded artifact is generated from the exact state the run names. **CONFIRMED** (`docs/adr/027-standalone-artifact-runtime-integration.md`, Decision section). That path has no disc at run time, so its sector reads (if any) have no source. **OBSERVED**: this is why #726 can mark the gap as predicted rather than observed.

## 3. Runtime dependency vs CHD-independent artifact

| Artifact kind | Needs disc bytes at run time? | Status |
|---|---|---|
| Legacy HLE standalone (ADR-027 embedded image, `LiveGameRunner`, `RecompiledArtifactLauncher`) | No (reads no input files) | **CONFIRMED** (ADR-027); no disc is wired, so any ReadN/ReadS would hit the no-disc behaviour in 2.1 |
| Generated host driven by `RecompiledHostExecutionEngine` with a disc supplied | Yes, through the injected source | **CONFIRMED** (wiring); **OBSERVED** for the OpenBIOS CLI paths |
| OpenBIOS standalone distribution (`package --bios openbios`) | Yes: `runtime/disc/` sidecar copied from the user's disc | **CONFIRMED** (2.3) |
| Any artifact that embeds disc sector bytes (option B below) | No, but the artifact itself contains the copyrighted sectors | **PREDICTED** design property, not implemented |

Risk: with no disc wired, the generated host gets an INT1 with no data (2.1). When #726 makes ReadN/ReadS observable on this path, the failure will look like a successful read. **PREDICTED** (the consequence is not yet observed in a run).

## 4. Options compared

**A. External supply at run time.** The user supplies a path to their own disc (CLI or config). The path is opened as an `ICdSectorSource` and injected into the existing device graph. The artifact records the disc's SHA-256 and rejects a mismatch. The disc is referenced, not copied.
- Fit: reuses the existing `ICdSectorSource` injection point and the SHA-pinning already used for `runtime/disc/`. **CONFIRMED** (2.1, 2.3).
- Cost: the artifact is not runnable without the disc. This is the case #13 quotes as forbidden. **CONFIRMED** (Issue body).
- Redistribution: nothing copyrighted is shipped; the user keeps their own disc. **CONFIRMED** that the repository does not ship discs (Section 5).

**B. Embed sectors in the artifact.** Sector bytes (or the whole disc) are compiled or embedded into the artifact, so no external file is needed.
- Fit: satisfies the run-time CHD-independence reading of #13. **PREDICTED** design property.
- Redistribution: the artifact then contains copyrighted game data. Under the project's rules this cannot be a project-distributed artifact. See Section 5. **CONFIRMED** (rules), **UNVERIFIED** (whether a partial sector subset is treated differently by law).
- Size: not measured here. **UNVERIFIED**.

**C. Build-time extraction only (no sector reads at run time).** The disc's executables are compiled at build time (as the boot executable already is). Data files read by the guest through the CD driver are either compiled into the artifact or not supported.
- Fit: removes run-time disc reads for code. **CONFIRMED** that the boot executable is compiled today (2.3). **UNVERIFIED** whether this covers streaming or data-file reads in real titles.
- Redistribution: the same copyright issue as B for any data that is embedded. **PREDICTED**.
- This overlaps with #775 (editable, disc-free finished artifact). **CONFIRMED** (Issue body).

**D. Keep the disc out of the generated host entirely (HLE only).** Sector reads are not supported on this artifact.
- Fit: matches ADR-027 today. **CONFIRMED**.
- Limit: the OpenBIOS path cannot boot the game without sectors, so this rules out the firmware track. **PREDICTED**.

## 5. Redistribution and licence constraints (existing documents)

These are the repository's current rules. They do not decide the architecture on their own, and this is not legal advice.

- `README.md` (line 193) and `README.ja.md` (line 187): the repository contains no copyrighted ROM, ISO, CHD, BIOS, firmware or commercial assets, and user-supplied inputs must be obtained legally and not committed. **CONFIRMED**.
- `docs/legal.html`: the project does not distribute ROM, ISO, CHD, BIOS, firmware or game assets, and users are responsible for their inputs. **CONFIRMED**.
- `docs/adr/007-repository-artifact-policy.md` (lines 9, 19): copyrighted media must never enter the repository, and data derived from copyrighted media must never be allowlisted. **CONFIRMED**. The same applies to a committed artifact that embeds disc bytes.
- `docs/development/artifact-policy.md`: `forbiddenExtensions` covers disc/ROM images and BIOS dumps. `contentSignatures` checks ISO 9660 (`CD001` at offset 32769) and CHD headers. The gate detects patterns and does not decide copyright. **CONFIRMED**. **UNVERIFIED**: whether a sector blob embedded in generated C would be detected by the signature check (the check is at fixed offsets of files, not of C literals).
- `docs/REFERENCES.md` (lines 61-67): a local build does not grant redistribution rights. A maintainer redistributing a standalone output that embeds firmware or ROM bytes requires an asset-level review. A user-generated standalone output may embed the user's own ROM, but any later redistribution by the user is subject to the applicable licences, and the docs must not imply otherwise. **CONFIRMED**. This is the closest existing rule to option B: it covers user-local outputs only.
- `docs/legal/openbios-license-audit.md` and `docs/legal/OPENBIOS-NOTICE.md`: the binary asset audit is open (`shell/blip.hit` and the font binaries are unresolved), so the OpenBIOS distribution cannot be project-distributed. **CONFIRMED** (as cited by `docs/REFERENCES.md`).
- `docs/community/get-involved.md` (line 40): do not link copyrighted dumps; describe commercial evidence without uploading protected files. **CONFIRMED**.

Consequence: option B is acceptable only as a user-local output, and only with the owner's explicit approval. It cannot be a project-distributed artifact under the current rules. **CONFIRMED** (by reading the rules above; the conclusion is the author's reading).

## 6. Recommendation

**Recommend option A (external supply at run time), with the artifact referencing the user's disc by path and SHA-256 and never copying or embedding its sectors.** Keep B out of any project-distributed artifact. Treat C as a separate, later track that depends on #775.

Reasons:

1. It reuses the injection point that already works (`PsxDeviceGraph` takes `ICdSectorSource`), so no new device model is needed. **CONFIRMED** (2.1).
2. It is the only option that gives the OpenBIOS track sectors without shipping copyrighted bytes. **CONFIRMED** (rules in Section 5).
3. It matches the existing local-build behaviour (`runtime/disc/`, SHA-verified), so the change is mostly a change of storage from copy to reference. **CONFIRMED** (2.3).
4. It keeps the fail-closed behaviour of `ICdSectorSource` (absent sector is an error, not zeros). **CONFIRMED** (2.1).

Conditions that come with A, which are part of the recommendation:

- A missing disc on a path that reads sectors must fail closed, not produce an INT1 with no data (2.1, Section 3). **PREDICTED** risk if not done.
- The `runtime/disc/` copy should become a path reference plus hash. **UNVERIFIED** whether this is wanted, because the copy may have been chosen for portability.
- A is not compatible with a reading of #13 that forbids any run-time disc input. If the owner keeps that reading, A is rejected and only C (plus a separate HLE track) remains. See Section 7.

Not decided here: which real title is the first to need sector reads (#712 is the current blocker), and the choice of CHD versus raw image as the supported input.

## 7. Owner decision required

1. **Does the #13 constraint ("no input CHD/BIN/CUE at run time") apply to the OpenBIOS verification package, or only to the finished product?** If it applies to both, option A is not admissible and the owner must choose C or D for the product, and state how the firmware track proceeds. If it applies only to the product, A is accepted for the verification package. **This is the decision that selects the option.**
2. **Accept or reject option B for user-local output only** (sector bytes embedded in a file the user generates from their own disc). Under current rules it cannot be project-distributed.
3. **Approve replacing the `runtime/disc/` copy with a path reference plus SHA-256** (option A's storage change), or keep the copy.
4. **Confirm that the Issue #727 decision is an ADR candidate for a numbered ADR (for example ADR-028, after the owner accepts this document)** and that the implementation Issues are filed after acceptance, per the Issue's Definition of Done.

Until decision 1 is made, the project should not implement any option. Implementation stays out of scope for this candidate.
