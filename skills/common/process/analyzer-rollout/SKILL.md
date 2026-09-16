---
name: analyzer-quality-rule-rollout
description: >
  Repeatable process for evaluating, introducing, migrating, and progressively
  enforcing Roslyn analyzers and other static quality rules without hiding
  existing debt or causing unsafe bulk rewrites.
version: 0.1.0
scope: process
platform: agent-agnostic
---

# Analyzer / Quality Rule Rollout

Use this skill when adding a Roslyn analyzer, upgrading analyzer rules, changing
rule severity, or turning an existing warning into a CI quality gate.

The goal is not to maximize the number of analyzers. The goal is to introduce
rules whose signal and enforcement value justify their migration, noise, and CI
cost while keeping the change explainable and reversible.

## Core principles

1. **Source first.** Verify the analyzer package, upstream documentation,
   supported Roslyn/.NET versions, rule catalog, and intended installation mode
   before editing the repository.
2. **SSOT before severity.** A project-specific rule must have a documented
   policy or architectural reason. The analyzer is an enforcement mechanism,
   not the place where the policy is invented.
3. **Measure before enforcing.** Obtain the current diagnostic baseline before
   changing severity.
4. **High-confidence correctness rules may block CI.** Style or naming rules
   with large legacy debt normally start below `error` and use a ratchet.
5. **Do not hide debt.** Lowering severity, broad suppression, or disabling the
   analyzer is not a substitute for an explicit migration decision.
6. **Do not perform blind symbol rewrites.** Regex/string replacement is not a
   safe rename mechanism for parameters, locals, members, tuple elements, or
   symbols with nested scopes. Prefer IDE/Roslyn symbol-aware rename or a
   deliberately reviewed migration tool.
7. **Local and CI behavior must match.** A rule called a quality gate must be
   evaluated by the normal build/test path used in CI.

## Inputs

Collect the following before implementation:

- analyzer/package name and exact version;
- authoritative upstream documentation or source repository;
- current project TFM, SDK and Roslyn/compiler versions;
- existing analyzer `PackageReference`s and `.editorconfig` severity policy;
- current diagnostic counts by rule ID;
- project SSOT/ADR/rule text that justifies any project-specific enforcement;
- CI build/test commands used for the target projects.

## Procedure

### 1. Verify the analyzer and integration mode

Confirm:

- package identity and license;
- latest intentionally selected version rather than an accidental floating
  dependency;
- compatibility with the repository SDK/compiler;
- whether the package is an analyzer-only dependency and should use
  `PrivateAssets="all"` / appropriate asset filtering;
- whether generated code is analyzed and whether that behavior is intentional;
- whether another analyzer already owns the same concern.

Do not proceed from a package name alone.

### 2. Establish a diagnostic baseline

Run the normal build with the candidate analyzer/rules enabled at a
non-blocking severity when necessary and record, per rule:

```text
Rule ID
Category / purpose
Current severity
Diagnostic count
Representative locations
False-positive assessment
Migration cost
Proposed target severity
```

Separate existing debt from newly introduced violations. A large pre-existing
count is evidence for a migration plan, not evidence that the rule is useless.

### 3. Classify each rule

Use three practical classes:

#### A. Correctness / safety / invariant

Examples: semantic correctness, purity/invariant violations, unsafe runtime
behavior, deterministic-contract violations, or an explicit SSOT prohibition.

Default target: **error**, if false positives are acceptably low and the
repository can be made green safely.

#### B. Maintainability with strong project value

Examples: resource/lifetime patterns or API use that are usually wrong but may
have valid contextual exceptions.

Default target: **warning** initially; promote only after the exception model
and baseline are understood.

#### C. Naming / style / advisory quality

Examples: naming preferences that do not themselves change behavior.

Default target: **warning/info** during migration. Do not create a huge semantic
rename solely to make an unrelated analyzer-introduction PR green.

Document exceptions; do not silently treat every diagnostic as equivalent.

### 4. Choose the migration strategy

For a rule with no meaningful existing debt:

```text
enable → fix valid diagnostics → set target severity → build/test → CI gate
```

For a rule with substantial existing debt:

```text
enable as warning
  → record baseline
  → prohibit net-new violations where tooling allows
  → reduce violations opportunistically and in focused cleanup PRs
  → verify baseline reaches the promotion threshold
  → promote to error
```

This is the **ratchet** model: the repository must not regress while existing
violations are reduced deliberately.

### 5. Apply the Boy Scout rule carefully

When touching a file that already contains diagnostics for a ratcheted rule,
fix nearby violations when the change is small, semantic-preserving, and easy
to review.

Do **not** turn a focused feature/fix PR into a repository-wide rename or
refactor merely because an advisory rule is visible there.

### 6. Handle mass diagnostics safely

Before any bulk fix, classify the affected symbol kinds and scopes.

Never use unqualified regex replacement for symbol renames. It can break:

- parameters vs locals with the same text;
- fields/properties vs local variables;
- tuple element names;
- nested scopes and shadowing;
- named arguments;
- deconstruction and pattern variables;
- generated code or serialized/public names.

Prefer, in order:

1. IDE/Roslyn rename;
2. a symbol-aware temporary migration tool;
3. small reviewed manual batches.

After every batch, build and inspect secondary compiler errors such as `CS0103`,
`CS8130`, ambiguous references, broken named arguments, and public API drift.

Temporary migration tooling must not become production code unless it has an
independent long-term purpose.

### 7. Configure severity explicitly

Keep rule IDs visible in configuration. Example:

```ini
# correctness rule: enforced
dotnet_diagnostic.EXAMPLE001.severity = error

# migration rule: debt remains; ratchet before promotion
dotnet_diagnostic.EXAMPLE100.severity = warning
```

If severity is intentionally below the analyzer default or below the desired
future state, record why and what condition will permit promotion.

Avoid broad `NoWarn`, project-wide suppression attributes, or disabling the
entire analyzer to make CI green.

### 8. Verify local and CI quality gates

At minimum verify:

- restore succeeds;
- normal `dotnet build` evaluates the analyzer;
- target `error` diagnostics fail the build;
- existing unit/analyzer tests remain green;
- CI invokes the same relevant build path;
- analyzer warnings/errors are visible enough for a developer to fix without
  reproducing a separate hidden command.

If the analyzer has project-specific configuration, add focused positive and
negative tests when feasible.

### 9. Review for rollout-specific regressions

Before PR creation, inspect the final diff for:

- accidental formatting churn;
- unrelated symbol renames;
- broad suppressions;
- analyzer disabled for generated/test code without policy evidence;
- package version drift;
- severity changes not justified by the baseline;
- rules duplicated by an existing analyzer;
- CI paths that do not actually compile the projects being protected.

## Promotion criteria: warning → error

Promote a migrated rule only when all of the following are true:

- the intended policy is still valid;
- false-positive behavior is acceptable;
- existing violations are zero or at an explicitly approved threshold;
- new violations have not been growing;
- the fix guidance is practical;
- representative build/test/CI paths pass with the rule at `error`;
- promotion does not require unsafe unrelated bulk refactoring.

Record the promotion as an intentional quality-gate change, not a cosmetic
configuration edit.

## Concrete reference: PureSharp 0.1.6 rollout

The PSXRecompStudio PureSharp rollout is a reference example, not a universal
severity table.

The decision used this shape:

- `RT0001`–`RT0003`, `LVP0001`–`LVP0002`, and `FIF0001`: treated as substantive
  purity/quality rules and enforced as `error`;
- `LVP0003`: treated as a naming-oriented migration rule and kept at `warning`
  because the repository had roughly 466 existing violations and a safe fix
  required symbol-aware renames rather than regex replacement.

The reusable lesson is the decision process: **rule value + false-positive risk
+ existing debt + safe migration cost determine rollout severity**.

## Required output for an analyzer rollout

Report:

```text
Analyzer / version:
Authoritative source checked:
Compatibility checked:
Rules enabled:
Baseline diagnostics by rule:
Rules set to error and why:
Rules kept below error and why:
Migration / ratchet plan:
Suppressions added (should normally be none or narrowly justified):
Build/test commands and results:
CI coverage confirmed:
Follow-up issue(s), if any:
```

## Completion checklist

- [ ] Analyzer/package identity and compatibility verified
- [ ] Rule ownership vs existing analyzers checked
- [ ] Baseline diagnostic counts measured
- [ ] Rules classified by correctness/value/migration cost
- [ ] Severity decisions documented
- [ ] High-value enforceable rules are CI-blocking
- [ ] Legacy warning debt has a ratchet/migration plan
- [ ] No blind regex/string symbol rename used
- [ ] Any temporary migration tooling is isolated
- [ ] Broad suppressions/disablement avoided
- [ ] Normal local build evaluates the analyzer
- [ ] CI evaluates the same quality gate
- [ ] Existing build/tests remain green
- [ ] Final diff reviewed for unrelated churn

## Non-goals

This skill does not require clearing every existing advisory warning in the
same PR, building a permanent bulk-rename utility, replacing other analyzers,
or treating "more analyzer rules" as an end in itself.
