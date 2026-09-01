# Repository Artifact Policy

**Status:** Stable

**Authority:** SSOT

**Related Issues:** #91

**Related Components:** `config/artifact-policy.json`, `scripts/ci/check-artifact-policy.ps1`, `.github/workflows/ci.yml` (`artifact-policy` job)

## Purpose

PSXRecompStudio handles PlayStation titles and will eventually load user-provided ROM / ISO / BIOS images. Copyrighted material and machine-generated artifacts must never enter the Git repository. This document defines the repository artifact policy and how it is enforced.

`.gitignore` alone is not sufficient: once a file is staged or committed, ignore rules no longer apply, and renamed binaries bypass extension checks entirely. The CI gate closes this gap mechanically before merge.

## Policy SSOT

`config/artifact-policy.json` is the single source of truth for what may exist as a tracked file. The checker script reads only this file; no rule thresholds or lists are duplicated in script or workflow logic.

| Field | Meaning |
|---|---|
| `forbiddenExtensions` | Extensions never allowed in the tree (disc/ROM images, BIOS dumps, build outputs). Matched case-insensitively. |
| `forbiddenPathSegments` | Directory names that must never contain tracked files (`rom/`, `bios/`, `bin/`, `obj/`, `build/`, `out/`, `publish/`, `artifacts/`, `dist/`, `node_modules/`). Matched as path segments, case-insensitively. |
| `maxFileSizeBytes` | Upper size limit per tracked file. Currently 1 MiB. |
| `contentSignatures` | Byte signatures checked at fixed offsets regardless of file name/extension: PS-X EXE header, ISO 9660 (`CD001` at offset 32769), CHD, CSO, PBP, MDS. |
| `allowedPaths` | Exact-path allowlist exempt from all rules. Keep empty unless a legitimate exception exists. |

## Rules evaluated per tracked file

1. **FORBIDDEN_PATH_SEGMENT** — any path segment matches `forbiddenPathSegments`.
2. **FORBIDDEN_EXTENSION** — file extension matches `forbiddenExtensions`.
3. **FILE_TOO_LARGE** — file size exceeds `maxFileSizeBytes`.
4. **CONTENT_SIGNATURE** — bytes at a signature's offset match its hex pattern (catches renamed binaries).

A file matching multiple rules is reported once per matched rule with the rule name, path, and reason, so CI logs identify both violation and cause.

## Enforcement

- **CI**: the `Artifact Contamination Gate` job runs the checker on every PR and push to `main`; violations fail the job and therefore the aggregate CI Gate.
- **Local**: run before committing binary-ish files:

```powershell
pwsh ./scripts/ci/check-artifact-policy.ps1
```

Exit codes: `0` clean, `1` violations, `2` setup/internal error.

The check scans the entire tracked tree on every run. At the current scale this is cheap and strictly stronger than diff-only scanning; revisit if the tree grows large enough for full scans to become slow.

## Reserved directory names in source

`forbiddenPathSegments` matches path segments anywhere in the tree, not only at the
root. A **source** directory named `artifacts`, `build`, `bin`, `obj`, `out`, `dist` or
`publish` therefore fails the gate even though it holds hand-written code. Rename the
directory rather than allowlisting it: an allowlist entry is exact-path, so it would
have to be extended for every file added to that directory, and the gate would weaken
over time. `src/PSXRecomp.Core/DiscImage/AnalysisArtifacts/` is named that way for this
reason.

## Allowlist procedure

To add an entry to `allowedPaths`:

1. Justify in the PR why the file is legitimate (synthetic fixture, project asset).
2. Prefer regenerating/synthesizing content over allowlisting real dumps. Real ROM/BIOS-derived data must never be allowlisted.
3. Update this document if the change alters the policy's intent.

## Locally generated analysis artifacts

`reports/` and `logs/` are git-ignored working directories for real-ROM analysis output.
Their format, and the rule separating deterministic artifacts (no timestamps, no local
paths) from execution logs (timestamps expected, local-only), are defined in
[Real-ROM Analysis Artifact Format](real-rom-analysis-artifacts.md).

Because `reports/` and `logs/` are git-ignored, this gate never scans their content — it
checks only the **Git-tracked tree** (`git ls-files`). The rule that neither directory
contains ROM, ISO, EXE or CHD content is upheld locally by `.gitignore` preventing
accidental staging, and mechanically here if such content is ever committed as a tracked
file: the forbidden-path-segment and content-signature rules then catch it like any other
contaminant.

## Test fixture boundary

Golden tests (Issue #39 Step 5) embed instruction encodings as C# literals; there are no binary test fixtures today. Any future fixture must be synthetic/generated data committed as source (text) where possible, documented here, and explicitly allowlisted when unavoidably binary.

## Limitations and future work

Tracked in #91 "将来的な拡張": known-ROM SHA-256 denylist, full Git-history audit mode, generated-artifact heuristics, secret scanning integration, promotion into a dedicated Analyzer / Repository Artifact Policy tooling. The gate detects contamination patterns; it does not perform copyright determination.
