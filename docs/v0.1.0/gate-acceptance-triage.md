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
| #351 | v0.1.0 Persona title-screen gate | OPEN, not reached | None of the nine acceptance criteria can be checked for the real title. The latest status in the issue body is the 2026-10-22 note: standalone display still differs from the interpreter (#774), and that note names no first blocker. The first blocker `A0:70 _bu_init` (#712, 2026-10-08) is historical: the 2026-10-07 section that names it is annotated as superseded, and the 2026-10-15 note limits it to the BIOS HLE bring-up and does not generalize it to the OpenBIOS main path. |
| #739 | Generated game executable shows the Persona opening | OPEN, not met | The 2026-10-11 scope correction makes a standalone generated game executable (`persona` / `persona.exe`) the primary gate. Merged work (#831) packages a synthetic PS-X EXE only. The real-ROM opening remains `BLOCKED`. |

## 2. Merged PRs mapped to Issue conditions

All PRs below are merged (verified with `gh pr list --state merged` and `git log origin/main`).
"Satisfies" means the PR text and its recorded verification cover the condition;
it is not an independent re-run. Items marked `UNVERIFIED` need a rerun before they count.

| PR | Issue(s) | Condition(s) addressed | Evidence level | Residual |
|---|---|---|---|---|
| #828 | #827 (also #630, #820) | Stop result classified from observed process state (`Cancelled` / `Clean` / `Residual` / `NotStopped`); real child-process integration tests for stop during drain, stop racing natural end, residual grandchild, and kill refused by OS | CONFIRMED (tests named in PR, run per PR text), Windows and macOS CI pass | Fallback-run stop and real-window child check (moved to #829); RHOST-wait stop is in no Issue (see section 3); Linux descendant tracking; OS kill refusal only by injection |
| #831 | #830 (B1) | `package --gui` produces a console-free launcher; host started suspended and placed in a kill-on-close Job; bounded wait and dialog timeout; GUI README; headless package output unchanged (`RuntimeHostPublishE2E` 15 pass) | CONFIRMED (unit/E2E tests, Windows harness for non-zero exit and 30 s auto-close, residual names count 0) | RGB frame screenshot on the packaged game EXE is not in the PR; Job kill-on-close recovery `UNVERIFIED`; 4-hour run `NOT_RUN`; `--self-contained` `UNVERIFIED` |
| #834 | #833 (B2) | Packaging with `--bios openbios`; manifest/report labels `biosMode`; no absolute developer paths in generated text; synthetic BIOS stub for wiring; real OpenBIOS run only when a user-supplied file exists | CONFIRMED for the labels and regression tests in `OpenBiosStandalonePackageE2E` (present in `origin/main`). `gh pr checks 834`: all pass except `CodeRabbit` (`fail`), which is recorded here as history. The PR was MERGED on 2026-10-27T04:41:17Z while its body still read "Draft, not mergeable" and "final independent review PENDING_CI" (see section 3) | Label names differ from the Issue text (see section 3); `openbios-rom` is never emitted today (see section 3); real OpenBIOS run `NOT_RUN` in this triage; runtime BIOS-to-RAM provenance tracked in #835, split BIOS in #836 |
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
  1. **Acceptance criterion 2 is not met today.** The Issue asks for `biosMode` `hle` / `openbios-rom`, with the two modes labelled separately. The code emits `hle`, `openbios-synthetic`, and `openbios-unverified`. `openbios-rom` is never emitted: `BiosProvenance.VerifiedOpenBiosSha256` is an empty set (`BiosProvenance.cs`), and `BiosProvenanceClassificationTests.TheDefaultRegistries_HoldNoVerifiedOpenBiosDigest` asserts it stays empty. A user's BIOS therefore takes the `openbios-unverified` path on every route. The owner must either accept the implemented vocabulary and update the Issue, or request a change. Either way, the `openbios-rom` case cannot be exercised until a digest is registered with a reproducible procedure (#794, #732, #730).
  2. **Final-HEAD mapping.** The PR body still says "Draft, not mergeable", cites HEAD `3fee551`, and says the final independent review (`34525b8`) is PENDING_CI. `gh pr view 834` reports `headRefOid` `62c06ca`, and `isDraft` false with `mergedAt` 2026-10-27T04:41:17Z. The checks listed by `gh pr checks 834` pass except `CodeRabbit` (`fail`). No final-HEAD independent review is recorded, so the run-to-SHA mapping and the review result are `UNVERIFIED` in this triage.
  3. **Real OpenBIOS run** with a user-supplied image (acceptance criteria 2 and 5): `NOT_RUN` (outside this worktree). This run is the only evidence that can produce `openbios-rom`.
  4. Follow-ups #835 and #836 are separate Issues and are not criteria of #833.
- Verdict: **not closable yet.** Closing #833 requires the execution evidence above (item 3) and the label decision and final-HEAD review (items 1 and 2). Acceptance criteria 2 and 5 cannot be checked without the real run. Not closed here.

### #830 (B1, merged as #831)

- Accepted: console-free launcher; `runtime/` contains the host; no absolute paths; ordinary headless output unchanged; window-close path uses the existing cancel API (per PR text, depends on #828).
- Remaining (not evidenced for the packaged game EXE):
  - Criterion 5: the red, green, and blue frames are shown in order and checked by screenshot pixels on the packaged EXE. The RGB evidence in #826 used a separate harness.
  - Criterion 4: child-tree recovery through the Job Object (kill-on-close) is explicitly `UNVERIFIED`.
  - Criterion 6: PID and name checks after close were done in one run only.
- Verdict: keep open.

### #827 (stop integration, merged as #828; residual split to #829)

- Accepted by #828: real-process tests for stop during drain, natural-end race, residual grandchild, and OS-refused kill; results are classified, not mixed.
- Remaining per the PR's own "Not verified" list: stop during fallback execution (moved to #829); stop during GPU frame emission (only a partial window); real-window child check after close (moved to #829); natural-end residual detection (moved to #829).
- **Unassigned residual: stop while waiting on RHOST reads.** #827 acceptance criterion 2 requires it, and #827 lists it under "未検証". #829's body does not include it (its scope is natural-end residual descendants, fallback stop, orphan recovery, and real-window check). No Issue owns it, so #829 must not be treated as its owner unless the owner adds it.
- Verdict: keep open. Not closable until the RHOST-wait stop is tested, or an owner is assigned for it and the split is accepted.

## 4. Closure candidates

| Category | Issues | Reason |
|---|---|---|
| Closable with evidence | None strictly in this triage | #820 is already closed. |
| Not closable yet (execution evidence missing) | #833 | Acceptance criteria 2 and 5 need a real OpenBIOS run (`NOT_RUN`), and `openbios-rom` is unreachable until a digest is registered. Items 1 and 2 in section 3 are also open. |
| Keep open (partially evidenced) | #827, #830 | Remaining items in section 3. #827 also has an unassigned RHOST-wait residual. |
| Keep open (gate not reached) | #351, #739, #205, #630, #768, #773, #806, #774 | Real-title or real-game evidence `BLOCKED` or unmeasured. |

## 5. Blockers and next steps

1. Provide the Persona CHD (`rom/PERSONA.chd`) in the local checkout to unblock real-ROM evidence for #351 and #739 (`BLOCKED`).
2. Decide the `biosMode` vocabulary for #833 (section 3).
3. Run the packaged `--gui` synthetic EXE to capture RGB frames by pixel check, and record Job Object recovery, for #830.
4. Add or confirm the fallback-stop and RHOST-wait stop tests for #827. Fallback stop is owned by #829; the RHOST-wait stop has no owner, so the owner must assign it (to #829 or another Issue) or add the test.
5. Re-measure the Persona root and fallback picture (#773, #806) once the CHD is available.
