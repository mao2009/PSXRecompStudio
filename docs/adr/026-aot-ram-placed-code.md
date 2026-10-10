# ADR-026: AOT RAM-placed code

- Status: Accepted
- Date: 2026-10-10
- Issue: #732
- Parent implementation: PR #733; dependent fixes: PR #742, #745, #746

## Context

OpenBIOS copies its kernel, vector stubs and shell into RAM and loads a PS-X EXE
from disc. Compiling only immutable ROM forces that code through interpreter
fallback. The existing multi-image implementation already refers to ADR-026;
this record documents its contract without introducing runtime compilation.

## Decision

Compile explicit images before execution at their guest destination addresses.
`LoadImageManifest` accepts ROM slices, PS-X EXE files, the disc boot EXE and
`interpret` observation entries. Roots are explicit build inputs; the EXE entry
is included, and excluded observation entries remain interpreted. In-image
observation PCs become discovery leaders, and every fused unit containing one
is omitted, including load observers and branch delay slots. Missing roots
or unsupported reachable entries reduce AOT coverage rather than permit guessed
code. Manifest image and roots paths resolve relative to the manifest directory.

`ReachableProgramBuilder.BuildLoadedImage` lowers those images into guarded
blocks. `LoadedCodeTable` links versions by guest entry PC and exact instruction
words; identical versions are deduplicated. Static ROM block entries and loaded
entries cannot collide. Different images may provide different versions at one
RAM address, including overlays.

Before selecting a loaded block, generated dispatch validates all words of its
fused instruction unit against guest RAM. Guest stores, host/device writes and
fallback commits invalidate the affected 4 KiB page generations. An unchanged
page generation permits reuse of a prior successful comparison; a changed
generation requires another exact word comparison. Unknown or changed code
must fall back, never execute a stale version. The immutable-ROM optimization
that proves a delay-slot load unobserved from successor instructions is disabled
for mutable loaded images: successors lie outside the unit's guarded words and
can change independently. Such units remain fallback until a stronger guarded
contract is implemented. These guards validate identity;
they do not generate new code or alter instruction semantics.

During mixed fallback, the existing ADR-025 RAM/state copy-sync contract remains
in force. Returning to a loaded entry requires a matching version in the native
core's RAM and a clean pipeline boundary (no branch delay or pending load), just
as static block returns do. Fallback transition observers remain connected for
both ROM-only and multi-image execution. Observation entries stay interpreted,
so probes can compare state at explicit boundaries.

Guest time, device scheduling and IRQ acceptance remain ADR-025 responsibilities.
AOT identity checks do not retire instructions or advance guest time. Native and
fallback counts remain separate; missing coverage and version mismatches must
be reported as fallback rather than hidden as native execution.

## Alternatives

- Runtime code generation was rejected: this project is AOT-only, and build
  provenance must remain explicit and reproducible.
- Trusting a load address alone was rejected: overlays and guest stores can
  replace code at that address.
- Comparing every word on every dispatch is correct but avoids no redundant
  work; page generations retain exact comparison after each relevant write.
- Forcing all RAM through fallback remains a supported ROM-only configuration,
  but cannot provide useful native coverage for firmware-loaded code.

## Consequences and verification

Callers must supply every desired image and its roots before compilation.
Unlisted versions, unsupported instructions and observation entries remain
fallback; this record makes no complete real-game coverage claim. Protocol
commands and the host-owned device graph are unchanged.

The implementation is in `LoadedCodeTable`, `LoadImageManifest`,
`ReachableProgramBuilder`, `RecompilerHostCodeGen`, `RecompiledArtifactCodeGen`
and `ArtifactFallbackSession`. Tests cover manifest validation, duplicate and
multiple versions, stale native/fallback writes, compiled RAM calls and
exception returns, and fallback observer composition in
`RuntimeCodeGeneratedHostTests`. The integration tip must independently verify
OpenBIOS differential parity; intermediate AOT builds do not contain later
exact IRQ timing fixes.
