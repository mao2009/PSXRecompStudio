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
