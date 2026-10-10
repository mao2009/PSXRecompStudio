# CI Policy: Linux-First with Scheduled Cross-Platform Verification

**Status:** Active (v0.1 target)
**Authority:** SSOT for CI scheduling policy
**Related Issues:** #752, #393, #603, #614, #80, #474

## Overview

This document defines the CI scheduling policy for PSXRecompStudio through v0.1. The policy optimizes for development velocity while maintaining cross-platform compatibility.

## Policy Summary

| Scenario | Required CI | Optional / Manual CI |
|---|---|---|
| **Routine PR / Push** | Linux (native, .NET, GUI) | — |
| **OS-specific changes** | Linux + affected OS | Other OSes on demand |
| **Weekly verification** | — | Windows + macOS (scheduled) |
| **Release candidate / v0.1** | Linux + Windows + macOS | — |

## Workflow Definitions

### 1. Primary CI Gate (`.github/workflows/ci.yml`)
**Triggers:** Pull requests to `main`, pushes to `main`
**Required jobs:**
- `changes` — Change classification (docs-only detection)
- `artifact-policy` — Artifact contamination check
- `native` — Native Core Build and Test (Linux, `ubuntu-24.04`)
- `dotnet` — .NET Build and Test (Linux, `ubuntu-24.04`)
- `gui-tests` — GUI Headless Tests (Linux, `ubuntu-24.04`)
- `ci` — CI Gate aggregator (requires all above to pass)

**Docs-only fast path:** When only documentation files change, heavyweight build/test jobs are skipped.

### 2. Cross-Platform Verification (`.github/workflows/cross-platform-verification.yml`)
**Triggers:**
- **Scheduled:** Weekly on Monday 06:00 UTC
- **Manual:** `workflow_dispatch` (any time)
- **PR with label:** When a PR has the `ci:cross-platform` label

**Jobs:**
- `cross-platform` — Native build/test on Windows and macOS
- `cross-platform-dotnet` — .NET build/test on Windows and macOS
- `cross-platform-gate` — Aggregates results, fails if any platform fails

**Label-based trigger:** Add `ci:cross-platform` label to any PR to run cross-platform verification before merge.

### 3. Release CLI (`.github/workflows/release.yml`)
**Triggers:** Tag push (`v*`), manual dispatch
**Platforms:** Linux, Windows, macOS (all required for release)
**Note:** Release validation requires all three OS builds/tests to pass.

### 4. Native Architecture (`.github/workflows/native-architecture.yml`)
**Triggers:** Native code changes
**Platform:** Linux only (`ubuntu-24.04`)
**Purpose:** Architecture linting for C++/Rust native code.

### 5. Documentation Translations (`.github/workflows/documentation-translations.yml`)
**Triggers:** Documentation changes
**Platform:** Linux only
**Purpose:** Translation freshness checks.

## Branch Protection / Required Status Checks

**Required checks for `main` branch:**
1. `changes` (Classify changes)
2. `artifact-policy` (Artifact Contamination Gate)
3. `native` (Native Core Build and Test)
4. `dotnet` (.NET Build and Test)
5. `gui-tests` (GUI Headless Tests)
4. `ci` (CI Gate)

**NOT required:**
- `cross-platform` / `cross-platform-dotnet` / `cross-platform-gate` (scheduled/manual only)
- Windows/macOS jobs from legacy CI (no longer exist in primary CI)

## Triggering OS-Specific Verification

### For PR Authors
1. **Linux only (default):** No action needed. Standard CI runs on Linux.
2. **Cross-platform before merge:** Add label `ci:cross-platform` to the PR.
3. **Single OS:** Not directly supported; use label for full cross-platform run.

### For Maintainers
1. **Manual dispatch:** Use Actions → Cross-Platform Verification → Run workflow
2. **Scheduled:** Runs automatically every Monday
3. **Release:** Tag with `v*` or use manual dispatch with version tag

## Handling OS-Specific Changes

When a change touches platform-specific code or tooling:

| Area | Verification Required |
|---|---|
| Linux-specific native code | Standard CI (Linux) |
| Windows-specific native code | Standard CI + `ci:cross-platform` label |
| macOS-specific native code | Standard CI + `ci:cross-platform` label |
| .NET runtime/CLI packaging | Standard CI + `ci:cross-platform` label |
| GUI framework changes | Standard CI (Linux headless) + manual Windows/macOS if GUI-affecting |

## Rollback / Emergency Procedure

If the Linux-first policy causes undetected regressions:

1. **Immediate:** Re-enable Windows/macOS in primary CI by reverting workflow changes
2. **Branch protection:** Update required checks to include cross-platform jobs
3. **Investigation:** Document failure mode and adjust policy

## Monitoring and Metrics

Track these metrics to evaluate policy effectiveness:

- **Queue time:** Linux CI queue time vs. previous full-matrix queue time
- **Failure detection rate:** Cross-platform scheduled failures vs. PR-time failures
- **Merge latency:** Time from PR ready to merge (Linux gate only vs. full matrix)
- **Release readiness:** Cross-platform verification pass rate at release time

## Migration Notes (from full-matrix CI)

### Before (legacy CI)
- All PRs required: Linux native, Linux .NET, Linux GUI, Windows native, Windows .NET, macOS native, macOS .NET
- CI Gate required all 7 jobs to pass
- Any slow Windows/macOS runner blocked all PRs

### After (this policy)
- All PRs require: Linux native, Linux .NET, Linux GUI (4 jobs + gate)
- Cross-platform runs weekly + on-demand + at release
- OS-specific changes explicitly request cross-platform via label
- No required check ever stays in `Pending` state

## Rationale

1. **Velocity:** Linux runners are consistently available; Windows/macOS runners have intermittent delays
2. **Cost:** Reduces CI minutes consumed by ~40-50% for routine changes
3. **Reliability:** Eliminates flaky cross-platform runner delays from critical path
4. **Safety:** Cross-platform coverage maintained via scheduled, manual, and release gates
5. **Flexibility:** OS-specific changes can still request full verification

## Future Review

This policy is effective through **v0.1 release**. Post-v0.1, evaluate:

- Actual queue time reduction achieved
- Cross-platform regression detection effectiveness
- Whether Windows/macOS should return to required for specific subsystems
- Potential for self-hosted runners for more reliable cross-platform CI

---
*This policy is a CI scheduling change, not a removal of platform support. All three platforms remain officially supported targets.*