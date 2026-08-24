# Project Profile: PSXRecompStudio

**Status:** Stable

**Authority:** Reference

**Related Issues:** #85

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

CI (GitHub Actions, ubuntu-latest): native build+ctest → .NET 10 restore,
NuGet vulnerability gate (High/Critical fails), Release build, both test
projects, aggregate CI Gate job.

## 5. Known environment caveats

- Primary development OS varies (Windows / Linux). Native-dependent .NET tests
  can behave differently on Windows locally; when results are ambiguous, trust
  CI (Linux) as the authoritative result.
- Sync local main only with fast-forward: `git pull --ff-only`.
- Do not commit anything under `build/`, `bin/`, `obj/`; ROM/BIOS files under
  `rom/` are gitignored.

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
