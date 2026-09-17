# ADR-016: Real-ROM Coverage Is Measured Per Instruction, Separately From Proof Selection

- **Status**: Accepted
- **Date**: 2026-09-17
- **Issue**: #410

## Context

[ADR-013](013-real-rom-candidate-selection.md) established that real-ROM
*candidate selection* grows a bounded window only while
`MipsToIrLowerer.LowerProgram` keeps accepting it, excludes `JR`/`JALR`
outright, and trims the window until every static branch/jump target stays
inside it. That is the right shape for a conservative differential proof, and
Issue #410 explicitly forbids weakening it.

It is the wrong shape for a different question the project now needs answered:

> How much of a real title is currently recompilable, and why is the rest
> rejected?

Counting successful proof candidates cannot answer that, because the candidate
pool is selection-biased by construction. As instruction support expands, a
proof-count metric can rise while whole-title coverage stands still — or fall
while coverage improves — and neither movement would be visible.

Two questions had to be settled before any measurement could be trusted.

**What is the unit?** Three candidates existed in the repository:

1. The **function**, via `FunctionDiscovery` (ADR-012).
2. The **basic block**, via `BasicBlockBuilder`.
3. The **instruction**, via the decoded stream.

**What does "covered" mean?** "The lowerer would accept this" and "this was
proven to execute identically" are different claims with very different
strength, and a single percentage that blends them is worse than no percentage.

## Decision

A separate `RealRomCoverageAnalyzer` (in `PSXRecomp.Core.Recompiler`, beside the
selector it deliberately does not touch) measures the whole analyzed corpus and
emits a deterministic `coverage.json` through the existing #215 artifact
contract.

**The coverage unit is one instruction-sized word of the executable's text
region**, with basic blocks reported as a secondary structural breakdown.
Function-level coverage is not reported at all. `FunctionDiscovery` grows each
function by reachability from a seed, so its functions overlap each other, stop
at unresolved indirect transfers, and do not partition the corpus. A
whole-program percentage computed over them would be a number the analyzer
cannot actually support — precisely what Issue #410's acceptance criteria
forbid. Basic blocks do partition the decoded stream, but a block is a variable
number of instructions, so they describe *distribution*, not *quantity*.

**"Lowerable" and "differentially validated" are separate metrics and are
recorded in separate sections.** `totals`/`classes` count instruction shapes the
lowering stage accepts; that is an explicit **upper bound** on what the selector
could take as a contiguous, self-contained window, and it proves nothing. The
`differential` section counts only windows where a differential run actually
executed. An empty `differential` section means *nothing was proven*, never
*nothing failed*.

**ADR-013's no-second-contract rule carries over unchanged.** Support is decided
by calling `MipsToIrLowerer.LowerProgram` on a minimal window, never by a
parallel opcode allowlist. Extending the lowerer automatically extends what this
analyzer reports, with no change here.

**A classification may only exist when the current repository can evidence it.**
Seven classes ship: `Lowerable`, `IndirectControlFlow`, `BiosDependency`,
`UnsupportedInstruction`, `MalformedOrUndecodable`, `AnalysisUncertainty` and
`NotAnalyzed`. MMIO/hardware dependency, dynamic/overlay suspicion and
self-modifying code are **not** emitted — not even as always-zero buckets —
because no analyzer computes a static effective address or overlay evidence
today, and a zero bucket would read as "measured and absent" rather than "not
measured".

**The denominator includes what was never examined.** `NotAnalyzed` counts text
words the bounded decode window never reached. Without it, a ratio over decoded
instructions alone would silently overstate whole-title coverage.

## Consequences

- **Positive**: proof and measurement are structurally separate, so raising a
  coverage number can never be achieved by relaxing the selector — the selector
  is not in the path at all.
- **Positive**: every reported number is traceable to evidence the pipeline
  already produces (the lowerer, `DecodeFailure`, `BiosCallRecognizer`, the block
  partition), so a coverage regression localizes to a real analyzer change.
- **Positive**: the artifact reuses the #215 canonical encoding, identity block
  and ordering discipline, so coverage diffs across revisions work exactly like
  every other artifact diff — no second reporting system.
- **Negative**: per-instruction "lowerable" over-counts what is actually
  recompilable today, because real recompilation needs contiguous,
  self-contained windows. This is stated in the document's own `unitRationale`
  field and in the artifact SSOT rather than left for a reader to discover.
- **Negative**: the seven classes leave real rejection causes unnamed (an MMIO
  store is currently just `Lowerable` or `UnsupportedInstruction` on its opcode
  shape). Naming them requires new analysis, which is a separate change and a
  `coverage.json` schema bump.
- **Deferred**: whole-title coverage over a *complete* decode of the text
  region, rather than a bounded window, depends on decode-window work outside
  this decision; `NotAnalyzed` makes the current shortfall explicit meanwhile.

## Alternatives Considered

- **Function-level coverage from `FunctionDiscovery`** — rejected: its functions
  overlap and do not partition the corpus, so every percentage derived from them
  would claim more precision than the analysis supports (ADR-012 built that
  projection to hand a *reachable CFG* to lowering, not to count a program).
- **Reusing `RealRomCandidateSelector` to measure coverage** — rejected: the
  selector's whole value is that it is conservative, and making it report
  coverage would create pressure to loosen it, which Issue #410 names as a
  non-goal.
- **A single blended "coverage %" including differential results** — rejected:
  it would let unproven lowering inflate a number readers take as a correctness
  claim.
- **Emitting always-zero MMIO / overlay / self-modifying buckets for
  completeness** — rejected: an unmeasured category reported as zero is a false
  statement, and ADR-014's no-guessing rule applies here as much as to BIOS
  identities.

## Related ADRs

- [ADR-012](012-function-discovery-cfg.md) — the function/CFG projection whose
  reachability semantics are the reason the coverage unit is not the function.
- [ADR-013](013-real-rom-candidate-selection.md) — the conservative proof
  selector this analyzer runs beside and must not alter, and the source of the
  "one true lowering contract" rule reused here.
- [ADR-014](014-bios-hle-runtime-contract.md) — the no-guessing rule applied to
  BIOS evidence, which `BiosDependency` classification inherits.
