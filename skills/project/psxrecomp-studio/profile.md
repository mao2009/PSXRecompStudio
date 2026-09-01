# Project Profile: PSXRecompStudio

**Status:** Stable

**Authority:** Reference

**Related Issues:** #85, #89

Project-specific inputs consumed by the generic skills in
`skills/common/`. This file is the only place that needs rewriting when a
common skill is ported to another project.

## 1. Authoritative documents

| Area | Document |
|---|---|
| Bootstrap / authority hierarchy | `docs/development/agent-guide.md` |
| Top-level architecture SSOT | `ARCHITECTURE.md` |
| Documentation index | `docs/README.md` |
| Layer/dependency SSOT (enforced by analyzer) | `docs/architecture-matrix.md` |
| Architecture overview | `docs/architecture/README.md` |
| CPU/R3000A subsystem specs | `docs/cpu/instruction-set.md`, `docs/cpu/instruction-format.md`, `docs/cpu/registers.md`, `docs/cpu/r3000a.md`, `docs/cpu/pipeline.md`, `docs/cpu/memory.md`, `docs/cpu/exceptions.md`, `docs/cpu/cop0.md` |
| Test specification | `docs/cpu/test-specification.md` |
| Repository artifact policy | `docs/development/artifact-policy.md` (SSOT: `config/artifact-policy.json`) |
| Real-ROM analysis flow (stages, failure kinds, artifacts, CI/SKIP policy) | `docs/development/real-rom-analysis.md` (procedure: `skills/project/psxrecomp-studio/real-rom-analysis/SKILL.md`) |
| API documentation & docstring policy | `docs/development/documentation-policy.md` (ADR-011), measured by `scripts/docs/measure-docstring-coverage.ps1` |

## 2. ADR directory

Location: `docs/adr/`

Current records:

| ADR | Title | Status |
|---|---|---|
| 001 | CPU Specification SSOT | Accepted |
| 002 | Instruction Definition YAML | Accepted |
| 003 | MIPS ISA / R3000A / PSX Layering | Accepted |
| 004 | Branch / Load-Delay Modeling | Accepted |
| 005 | PC Update Model | Accepted |
| 006 | Architecture Enforcement via Analyzer | Accepted |
| 007 | Repository Artifact Policy and CI Contamination Gate | Accepted |
| 008 | Batch Orchestrator Checkpoint and Resume Design | Accepted |
| 009 | PR-Triggered README Auto-Update via OpenCode | Accepted |
| 010 | CodeRabbit Review Runs After README Auto-Update | Accepted |
| 011 | API Documentation & Docstring Policy | Accepted |

ADR numbering is sequential with zero-padded three digits; format follows the
existing records (`Context` / `Decision` / `Consequences`, Status/Date/Issue header).

## 3. Architecture enforcement

`PSXRecomp.Analyzer` enforces `docs/architecture-matrix.md` at compile time
(ADR-006). Rules **PSXR001–PSXR006**, all build-breaking:

| ID | Rule |
|---|---|
| PSXR001 | Class missing architecture layer attribute |
| PSXR002 | Multiple layer attributes on one type |
| PSXR003 | Namespace ↔ layer mapping mismatch |
| PSXR004 | Forbidden dependency direction |
| PSXR005 | Forbidden API per layer (e.g. non-deterministic randomness in Domain) |
| PSXR006 | P/Invoke outside `PSXRecomp.Core` |

Analyzer violations fail the build; treat them as blockers, not warnings.

## 4. Verification ladder

Run in this order before any PR (targeted first, then widen):

```powershell
# 1. Targeted tests for the touched area
dotnet test src/PSXRecomp.Tests --filter "<FullyQualifiedName~ChangedArea>"

# 2. Full .NET test suite (Release)
dotnet test src/PSXRecomp.Tests/PSXRecomp.Tests.csproj -c Release
dotnet test src/PSXRecomp.Analyzer.Tests/PSXRecomp.Analyzer.Tests.csproj -c Release

# 3. Native core (C ABI)
cmake -S src/PSXRecomp.Native -B build/native -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build/native --parallel
ctest --test-dir build/native --output-on-failure
```

CI (GitHub Actions, ubuntu-latest): artifact contamination gate → native
build+ctest → .NET 10 restore, NuGet vulnerability gate (High/Critical fails),
Release build, both test projects, aggregate CI Gate job.

## 5. Known environment caveats

- Primary development OS varies (Windows / Linux). Native-dependent .NET tests
  can behave differently on Windows locally; when results are ambiguous, trust
  CI (Linux) as the authoritative result.
- Sync local main only with fast-forward: `git pull --ff-only`.
- Do not commit anything under `build/`, `bin/`, `obj/`; ROM/BIOS files under
  `rom/` are gitignored. The artifact policy gate
  (`pwsh ./scripts/ci/check-artifact-policy.ps1`) enforces this mechanically;
  run it before committing binary-ish files.

## 6. Historical review findings sources

- External review bot (CodeRabbit) comments: visible on every PR; check merged
  PRs in the touched area before implementing.
- Self-review records: recorded per session in the working agent's final report.
- Proven implementation pattern: R3000A semantics extraction series (PRs #79,
  #82, #83 — branch/jump/link target semantics as pure Domain functions over
  decoder metadata), repeatedly executed without post-merge rework; a candidate
  for extraction into a project-specific skill (tracked under #81).

## 7. Issue / PR conventions

- Use closing keywords (`Fixes`/`Closes`) only when **all** Issue completion
  criteria are met; otherwise reference the plain issue number and state why it
  stays open.
- PR body must include: implemented scope, verification results, remaining
  items, and reason when an Issue is intentionally left open.
- Prefer one concern per PR; keep unrelated refactors out.

## 8. Documentation synchronization inputs

Project-specific inputs for the `common/process/doc-sync` skill (#89).

### Concrete impact map

Refinement of the generic impact matrix onto this repository's actual files.
Levels follow the skill's semantics (Update / Check / —).

| Change category | Entry-point README | Docs index | Architecture SSOT | ADR (`docs/adr/`) | Dev / process docs | Skills / profile |
|---|---|---|---|---|---|---|
| User-facing feature/capability | Update `README.md` | Check `docs/README.md` | Check `ARCHITECTURE.md`, affected `docs/cpu/*.md` / `docs/architecture/*` | Check¹ | — | — |
| Architecture / design | Check `README.md` | Check `docs/README.md` | Update `ARCHITECTURE.md` + affected SSOT page (`docs/architecture-matrix.md`, `docs/cpu/*`) | Update¹ | Check `docs/development/agent-guide.md` | Check `skills/project/psxrecomp-studio/profile.md` |
| CI / build / test infra | Check `README.md` (build/test sections) | Check `docs/README.md` | — | Update¹ | Update relevant `docs/development/*.md` | Check profile §4–5 |
| Dev workflow / repo policy | Update `README.md` | Check `docs/README.md` | — | Update¹ | Update relevant `docs/development/*.md` | Update affected skills/profile |
| Configuration-as-policy (e.g. `config/artifact-policy.json`) | Check `README.md` | Check `docs/README.md` | — | Update¹ | Update the policy's development page | Check profile §1, §5 |
| Process artifacts (skills, profiles, agent guides) | — | — | — | Check¹ | Check `docs/development/agent-guide.md`; update the `skills/README.md` index when adding/renaming/removing a skill | Update affected skills/profile |

Internal-only work maps to no row by design: its expected outcome is a
recorded no-op across all columns.

1. Only for significant design decisions per the self-review skill's ADR
   conditions; an ADR addition alone never forces a README edit.

### SSOT precedence

This repository uses the doc-sync skill's default order unchanged:

Architecture SSOT → accepted ADRs → development/reference docs →
skills & profiles → README.

It is consistent with (but not identical to) two existing sources: the
Authority model in `docs/README.md` (SSOT outranks Reference), and the
agent-guide's authority hierarchy, which additionally ranks open Issues,
current code, and historical discussions for determining current intent.

### Worked example: Issue #91 / PR #110 (Artifact Contamination Gate)

Why the doc-sync gate exists, in one concrete change. The CI/policy feature
touched nine tracked files, eight of which carry documentation-relevant
content:

- Policy SSOT: `config/artifact-policy.json` (new)
- Enforcement: `scripts/ci/check-artifact-policy.ps1` (new), `.github/workflows/ci.yml`
- Decision record: `docs/adr/007-repository-artifact-policy.md` (new)
- Operating rules: `docs/development/artifact-policy.md` (new)
- Entry points: `README.md` (注意事項 section now names the gate),
  `docs/README.md` (hierarchy + current-subsystem list)
- Agent inputs: this profile (§1 authoritative docs, §5 caveats)

Categories tagged: configuration-as-policy + CI infra + repo policy → matrix
produced exactly these candidates; every one was updated in the same change,
so no no-op decisions were recorded. Missing any single link (e.g. leaving
the README's "成果物をリポジトリに含めない" bullet unbacked by the policy
page, or an agent not knowing the local gate command) is precisely the drift
class this skill prevents.
