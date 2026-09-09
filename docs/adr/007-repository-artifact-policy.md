# ADR-007: Repository Artifact Policy and CI Contamination Gate

- **Status**: Accepted
- **Date**: 2026-08-24
- **Issue**: #91

## Context

PSXRecompStudio will eventually handle user-provided ROMs, ISOs, and BIOS images. These are copyrighted materials and must never be committed to the repository. `.gitignore` alone is insufficient because it cannot stop files that have already been staged or committed, nor can it detect binaries that have simply been renamed with a different extension.

Build outputs such as `bin/`, `obj/`, and `build/`, as well as accidentally committed large files, are also currently dependent on manual review. As test data grows after the Golden Tests work (Issue #39), the project needs a machine-enforced boundary between legitimate fixtures and data derived from real hardware or copyrighted media.

## Decision

1. **Policy SSOT**: Define forbidden extensions, forbidden path segments, size limits, content signatures, and the allowlist centrally in `config/artifact-policy.json`. Do not duplicate thresholds or lists in the checking script.
2. **CI quality gate**: Run `scripts/ci/check-artifact-policy.ps1` (pwsh, shared by CI and local validation) as the GitHub Actions `Artifact Contamination Gate` job, and make it a required dependency of the aggregate `ci` job.
3. **Full-tree scan**: Scan the entire tracked repository tree on every run rather than only the PR diff. At the current repository size, the cost is negligible, and the full-tree scan is a strict superset of diff-only scanning.
4. **Renamed-binary detection**: Inspect file contents using fixed-offset signatures for PS-X EXE, ISO 9660, CHD, CSO, PBP, and MDS rather than relying only on extensions.
5. **Allowlist**: Legitimate exceptions must be registered explicitly as exact paths in `allowedPaths`. Data derived from real hardware or copyrighted media must never be allowlisted.

## Alternatives Considered

- **Strengthen `.gitignore` only**: Insufficient because it cannot detect files after they have been staged. Keep `.gitignore` only as a complementary safeguard.
- **Scan only the PR diff**: Simpler, but it can miss pre-existing contamination or changes after merge. It has no meaningful advantage over a full-tree scan at the current repository size.
- **Implement with bash + jq**: Prefer runner-standard pwsh + `ConvertFrom-Json`, which also matches local Windows validation, rather than adding runner-specific dependencies.
- **Immediately promote this into a dedicated Roslyn Analyzer**: Analyzers enforce rules at compilation scope, while binary artifact scanning is a different concern. A future promotion remains possible, but the CI script is the appropriate initial enforcement layer.

## Consequences

- ROMs, BIOS images, generated artifacts, oversized files, and renamed binaries are mechanically blocked before merge.
- Policy changes are reviewable as a diff to a single JSON file, preserving the decision history.
- Legitimate assets that exceed configured thresholds incur the deliberate friction of an explicit allowlist entry.
- Historical repository auditing, hash denylists, and similar extensions remain outside this ADR's scope and may be addressed later under #91.
- Detailed operational rules use [docs/development/artifact-policy.md](../development/artifact-policy.md) as the SSOT.
