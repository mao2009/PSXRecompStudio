# Handoff: PR #765 — real-Persona firmware-path verification (work state)

Tracking: [#765](https://github.com/mao2009/PSXRecompStudio/pull/765) (Draft, unmerged — keep it that way),
[#732](https://github.com/mao2009/PSXRecompStudio/issues/732) (OpenBIOS firmware backend),
[#739](https://github.com/mao2009/PSXRecompStudio/issues/739) (standalone packaging, Phase 2 Step C),
[#730](https://github.com/mao2009/PSXRecompStudio/issues/730) (distribution/license audit — OpenBIOS not redistributable yet).

This file is a cross-terminal handoff snapshot, committed on the WIP branch so the next
terminal can resume without this conversation. It is **not** a PR artifact and must not be
merged into any PR; when the work resumes, leave this file on this WIP branch only.

---

## 1. Verified state at handoff

| Item | Value |
|---|---|
| WIP branch | `wip/765-persona-build-handoff` |
| WIP HEAD | `73d24a9b82ea7fc6a7f0211a801cbe645786c8cb` |
| Base branch (unchanged) | `feat/732-openbios-standalone-integration` (origin HEAD == `10c1b55…95d5`; local is ahead by the 3 commits below) |
| Working tree | clean (nothing uncommitted, nothing untracked in the repo) |
| Origin | `https://github.com/mao2009/PSXRecompStudio` |
| PR #765 | Draft, not merged, not un-drafted. Do not merge to `main` or to the PR branch. |

The WIP branch is the feature branch tip plus the handoff note only. All source/test/docs
changes are already committed in the 3 commits below; there is no other work to save.

## 2. Completed work (Phase 1 of the #765 review — committed)

Three commits on top of `10c1b55`, sitting on `feat/732-openbios-standalone-integration`
(not yet pushed to origin — origin is still at `10c1b55`):

| Commit | Summary |
|---|---|
| `6073ab3` | `fix(standalone): fail closed on invalid firmware AOT inputs` — staging-before-codegen rewrite of `FirmwareDistributionBuilder.Build` (freeze/hash ROM+AOT+disc, plan from staged copies, commit-by-rename, backup/restore of a pre-existing output, no `.staging-*` leftovers) + `FirmwareDistributionBuilderTests`. |
| `447fb1a` | `test(e2e): scope the packaging E2E to AOT mapping, not runtime disc load` — renamed/scoped `OpenBiosStandalonePackageE2E` (`BootExeAotImage_RunsNativelyInTheOneGuestState_AndReportsControlContinuity`); runtime disc load is a separate gate. |
| `73d24a9` | `docs(runtime): record firmware input-validation gates and E2E scope` — `docs/runtime/openbios-firmware-backend.md` scoped to AOT mapping + measured table + gates. |

Also part of Phase 1 (inside `6073ab3`): linked/escaping/absolute AOT-root rejection in
`FirmwareAotBundle.Collect` + tests; shared roots parser `LoadImageManifest.ReadRootsFile`/
`ParseRoots` + `FormatException` catch in `FirmwarePackageCommand` + roots-syntax tests
(`FirmwareAotPlanTests`, `LoadImageManifestTests`, CLI exit-1 test in
`OpenBiosStandaloneSelectionTests`).

Quality gates passed locally on the feature branch before this handoff session:
.NET full suite 3997 pass / 16 SKIP / 0 fail; GUI headless 86/1/0; native ctest 3/3;
Rust 229 pass (`rustup run 1.97.1-…`); artifact-policy replica clean; NuGet vulns none;
solution Release build 0 errors; `git diff --check` clean.

## 3. Incomplete / remaining work (feature branch)

- Phase 2–6 of the PR task: package **real Persona** via the firmware path, launch the
  packaged native artifact directly, capture `firmware-run` JSON + `--diagnostics` JSON,
  compare honestly against the prior numbers (§8), then report.
- Push the 3 commits to `origin/feat/732-openbios-standalone-integration` (plain fast-forward
  push — origin is at `10c1b55`, force push is prohibited) and post the required PR comment
  (see `skills/common/process/doc-sync/SKILL.md` pre-PR gate).
- Final Japanese report with the honest classification: `CODE FIXED` / `SYNTHETIC E2E PASS` /
  `PERSONA AOT VERIFIED` / `PERSONA PROGRESSION VERIFIED` / `BLOCKED-NOT VERIFIED`.
- Windows verification is separate and likely `NOT VERIFIED` (no Windows environment).

## 4. Real-Persona verification progress — BLOCKED at the packaging compile step

**Goal of the run:** package `PERSONA.chd` (`--bios openbios --bios-rom <openbios.bin>
--aot-dir /home/loach/build/psx-review/aot-745-first --run-timeout-seconds 1800`), then launch
the packaged artifact directly and record honest counters. Not reached: the package command
fails inside the native-artifact **compile** step, so no distribution was produced.

**Why it fails: the host compiles ~515,783 lines of generated C at `gcc -O0`, and cc1 needs
~4–5 GB peak RAM. This machine has 7.6 GiB total, a 2 GiB `/swapfile` that is permanently
full, and the desktop holds ~3 GB** (Brave ~1.1 GB, gnome-software ~370 MB, gnome-shell,
opencode service). Kernel evidence (journalctl):

```
10月13 21:05:25 … oom-kill:constraint=CONSTRAINT_NONE,…global_oom,… task=cc1,pid=738005
   Out of memory: Killed process 738005 (cc1) total-vm:4689496kB, anon-rss:3693772kB …
10月09 20:53:39 … Out of memory: Killed process 168588 (cc1) total-vm:6604096kB, anon-rss:5229168kB …
```

**Attempts made (all locally, nothing committed):**

| # | Setup | Result |
|---|---|---|
| A1 | Fresh rather; free ~3.8 GiB; no shim | `CompileFailed` — `gcc: fatal error: signal terminated program cc1` (OOM), exit 1, ~1m38s |
| A2 | `dotnet build-server shutdown` (~380 MB back), free ~4.6 GiB; no shim | same OOM, exit 1, ~1m38s |
| A3 | PATH-shim `gcc` with `--param ggc-min-expand=0 --param ggc-min-heapsize=131072` | no OOM, but `TimedOut` — "Host compilation exceeded the build timeout" (3-min per-step cap), ~3m04s |

Key facts for the next terminal:

- The 3-minute-per-call toolchain cap comes from
  `FirmwareDistributionBuilder.Build` → `new GeneratedHostBuildService(TimeSpan.FromMinutes(3))`
  (`src/PSXRecomp.Infrastructure/GeneratedHostBuildService.cs`; default is 30 s for non-firmware paths).
  With the GGC memory shim the compile is slower than 3 minutes, so the shim alone cannot win;
  you need **real free RAM (or swap)** so the compile runs at natural speed inside the cap.
- Failures are fail-closed: no partial distribution survives, no `.staging-*` directory remains,
  and nothing was ever left inside the repo. The only traces are
  `/tmp/opencode/pr765-outs/run-logs/persona-package.err` (`CompileFailed`/`TimedOut` diagnostics)
  and `/tmp/opencode/pr765-outs/run-logs/persona-package.json` (empty stdout on failure).
  These live **outside** the repo and must not be committed.
- The generated C is written under a sibling staging dir
  `.<artifact>.staging-<guid>/<binary>.c` (e.g. `<output>/persona.c` inside the staging copy),
  then deleted on failure. Never commit a generated `.c`/`.o`/binary.

**Options for the next terminal (choose one):**

1. Add swap (safest for the desktop): run with sudo, e.g.
   `sudo fallocate -l 8G /swapfile2 && sudo chmod 600 /swapfile2 && sudo mkswap /swapfile2 && sudo swapon /swapfile2`
   then retry **without** the gcc shim.
2. Free RAM: close Brave (~1.1 GB) and gnome-software (~370 MB) for the build, retry without the
   shim. Watch `free -h` — cc1 needs ~4–5 GB available.
3. Shim + swap: keep the PATH-shim gcc (below) and add swap; still bounded by the 3-min cap —
   not recommended unless the compile clearly fits.

## 5. Inputs, tools, paths (paths are for the source machine; adjust on the new terminal)

| Input | Path / identity |
|---|---|
| Persona disc (never commit) | `/home/loach/src/PSXRecompStudio/rom/PERSONA.chd` — 379,075,306 bytes; SHA-256 `99f7c7f96f2332f8d8e9df67fdbdd6c09cb75727110135ea02f4e5bd478280bc`; boot EXE `SLPS_005.00` / `SLPS-00500`, entry `0x80011930` |
| OpenBIOS ROM (never commit) | `/home/loach/nugget/openbios/openbios.bin` — 524,288 bytes; SHA-256 `96889cfc16e3637e58e3b709a8de4f47725a4c9f2cafde57e0c24dc075f5cfb7` (pinned Nugget `c950e18a`) |
| AOT inputs | `/home/loach/build/psx-review/aot-745-first/` (and `aot-745-second/`): `rom.roots`, `images.txt` (`kernel`/`vector80/a0/b0/c0`/`shell` + `boot-exe synthetic` + `interpret` lines; the legacy `interpret 0x80010000` line is harmless now) — tools: `scripts/openbios/prepare-aot.py` |
| CLI binary | `src/PSXRecomp.Cli/bin/Release/net10.0/psxrecomp.dll` (rebuilt 21:03 with the Phase-1 fix; rebuild if stale) |
| Output/log scratch | `/tmp/opencode/pr765-outs/` (run-logs, package output) — outside the repo |
| .NET | `/home/loach/.dotnet` (`dotnet` is **not** on PATH; use `/home/loach/.dotnet/dotnet` or set `DOTNET_ROOT`) |
| Compiler | gcc 13.3.0; **no clang**; no `pwsh`; Rust needs `rustup run 1.97.1-x86_64-unknown-linux-gnu` |

Do not commit: the CHD, the ROM, any commercial game data, generated `.c`/`.o`/binaries, or
secrets. The repo / PR / CI / artifacts must stay free of them.

## 6. Build commands used

```bash
# Rebuild the CLI (Release) if needed:
export DOTNET_ROOT=/home/loach/.dotnet
/home/loach/.dotnet/dotnet build src/PSXRecomp.Cli/PSXRecomp.Cli.csproj -c Release --nologo

# Free the .NET build servers when retrying (~380 MB):
/home/loach/.dotnet/dotnet build-server shutdown

# gcc PATH-shim used in attempt A3 (local only; recreate on the next terminal if used):
mkdir -p /tmp/opencode/pr765-bin
cat > /tmp/opencode/pr765-bin/gcc <<'EOF'
#!/bin/sh
exec /usr/bin/gcc --param ggc-min-expand=0 --param ggc-min-heapsize=131072 "$@"
EOF
chmod +x /tmp/opencode/pr765-bin/gcc
export PATH="/tmp/opencode/pr765-bin:$PATH"

# Package real Persona through the firmware path (ran; blocked at the compile step):
/home/loach/.dotnet/dotnet src/PSXRecomp.Cli/bin/Release/net10.0/psxrecomp.dll package \
  /home/loach/src/PSXRecompStudio/rom/PERSONA.chd \
  --bios openbios \
  --bios-rom /home/loach/nugget/openbios/openbios.bin \
  --aot-dir /home/loach/build/psx-review/aot-745-first \
  --output /tmp/opencode/pr765-outs/persona \
  --run-timeout-seconds 1800 \
  --json
# Success prints: "Firmware distribution packaged: <dir>" and a --json document; exit 0.

# Direct launch of the packaged distribution (what ./persona/persona execs):
cd /tmp/opencode/pr765-outs/persona
./persona/persona
#   -> execs runtime/PSXRecomp.RuntimeHost runtime/runtime-host.json (needs DOTNET_ROOT),
#      which detects runtime/firmware-boot.json and drives the artifact in one guest state.
# With the diagnostics document (same run; Issue #763 stop diagnostic):
/home/loach/.dotnet/dotnet runtime/PSXRecomp.RuntimeHost --diagnostics <diag.json> runtime/runtime-host.json
```

Exit codes (firmware distribution): 0 completed; 2 blocked/bounded by design; 1 tooling;
78 setup failure (identity/backend/AOT verification failed before execution).

## 7. Next steps on the new terminal (in order)

1. **Verify state**: `git status`, `git log --oneline -6`, `git rev-parse HEAD` — expect
   `wip/765-persona-build-handoff` at `73d24a9…` (this file committed on it) and a clean tree.
   Make sure the worktree tracks the repo (`main` checkout untouched; keep it that way).
2. **Free memory for the compile** (§4 options) — without it the package cannot produce the artifact.
3. **Package** (exact command in §6). Confirm exit 0 and the JSON output. If it fails, collect the
   new `persona-package.err` and re-diagnose; do not loosen expectations or fake a pass.
4. **Verify the distribution** before launching:
   - `runtime/firmware-boot.json` exists (schema 2) with `aotInputs`; `runtime/artifact-image.bin`
     SHA-256 == `96889cfc…cfb7`; `runtime/disc/disc.chd` SHA-256 == `99f7c7f9…280bc`;
     `runtime/firmware/*` pins match packaging time.
   - `runtime/runtime-host.json` has `guestFirmware:true`, mixed fallback on (default), and the
     requested `runTimeoutMs` (1800 s = 30 min in the command above; prior packed run used ~55 min).
5. **Launch** (§6) and capture stderr (`firmware-run` JSON line) and the `--diagnostics` JSON.
   Expected outcome from the prior profile: `ARTIFACT_TIMEOUT`, **no title**, fallback-dominated
   game body classified `unknown-code` (top PC ~`0x8008A484`, loop `0x8008AECC..0x8008AF04`),
   native count dominated by ROM/kernel/shell plus the guarded `boot-exe` entry block (`0x80011930`).
   Record native/fallback/transitions/state/exit code honestly; note the run-time bound used.
6. **Compare vs prior** (§8). Do **not** report "Persona game AOT success" from BIOS-only native
   counts or a fallback-only run. `PERSONA AOT VERIFIED` needs guarded game-block evidence
   (e.g. fallback segments returning to a compiled block inside the `ps-x-exe` AOT image, or the
   content-matched entry block contribution), never the aggregate native number alone.
7. **Finish the feature branch**: plain fast-forward push of `6073ab3 447fb1a 73d24a9` to
   `origin/feat/732-openbios-standalone-integration` (force push prohibited), post the required
   PR comment (doc-sync skill), keep PR #765 Draft, then write the final Japanese report.
8. **Windows**: separate verification; no Windows environment available → likely `NOT VERIFIED`.

## 8. Prior measurements to compare against (do not regress expectations)

| Run | Result |
|---|---|
| Packaged Persona CHD, synthetic-disc AOT inputs, ~55 min bound | `ARTIFACT_TIMEOUT`, no title. Native `357,153,833` / fallback `23,541,800,409` / `24,625` transitions. "Not a pass." |
| Same Persona disc, `openbios-probe --standalone` on merged #764 main, 30 min bound | `titleStarted:true`; native `275,017,697` / fallback `11,066,526,413` / `9,118` transitions; 99.9 % fallback is `user-ram`/`ps-x-exe` `unknown-code` at entry `0x8008A484` (1,127 entries, ~10.9 G instructions; loop `0x8008AECC..0x8008AF04`) — the boot EXE does not cover this code (loaded at run time), so more game AOT images are needed via `--aot-dir`. |
| Packaged synthetic disc, 200M budget direct launch | reached synthetic EXE `b .` at `0x80010014`; native `278,673,285` / fallback `2,338` / `326` transitions; `OUTER_BUDGET_EXHAUSTED` (exit 2 = bounded by design). |

## 9. Absolute constraints (apply on any terminal)

- Keep PR #765 **Draft**; never merge to `main` or the PR branch; never close/un-draft issues.
- Never commit/substitute: commercial ROMs/discs/BINs/CHDs, the OpenBIOS ROM, generated giant C,
  compiled binaries, secrets — to Git, CI, or PR artifacts.
- Reuse the existing CPU/Runtime/Recompiler/GPU/CD-ROM/Device Graph. No JIT, no second CPU/GPU
  implementation, no title-specific hardcoded PCs, no host HLE substituted for OpenBIOS MIPS.
- No fabricated PASS, no unjustified SKIP, no loosened expectations. Counters must be honest and
  reported as measured; the prior numbers in §8 are the floor for any regression claim.
- No force push anywhere; the feature-branch push to origin is a plain fast-forward.
- Leave the main checkout's uncommitted `feat/739-phase2-step-c-runtime-packaging` work untouched.