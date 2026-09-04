#!/bin/sh
# Test Suite: approval / rebase ordering (Issue #247)
#
# The SHA-bound human approval gate must run on the FINAL merge candidate, i.e.
# after the mandatory rebase and the CI/review gates. These tests pin that
# ordering and the fail-closed behaviour around it:
#
#   TRIGGER_CHECK -> MAIN_HEAD_REFRESH -> REBASE -> VALIDATING
#                 -> APPROVAL_VALIDATION -> MERGING -> MERGED
#
# Invariant: the commit that is merged is always the commit the approval record
# is bound to. Approval is never requested for an intermediate SHA that the
# mandatory rebase is known to discard.

PASS=0
FAIL=0
ok() { PASS=$((PASS + 1)); }
bad() { FAIL=$((FAIL + 1)); echo "FAIL: $1"; }
assert() { _d="$1"; shift; if "$@" >/dev/null 2>&1; then ok; else bad "$_d"; fi; }
refute() { _d="$1"; shift; if "$@" >/dev/null 2>&1; then bad "$_d (expected failure)"; else ok; fi; }

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
WORK=$(mktemp -d 2>/dev/null || echo "/tmp/merge-approval-ordering.$$")
trap 'rm -rf "$WORK"' EXIT
FAKEBIN="$WORK/bin"
mkdir -p "$FAKEBIN" "$WORK/wt"

H1=1111111111111111111111111111111111111111
H2=2222222222222222222222222222222222222222
H3=3333333333333333333333333333333333333333
M1=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
M2=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb

# gh is only probed for availability here; PR payloads come from stubs below.
printf '#!/bin/sh\nexit 0\n' > "$FAKEBIN/gh"
chmod +x "$FAKEBIN/gh"
PATH="$FAKEBIN:$PATH"
export PATH

git -C "$WORK/wt" init -q
git -C "$WORK/wt" config user.name test
git -C "$WORK/wt" config user.email test@example.com
touch "$WORK/wt/file"
git -C "$WORK/wt" add file
git -C "$WORK/wt" commit -qm initial

MERGE_RUNTIME_DIR="$SCRIPT_DIR/.."
# shellcheck disable=SC1091
. "$SCRIPT_DIR/../orchestrator.sh"

MERGE_PR_NUMBER=243
MERGE_ISSUE_NUMBER=242
MERGE_WORKTREE="$WORK/wt"
MERGE_BRANCH="issue/242-x"
MERGE_MAIN_DIR="$WORK/wt"
MERGE_REPOSITORY=""

# Build a gh pr view payload. Usage: _pr_json <head_oid> [mergeable] [review] [ci]
_pr_json() {
    _oid="$1"
    _mergeable="${2:-MERGEABLE}"
    _review="${3:-APPROVED}"
    _ci="${4:-SUCCESS}"
    printf '{"number":243,"title":"t","headRefName":"issue/242-x","headRefOid":"%s","baseRefName":"main","state":"OPEN","isDraft":false,"mergeable":"%s","reviewDecision":"%s","commits":[{"conclusion":"%s"}]}' \
        "$_oid" "$_mergeable" "$_review" "$_ci"
}

# Fresh state file positioned at a given state, with the fields a scenario needs.
# Usage: _state <name> <state> <current> <rebased_onto> [approved_sha]
_state() {
    _sf="$WORK/$1.json"
    merge_new_state "$MERGE_PR_NUMBER" "$MERGE_ISSUE_NUMBER" "$MERGE_WORKTREE" "$MERGE_BRANCH" > "$_sf"
    merge_state_set_string "$_sf" \
        "State" "$2" \
        "CurrentCommitSha" "$3" \
        "MainHeadSha" "$4" \
        "RebasedOntoMainSha" "$4"
    if [ -n "$5" ]; then
        merge_state_set_string "$_sf" "ApprovedCommitSha" "$5"
    fi
    printf '%s' "$_sf"
}

_reset_stubs() {
    merge_gh_available() { return 0; }
    merge_rebase_force_with_lease_enabled() { return 1; }
    merge_get_main_head() { printf '%s\n' "$M1"; }
    merge_get_current_commit() { printf '%s\n' "$H1"; }
    merge_get_pr_info() { _pr_json "$H1"; }
    merge_rebase() { printf '%s\n' success=true has_conflicts=false; }
    # Fixtures below use synthetic SHAs and no remote; Scenario 5c re-sources
    # the runtime to exercise the real PR-head synchronisation against git.
    merge_remote_head_state() { printf '%s\n' "relation=same"; }
    merge_ff_worktree_to_remote() { return 0; }
    MERGE_CALLED=0
    merge_normal_merge() { MERGE_CALLED=$((MERGE_CALLED + 1)); return 0; }
}

echo "=== Merge Approval / Rebase Ordering Tests (Issue #247) ==="
echo ""

# ------------------------------------------------------------------
# Ordering contract in the state machine itself
# ------------------------------------------------------------------
echo "--- State machine ordering ---"
assert "TRIGGER_CHECK enters MAIN_HEAD_REFRESH" merge_valid_transition TRIGGER_CHECK MAIN_HEAD_REFRESH
refute "TRIGGER_CHECK no longer enters APPROVAL_VALIDATION" merge_valid_transition TRIGGER_CHECK APPROVAL_VALIDATION
assert "REBASE enters VALIDATING" merge_valid_transition REBASE VALIDATING
refute "REBASE no longer enters APPROVAL_VALIDATION" merge_valid_transition REBASE APPROVAL_VALIDATION
assert "VALIDATING enters APPROVAL_VALIDATION" merge_valid_transition VALIDATING APPROVAL_VALIDATION
refute "VALIDATING cannot skip approval into MERGING" merge_valid_transition VALIDATING MERGING
assert "APPROVAL_VALIDATION enters MERGING" merge_valid_transition APPROVAL_VALIDATION MERGING
assert "APPROVAL_VALIDATION can return for re-rebase" merge_valid_transition APPROVAL_VALIDATION MAIN_HEAD_REFRESH
refute "APPROVAL_VALIDATION cannot reach MERGED directly" merge_valid_transition APPROVAL_VALIDATION MERGED
assert "MERGING can fall back for fresh approval" merge_valid_transition MERGING APPROVAL_VALIDATION
assert "MERGING can fall back for re-rebase" merge_valid_transition MERGING MAIN_HEAD_REFRESH
refute "CONFLICT reaches no approval stage" merge_valid_transition CONFLICT APPROVAL_VALIDATION

# ------------------------------------------------------------------
# Scenario 1: main current, no rebase movement, valid approval -> merge
# ------------------------------------------------------------------
echo ""
echo "--- Scenario 1: rebase not needed, valid approval on current HEAD ---"
_reset_stubs
S=$(_state s1 VALIDATING "$H1" "$M1")
MERGE_STATE_FILE="$S"
_merge_handle_validating >/dev/null 2>&1
assert "gates pass -> APPROVAL_VALIDATION" test "$(merge_state_get "$S" State)" = APPROVAL_VALIDATION
merge_state_set_approval "$S" "$(merge_approval_object 243 242 "$H1" "$M1" alice 2026-01-01T00:00:00Z)"
_merge_handle_approval_validation >/dev/null 2>&1
assert "valid approval on final HEAD -> MERGING" test "$(merge_state_get "$S" State)" = MERGING
assert "approved SHA is the final candidate" test "$(merge_state_get "$S" ApprovedCommitSha)" = "$H1"
_merge_handle_merging >/dev/null 2>&1
assert "revalidation passes -> MERGED" test "$(merge_state_get "$S" State)" = MERGED
assert "standard merge was executed once" test "$MERGE_CALLED" -eq 1

# ------------------------------------------------------------------
# Scenario 2: main ahead, rebase required, no approval.
# The flow MUST reach and complete the rebase, then wait for approval on the
# post-rebase HEAD. It must NOT stall before the rebase.
# ------------------------------------------------------------------
echo ""
echo "--- Scenario 2: rebase required, no approval ---"
_reset_stubs
S="$WORK/s2.json"
merge_new_state 243 242 "$MERGE_WORKTREE" "$MERGE_BRANCH" > "$S"
MERGE_STATE_FILE="$S"
merge_state_set_string "$S" "State" "MAIN_HEAD_REFRESH"
printf '0\n' > "$WORK/s2calls"
merge_get_current_commit() {
    _c=$(cat "$WORK/s2calls"); _c=$((_c + 1)); printf '%s\n' "$_c" > "$WORK/s2calls"
    if [ "$_c" -le 1 ]; then printf '%s\n' "$H1"; else printf '%s\n' "$H2"; fi
}
merge_get_pr_info() { _pr_json "$H2"; }
SEEN_REBASE=0
_merge_handle_main_head_refresh >/dev/null 2>&1
assert "MAIN_HEAD_REFRESH -> REBASE" test "$(merge_state_get "$S" State)" = REBASE
[ "$(merge_state_get "$S" State)" = REBASE ] && SEEN_REBASE=1
_merge_handle_rebase >/dev/null 2>&1
assert "rebase runs without any approval -> VALIDATING" test "$(merge_state_get "$S" State)" = VALIDATING
assert "rebase happened before approval was requested" test "$SEEN_REBASE" -eq 1
assert "post-rebase candidate recorded" test "$(merge_state_get "$S" CurrentCommitSha)" = "$H2"
assert "rebase base recorded" test "$(merge_state_get "$S" RebasedOntoMainSha)" = "$M1"
_merge_handle_validating >/dev/null 2>&1
assert "gates pass -> APPROVAL_VALIDATION" test "$(merge_state_get "$S" State)" = APPROVAL_VALIDATION
_merge_handle_approval_validation >/dev/null 2>&1
assert "no approval -> holds at APPROVAL_VALIDATION" test "$(merge_state_get "$S" State)" = APPROVAL_VALIDATION
assert "approval is awaited on the post-rebase HEAD" test "$(merge_state_get "$S" CurrentCommitSha)" = "$H2"
_out=$(_merge_handle_approval_validation 2>&1)
assert "approval request names the post-rebase SHA" \
    sh -c "printf '%s' \"\$1\" | grep -q 'READY FOR HUMAN APPROVAL: $H2'" _ "$_out"

# ------------------------------------------------------------------
# Scenario 3: approval bound to the pre-rebase HEAD is destroyed by the rebase
# ------------------------------------------------------------------
echo ""
echo "--- Scenario 3: pre-rebase approval invalidated by SHA change ---"
_reset_stubs
S=$(_state s3 REBASE "$H1" "$M1")
MERGE_STATE_FILE="$S"
merge_state_set_approval "$S" "$(merge_approval_object 243 242 "$H1" "$M1" alice 2026-01-01T00:00:00Z)"
merge_state_set_string "$S" "ApprovedCommitSha" "$H1"
printf '0\n' > "$WORK/s3calls"
merge_get_current_commit() {
    _c=$(cat "$WORK/s3calls"); _c=$((_c + 1)); printf '%s\n' "$_c" > "$WORK/s3calls"
    if [ "$_c" -le 1 ]; then printf '%s\n' "$H1"; else printf '%s\n' "$H2"; fi
}
merge_get_pr_info() { _pr_json "$H2"; }
_merge_handle_rebase >/dev/null 2>&1
assert "changed HEAD still advances to VALIDATING" test "$(merge_state_get "$S" State)" = VALIDATING
assert "stale approval record removed" test -z "$(merge_state_approval_commit "$S")"
assert "stale approved SHA cleared" test -z "$(merge_state_get "$S" ApprovedCommitSha)"
_merge_handle_validating >/dev/null 2>&1
_merge_handle_approval_validation >/dev/null 2>&1
assert "new HEAD requires a fresh approval" test "$(merge_state_get "$S" State)" = APPROVAL_VALIDATION
# The old approval SHA must not validate against the new candidate.
merge_state_set_approval "$S" "$(merge_approval_object 243 242 "$H1" "$M1" alice 2026-01-01T00:00:00Z)"
merge_get_current_commit() { printf '%s\n' "$H2"; }
_merge_handle_approval_validation >/dev/null 2>&1
assert "old-SHA approval does not unlock the new candidate" test "$(merge_state_get "$S" State)" = APPROVAL_VALIDATION

# ------------------------------------------------------------------
# Scenario 4: approval taken on the post-rebase HEAD merges
# ------------------------------------------------------------------
echo ""
echo "--- Scenario 4: approval on the post-rebase HEAD ---"
_reset_stubs
merge_get_current_commit() { printf '%s\n' "$H2"; }
merge_get_pr_info() { _pr_json "$H2"; }
S=$(_state s4 APPROVAL_VALIDATION "$H2" "$M1")
MERGE_STATE_FILE="$S"
merge_state_set_approval "$S" "$(merge_approval_object 243 242 "$H2" "$M1" alice 2026-01-01T00:00:00Z)"
_merge_handle_approval_validation >/dev/null 2>&1
assert "post-rebase approval -> MERGING" test "$(merge_state_get "$S" State)" = MERGING
_merge_handle_merging >/dev/null 2>&1
assert "unchanged final HEAD -> MERGED" test "$(merge_state_get "$S" State)" = MERGED
assert "merged exactly the approved SHA" test "$MERGE_CALLED" -eq 1

# ------------------------------------------------------------------
# Scenario 5: PR HEAD moves after approval -> must not merge
# ------------------------------------------------------------------
echo ""
echo "--- Scenario 5: PR HEAD changes after approval ---"
_reset_stubs
S=$(_state s5 MERGING "$H2" "$M1" "$H2")
MERGE_STATE_FILE="$S"
merge_state_set_approval "$S" "$(merge_approval_object 243 242 "$H2" "$M1" alice 2026-01-01T00:00:00Z)"
merge_get_current_commit() { printf '%s\n' "$H3"; }
merge_get_pr_info() { _pr_json "$H3"; }
_merge_handle_merging >/dev/null 2>&1
assert "moved HEAD blocks the merge" test "$(merge_state_get "$S" State)" = APPROVAL_VALIDATION
assert "no merge was executed" test "$MERGE_CALLED" -eq 0
assert "approval revoked on HEAD move" test -z "$(merge_state_approval_commit "$S")"

# A remote-only move (push landing after approval) is caught even when the
# local worktree still sits on the approved commit.
_reset_stubs
S=$(_state s5b MERGING "$H2" "$M1" "$H2")
MERGE_STATE_FILE="$S"
merge_state_set_approval "$S" "$(merge_approval_object 243 242 "$H2" "$M1" alice 2026-01-01T00:00:00Z)"
merge_get_current_commit() { printf '%s\n' "$H2"; }
merge_get_pr_info() { _pr_json "$H3"; }
_merge_handle_merging >/dev/null 2>&1
assert "no merge on remote HEAD move" test "$MERGE_CALLED" -eq 0
assert "remote HEAD move revokes the approval" test -z "$(merge_state_approval_commit "$S")"
# A moved remote head means the CANDIDATE changed, so the flow must rebuild it
# rather than re-offer the stale local HEAD for approval (Scenario 5c proves the
# rebuild converges; offering the stale HEAD here would loop).
assert "remote HEAD move rebuilds the candidate" test "$(merge_state_get "$S" State)" = MAIN_HEAD_REFRESH
refute "remote HEAD move does not re-prompt on the stale candidate" test "$(merge_state_get "$S" State)" = APPROVAL_VALIDATION

# ------------------------------------------------------------------
# Scenario 5c: remote-only HEAD drift must RECOVER, not re-ask for the stale
# local candidate. Blocking the first merge is not enough: the flow has to take
# the remote PR head as the new candidate and reach a merge of that SHA.
# Runs against a real repository so the fast-forward and rebase are real.
# ------------------------------------------------------------------
echo ""
echo "--- Scenario 5c: remote-only HEAD drift recovery ---"
_reset_stubs
# Restore the real git-facing helpers this scenario is about.
# shellcheck disable=SC1091
. "$SCRIPT_DIR/../git-operations.sh"

R="$WORK/5c"
mkdir -p "$R"
git init -q --bare "$R/remote.git"
git init -q -b main "$R/repo"
git -C "$R/repo" config user.email t@e.com
git -C "$R/repo" config user.name t
git -C "$R/repo" remote add origin "$R/remote.git"
echo base > "$R/repo/base.txt"
git -C "$R/repo" add base.txt
git -C "$R/repo" commit -qm base
git -C "$R/repo" push -q -u origin main
R_M1=$(git -C "$R/repo" rev-parse main)

git -C "$R/repo" worktree add -q -b issue/242-5c "$R/wt" main
echo two > "$R/wt/a.txt"
git -C "$R/wt" add a.txt
git -C "$R/wt" commit -qm h2
git -C "$R/wt" push -q -u origin issue/242-5c
R_H2=$(git -C "$R/wt" rev-parse HEAD)

# Someone pushes on top of the PR branch; the worktree stays behind on H2.
git clone -q "$R/remote.git" "$R/other" 2>/dev/null
git -C "$R/other" config user.email o@e.com
git -C "$R/other" config user.name o
git -C "$R/other" checkout -q issue/242-5c
echo three > "$R/other/b.txt"
git -C "$R/other" add b.txt
git -C "$R/other" commit -qm h3
git -C "$R/other" push -q origin issue/242-5c
R_H3=$(git -C "$R/other" rev-parse HEAD)

assert "fixture: remote moved ahead of the worktree" test "$R_H2" != "$R_H3"
assert "fixture: worktree is still on the approved SHA" test "$(git -C "$R/wt" rev-parse HEAD)" = "$R_H2"

S="$WORK/s5c.json"
MERGE_STATE_FILE="$S"
MERGE_WORKTREE="$R/wt"
MERGE_BRANCH="issue/242-5c"
MERGE_MAIN_DIR="$R/repo"
merge_new_state 243 242 "$R/wt" issue/242-5c > "$S"
merge_state_set_string "$S" \
    "State" "MERGING" \
    "BranchName" "issue/242-5c" \
    "CurrentCommitSha" "$R_H2" \
    "ApprovedCommitSha" "$R_H2" \
    "MainHeadSha" "$R_M1" \
    "RebasedOntoMainSha" "$R_M1"
merge_state_set_approval "$S" "$(merge_approval_object 243 242 "$R_H2" "$R_M1" alice 2026-01-01T00:00:00Z)"

merge_gh_available() { return 0; }
merge_get_main_head() { printf '%s\n' "$R_M1"; }
merge_rebase_force_with_lease_enabled() { return 1; }
merge_get_pr_info() {
    printf '{"number":243,"title":"t","headRefName":"issue/242-5c","headRefOid":"%s","baseRefName":"main","state":"OPEN","isDraft":false,"mergeable":"MERGEABLE","reviewDecision":"APPROVED","commits":[{"conclusion":"SUCCESS"}]}' "$R_H3"
}
MERGE_CALLED=0
merge_normal_merge() { MERGE_CALLED=$((MERGE_CALLED + 1)); return 0; }

_5c_step() {
    case "$(merge_state_get "$S" State)" in
        MAIN_HEAD_REFRESH)   _merge_handle_main_head_refresh ;;
        REBASE)              _merge_handle_rebase ;;
        VALIDATING)          _merge_handle_validating ;;
        APPROVAL_VALIDATION) _merge_handle_approval_validation ;;
        MERGING)             _merge_handle_merging ;;
        *) return 0 ;;
    esac
}

# Drive the recovery to the point where a human is asked for an approval.
_5c_log="$WORK/5c.log"
: > "$_5c_log"
_i=0
while [ "$_i" -lt 8 ]; do
    _i=$((_i + 1))
    _5c_step >> "$_5c_log" 2>&1
    case "$(merge_state_get "$S" State)" in
        APPROVAL_VALIDATION)
            grep -q "READY FOR HUMAN APPROVAL" "$_5c_log" && break ;;
        FAILED|CONFLICT|MERGED|COMPLETED) break ;;
    esac
done

assert "first merge attempt is blocked" test "$MERGE_CALLED" -eq 0
assert "stale approval is revoked" test -z "$(merge_state_approval_commit "$S")"
assert "recovery rebuilds from the remote PR head" test "$(merge_state_get "$S" RebasedOntoMainSha)" = "$R_M1"
assert "worktree fast-forwarded onto the remote PR head" test "$(git -C "$R/wt" rev-parse HEAD)" = "$R_H3"
assert "candidate becomes the remote PR head" test "$(merge_state_get "$S" CurrentCommitSha)" = "$R_H3"
assert "flow settles at the approval gate" test "$(merge_state_get "$S" State)" = APPROVAL_VALIDATION

# Negative assertion: the stale local candidate must never be offered.
refute "stale local H2 is never offered for approval" grep -q "READY FOR HUMAN APPROVAL: $R_H2" "$_5c_log"
assert "the new candidate H3 is offered for approval" grep -q "READY FOR HUMAN APPROVAL: $R_H3" "$_5c_log"

# Approve the new candidate and finish: the merge must happen, on that SHA.
merge_state_set_approval "$S" "$(merge_approval_object 243 242 "$R_H3" "$R_M1" alice 2026-01-01T00:00:00Z)"
_merge_handle_approval_validation >> "$_5c_log" 2>&1
assert "approval on the recovered candidate -> MERGING" test "$(merge_state_get "$S" State)" = MERGING
assert "approved SHA is the remote PR head" test "$(merge_state_get "$S" ApprovedCommitSha)" = "$R_H3"
_merge_handle_merging >> "$_5c_log" 2>&1
assert "recovered candidate merges" test "$(merge_state_get "$S" State)" = MERGED
assert "merge executed exactly once" test "$MERGE_CALLED" -eq 1
assert "merged SHA equals approved SHA" test "$(merge_state_get "$S" ApprovedCommitSha)" = "$R_H3"

# --- PR head synchronisation primitive, against the same real repository ---
echo ""
echo "--- Remote PR head relation ---"
_rel() { merge_remote_head_field "$(merge_remote_head_state "$1" origin "$2")" relation; }
assert "in sync -> same" test "$(_rel "$R/wt" issue/242-5c)" = same
git -C "$R/wt" reset -q --hard "$R_H2"
assert "remote ahead -> remote_ahead" test "$(_rel "$R/wt" issue/242-5c)" = remote_ahead
assert "fast-forward succeeds" merge_ff_worktree_to_remote "$R/wt" origin issue/242-5c
assert "fast-forward reaches the remote head" test "$(git -C "$R/wt" rev-parse HEAD)" = "$R_H3"
echo local > "$R/wt/local.txt"
git -C "$R/wt" add local.txt
git -C "$R/wt" commit -qm "local only"
assert "unpushed local work -> local_ahead" test "$(_rel "$R/wt" issue/242-5c)" = local_ahead
git -C "$R/wt" reset -q --hard "$R_H2"
echo fork > "$R/wt/fork.txt"
git -C "$R/wt" add fork.txt
git -C "$R/wt" commit -qm "diverging"
assert "rewritten history -> diverged" test "$(_rel "$R/wt" issue/242-5c)" = diverged
assert "missing branch -> unknown" test "$(_rel "$R/wt" issue/242-does-not-exist)" = unknown

# A diverged or unverifiable remote head must fail closed in REBASE, never
# silently rebase a candidate the PR does not contain.
merge_state_set_string "$S" "State" "REBASE"
_merge_handle_rebase >/dev/null 2>&1
assert "diverged remote head fails closed at REBASE" test "$(merge_state_get "$S" State)" = FAILED
assert "diverged remote head does not merge" test "$MERGE_CALLED" -eq 1

merge_state_set_string "$S" "State" "REBASE" "BranchName" "issue/242-does-not-exist"
_merge_handle_rebase >/dev/null 2>&1
assert "unverifiable remote head fails closed at REBASE" test "$(merge_state_get "$S" State)" = FAILED

# Restore the shared fixture context for the scenarios that follow.
MERGE_WORKTREE="$WORK/wt"
MERGE_BRANCH="issue/242-x"
MERGE_MAIN_DIR="$WORK/wt"

# ------------------------------------------------------------------
# Scenario 6: main advances after approval -> stale approval cannot merge
# ------------------------------------------------------------------
echo ""
echo "--- Scenario 6: main advances after approval ---"
_reset_stubs
merge_get_current_commit() { printf '%s\n' "$H2"; }
merge_get_pr_info() { _pr_json "$H2"; }
S=$(_state s6 MERGING "$H2" "$M1" "$H2")
MERGE_STATE_FILE="$S"
merge_state_set_approval "$S" "$(merge_approval_object 243 242 "$H2" "$M1" alice 2026-01-01T00:00:00Z)"
merge_get_main_head() { printf '%s\n' "$M2"; }
_merge_handle_merging >/dev/null 2>&1
assert "moved main blocks the merge" test "$(merge_state_get "$S" State)" = MAIN_HEAD_REFRESH
assert "no merge on stale rebase base" test "$MERGE_CALLED" -eq 0
assert "stale approval revoked" test -z "$(merge_state_approval_commit "$S")"

# The same drift observed at the approval gate re-runs the rebase instead of
# binding an approval to a candidate that is no longer rebased onto main.
_reset_stubs
merge_get_current_commit() { printf '%s\n' "$H2"; }
merge_get_main_head() { printf '%s\n' "$M2"; }
S=$(_state s6b APPROVAL_VALIDATION "$H2" "$M1")
MERGE_STATE_FILE="$S"
merge_state_set_approval "$S" "$(merge_approval_object 243 242 "$H2" "$M1" alice 2026-01-01T00:00:00Z)"
_merge_handle_approval_validation >/dev/null 2>&1
assert "approval gate re-rebases on main drift" test "$(merge_state_get "$S" State)" = MAIN_HEAD_REFRESH
assert "approval discarded on main drift" test -z "$(merge_state_approval_commit "$S")"

# ------------------------------------------------------------------
# Scenario 7: rebase conflict fails closed, never reaching the approval gate
# ------------------------------------------------------------------
echo ""
echo "--- Scenario 7: rebase conflict ---"
_reset_stubs
S=$(_state s7 REBASE "$H1" "$M1")
MERGE_STATE_FILE="$S"
# MAIN_HEAD_REFRESH clears the marker on entry to REBASE, so a conflicting
# rebase leaves the candidate provably un-rebased.
merge_state_set_string "$S" "RebasedOntoMainSha" ""
merge_rebase() { printf '%s\n' success=false has_conflicts=true conflict_files=a.txt; }
_merge_handle_rebase >/dev/null 2>&1
assert "conflict -> CONFLICT" test "$(merge_state_get "$S" State)" = CONFLICT
refute "CONFLICT cannot advance to APPROVAL_VALIDATION" merge_valid_transition CONFLICT APPROVAL_VALIDATION
refute "CONFLICT cannot advance to MERGING" merge_valid_transition CONFLICT MERGING
assert "conflict leaves no rebase base recorded" test -z "$(merge_state_get "$S" RebasedOntoMainSha)"

# A rebase that fails without conflicts also stops short of the approval gate.
_reset_stubs
S=$(_state s7b REBASE "$H1" "$M1")
MERGE_STATE_FILE="$S"
merge_rebase() { printf '%s\n' success=false has_conflicts=false; }
_merge_handle_rebase >/dev/null 2>&1
assert "rebase failure -> FAILED" test "$(merge_state_get "$S" State)" = FAILED

# ------------------------------------------------------------------
# Scenario 8: post-rebase required CI failure blocks before approval
# ------------------------------------------------------------------
echo ""
echo "--- Scenario 8: post-rebase CI failure ---"
_reset_stubs
S=$(_state s8 VALIDATING "$H2" "$M1")
MERGE_STATE_FILE="$S"
merge_get_current_commit() { printf '%s\n' "$H2"; }
merge_get_pr_info() { _pr_json "$H2" MERGEABLE APPROVED FAILURE; }
_merge_handle_validating >/dev/null 2>&1
assert "failing CI -> FAILED" test "$(merge_state_get "$S" State)" = FAILED
refute "failing CI never reaches the approval gate" test "$(merge_state_get "$S" State)" = APPROVAL_VALIDATION
assert "no approval was recorded" test -z "$(merge_state_approval_commit "$S")"

# A non-mergeable PR is likewise stopped before any approval is requested.
_reset_stubs
S=$(_state s8b VALIDATING "$H2" "$M1")
MERGE_STATE_FILE="$S"
merge_get_current_commit() { printf '%s\n' "$H2"; }
merge_get_pr_info() { _pr_json "$H2" CONFLICTING APPROVED SUCCESS; }
_merge_handle_validating >/dev/null 2>&1
assert "non-mergeable PR -> FAILED before approval" test "$(merge_state_get "$S" State)" = FAILED

# ------------------------------------------------------------------
# Scenario 9: review gate failure keeps the PR unmerged
# ------------------------------------------------------------------
echo ""
echo "--- Scenario 9: review gate failure ---"
_reset_stubs
S=$(_state s9 VALIDATING "$H2" "$M1")
MERGE_STATE_FILE="$S"
merge_get_current_commit() { printf '%s\n' "$H2"; }
merge_get_pr_info() { _pr_json "$H2" MERGEABLE REVIEW_REQUIRED SUCCESS; }
_merge_handle_validating >/dev/null 2>&1
assert "review gate failure -> FAILED" test "$(merge_state_get "$S" State)" = FAILED

# A review gate that fails only after approval still blocks at the final
# revalidation, so the merge cannot slip through the window.
_reset_stubs
S=$(_state s9b MERGING "$H2" "$M1" "$H2")
MERGE_STATE_FILE="$S"
merge_state_set_approval "$S" "$(merge_approval_object 243 242 "$H2" "$M1" alice 2026-01-01T00:00:00Z)"
merge_get_current_commit() { printf '%s\n' "$H2"; }
merge_get_pr_info() { _pr_json "$H2" MERGEABLE REVIEW_REQUIRED SUCCESS; }
_merge_handle_merging >/dev/null 2>&1
assert "late review gate failure blocks the merge" test "$(merge_state_get "$S" State)" = APPROVAL_VALIDATION
assert "no merge on late gate failure" test "$MERGE_CALLED" -eq 0

# ------------------------------------------------------------------
# Scenario 10: resume / recovery keeps approved SHA == merged SHA
# ------------------------------------------------------------------
echo ""
echo "--- Scenario 10: resume and recovery ---"
# A state file written by the previous (approval-first) ordering sits at
# APPROVAL_VALIDATION *before* any rebase. Resuming it must not merge; it must
# fall back to the mandatory rebase.
_reset_stubs
S="$WORK/s10-legacy.json"
cat > "$S" <<'LEGACY'
{
  "PrNumber": 243,
  "IssueNumber": 242,
  "BranchName": "issue/242-x",
  "WorktreePath": "WORKTREE",
  "State": "APPROVAL_VALIDATION",
  "CurrentCommitSha": null,
  "ApprovedCommitSha": null,
  "MainHeadSha": null,
  "Approval": null,
  "ConflictFiles": null,
  "FailureReason": null,
  "CreatedAt": "2026-01-01T00:00:00Z",
  "UpdatedAt": "2026-01-01T00:00:00Z"
}
LEGACY
MERGE_STATE_FILE="$S"
refute "legacy state lacks the rebase marker" grep -q RebasedOntoMainSha "$S"
assert "legacy state migrates" merge_state_migrate "$S"
assert "migration adds the rebase marker" grep -q '"RebasedOntoMainSha": null' "$S"
assert "migration preserves the persisted state" test "$(merge_state_get "$S" State)" = APPROVAL_VALIDATION
# Even with an approval that looks valid for the current HEAD, an unrebased
# candidate must go back through the mandatory rebase.
merge_state_set_approval "$S" "$(merge_approval_object 243 242 "$H1" "$M1" alice 2026-01-01T00:00:00Z)"
_merge_handle_approval_validation >/dev/null 2>&1
assert "unrebased resume returns to MAIN_HEAD_REFRESH" test "$(merge_state_get "$S" State)" = MAIN_HEAD_REFRESH
assert "unrebased resume does not keep the approval" test -z "$(merge_state_approval_commit "$S")"

# Migration is idempotent and does not duplicate the field.
assert "migration is idempotent" merge_state_migrate "$S"
assert "no duplicate marker after re-migration" test "$(grep -c RebasedOntoMainSha "$S")" -eq 1

# Resuming at MERGING with a state whose approval no longer matches must not
# merge: the approved SHA and the merged SHA can never diverge.
_reset_stubs
S=$(_state s10b MERGING "$H2" "$M1" "$H2")
MERGE_STATE_FILE="$S"
merge_get_current_commit() { printf '%s\n' "$H2"; }
merge_get_pr_info() { _pr_json "$H2"; }
_merge_handle_merging >/dev/null 2>&1
assert "resume without an approval record refuses to merge" test "$(merge_state_get "$S" State)" = APPROVAL_VALIDATION
assert "no merge without an approval record" test "$MERGE_CALLED" -eq 0

# Resuming at MERGING with a consistent record does merge, and merges exactly
# the approved SHA.
_reset_stubs
S=$(_state s10c MERGING "$H2" "$M1" "$H2")
MERGE_STATE_FILE="$S"
merge_state_set_approval "$S" "$(merge_approval_object 243 242 "$H2" "$M1" alice 2026-01-01T00:00:00Z)"
merge_get_current_commit() { printf '%s\n' "$H2"; }
merge_get_pr_info() { _pr_json "$H2"; }
_merge_handle_merging >/dev/null 2>&1
assert "consistent resume merges" test "$(merge_state_get "$S" State)" = MERGED
assert "merged SHA equals approved SHA" test "$(merge_state_get "$S" ApprovedCommitSha)" = "$H2"

# ------------------------------------------------------------------
# Final HEAD guard unit checks
# ------------------------------------------------------------------
echo ""
echo "--- Final HEAD guard ---"
assert "guard is silent when every binding holds" \
    test -z "$(merge_final_head_guard "$H2" "$H2" "$H2" "$H2" "$M1" "$M1" "")"
assert "guard rejects a missing approved SHA" \
    test -n "$(merge_final_head_guard "" "$H2" "$H2" "$H2" "$M1" "$M1" "")"
assert "guard rejects a missing approval record" \
    test -n "$(merge_final_head_guard "$H2" "" "$H2" "$H2" "$M1" "$M1" "")"
assert "guard rejects a local HEAD move" \
    test -n "$(merge_final_head_guard "$H2" "$H2" "$H3" "$H2" "$M1" "$M1" "")"
assert "guard rejects a remote HEAD move" \
    test -n "$(merge_final_head_guard "$H2" "$H2" "$H2" "$H3" "$M1" "$M1" "")"
assert "guard rejects an undeterminable remote HEAD" \
    test -n "$(merge_final_head_guard "$H2" "$H2" "$H2" "" "$M1" "$M1" "")"
assert "guard rejects a missing rebase base" \
    test -n "$(merge_final_head_guard "$H2" "$H2" "$H2" "$H2" "" "$M1" "")"
assert "guard rejects a moved main" \
    test -n "$(merge_final_head_guard "$H2" "$H2" "$H2" "$H2" "$M1" "$M2" "")"
assert "guard rejects a failing PR gate" \
    test -n "$(merge_final_head_guard "$H2" "$H2" "$H2" "$H2" "$M1" "$M1" "Required checks failed")"

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ]
