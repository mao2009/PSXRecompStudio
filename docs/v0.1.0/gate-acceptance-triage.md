# Gate Acceptance Triage: #205, #351, #739 (2026-10-27)

**Status:** Draft (triage record)

**Authority:** Reference

**Related Issues:** #205 (Recompiler roadmap), #351 (v0.1.0 Persona title-screen gate), #739 (generated game executable milestone), #833, #830, #827 (open follow-ups), #630, #768, #773, #806, #820, #823, #825, #829

**Baseline:** `origin/main` = `52f363d` (Merge of PR #834)

## Purpose

This page records, at one point in time, which acceptance conditions of the three
gate-level Issues are met by merged code, which are not, and which open Issues
could be closed on their current evidence. It does not replace the Issues or
`persona-e2e-status.md`. Closing an Issue remains a human decision.

Evidence labels: `CONFIRMED` (observed in code or a recorded run),
`INFERRED` (derived from code or PR text), `UNVERIFIED` (claimed but not
re-run), `NOT_RUN` (not executed for this triage), `BLOCKED` (requires a local
fixture that is absent).

Local fixtures: the Persona CHD (`rom/PERSONA.chd`) is absent in the triage
worktree, so every real-title check is `BLOCKED` (`MISSING_LOCAL_FIXTURE`).
No ROM, BIOS, or extracted binary is referenced or committed by this page.

## 1. Gate status

| Issue | Scope | Status | Basis |
|---|---|---|---|
| #205 | Parent roadmap | OPEN, KPI 4 in progress, KPI 6 open | Roadmap checklist: 13 of 15 items checked. Unchecked: real-title bring-up gaps (evidence-driven) and "#351 reached or roadmap updated to the next release gate". |
| #351 | v0.1.0 Persona title-screen gate | OPEN, not reached | None of the nine acceptance criteria can be checked for the real title. Last measured first blocker in the issue body is `A0:70 _bu_init` (#712, 2026-10-08); the 2026-10-22 note records standalone display still differing from the interpreter (#774). |
| #739 | Generated game executable shows the Persona opening | OPEN, not met | The 2026-10-11 scope correction makes a standalone generated game executable (`persona` / `persona.exe`) the primary gate. Merged work (#831) packages a synthetic PS-X EXE only. The real-ROM opening remains `BLOCKED`. |

## 2. Merged PRs mapped to Issue conditions

All PRs below are merged (verified with `gh pr list --state merged` and `git log origin/main`).
"Satisfies" means the PR text and its recorded verification cover the condition;
it is not an independent re-run. Items marked `UNVERIFIED` need a rerun before they count.

| PR | Issue(s) | Condition(s) addressed | Evidence level | Residual |
|---|---|---|---|---|
| #828 | #827 (also #630, #820) | Stop result classified from observed process state (`Cancelled` / `Clean` / `Residual` / `NotStopped`); real child-process integration tests for stop during drain, stop racing natural end, residual grandchild, and kill refused by OS | CONFIRMED (tests named in PR, run per PR text), Windows and macOS CI pass | Fallback-run stop, real-window child check (both moved to #829); Linux descendant tracking; OS kill refusal only by injection |
| #831 | #830 (B1) | `package --gui` produces a console-free launcher; host started suspended and placed in a kill-on-close Job; bounded wait and dialog timeout; GUI README; headless package output unchanged (`RuntimeHostPublishE2E` 15 pass) | CONFIRMED (unit/E2E tests, Windows harness for non-zero exit and 30 s auto-close, residual names count 0) | RGB frame screenshot on the packaged game EXE is not in the PR; Job kill-on-close recovery `UNVERIFIED`; 4-hour run `NOT_RUN`; `--self-contained` `UNVERIFIED` |
| #834 | #833 (B2) | Packaging with `--bios openbios`; manifest/report labels `biosMode`; no absolute developer paths in generated text; synthetic BIOS stub for wiring; real OpenBIOS run only when a user-supplied file exists | CONFIRMED for the labels and regression tests in `OpenBiosStandalonePackageE2E` (present in `origin/main`); CI pass on the PR checks | Label names differ from the Issue text (see section 3); real OpenBIOS run `NOT_RUN` in this triage; runtime BIOS-to-RAM provenance tracked in #835, split BIOS in #836 |
| #821 | #820 (also #630) | `ICancellableExecutionEngine` and `ExecutionOrchestrator.Execute(..., CancellationToken)`; stop before start and during run produce `Cancelled` | CONFIRMED (tests in PR); #820 is already closed | None for #820 |
| #824 | #630, #823 | Synthetic GP0 red/green/blue program shown in a GUI-only host through the real runtime and `LiveFrameSlot` | CONFIRMED (PR text, Windows run) | Live Persona frames `BLOCKED` |
| #826 | #630, #825 | Disk-loaded synthetic PS-X EXE read by existing loader and shown in GUI | CONFIRMED on Windows (red to green to blue, per PR text) | Persona disk EXE `BLOCKED` |
| #817 | #630 | Latest-one-frame live buffer | CONFIRMED (unit tests in PR) | Not a game proof |
| #819 | #630 (via #818) | Live frame published at VBlank on the pump thread | CONFIRMED (PR text) | Continuous display of a real title `BLOCKED` |
| #784 | #773 | Observed-roots planning from fallback observation; full probe export | CONFIRMED (PR text); Issue #773 is referenced, not closed | Root coverage of Persona not re-measured (`BLOCKED`) |
| #786 | #773 | Opt-in diagnostic report for rejected RAM-placed code | CONFIRMED (PR text) | Diagnostic only |
| #792 | #773 | Opt-in indirect-call root discovery | CONFIRMED (PR text); the PR body said Draft, but `mergedAt` is set | Performance evaluation not complete per its body |
| #797 | #773 | Opt-in native guard statistics; in measured Persona run, `0x80084C88` reached natively and all recorded guard successes were ATLUS v0 | CONFIRMED for the measurement, under the PR's own conditions (600 s, one run, shared machine) | Not a repeatable benchmark |
| #796 | #768 | MMIO request and time report fused into one round trip; per-message profile | CONFIRMED (PR text; synthetic and Windows Persona run) | #768 remains open (wall clock still limited by sync IPC) |
| #801 | #768 | Synthetic test pins MMIO round trips per access (ceiling) | CONFIRMED (test added; no production change) | No deterministic further reduction found per PR |
| #810 | #806 | Fallback position diagnostics; finds standalone firmware fallback budget defaults to `uint.MaxValue` | CONFIRMED by code reference in PR (`FirmwarePackageCommand.cs:46`); diagnostic only | Root cause of stuck fallback segments not fixed (#806 open) |
| #811 | #806 | Classified reasons for rejected fallback return points | CONFIRMED (diagnostic, behavior unchanged) | Root cause open |

Notes:

- #630 stays open: the PR texts of #828 and #826 both say live verification on the real Persona ROM is outstanding.
- #768, #773, #806 stay open for the same reason: merged PRs are measurement or diagnostic slices.

## 3. Why #833, #830, #827 remain open after their PRs merged

### #833 (merged as #834)

- Accepted (CONFIRMED in `origin/main`): `biosMode` is written to the report; regression tests for the OpenBIOS standalone package exist; the BIOS image is not committed; the real OpenBIOS run is skipped when no user file is supplied, and the skip reason is printed.
- Remaining:
  1. **Label mismatch.** The Issue asks for `biosMode`: `hle` / `openbios-rom`. The code uses `hle`, `openbios-synthetic`, and `openbios-unverified`. `openbios-rom` is emitted only for an allowlisted, independently verified digest (`BiosProvenance.cs`). The owner must either accept the implemented vocabulary and update the Issue, or request a change.
  2. **Final-HEAD mapping.** The PR body still says Draft with CI queued and cites HEAD `3fee551`; `gh` reports the head as `62c06ca`. The checks listed by `gh pr checks 834` all pass, but the run-to-SHA mapping is `UNVERIFIED` in this triage.
  3. **Real OpenBIOS run** with a user-supplied image: `NOT_RUN` (outside this worktree).
  4. Follow-ups #835 and #836 are separate Issues and are not criteria of #833.
- Verdict: closable after items 1 and 2 are settled. Not closed here.

### #830 (B1, merged as #831)

- Accepted: console-free launcher; `runtime/` contains the host; no absolute paths; ordinary headless output unchanged; window-close path uses the existing cancel API (per PR text, depends on #828).
- Remaining (not evidenced for the packaged game EXE):
  - Criterion 5: the red, green, and blue frames are shown in order and checked by screenshot pixels on the packaged EXE. The RGB evidence in #826 used a separate harness.
  - Criterion 4: child-tree recovery through the Job Object (kill-on-close) is explicitly `UNVERIFIED`.
  - Criterion 6: PID and name checks after close were done in one run only.
- Verdict: keep open.

### #827 (stop integration, merged as #828; residual split to #829)

- Accepted by #828: real-process tests for stop during drain, natural-end race, residual grandchild, and OS-refused kill; results are classified, not mixed.
- Remaining per the PR's own "Not verified" list: stop during fallback execution; stop while waiting on RHOST reads (not listed as tested); stop during GPU frame emission (only a partial window); real-window child check after close (moved to #829); natural-end residual detection (moved to #829).
- Verdict: keep open until fallback and RHOST-wait stops are tested, or until #829 is merged and the owner accepts the split.

## 4. Closure candidates

| Category | Issues | Reason |
|---|---|---|
| Closable with evidence | None strictly in this triage | #820 is already closed. |
| Closable after a named confirmation | #833 | Items 1 and 2 in section 3. |
| Keep open (partially evidenced) | #827, #830 | Remaining items in section 3. |
| Keep open (gate not reached) | #351, #739, #205, #630, #768, #773, #806, #774 | Real-title or real-game evidence `BLOCKED` or unmeasured. |

## 5. Blockers and next steps

1. Provide the Persona CHD (`rom/PERSONA.chd`) in the local checkout to unblock real-ROM evidence for #351 and #739 (`BLOCKED`).
2. Decide the `biosMode` vocabulary for #833 (section 3).
3. Run the packaged `--gui` synthetic EXE to capture RGB frames by pixel check, and record Job Object recovery, for #830.
4. Add or confirm the fallback-stop and RHOST-wait stop tests for #827 (or accept #829 as the owner of these).
5. Re-measure the Persona root and fallback picture (#773, #806) once the CHD is available.
