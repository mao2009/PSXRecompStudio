# ADR-024: Reference-First Execution-Window Alignment for Real-ROM Differential Fixtures

- Status: Accepted
- Date: 2026-09-28
- Issue: #578

## Context

ADR-016 documents that `RecompilerDifferentialFixture.StepBudget` (retired
generated-host dispatch units) and `ReferenceStepBudget` (retired MIPS
instructions) are independent units unless a fixture "explicitly asserts a
shared execution window" (`BudgetsAreShared`). `RealRomFixtureAdapter.ToDifferentialFixture`
sets both budgets to the same numeric value — the candidate's instruction
count — which only coincides with the same real execution window for
straight-line code.

A real-ROM candidate with a fused control transfer (a BEQ/BNE/J and its delay
slot lower to one generated block but retire as two MIPS instructions) breaks
that coincidence once the candidate is long enough for the numeric budget to
actually bind. A Persona fixture (204 guest instructions, 18 of which are
retirements of a fused loop-exit block) demonstrated this concretely: the
naive equal-numeric-budget host ran a full loop further than the interpreter
before its own budget was exhausted, producing a MISMATCH on the diverging
loop-counter register and PC that was not a lowering divergence at all — an
artifact of comparing two different real execution windows under the guise of
"the same" numeric budget.

This is exactly the situation ADR-016 anticipates ("unless the fixture
explicitly asserts a shared execution window") but does not itself solve:
nothing in the existing pipeline *measures* whether a chosen `StepBudget`
covers the same guest-instruction window as `ReferenceStepBudget`, so no
caller could safely assert `BudgetsAreShared` for a real-ROM candidate without
either guessing or re-deriving block/instruction accounting by hand.

## Decision

`RecompilerDifferentialRunner.RunReferenceFirstAligned` is the sanctioned,
generic mechanism for asserting `BudgetsAreShared` on a fixture whose budgets
were not hand-verified by its author:

1. Run the reference (interpreter) executor first, under the fixture's own
   `ReferenceStepBudget`.
2. Project the interpreter's retired PC trace onto the lowered program's
   authoritative static block-entry PCs (the same set `RecompilerStateDiff`'s
   budget-tail check already uses), and count only the entries whose *entire*
   block — every one of its guest instructions, in order — the trace actually
   retired. A host block is atomic: it always retires all of its guest
   instructions (a control transfer and its delay slot, or a fused load-delay
   pair) as one unit, so entering a block is not the same as retiring it. A
   trace that stops after a fused block's control transfer but before its
   delay slot has retired that block's entry PC but not the block itself, and
   must not be counted.
3. If the reference trace ends inside such an incomplete block, alignment is
   unprovable for this run: no `StepBudget` exists that reproduces the
   reference's exact stopping point, because the host has no dispatch point
   between a control transfer and its delay slot. In that case the fixture is
   used unchanged (`BudgetsAreShared` stays whatever the caller supplied,
   never asserted `true`), exactly like the case where nothing projects at
   all.
4. Otherwise, rebuild the fixture with the count of *completely* retired
   blocks as `StepBudget` and `BudgetsAreShared: true`
   (`RecompilerDifferentialFixture.WithStepBudget`), and run the actual
   (recompiled-host) executor under the rebuilt fixture.
5. Compare the two snapshots exactly as `Run` does — no change to
   `RecompilerStateDiff`'s classification rules.

Projection is therefore only ever computed over a **complete host-block
prefix** of the reference trace: `BudgetsAreShared: true` is asserted only for
a window bounded at a block boundary both sides can reach, never for a window
that ends mid-block on the reference side. Issue #578's own regression
(CodeRabbit review on PR #580) is a reference trace that stops between a
fused block's control transfer and its delay slot; the projection excludes
that trailing partial block instead of crediting it as retired.

This is the only caller allowed to derive `BudgetsAreShared: true` for a
fixture it did not build by hand, because it is the only one that *measures*
the alignment rather than assuming it: the projected count is read off the
reference executor's own trace, never inferred from `StepBudget ==
ReferenceStepBudget` (that comparison remains meaningless per ADR-016) and
never guessed from a title-specific instruction count.

`RealRomRecompilerVerticalSliceTests` (the Persona-class real-ROM proof) uses
`RunReferenceFirstAligned` instead of `Run`. `RecompilerDifferentialRunner.Run`
is unchanged and remains the correct entry point for any fixture whose author
has independently verified its own budgets (synthetic fixtures, hand-built
regression cases) or that intentionally exercises the naive equal-numeric-budget
failure mode itself.

## Consequences

- **Positive**: a real-ROM candidate with an internal loop and fused control
  transfer can be differentially validated without either lengthening the
  candidate to avoid budget exhaustion or accepting a false MISMATCH.
- **Positive**: no title-specific logic enters `Core`/`Recompiler` — the
  alignment is entirely mechanical (trace projection over the existing static
  block-entry set) and generic across any fixture, real-ROM or synthetic.
- **Positive**: every existing safety property is preserved unmodified, because
  classification logic (`RecompilerStateDiff.Compare`) is not touched: checkpoint
  ordering, skipped static blocks, and behavioral-field divergence (memory,
  HI/LO, exceptions) still surface as a hard `Mismatch` regardless of alignment.
- **Negative**: a caller using `RunReferenceFirstAligned` gives up an
  independently-chosen host budget in exchange for one derived from the
  reference run; this is inappropriate for a fixture that intentionally wants
  to probe the naive equal-numeric-budget behavior (`Run` remains available for
  that).
- **Negative**: a `ReferenceStepBudget` that happens to land between a fused
  block's control transfer and its delay slot gets no alignment at all for
  that run — the fixture falls back to its own unaligned budget. This is
  inherent to the host's atomic block dispatch, not a gap this mechanism can
  close; a caller who wants alignment for a specific window should choose a
  `ReferenceStepBudget` that lands on a block boundary.

## Alternatives considered

### Widen `BudgetInconclusive` to cover this case

Rejected. The Persona divergence is not "both sides cut off by the same budget,
tail-continuation shaped" (the existing #304 `BudgetInconclusive` contract) — it
is one side's budget genuinely representing a different, larger execution
window than the other's. Downgrading it to `BudgetInconclusive` would hide a
class of false positive as a shrug rather than fixing the actual unit mismatch,
and would weaken the #305 safety contract's requirement that
`BudgetsAreShared` only ever be an author-proven fact.

### Hardcode Persona's own budget/instruction accounting

Rejected outright by the driving Issue: any title-specific address or
instruction-count carve-out in `Core`/`Recompiler` reintroduces exactly the
kind of parallel, drift-prone logic ADR-013 already rejects for candidate
selection.

## Related ADRs

- [ADR-016](016-generated-host-execution-budget.md) — defines the two
  independent budget units this decision reconciles for a specific fixture,
  without redefining them for every backend.
- [ADR-013](013-real-rom-candidate-selection.md) — the real-ROM candidate
  selection this alignment sits downstream of; neither introduces a
  title-specific carve-out.
