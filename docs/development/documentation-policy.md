# API Documentation & Docstring Policy

**Status:** Stable

**Authority:** SSOT

**Related Issues:** #183

**Related Components:** `.coderabbit.yaml`, `scripts/docs/measure-docstring-coverage.ps1`, `src/PSXRecomp.Core/NativeInterop.cs`, `src/PSXRecomp.Core/PSXCoreWrapper.cs`, `src/PSXRecomp.Native/include/psx_core.h`

## Purpose

CodeRabbit reviews of PR #181 and PR #182 reported 5.07% and 15.22%
docstring coverage against the tool default of an unconditional 80%
threshold applied to every function touched by a diff. Both PRs were
otherwise adequately tested; the low numbers reflected a missing
project-wide documentation policy, not a defect in either change. This
document is the SSOT for what source-code documentation this project
requires, to what standard, and how that requirement is measured and
enforced. See ADR-011 for the decision record.

## Scope: what must be documented

Four symbol categories, in priority order:

1. **Public API** -- every `public` C# type and member, and every
   exported native symbol (any function marked `PSX_API` in a native
   header). This is the contract other code and other projects can rely
   on; it must be documented regardless of how simple it looks.
2. **Interop boundary** -- the native/managed crossing, regardless of C#
   accessibility. `src/PSXRecomp.Core/NativeInterop.cs` is `internal`, but
   every P/Invoke declaration there is required to document ownership of
   any native handle/pointer, the unit/semantics of non-obvious
   parameters, and side effects on native state. The corresponding native
   header (`src/PSXRecomp.Native/include/psx_core.h`) carries the same
   obligation from the C++ side.
3. **Architecture-significant state machines / runtime components** --
   code whose correctness depends on a non-obvious timing, ordering, or
   ownership rule, even when it is not part of the public API. Examples
   already in the codebase: branch/load-delay pipeline state (ADR-004,
   ADR-005), timer synchronization modes, interrupt controller
   set/clear/mask semantics. Document the invariant and reference the
   governing ADR/SSOT instead of re-deriving it in prose.
4. **Non-obvious timing/ordering/ownership semantics** -- any function
   or field, public or not, where behavior depends on a rule a reader
   could not infer from the signature alone (e.g. "must be called before
   X", "value is in CPU cycles, not milliseconds", "caller keeps
   ownership of the buffer").

## Explicit exclusions

- **Trivial private implementation details.** A private helper whose
  documentation would only restate the code in prose adds no value and is
  explicitly out of scope. Prefer a clear name over a comment that repeats
  it.
- **Tests, by default.** Test code does not require documentation unless
  the test encodes a non-obvious architectural invariant (an ADR-mandated
  ordering or timing rule) that is not evident from the test name and
  body. Most tests in this repository are already self-describing through
  naming (`MethodName_Scenario_ExpectedResult`); do not add restating
  comments to them.
- **Do not document to satisfy a percentage.** Boilerplate `<summary>`
  text added only to raise a coverage number is worse than no comment: it
  erodes trust in the documentation that does carry information. Every
  addition must satisfy the quality bar below.

## Minimum documentation quality

Every in-scope symbol needs, at minimum:

- **Purpose** -- one sentence describing what the symbol does or
  represents.
- **Important invariants / side effects** -- anything the caller must
  hold true before/after calling, or any state the call mutates beyond
  its return value (e.g. "flushes pending branch/load-delay pipeline
  state").
- **Units / semantics** where a parameter is not self-evident (cycles vs.
  milliseconds, absolute address vs. offset, 0-based vs. 1-based index).
- **Ownership / lifetime** wherever a handle, pointer, or `IDisposable`
  resource is involved: who allocates it, who must release it, and how
  many times release is safe to call.

Prefer linking to the authoritative design document (an ADR or a
`docs/cpu/*.md` SSOT page) over duplicating its content in a comment; the
comment should say *that* a rule applies and where to read it, not
re-derive the rule.

### C# convention

XML doc comments (`///`) with `<summary>`, and `<param>`/`<returns>` when
non-obvious, `<exception>` for exceptions a caller should expect, and
`<remarks>` for invariants/ownership notes too long for a summary line.
See `src/PSXRecomp.Core/PSXCoreWrapper.cs` for the reference example.

### C++ convention

Doxygen-style comments (`/** ... */` block or `///`/`//!` line comments)
directly above the declaration. Public headers additionally carry a
`@file`/`@brief` file-level comment describing the header role and its
interop contract. See `src/PSXRecomp.Native/include/psx_core.h` for the
reference example.

## Measurement mechanism

`scripts/docs/measure-docstring-coverage.ps1` is a heuristic, line-based
scanner: for each declaration line matching a C# public/internal
type/member pattern (or a `PSX_API` C++ declaration), it checks whether
the nearest non-blank, non-attribute line above is a doc comment. It is
**not** a Roslyn/Clang syntax-tree analyzer and can misclassify unusual
formatting; treat its output as a directional trend indicator, not a
precise audit. Run it locally:

```powershell
pwsh ./scripts/docs/measure-docstring-coverage.ps1
```

Add `-Verbose` to list every undocumented symbol found, or `-CSharpPath`
/ `-CppPath` to scan a different subtree than the defaults
(`src/PSXRecomp.Core` and `src/PSXRecomp.Native/include`). The script
always exits 0 unless `-FailUnder <percent>` is passed explicitly; it is a
report, not a merge gate.

## Baseline coverage (measured 2026-08-31, Issue #183)

| Category | Scope | Before this change | After this change |
|---|---|---|---|
| C# (managed) | `src/PSXRecomp.Core/**/*.cs`, public/internal symbols | 19.19% (71/370) | 37.84% (140/370) |
| C++ (native, public ABI) | `src/PSXRecomp.Native/include/*.h`, `PSX_API` functions | 0.00% (0/39) | 100.00% (39/39) |

The two named priority interop files
(`src/PSXRecomp.Core/NativeInterop.cs`, `src/PSXRecomp.Core/PSXCoreWrapper.cs`)
and the full public C ABI header are fully documented as of this change.
The remaining `PSXRecomp.Core` gap is other subsystems (Analysis
contracts, DMA/MMIO routing, GUI-facing types, etc.) not yet brought under
this policy; track that work under Issue #183.

## CodeRabbit / CI alignment

`.coderabbit.yaml` configures the tool built-in "Docstring Coverage"
pre-merge check to match this policy rather than the tool default:

- `reviews.pre_merge_checks.docstrings.mode: warning` -- documentation
  gaps are surfaced on every PR but never block merge by themselves. A
  functional PR that happens to touch undocumented legacy code is not
  blocked; a reviewer judges the change against this policy, not the raw
  percentage.
- `reviews.pre_merge_checks.docstrings.threshold: 40` -- lowered from the
  tool default of 80, matching the measured baseline in this document.
  Raise this number in a follow-up change as the in-scope surface
  (Scope section above) accumulates documentation; do not raise it purely
  to make a specific PR pass.
- `code_generation.docstrings.path_instructions` -- steers CodeRabbit
  AI-assisted docstring generation toward the same categories: explicit
  ownership/interop guidance for the interop boundary files and the
  public native headers, and an explicit exemption for ordinary test
  methods so generated docstrings do not add boilerplate where the policy
  excludes it.

CodeRabbit has no first-party way to scope its coverage check by the four
categories in this document; the threshold and path instructions are the
practical levers available. This policy document remains the authority
for *what* must be documented -- reviewers and authors should reference it
directly rather than treating the CodeRabbit percentage as the standard.

No build-breaking Roslyn analyzer rule is added by this change. The
existing `PSXR001`-`PSXR006` rules (`src/PSXRecomp.Analyzer`) enforce
structural architecture constraints (layering, dependency direction,
forbidden APIs); a documentation-presence rule is a different kind of
check and is left as potential future work under a new `PSXR0xx` rule
(see ADR-011).

## Prioritization for incremental documentation

Work through the codebase in this order (highest-value first), one PR at
a time, never as a standalone mass-reformat unless explicitly scoped:

1. `src/PSXRecomp.Core/NativeInterop.cs`, `PSXCoreWrapper.cs` -- done in
   this change.
2. `src/PSXRecomp.Native/include/psx_core.h` -- done in this change.
3. Architecture-significant native runtime headers under
   `src/PSXRecomp.Native/src/*.h` (`psx_timer.h`, `psx_cpu.h`, the
   interrupt controller header, DMA header): these define the timing and
   ordering rules ADR-004/005 and the timer/interrupt subsystems depend
   on.
4. Remaining `PSXRecomp.Core` public surface (`Dma/*.cs` MMIO routing,
   `Analysis/*` contracts already partially documented, GUI-facing
   public types).
5. Any new public API or interop code introduced going forward is
   documented in the same PR that introduces it (see policy scope above);
   this is enforced going forward by the CodeRabbit check, not backfilled
   retroactively as a blocking condition on unrelated PRs.

## Maintenance rule

When a change adds or modifies an in-scope symbol (Scope section above),
update its documentation in the same change. When a change alters an
architectural invariant this document or ADR-011 assumes, update both in
the same change or an immediately following one, per
`docs/README.md`'s maintenance rule.
