# ADR-013: Real-ROM Candidate Selection Reuses the Recompiler as Its Own Validator

- **Status**: Accepted
- **Date**: 2026-09-08
- **Issue**: #225

## Context

Issue #225 asks for the first real-ROM function to be recompiled and
differentially validated through the existing Recompiler contract
(#206/#207/#208/#209/#211), explicitly forbidding a second, real-ROM-specific
semantics implementation and forbidding any indirect jump, unresolved MMIO, or
other unsupported dependency in the selected function.

The existing `MipsToIrLowerer` already defines, precisely, which instruction
shapes it accepts: a fixed opcode subset, correct delay-slot pairing, and no
runtime-resolved (register-indirect) control transfer. A real-ROM function
picked from disc/EXE analysis (`DiscImageAnalysisReport`, #210/#212/#213) is
real code: it routinely contains instructions the lowering stage does not yet
support (`SLTU`, `ORI`, `MULT`, unaligned loads/stores, `BREAK`, …) interleaved
with supported ones, and its natural end is a register-indirect return (`JR
$ra`) that the current contract cannot express as a resumable transfer
(`RecompilerIrFlowKind.Return` is reserved, not implemented).

Two designs were evaluated for deciding which real-ROM bytes are safe to feed
into the pipeline:

1. Maintain a second, independent list of "supported opcodes" for real-ROM
   candidate selection, checked before lowering is attempted.
2. Attempt the real lowering (`MipsToIrLowerer.LowerProgram`) on a growing
   instruction window and treat its success or failure as the answer.

## Decision

Real-ROM candidate selection (`RealRomCandidateSelector`, added in
`PSXRecomp.Core.Recompiler`) uses design 2: it grows a contiguous instruction
window from a starting address and, at every step, actually attempts
`MipsToIrLowerer.LowerProgram` over the accumulated window. The window is
extended only while lowering succeeds; the first instruction that fails to
lower, or the first indirect jump (`JR`/`JALR`, checked explicitly and never
handed to the lowerer), ends the window without being included. The result — a
bounded `RealRomFunctionCandidate` — is fed to the unmodified differential
contract via `RealRomFixtureAdapter.ToDifferentialFixture`, producing a
`RecompilerDifferentialFixture` indistinguishable, from the pipeline's point of
view, from a synthetic one.

This binds future work: **a real-ROM input adapter may only ever select
instructions the existing Recompiler contract already accepts.** Extending
real-ROM coverage (a new opcode, or resolving `JR $ra` as an in-window return)
means extending `MipsToIrLowerer`/`RecompilerContract` themselves — the one
true source of "supported" — never adding a parallel allowlist that could
drift from it, and never a title-specific carve-out in `Core`, `Recompiler`, or
`Runtime`.

## Consequences

- **Positive**: selection can never silently accept an instruction shape the
  Recompiler does not actually implement — the two can't drift apart, because
  there is only one. Extending Recompiler support automatically extends what
  real-ROM candidates can include, with no change to the selector.
- **Positive**: the real-ROM path and the synthetic path share every stage from
  IR lowering onward; `RealRomRecompilerVerticalSliceTests` and the synthetic
  `RealRomRecompilerBridgeTests` both exercise the same
  `RecompilerDifferentialRunner`/`RecompilerStateDiff` contract.
- **Negative**: a "first real-ROM function" is, in practice, a bounded prefix
  of a real subroutine — up to but excluding its `JR $ra` return — not the
  whole function as a human would delimit it. This is recorded, not hidden:
  `RealRomFunctionCandidate.StopReason` and `StopAddress` always say why the
  window ends where it does.
- **Deferred**: general real-ROM function coverage still requires closing the
  gaps `MipsToIrLowerer` currently has (the full MIPS I subset, and a
  register-indirect return/call flow) — tracked by the existing Recompiler
  roadmap, not newly introduced here.

## Alternatives Considered

- **A parallel "supported opcode" list for candidate selection** — rejected:
  it is exactly the kind of second semantics surface #225 forbids, and every
  future lowering change would need a matching, easy-to-forget update to stay
  correct.
- **Treating `JR $ra` as an accepted candidate terminator with a synthesized
  "return" exit** — rejected: `RecompilerIrFlowKind.Return` is explicitly
  reserved and unimplemented (`RecompilerContract.cs`); inventing a real-ROM-only
  interpretation of it would itself be the forbidden second semantics
  implementation.

## Related ADRs

- [ADR-012](012-function-discovery-cfg.md) — the function/CFG projection this
  candidate selector reads its instruction stream from; #225 adds no second
  basic-block builder or CFG analysis either, for the same reason.
