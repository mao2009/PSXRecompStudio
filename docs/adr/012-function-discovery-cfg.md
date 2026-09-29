# ADR-012: Function discovery and CFG hand-off

Status: accepted for Issue #210

## Decision

Function discovery is a projection over the existing PS-X EXE analysis result. The
`R3000aDecoder`, `R3000aBranchSemantics`, `R3000aJumpSemantics`, `BasicBlockBuilder`,
and deterministic artifact serializer remain the single sources of truth. Issue #210
adds no decoder, executable parser, or second basic-block builder.

`FunctionDiscoveryArtifact` carries the executable text-region identity, entry point,
stable function identities, block lists, CFG edges, direct call targets, recognized
returns, and unresolved indirect-flow source addresses. Candidates are seeded by the
PS-X EXE entry point, direct link targets, and optional explicit entries. Traversal
does not guess indirect targets, does not cross a direct call into the callee, and
keeps delay-slot instructions in the source block.

Functions and blocks are ordered by entry/start address. Edges are ordered by source,
target, then kind. The artifact is canonical UTF-8/LF JSON and exposes a SHA-256 for
two-run reproducibility checks. It is an input contract for later #207 lowering; it
does not contain generated code or CPU-lowering policy.

The existing real-ROM report remains backward-compatible: its function projection is
optional for callers constructing reports directly, and the existing four-file
real-ROM artifact format remains unchanged for reports without the projection.

## Amendment: reachable-program roots (Issue #644)

`ReachableProgramBuilder` (the recompiler's statically reachable-program discovery) is a
separate consumer of the same decoder and jump/branch semantics; it does not consume
`FunctionDiscoveryArtifact`. Its roots are exactly:

1. the PS-X EXE entry point, and
2. caller-supplied explicit roots (`ReachableProgramBuilder.Build(..., additionalRoots)`,
   `psxrecomp run|recompile --entry-root 0xPC`).

All roots are discovered in one shared pass, so leaders, delay slots, load-delay pairs and
conflict detection are common to every root. Each explicit root must be a 4-byte-aligned PC
naming a complete word inside the text image, otherwise the build fails closed with an
`InvalidFlow` diagnostic naming the root. Duplicate roots, the entry point, and PCs already
reached are accepted. With no explicit roots the result is identical to entry-only discovery.

Roots are an input, never a guess. The builder does not derive roots from
`FunctionDiscoveryArtifact` entries, does not scan data words for pointers, and does not treat
the whole text region as block candidates; #639 measured each of those (function entries added
0 blocks; blanket pointer harvesting fail-closed 432 of 562 candidates and added 9,170 blocks;
11.8% of Persona's text words decode as Reserved). A transfer that lands on an in-image PC with
no compiled block still stops the run (`UNRESOLVED_TRANSFER_IN_IMAGE`); the runtime does not
claim or compile it. Interpreter fallback and mixed execution remain out of scope (ADR-015, #249).
