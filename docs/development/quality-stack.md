# Quality and Verification Stack

**Status:** Stable

**Authority:** SSOT for quality-gate ownership and rule-routing

**Related Issues:** #106, #23, #98, #102, #105, #290

## Purpose

PSXRecompStudio uses one evidence-driven quality stack. A defect or policy does
not get a new analyzer, harness, or CI subsystem merely because it can be
described as a rule.

This document owns **which mechanism is responsible for which kind of quality
requirement**. It does not replace the semantic SSOT for CPU/runtime behavior,
the architecture matrix, analyzer upstream documentation, or individual test
specifications.

## Current stack

| Concern | Primary owner | Repository evidence / SSOT |
|---|---|---|
| C#/.NET language and API correctness | Compiler + Microsoft .NET Analyzers | `Directory.Build.props`, `.editorconfig` |
| Purity / adopted immutability rules | PureSharp | `.editorconfig`; package references in managed projects |
| Layer, dependency, forbidden-API and interop boundaries | `loach.ArchitectureAnalyzer` | `src/architecture.contract.json`, `docs/architecture-matrix.md`, ADR-006 |
| Repository / artifact / workflow invariants | Focused scripts, CI and Skills | `.github/workflows/`, `scripts/ci/`, `skills/` |
| CPU / Recompiler / Runtime semantics | Golden, differential, contract and focused regression tests | subsystem SSOT + corresponding tests |
| Cross-language/native behavior | Rust/native tests + C ABI contract tests | `docs/development/rust-ffi-contract.md`, native/Rust test suites |
| Integration and platform behavior | Integration/E2E tests + cross-platform CI | `.github/workflows/ci.yml` |
| Review-only ambiguity, scope and design judgment | Human/independent review + self-review | `skills/common/process/self-review/SKILL.md`, review policy |

There is no separate "AI correctness" stack. AI-authored changes pass through
the same compiler, analyzers, tests, CI, documentation and review gates as any
other change.

## Routing a new finding

Start with the smallest mechanism that can reliably prevent recurrence.

```text
concrete finding
  |
  +-- local one-off implementation bug
  |     -> focused fix + regression test
  |
  +-- missing or ambiguous contract
  |     -> subsystem SSOT / ADR + executable check where practical
  |
  +-- architecture/API boundary
  |     -> architecture.contract.json + ArchitectureAnalyzer coverage
  |
  +-- purity / immutability policy
  |     -> PureSharp policy/configuration
  |
  +-- execution semantics
  |     -> Golden / differential / contract / regression test
  |
  +-- repository/process invariant
  |     -> focused CI / script / Skill validation
  |
  +-- recurring mechanically detectable source pattern
        -> evaluate a static rule through the Analyzer Rollout Skill
```

Do not route a semantic runtime bug to a Roslyn analyzer when the static source
shape cannot prove the behavior. Do not build a generic runtime/harness system
for a rule already owned by an existing test or gate.

## Adding a new analyzer or static rule

A new analyzer, package, or rule is justified only when all of these are true:

1. there is concrete evidence of a bug class or recurring review finding;
2. the current compiler/.NET/PureSharp/ArchitectureAnalyzer/test stack does not
   already own it;
3. the condition is mechanically detectable with acceptable false positives;
4. static enforcement is more useful than a focused semantic test;
5. CI/runtime/maintenance cost is acceptable;
6. an SSOT or explicit project contract defines the intended rule;
7. positive, negative and valid-exception behavior can be verified.

Then follow
[`skills/common/process/analyzer-rollout/SKILL.md`](../../skills/common/process/analyzer-rollout/SKILL.md)
for baseline measurement, overlap review, severity choice and ratcheting.

An analyzer proposal that fails these conditions should not be kept alive merely
as a placeholder for "more quality".

## Severity and gate policy

- High-confidence architecture and adopted correctness invariants may be
  build-breaking when false positives are acceptably low.
- Advisory/style rules with existing debt should use an explicit warning/ratchet
  strategy rather than forcing an unrelated repository-wide rewrite.
- Broad suppression, lowering severity solely to make CI green, or disabling a
  valid analyzer for generated/test code is not an acceptable migration plan.
- Local and CI behavior must agree: a rule described as mandatory must be
  exercised by the normal CI build/test path.
- Adding a mandatory gate requires considering signal, false-positive rate,
  execution cost and maintenance cost.

Current pinned examples live in `.editorconfig`: selected Microsoft `CA*`,
PureSharp, and ArchitectureAnalyzer `AARC*` rules are explicit rather than
depending on package defaults.

## Execution semantics belong in executable evidence

PS1 behavior normally depends on values and state transitions rather than a
source-code pattern. Examples include:

- branch/load delay behavior;
- signed/unsigned arithmetic and overflow;
- HI/LO state;
- exceptions and COP0 state;
- MMIO and device scheduling;
- DMA/IRQ interactions;
- recompiler/interpreter equivalence;
- deterministic runtime/frame evidence.

These belong primarily in focused unit, Golden, differential, contract or E2E
tests, backed by the applicable subsystem SSOT. Static analysis may enforce
structural boundaries around those implementations, but it must not pretend to
prove runtime semantics it cannot observe.

## External analyzers

SonarAnalyzer, security analyzers, performance analyzers and similar tools are
optional candidates, not sequential roadmap stages.

Evaluate one only for a concrete uncovered bug class and measure:

- unique useful findings;
- overlap with current owners;
- false positives/noise;
- migration effort;
- CI execution cost;
- ongoing package/configuration maintenance;
- an appropriate severity rollout.

If unique value is not demonstrated, do not adopt it.

## Historical consolidation

The earlier plan for many PSXRecomp-specific analyzer and AI-harness
subsystems has already been retired or absorbed into the current stack:

- #55-#64 — speculative custom Analyzer families: closed as not planned;
- #65, #70, #72, #73 — overlapping AI Harness infrastructure: closed as not planned;
- #103/#104 — sequential Sonar/security quality-gate stages: closed as not planned;
- #105 — architecture quality-gate integration: completed;
- #290-#295 — the built-in PSXRecomp analyzer was replaced by
  `loach.ArchitectureAnalyzer` + the consumer-owned
  `src/architecture.contract.json`.

Do not recreate these retired umbrellas under new names. A future concrete gap
should enter through the routing rules above and #23's review-finding feedback
path.

## Review-finding feedback

Issue #23 owns the process for converting recurring findings into durable
prevention. The current self-review and analyzer-rollout Skills are the
execution mechanisms for that process.

A finding must first be classified and assigned to an existing owner. Creating
a new analyzer is the last branch of the decision tree, not the default.

## Verification map

When changing a quality mechanism, verify the owner that actually changed:

| Change | Minimum focused verification |
|---|---|
| `.editorconfig` analyzer severity | build the affected managed projects; demonstrate target diagnostic behavior where practical |
| `architecture.contract.json` | ArchitectureAnalyzer consumer regression + normal build |
| analyzer package/version | Analyzer Rollout Skill baseline + normal build/test + CI path |
| semantic runtime rule | focused semantic regression/differential test + applicable broader suite |
| repository policy/script | focused script/fixture test + CI path that consumes it |
| Skill/process contract | scenario/conformance review or test appropriate to the process; do not invent runtime machinery solely to test Markdown |

The project profile defines the normal widening verification ladder.

## Maintenance rules

- Extend an existing owner before introducing a parallel one.
- Keep rule/policy meaning in an SSOT; enforcement configuration points to it.
- Never reserve diagnostic ID families or infrastructure for hypothetical rules.
- A closed historical Issue is evidence of prior planning, not a current
  architecture source.
- When a new quality gate is adopted, update this page if ownership changes.
- #23 remains the intake/feedback path; #98 remains the rollout procedure for a
  genuinely justified static rule.
