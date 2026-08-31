# ADR-011: API Documentation & Docstring Policy

- **Status**: Accepted
- **Date**: 2026-08-31
- **Issue**: #183

## Context

CodeRabbit reviews of PR #181 (PS1 timer runtime) and PR #182 (CPU branch/
load-delay modeling) both failed the tool built-in "Docstring Coverage"
pre-merge check:

| PR | Coverage | Functions analyzed | Files |
|---|---|---|---|
| #181 | 5.07% | 138 | 15 |
| #182 | 15.22% | 46 | 3 |

Both reviews used the tool default: an unconditional 80% coverage threshold
computed over every function touched by the diff, regardless of visibility,
triviality, or whether the function is test code. Neither report indicated a
functional problem; both PRs were otherwise assessed as adequately tested.

Investigating the repository confirms the gap is structural, not
PR-specific: as of this change, the C# managed interop surface under
`src/PSXRecomp.Core` was 19.19% documented (71/370 heuristically-detected
public/internal symbols) and the public C ABI header
`src/PSXRecomp.Native/include/psx_core.h` was 0% documented (0/39 `PSX_API`
functions) before this change (see
[docs/development/documentation-policy.md](../development/documentation-policy.md)
for the measurement method and current numbers). No project-wide policy
existed to say what should be documented, to what standard, or how
CI/review tooling should enforce it.

## Decision

### 1. Policy scope (what must be documented)

A dedicated SSOT,
[docs/development/documentation-policy.md](../development/documentation-policy.md),
defines four symbol categories:

1. **Public API** -- any `public` C# type/member, and any exported C++
   symbol (`PSX_API` in the native ABI).
2. **Interop boundary** -- the native/managed crossing, regardless of C#
   accessibility (`NativeInterop.cs` is `internal` but is required to
   document ownership/lifetime and unit/semantics of every P/Invoke
   parameter).
3. **Architecture-significant runtime/state machine** -- components whose
   correctness depends on non-obvious timing, ordering, or ownership rules
   (e.g. branch/load-delay pipeline state, timer synchronization modes,
   interrupt controller semantics) even when not part of the public API.
4. **Trivial implementation detail** -- explicitly excluded: private helpers
   whose documentation would only restate the code.

Tests are excluded by default unless a test encodes a non-obvious
architectural invariant (e.g. an ADR-mandated ordering or timing rule) that
is not evident from the test name/body -- matching the issue's stated
default.

### 2. Minimum documentation quality

For every in-scope symbol: purpose (one sentence), important
invariants/side effects, units/semantics of non-obvious parameters, and
ownership/lifetime expectations where a handle, pointer, or disposable
resource is involved. C# uses XML doc comments (three slashes); C++ uses
Doxygen-style block or triple-slash comments. Both should link to the
authoritative design document (ADR/SSOT) instead of duplicating its content.

### 3. Measurement mechanism

`scripts/docs/measure-docstring-coverage.ps1` is a heuristic, line-based
scanner (not a Roslyn/Clang syntax-tree analyzer) that reports C# and C++
coverage separately for a configurable set of target paths, defaulting to
`src/PSXRecomp.Core` (every public/internal C# symbol under it, matching
policy scope category 1 above) and `src/PSXRecomp.Native/include` (every
`PSX_API` function). It always exits 0 (report-only) unless `-FailUnder`
is explicitly passed; it is a tracking tool for the policy, not a merge
gate by itself.

### 4. CodeRabbit / CI alignment

`.coderabbit.yaml` is updated instead of accepting the tool default:

- `reviews.pre_merge_checks.docstrings.mode` stays `warning` (the tool
  default): a documentation gap is surfaced on every PR but never blocks
  merge by itself, matching the issue's non-goal "do not block functional
  PRs on mass documentation cleanup".
- `reviews.pre_merge_checks.docstrings.threshold` is lowered from the tool
  default of 80 to **40**, a realistic incremental target given the
  measured baseline, to be raised as the in-scope surface accumulates
  documentation. CodeRabbit check still applies to every function touched
  by a diff (it has no first-party way to scope by the four categories
  above), so the threshold is the practical lever available; the project
  documentation policy is the actual authority for what must be
  documented, and reviewers should judge individual PRs against it rather
  than the raw percentage.
- `code_generation.docstrings.path_instructions` steers the tool
  "Generate docstrings" finishing touch (and any AI-assisted authoring) to
  follow the same categories: explicit ownership/interop guidance for
  `NativeInterop.cs` and the public C++ headers, and an explicit exemption
  for ordinary test methods.

No Roslyn analyzer rule is added in this change (the issue allows either a
coverage script or an analyzer rule): a build-breaking analyzer over
free-text documentation quality is not practical, and the existing
`PSXR001`-`PSXR006` architecture rules are structural, not documentation
rules. Should the project later want a build-breaking presence check (e.g.
"every public type in `PSXRecomp.Core` must have a `<summary>`"), it can be
added as a new `PSXR0xx` rule against this ADR without revisiting the
policy itself.

### 5. Incremental application

This change documents the two named priority targets end-to-end
(`src/PSXRecomp.Core/NativeInterop.cs`, `src/PSXRecomp.Core/PSXCoreWrapper.cs`)
and the full public C ABI (`src/PSXRecomp.Native/include/psx_core.h`,
now 100% covered by the measurement script). Remaining `PSXRecomp.Core`
surface, and the architecture-significant native runtime headers under
`src/PSXRecomp.Native/src/*.h` (timers, interrupt controller, CPU pipeline),
are left for follow-up PRs per the issue's incremental framing; Issue #183
stays open to track that remaining work (see the policy document
prioritization list).

## Consequences

### Positive

- CodeRabbit reviews for the touched files (and future documented files) no
  longer produce a warning for the same structural reason every time.
- New/changed public API and interop code has a written, referenceable
  quality bar instead of relying on reviewer judgment call by call.
- The measurement script gives a repeatable, versioned coverage number for
  both languages, matching the issue's "measured separately for C# and C++"
  acceptance criterion.

### Negative

- The heuristic scanner is line-based and can misclassify unusual
  formatting (e.g. multi-line signatures); it is documented as approximate
  and is not a merge gate. A Roslyn/Clang-based version is future work.
- Lowering the CodeRabbit threshold to 40% is still a per-PR aggregate over
  all touched functions, including any out-of-policy trivial code a future
  PR happens to touch; the mode stays warning specifically so this
  cannot silently block an unrelated functional PR.

## Alternatives Considered

### Keep the CodeRabbit default (80%, warning)

Rejected: the issue's evidence (4.44%/15.22% consistently on two unrelated
PRs) shows 80% is not attainable without dedicating a PR-sized effort to
documentation on every functional change, which the issue's own non-goals
reject ("do not add boilerplate comments solely to satisfy a percentage
metric").

### Switch mode to `error` at a lower threshold

Rejected for now: `error` can block merge per the tool docs. Until the
in-scope surface is substantially documented, a hard gate would risk
blocking unrelated functional PRs exactly as the issue's non-goals warn
against. Revisit once coverage is stable well above the configured
threshold.

### Add a build-breaking Roslyn analyzer rule now

Rejected for this change: documentation-quality checks (presence of a
`<summary>`, non-triviality of its content) are a different kind of rule
from the existing structural `PSXR001`-`PSXR006` rules and deserve their
own design pass; a script-based, non-blocking measurement is lower-risk to
land first and does not preclude adding an analyzer rule later.

## Related ADRs

- ADR-006: Architecture Enforcement via Analyzer (the `PSXR0xx` rule family
  a future documentation-presence rule would join)
- ADR-010: CodeRabbit Review Runs After README Auto-Update (the other
  `.coderabbit.yaml`-governing ADR)
